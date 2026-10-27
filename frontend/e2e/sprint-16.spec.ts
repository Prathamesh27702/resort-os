import { APIRequestContext, Browser, Page, expect, test } from '@playwright/test';
import { apiAs, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 16 — the same checks as docs/sprints/REVIEWING.md → "Sprint 16", automated. The demo: the full purchase cycle
// for vegetables. The kitchen's request PR00001 becomes an order to Mapusa Fresh Vegetables; at ₹11,000 it is over the
// ₹10,000 limit, so the owner approves it; it comes in over two deliveries (the remaining quantity is tracked between
// them) and each delivery puts stock into the main store; the supplier's bill is recorded and paid by UPI.

test.describe.configure({ mode: 'serial' });

const PHONE = { viewport: { width: 360, height: 780 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 };
let m: Page;          // the owner, on a desktop
let p: Page;          // the purchasing clerk, on a phone
let api: APIRequestContext;
let pid = '';
let storeId = '';
let orderUrl = '';
let orderNumber = '';
const before: Record<string, number> = {};
const pw = `Staff-${runId}-2026`;
const clerk = { email: `p16.${runId}@demo.local`, name: `Priya Purchase ${runId}` };

interface Item { id: string; code: string; onHand: number; levels: { locationId: string; onHand: number }[]; }

const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text }).last()).toBeVisible();
const noSideScroll = async (page: Page) => expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);
const pick = async (page: Page, select: string, option: string) => {
  await page.getByRole('dialog').getByRole('combobox', { name: select }).click();
  await page.getByRole('option', { name: option, exact: true }).click();
};
const status = (page: Page) => page.getByTestId('po-status');
const line = (page: Page, name: string) => page.locator(`.tr[data-line="${name}"]`);
const inStore = async () => {
  const s = (await (await api.get(`/api/v1/properties/${pid}/inventory`)).json()) as { items: Item[] };
  return (code: string) => s.items.find(i => i.code === code)!.levels.find(l => l.locationId === storeId)?.onHand ?? 0;
};

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  pid = (await (await api.get('/api/v1/properties')).json())[0].id;
  await createStaff(api, clerk.email, clerk.name, 'PURCHASING', pid, pw);
  const inv = (await (await api.get(`/api/v1/properties/${pid}/inventory`)).json()) as { locations: { id: string; code: string }[] };
  storeId = inv.locations.find(l => l.code === 'STORE')!.id;
  const at = await inStore();
  for (const code of ['ONION-CAPSICUM', 'MIXED-VEG', 'LIME']) before[code] = at(code);

  m = await (await browser.newContext()).newPage();
  await signIn(m, owner.email, owner.password);
  p = await (await browser.newContext(PHONE)).newPage();
  await signIn(p, clerk.email, pw);
});

test('S16-01 Suppliers: GSTIN and state for each, the fishermen unregistered; a mistyped GSTIN is refused under its field', async () => {
  await m.goto('/inventory/suppliers');
  await expect(m.getByRole('heading', { name: 'Suppliers' })).toBeVisible();
  await expect(m.locator('li[data-supplier="FRESH-VEG"]')).toContainText('GSTIN 30AAACF1234A1ZS · Goa');
  await expect(m.locator('li[data-supplier="FRESH-VEG"]')).toContainText('pays after 7 days');
  await expect(m.locator('li[data-supplier="SPICES"]')).toContainText('Maharashtra');
  await expect(m.locator('li[data-supplier="FISH"]')).toContainText('Unregistered: no GST');
  await shot(m, '01-suppliers');

  await m.getByRole('button', { name: 'Supplier', exact: true }).click();
  const d = m.getByRole('dialog');
  await d.getByLabel('Name (as on their bills)').fill(`Calangute Bakery ${runId}`);
  await d.getByLabel('Code').fill(`BAKE-${runId}`.toUpperCase());
  await d.getByLabel('GSTIN (empty if unregistered)').fill('30AAACF1234A1ZT');
  await d.getByRole('button', { name: 'Save' }).click();
  await expect(d.locator('mat-error')).toHaveText('The last character (check digit) should be S for this GSTIN; check it for a typing mistake.');
  await shot(m, '02-gstin-refused');
  await d.getByRole('button', { name: 'Cancel' }).click();
});

