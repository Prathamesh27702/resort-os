import { APIRequestContext, Browser, Page, expect, test } from '@playwright/test';
import { addDays, apiAs, choose, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 09 — the same checks as docs/sprints/REVIEWING.md → "Sprint 09", automated.
// The demo data (database/seed/dev/S008) registers the demo property for GST in Goa (30AABCT1234C1Z9); migration V011 seeds
// the accommodation and restaurant rules for every organisation. This file adds an accountant (organisation-wide), who
// adds next month's restaurant rule and verifies a seeded one, and books its own guest to see GST on a bill.

test.describe.configure({ mode: 'serial' });

let a: Page;          // the accountant
let api: APIRequestContext;
let pid = '';
let today = '';
const staffPassword = `Staff-${runId}-2026`;
const accountant = { email: `accounts9.${runId}@demo.local`, name: `Anil Accounts ${runId}` };
const reception = { email: `reception9.${runId}@demo.local`, name: `Rina Reception ${runId}` };

const money = (v: number) => new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', minimumFractionDigits: 2 }).format(v);
const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text })).toBeVisible();
const firstOfNextMonth = (iso: string) => { const [y, m] = iso.split('-').map(Number); return new Date(Date.UTC(y, m, 1)).toISOString().slice(0, 10); };
const longDay = (iso: string) => new Intl.DateTimeFormat('en-IN', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' })
  .format(new Date(iso + 'T00:00:00Z'));

async function calculate(page: Page, what: string, day: string, amount: string) {
  await choose(page, 'form.calc mat-select[formcontrolname=chargeCategory]', what);
  await page.getByLabel('Day of the service').fill(day);
  await page.getByLabel('Amount (₹, before GST)').fill(amount);
  await page.getByRole('button', { name: 'Calculate' }).click();
  return page.getByTestId('tax-result');
}

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  const prop = (await (await api.get('/api/v1/properties')).json())[0];
  pid = prop.id;
  today = prop.localDate as string;
  await createStaff(api, accountant.email, accountant.name, 'ACCOUNTANT', null, staffPassword);
  await createStaff(api, reception.email, reception.name, 'RECEPTIONIST', pid, staffPassword);
  a = await (await browser.newContext()).newPage();
  await signIn(a, accountant.email, staffPassword);
});

test('S09-01 Settings → GST: the registration and the seeded rules, with their source, waiting to be verified', async () => {
  await a.goto('/dashboard');
  await a.locator('nav a[href="/settings/gst"]').click();
  await expect(a).toHaveURL(/\/settings\/gst/);
  await expect(a.locator('tr[data-gstin="30AABCT1234C1Z9"]')).toContainText('Current');
  await expect(a.locator('tr[data-gstin="30AABCT1234C1Z9"]')).toContainText('From 1 Jul 2017');

  const rooms = a.locator('article[data-category="ACCOMMODATION"]');
  await expect(rooms).toContainText('SAC/HSN 996311');
  await expect(rooms.locator('tr[data-rule="2025-09-22"]')).toContainText('Up to ₹7,500: 5% · Above ₹7,500: 18% with ITC');
  await expect(rooms.locator('tr[data-rule="2025-09-22"]')).toContainText('Now');
  await expect(rooms.locator('tr[data-rule="2025-09-22"]')).toContainText('Notification 05/2025-Central Tax (Rate)');
  await expect(rooms.locator('tr[data-rule="2022-07-18"]')).toContainText('Up to ₹7,500: 12% with ITC · Above ₹7,500: 18% with ITC');
  await expect(a.locator('article[data-category="RESTAURANT"] tr[data-rule="2017-11-15"]')).toContainText('5%');
  await expect(a.getByTestId('unverified')).toContainText('2 rules are not verified yet');
  await shot(a, '01-gst');
});

test('S09-02 The demo: the same ₹3,500 room night is 12% GST on 21 Sep 2025 and 5% from 22 Sep 2025', async () => {
  let result = await calculate(a, 'Room', '2025-09-21', '3500');
  await expect(result).toContainText('12%');
  await expect(result.locator('tr[data-tax="CGST"]')).toContainText(money(210));
  await expect(result.locator('tr[data-tax="SGST"]')).toContainText(money(210));
  await expect(result).toContainText(money(3920));
  await shot(a, '02-before-change');

  result = await calculate(a, 'Room', '2025-09-22', '3500');
  await expect(result).toContainText('5%');
  await expect(result.locator('tr[data-tax="CGST"]')).toContainText(money(87.5));
  await expect(result).toContainText(money(3675));
  await expect(result).toContainText('Same state: CGST + SGST');
  await shot(a, '03-after-change');

  // another place of supply is IGST; transport has no tax category, and the reason is shown
  await choose(a, 'form.calc mat-select[formcontrolname=placeOfSupply]', 'Maharashtra (27)');
  result = await calculate(a, 'Food', today, '1000');
  await expect(result.locator('tr[data-tax="IGST"]')).toContainText(money(50));
  await choose(a, 'form.calc mat-select[formcontrolname=placeOfSupply]', 'This property\'s state');
  result = await calculate(a, 'Transport', today, '1200');
  await expect(result).toContainText('Transport charges carry no GST: no tax category covers them.');
});

test('S09-03 The accountant adds next month’s restaurant rule; a missing source is named under its field', async () => {
  const next = firstOfNextMonth(today);
  await a.getByRole('button', { name: 'Add rule' }).click();
  const dlg = a.locator('mat-dialog-container');
  await choose(a, 'mat-dialog-container mat-select[formcontrolname=taxCategoryId]', /Restaurant service/);
  await dlg.getByLabel('Applies from').fill(next);
  await dlg.getByLabel('GST rate (%)').fill('18');
  await dlg.getByRole('checkbox', { name: 'With input tax credit' }).check();
  await dlg.getByRole('button', { name: 'Add rule' }).click();
  await expect(dlg.locator('mat-form-field', { hasText: 'Source' }).locator('mat-error')).toContainText('Say where the rule comes from');

  // a rate that can't be split evenly into CGST and SGST: the API's reason shows under the rate
  await dlg.getByLabel('Source').fill(`Accountant's note ${runId}: the restaurant is now in specified premises`);
  await dlg.getByLabel('GST rate (%)').fill('0.25');
  await dlg.getByRole('button', { name: 'Add rule' }).click();
  await expect(dlg.locator('mat-form-field', { hasText: 'GST rate' }).locator('mat-error')).toContainText('must be a multiple of 0.02%');
  await dlg.getByLabel('GST rate (%)').fill('18');
  await shot(a, '04-new-rule');
  await dlg.getByRole('button', { name: 'Add rule' }).click();
  await said(a, `The rule before it now ends on ${longDay(addDays(next, -1))}.`);

  const food = a.locator('article[data-category="RESTAURANT"]');
  await expect(food.locator(`tr[data-rule="${next}"]`)).toContainText('Upcoming');
  await expect(food.locator(`tr[data-rule="${next}"]`)).toContainText('18% with ITC');
  await expect(food.locator('tr[data-rule="2017-11-15"]')).toContainText(`– ${longDay(addDays(next, -1))}`);
  await shot(a, '05-rules');

  // dinner today is still 5%; dinner on the 1st of next month is 18%
  let result = await calculate(a, 'Food', today, '1000');
  await expect(result).toContainText(money(1050));
  result = await calculate(a, 'Food', next, '1000');
  await expect(result).toContainText(money(1180));
});

test('S09-04 The accountant verifies a seeded rule against its notification', async () => {
  const rooms = a.locator('article[data-category="ACCOMMODATION"]');
  await rooms.locator('tr[data-rule="2025-09-22"]').getByRole('button', { name: /^Verify / }).click();
  await said(a, 'Verified: Accommodation');
  await expect(rooms.locator('tr[data-rule="2025-09-22"]')).toContainText(`✓ ${accountant.name}`);
});