test('S16-02 The clerk raises an order from the kitchen\'s request PR00001; over the limit, it waits for approval', async () => {
  await p.goto('/inventory/purchases?view=requests');
  const req = p.locator('li[data-request="PR00001"]');
  await expect(req).toContainText('15 kg Onion and capsicum');
  await expect(req).toContainText('Open');
  await noSideScroll(p);
  await shot(p, '03-request-phone');

  await req.getByRole('button', { name: 'Raise an order for PR00001' }).click();
  const d = p.getByRole('dialog');
  await expect(d.getByRole('heading', { name: 'Order for PR00001' })).toBeVisible();
  await pick(p, 'Supplier', 'Mapusa Fresh Vegetables');
  await expect(d.getByText('Goa (30) · CGST + SGST')).toBeVisible();
  // A wedding at the weekend: 160 kg of mixed vegetables instead of 10.
  await d.getByRole('spinbutton', { name: 'Quantity 2' }).fill('160');
  await expect(d.getByTestId('order-total')).toHaveText('Before GST ₹11,000.00 · GST ₹0.00 · Total ₹11,000.00');
  await d.getByRole('button', { name: 'Save draft' }).click();

  await expect(p).toHaveURL(/\/inventory\/purchases\/[0-9a-f-]{36}$/);
  orderUrl = new URL(p.url()).pathname;
  orderNumber = (await p.getByRole('heading', { level: 1 }).innerText()).split(' ')[0];
  expect(orderNumber).toMatch(/^PO\d{5}$/);
  await expect(status(p)).toHaveText('Draft');
  await expect(p.getByText('Over the ₹10,000 limit: once submitted, someone else with purchase approval must approve it.')).toBeVisible();
  await p.getByTestId('submit').click();
  await said(p, `${orderNumber} sent for approval.`);
  await expect(status(p)).toHaveText('Waiting for approval');
  await expect(p.getByTestId('approve')).toHaveCount(0);   // the clerk has no purchase approval
  await noSideScroll(p);
  await shot(p, '04-order-waiting-phone');
});

test('S16-03 The owner approves it', async () => {
  await m.goto('/inventory/purchases?view=orders');
  await m.getByRole('radio', { name: 'Waiting for approval' }).click();
  await expect(m.locator(`li[data-order="${orderNumber}"]`)).toContainText('₹11,000');
  await m.locator(`li[data-order="${orderNumber}"] a`).click();
  await expect(m.getByTestId('waiting')).toContainText('₹11,000 is over the ₹10,000 limit');
  await shot(m, '05-approve');
  await m.getByTestId('approve').click();
  await said(m, `${orderNumber} approved.`);
  await expect(status(m)).toHaveText('Approved');
  await expect(m.getByText(/^Approved .* by /)).toBeVisible();
});

test('S16-04 A part delivery: more than is still to come is refused under the line; the rest stays to come', async () => {
  await p.goto(orderUrl);
  await p.getByTestId('receive').click();
  const d = p.getByRole('dialog');
  await d.locator('[data-line="Onion and capsicum"]').getByRole('button', { name: 'All' }).click();
  await d.getByRole('spinbutton', { name: 'Mixed vegetables received' }).fill('200');
  await d.getByRole('spinbutton', { name: 'Lime received' }).fill('100');
  await d.getByLabel('Delivery challan no.').fill('MFV-C-0412');
  await d.getByRole('button', { name: 'Save delivery' }).click();
  await expect(d.locator('[data-line="Mixed vegetables"] mat-error'))
    .toHaveText(`Only 160 kg of Mixed vegetables is still to come on ${orderNumber} (0 kg of 160 kg already in). Record the extra on a new order, or as stock received in Inventory with the reason.`);
  await d.getByRole('spinbutton', { name: 'Mixed vegetables received' }).fill('100');
  await shot(p, '06-receive-phone');
  await d.getByRole('button', { name: 'Save delivery' }).click();
  await said(p, /GRN\d{5} saved: 15 kg Onion and capsicum, 100 kg Mixed vegetables, 100 pcs Lime into Main store\./);

  await expect(status(p)).toHaveText('Partly received');
  await expect(line(p, 'Mixed vegetables').locator('[data-l="Received"]')).toHaveText('100 kg');
  await expect(line(p, 'Mixed vegetables').locator('[data-l="To come"]')).toHaveText('60 kg');
  await expect(line(p, 'Lime').locator('[data-l="To come"]')).toHaveText('0 pcs');
  await noSideScroll(p);
  await shot(p, '07-partly-received-phone');

  const at = await inStore();
  expect(at('ONION-CAPSICUM')).toBeCloseTo(before['ONION-CAPSICUM'] + 15, 3);
  expect(at('MIXED-VEG')).toBeCloseTo(before['MIXED-VEG'] + 100, 3);
  expect(at('LIME')).toBeCloseTo(before['LIME'] + 100, 3);
});

test('S16-05 The rest comes the next morning: everything is in and stock is up by the whole order', async () => {
  await p.getByTestId('receive').click();
  const d = p.getByRole('dialog');
  await expect(d.locator('[data-line="Mixed vegetables"]')).toContainText('Still to come 60 kg of 160 kg');
  await expect(d.locator('[data-line="Lime"]')).toHaveCount(0);
  await d.getByRole('button', { name: 'All' }).click();
  await d.getByRole('button', { name: 'Save delivery' }).click();
  await said(p, /GRN\d{5} saved: 60 kg Mixed vegetables into Main store\. Everything has come in\./);
  await expect(status(p)).toHaveText('Received');
  await expect(p.locator('li[data-grn]')).toHaveCount(2);
  const at = await inStore();
  expect(at('MIXED-VEG')).toBeCloseTo(before['MIXED-VEG'] + 160, 3);

  // The stock ledger names the goods received note and the order.
  await m.goto('/inventory?view=ledger');
  await expect(m.getByText(new RegExp(`${orderNumber} · Mapusa Fresh Vegetables`)).first()).toBeVisible();
});

test('S16-06 The supplier\'s bill is recorded against what came in', async () => {
  await m.goto(orderUrl);
  await m.getByTestId('bill').click();
  const d = m.getByRole('dialog');
  await expect(d.getByRole('heading', { name: 'Mapusa Fresh Vegetables\'s bill' })).toBeVisible();
  await d.getByLabel('Their bill number').fill(`MFV/${runId}`);
  await expect(d.getByTestId('bill-total')).toContainText('Total ₹11,000.00');
  await shot(m, '08-bill');
  await d.getByRole('button', { name: 'Record bill' }).click();
  await said(m, /SI\d{5} recorded: ₹11,000, due /);
  await expect(m.locator('li[data-bill]')).toContainText(`bill MFV/${runId}`);
  await expect(m.locator('li[data-bill]')).toContainText('Unpaid');
});

test('S16-07 It is paid by UPI: the UTR is needed, said under its field; then the bill is paid and nothing is owed', async () => {
  await p.goto('/inventory/purchases?view=bills');
  const bill = p.locator('li[data-bill]', { hasText: `MFV/${runId}` });
  await expect(bill).toContainText('₹11,000');
  const number = await bill.getAttribute('data-bill');
  await bill.getByRole('button', { name: `Pay ${number}` }).click();
  const d = p.getByRole('dialog');
  await pick(p, 'Paid by', 'UPI');
  await d.getByRole('button', { name: 'Record payment' }).click();
  await expect(d.locator('mat-error')).toHaveText(
    'Enter the UTR (bank transfer or UPI) or the cheque number, 3–60 characters, so the payment can be matched to the bank statement.');
  await d.getByLabel('UTR / transaction reference').fill('UPI-430211987654');
  await shot(p, '09-pay-phone');
  await d.getByRole('button', { name: 'Record payment' }).click();
  await said(p, `${number} is paid in full.`);
  await expect(bill).toContainText('Paid');
  await expect(bill.getByRole('button', { name: `Pay ${number}` })).toHaveCount(0);
  await noSideScroll(p);
  await shot(p, '10-bills-phone');

  await m.goto('/inventory/suppliers');
  await expect(m.locator('li[data-supplier="FRESH-VEG"]')).toContainText('Nothing owed');
  await m.goto('/inventory/purchases?view=requests');
  await expect(m.locator('li[data-request="PR00001"]')).toContainText(`On ${orderNumber}`);
});

test('S16-08 The clerk can\'t approve an order, even through the API', async () => {
  const { api: clerkApi } = await apiAs(clerk.email, pw);
  const order = await (await clerkApi.get(`/api/v1/properties/${pid}${orderUrl.replace('/inventory/purchases', '/purchasing/orders')}`)).json();
  const res = await clerkApi.post(`/api/v1/properties/${pid}/purchasing/orders/${order.id}/approve`, { data: { note: null, version: order.version } });
  expect(res.status()).toBe(403);
});