test('S09-05 A dinner on a guest’s bill carries GST, shown under its amount', async ({ browser }: { browser: Browser }) => {
  const avail = await (await api.get(`/api/v1/properties/${pid}/availability?from=${today}&to=${addDays(today, 1)}&adults=1&children=0`)).json();
  const type = (avail.types as { inventoryMode: string; accommodationTypeId: string; freeRooms: { id: string }[] }[])
    .find(t => t.inventoryMode === 'UNIT' && t.freeRooms.length >= 1)!;
  const source = (await (await api.get(`/api/v1/properties/${pid}/reservation-sources`)).json()).find((s: { code: string }) => s.code === 'PHONE').id;
  const guest = (await (await api.get('/api/v1/guests?pageSize=8')).json()).items[7];
  const res = await api.post(`/api/v1/properties/${pid}/reservations`, {
    headers: { 'Idempotency-Key': `e2e-s09-own-${runId}` },
    data: { checkIn: today, checkOut: addDays(today, 1), rooms: [{ accommodationTypeId: type.accommodationTypeId, unitId: type.freeRooms[0].id, adults: 1, children: 0 }],
            guestIds: [guest.id], sourceId: source, ratePlanId: null, status: 'CONFIRMED', specialRequests: null, note: null },
  });
  expect(res.status(), await res.text()).toBe(201);
  const booked = await res.json();
  const inn = await api.post(`/api/v1/properties/${pid}/reservations/${booked.id}/check-in`,
    { data: { version: null, overrideHousekeeping: true, overrideReason: 'Browser test: room checked by hand' } });
  expect(inn.status(), await inn.text()).toBe(200);
  const folio = (await (await api.get(`/api/v1/properties/${pid}/reservations/${booked.id}/folios`)).json()).find((f: { isPrimary: boolean }) => f.isPrimary);
  const charge = await api.post(`/api/v1/properties/${pid}/folios/${folio.id}/charges`, {
    headers: { 'Idempotency-Key': `e2e-s09-dinner-${runId}` }, data: { category: 'FOOD', description: 'Seafood thali', quantity: 1, unitPrice: 1450 },
  });
  expect(charge.status(), await charge.text()).toBe(201);

  const o = await (await browser.newContext()).newPage();
  await signIn(o, owner.email, owner.password);
  await o.goto(`/folios/${folio.id}`);
  const dinner = o.locator('tr[data-line="Seafood thali"]');
  await expect(dinner).toContainText(money(1522.5));
  await expect(dinner).toContainText(`${money(1450)} + GST 5% ${money(72.5)}`);
  await expect(o.locator('tr[data-line^="Room night"]').first()).toContainText(/GST (5|18)%/);
  await shot(o, '06-bill-with-gst');
});

test('S09-06 A wrong GSTIN says what is wrong; staff without tax permissions see no GST page', async ({ browser }: { browser: Browser }) => {
  await a.goto('/settings/gst');
  await a.getByRole('button', { name: 'Add registration' }).click();
  const dlg = a.locator('mat-dialog-container');
  await dlg.getByLabel('GSTIN').fill('30AABCT1234C1Z8');
  await dlg.getByLabel('Legal name').fill('Palm Grove Hospitality Pvt Ltd');
  await dlg.getByLabel('Valid from').fill('2026-04-01');
  await dlg.getByRole('button', { name: 'Save registration' }).click();
  await expect(dlg.locator('mat-form-field', { hasText: 'GSTIN' }).locator('mat-error'))
    .toContainText('The last character (check digit) should be 9 for this GSTIN');
  await shot(a, '07-gstin-error');
  await dlg.getByRole('button', { name: 'Cancel' }).click();

  const r = await (await browser.newContext()).newPage();
  await signIn(r, reception.email, staffPassword);
  await expect(r.locator('nav a[href="/settings/gst"]')).toHaveCount(0);
  const recApi = (await apiAs(reception.email, staffPassword)).api;
  expect((await recApi.get('/api/v1/tax/categories')).status()).toBe(403);
});

test('S09-07 On a phone the GST page stays readable', async () => {
  await a.setViewportSize({ width: 390, height: 844 });
  await a.goto('/settings/gst');
  await expect(a.locator('article[data-category="ACCOMMODATION"]')).toBeVisible();
  await shot(a, '08-phone');
  await a.setViewportSize({ width: 1280, height: 860 });
});
