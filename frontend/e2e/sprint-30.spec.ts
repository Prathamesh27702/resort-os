import { APIRequestContext, Browser, BrowserContext, Page, expect, test } from '@playwright/test';
import { addDays, apiAs, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 30 — the same checks as docs/sprints/REVIEWING.md → "Sprint 30", automated: what an Indian property is expected
// to print and file. The owner enters the FSSAI licence number and the UPI ID (a wrong one is explained under its field).
// A restaurant bill prints with the FSSAI number and a UPI QR for its total, on 80 mm or 58 mm paper; the Paid dialog
// shows the same QR; a kitchen ticket prints. A foreign guest who checks in gets a Form C sheet with 24 hours to file:
// reception sees it on the front desk, fills in what is missing (each gap named under its field), marks it as filed with
// the FRRO number, and downloads the foreign guest register. On a phone the Form C list fits.

test.describe.configure({ mode: 'serial' });

const PHONE = { viewport: { width: 360, height: 780 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 };
let api: APIRequestContext;
let pid: string;
let today: string;
let rec: Page;
let recCtx: BrowserContext;
let own: Page;
let ownCtx: BrowserContext;
const pw = `Staff-${runId}-2026`;
const reception = { email: `r30.${runId}@demo.local`, name: `Rhea Reception ${runId}` };
const guest = { first: `Lena ${runId}`, last: 'Fischer', full: `Lena ${runId} Fischer` };
const stay = { id: '', ref: '' };
const order = { id: '', number: '', kot: '' };
let formId = '';

interface FreeType { inventoryMode: string; accommodationTypeId: string; freeRooms: { id: string; name: string }[] }
interface SheetField { key: string; required: boolean; kind: string; value: string | null }

const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text }).last()).toBeVisible();
const noSideScroll = async (page: Page) => expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);
const dialog = async (page: Page) => {
  const d = page.locator('mat-dialog-container');
  await expect(d).toHaveClass(/mdc-dialog--open/);
  await expect(d).not.toHaveClass(/mdc-dialog--opening/);
  return d;
};

/** The input of a Form C field by its exact label (the copy button next to it carries the label too). */
const field = (label: string) => rec.locator('mat-form-field', { has: rec.locator('mat-label', { hasText: new RegExp(`^${label.replace(/[()]/g, '\\$&')}$`) }) }).locator('input');

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  const prop = (await (await api.get('/api/v1/properties')).json())[0];
  pid = prop.id;
  today = prop.localDate as string;
  await createStaff(api, reception.email, reception.name, 'RECEPTIONIST', pid, pw);

  // A guest from Germany, booked for tonight. She is checked in in S30-04.
  const g = await api.post('/api/v1/guests', { data: {
    firstName: guest.first, lastName: guest.last, phone: `+4930${String(Date.now()).slice(-8)}`, email: `lena.${runId}@example.com`,
    dateOfBirth: '1990-04-12', nationality: 'DE', isVip: false, ignoreDuplicates: true,
    address: { line1: 'Torstrasse 12', line2: null, city: 'Berlin', state: null, stateCode: null, postalCode: '10119', countryCode: 'DE' },
  } });
  expect(g.status(), await g.text()).toBe(201);
  const avail = await (await api.get(`/api/v1/properties/${pid}/availability?from=${today}&to=${addDays(today, 2)}&adults=1&children=0`)).json();
  const type = (avail.types as FreeType[]).find(t => t.inventoryMode === 'UNIT' && t.freeRooms.length >= 1)!;
  const source = (await (await api.get(`/api/v1/properties/${pid}/reservation-sources`)).json()).find((s: { code: string }) => s.code === 'PHONE').id;
  const res = await api.post(`/api/v1/properties/${pid}/reservations`, {
    headers: { 'Idempotency-Key': `e2e-s30-${runId}` },
    data: { checkIn: today, checkOut: addDays(today, 2), rooms: [{ accommodationTypeId: type.accommodationTypeId, unitId: type.freeRooms[0].id, adults: 1, children: 0 }],
            guestIds: [(await g.json()).id], sourceId: source, ratePlanId: null, status: 'CONFIRMED', specialRequests: null, note: null },
  });
  expect(res.status(), await res.text()).toBe(201);
  ({ id: stay.id, reference: stay.ref } = await res.json());

  // A takeaway order with one dish, sent to the kitchen: the bill and the ticket to print.
  const menu = await (await api.get(`/api/v1/properties/${pid}/menu`)).json();
  const dish = menu.categories.flatMap((c: { items: { variants: unknown[]; modifierGroups: unknown[]; isAvailable?: boolean }[] }) => c.items)
    .find((i: { variants: unknown[]; modifierGroups: unknown[] }) => !i.variants.length && !i.modifierGroups.length);
  const made = await api.post(`/api/v1/properties/${pid}/orders`, {
    headers: { 'Idempotency-Key': `e2e-s30-order-${runId}` },
    data: { orderType: 'TAKEAWAY', tableId: null, unitId: null, covers: null, note: null, items: [{ menuItemId: dish.id, variantId: null, modifierIds: [], quantity: 2, note: null }] },
  });
  expect(made.status(), await made.text()).toBe(201);
  let o = await made.json();
  o = await (await api.post(`/api/v1/properties/${pid}/orders/${o.id}/send`, { data: { note: null, version: o.version } })).json();
  order.id = o.id; order.number = o.number; order.kot = o.kots[0].number;

  recCtx = await browser.newContext();
  rec = await recCtx.newPage();
  await signIn(rec, reception.email, pw);
  ownCtx = await browser.newContext();
  own = await ownCtx.newPage();
  await signIn(own, owner.email, owner.password);
});

test.afterAll(async () => {
  await recCtx?.close();
  await ownCtx?.close();
});

test('S30-01 The owner enters the FSSAI licence number and the UPI ID; a wrong one is explained under its field', async () => {
  await own.goto(`/settings/properties/${pid}`);
  const fssai = own.getByLabel('FSSAI licence number');
  const upi = own.getByLabel('UPI ID for payments');
  await expect(fssai).toHaveValue(/^\d{14}$/);          // the demo data's made-up number
  await fssai.fill('1002009900012');                    // 13 digits
  await upi.fill('palmgrove');                          // no @bank
  await own.getByRole('button', { name: 'Save' }).click();
  await expect(own.locator('mat-form-field', { has: fssai }).locator('mat-error')).toContainText('14 digits');
  await expect(own.locator('mat-form-field', { has: upi }).locator('mat-error')).toContainText('name@bank');
  await shot(own, '01-settings-errors');

  await fssai.fill('10020099000456');
  await upi.fill(`palmgrove.${runId}@exampleupi`);
  await own.getByLabel('Name shown to the payer').fill('Palm Grove Beach Resort');
  await own.getByRole('button', { name: 'Save' }).click();
  await said(own, /saved/i);
  await own.goto(`/settings/properties/${pid}`);
  // Saved: the QR guests will scan is shown, so the owner can try it with their own phone before printing it on bills.
  const qr = own.getByTestId('upi-qr');
  await expect(qr).toContainText(`palmgrove.${runId}@exampleupi`);
  await expect(qr.locator('img')).toHaveAttribute('src', /^data:image\/png;base64,/);
  await qr.scrollIntoViewIfNeeded();
  await shot(own, '02-settings-upi-qr');
});

test('S30-02 A restaurant bill prints with the FSSAI number and a UPI QR for its total, on 80 mm or 58 mm paper', async () => {
  await own.goto(`/restaurant/orders/${order.id}`);
  const total = (await own.getByTestId('total').innerText()).replace(/[^\d.]/g, '');
  await own.getByTestId('print-bill').click();
  const d = await dialog(own);
  const bill = d.getByTestId('receipt');
  await expect(bill).toContainText('FSSAI Lic. No. 10020099000456');
  await expect(bill).toContainText(`Bill no. ${order.number}`);
  await expect(bill).toContainText('TOTAL');
  await expect(bill).toContainText(/Scan to pay ₹[\d,.]+ by UPI/);
  await expect(bill).toContainText(`palmgrove.${runId}@exampleupi`);
  await expect(bill.locator('.qr img')).toHaveAttribute('src', /^data:image\/png;base64,/);
  await expect(bill).toHaveClass(/w80/);
  await shot(own, '03-bill-80mm');
  await d.getByRole('radio', { name: '58 mm' }).click();
  await expect(bill).toHaveClass(/w58/);
  await shot(own, '04-bill-58mm');
  await expect(d.getByTestId('print-receipt')).toBeEnabled();
  await d.getByRole('button', { name: 'Close' }).click();

  // The link behind the QR carries the UPI ID, the name and the exact total.
  const q = await (await api.get(`/api/v1/properties/${pid}/upi-qr?amount=${total}&note=${order.number}`)).json();
  expect(q.link).toContain(`pa=palmgrove.${runId}%40exampleupi`);
  expect(q.link).toContain(`am=${Number(total).toFixed(2)}`);
  expect(q.link).toContain('cu=INR');
});

test('S30-03 The kitchen ticket prints, and Paid by UPI shows the QR for the amount', async () => {
  await own.getByRole('button', { name: `Print ticket ${order.kot}` }).click();
  let d = await dialog(own);
  await expect(d.getByTestId('receipt')).toContainText(order.kot);
  await expect(d.getByTestId('receipt')).toContainText('2 ×');
  await shot(own, '05-kitchen-ticket');
  await d.getByRole('button', { name: 'Close' }).click();
  await expect(own.locator('mat-dialog-container')).toHaveCount(0);

  // The kitchen cooks it and it is served (Sprint 14); then the bill can be settled.
  const o = await (await api.get(`/api/v1/properties/${pid}/orders/${order.id}`)).json();
  expect((await api.post(`/api/v1/properties/${pid}/kitchen/kots/${o.kots[0].id}/ready`, { data: { version: null } })).status()).toBe(200);
  expect((await api.post(`/api/v1/properties/${pid}/orders/${order.id}/kots/${o.kots[0].id}/serve`)).status()).toBe(200);
  await own.reload();
  await own.getByTestId('pay').click();
  d = await dialog(own);
  await d.getByRole('radio', { name: 'UPI' }).click();
  await expect(d.getByTestId('upi-qr')).toContainText('Scan to pay ₹');
  await shot(own, '06-paid-by-upi');
  await d.getByRole('button', { name: 'Cancel' }).click();
});

test('S30-04 A foreign guest who checks in gets a Form C with 24 hours to file, shown on the front desk', async () => {
  const inn = await api.post(`/api/v1/properties/${pid}/reservations/${stay.id}/check-in`,
    { data: { version: null, overrideHousekeeping: true, overrideReason: 'Browser test: room checked by hand' } });
  expect(inn.status(), await inn.text()).toBe(200);

  await rec.goto('/front-desk/in-house');
  const card = rec.getByTestId('form-c-card');
  await expect(card).toContainText(/\d+ Form C to file/);
  await shot(rec, '07-front-desk-form-c');
  await card.click();
  await expect(rec).toHaveURL(/\/form-c$/);
  const row = rec.locator(`tr[data-guest="${guest.full}"]`);
  await expect(row).toContainText('Germany');
  await expect(row).toContainText(stay.ref);
  await expect(row).toContainText(/2[34] h left|23 h left/);
  await expect(row).toContainText(/\d+ fields? to fill/);
  await shot(rec, '08-form-c-list');
  await row.getByRole('link', { name: `Open the Form C of ${guest.full}` }).click();
  await expect(rec).toHaveURL(/\/form-c\/[0-9a-f-]{36}$/);
  formId = rec.url().split('/').pop()!;
});

test('S30-05 The sheet is filled in from the profile; each gap is named under its field; then it is marked as filed', async () => {
  await expect(rec.getByRole('heading', { name: `Form C: ${guest.full}` })).toBeVisible();
  await expect(rec.getByTestId('form-c-deadline')).toContainText('left');
  // What the profile and the booking already know is there.
  await expect(field('Nationality')).toHaveValue('Germany');
  await expect(field('Given name')).toHaveValue(guest.first);
  await expect(field('Surname (optional)')).toHaveValue('Fischer');
  await expect(field('Address in the country of residence')).toHaveValue(/Berlin/);
  await expect(field('Intended stay here (days)')).toHaveValue('2');

  // Filing with gaps is refused: the gaps are listed and each empty field says so.
  await rec.getByTestId('form-c-file').click();
  await expect(rec.getByTestId('form-c-error')).toContainText('still empty: ');
  await expect(rec.getByTestId('form-c-error')).toContainText('Passport: Passport number');
  const passport = field('Passport number');
  await expect(passport.locator('xpath=ancestor::mat-form-field').locator('mat-error')).toHaveText('The FRRO portal needs this. Fill it in before filing.');
  await shot(rec, '09-form-c-gaps');

  // A value that can't be right is refused under its field, in words.
  const days = field('Intended stay here (days)');
  await days.fill('0');
  await passport.fill('c4fz2x9p1');
  await rec.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(days.locator('xpath=ancestor::mat-form-field').locator('mat-error')).toHaveText('Intended stay here (days) must be a whole number of days from 1 to 3650.');
  await days.fill('2');
  await rec.getByRole('button', { name: 'Save', exact: true }).click();
  await said(rec, 'Form C saved.');
  await expect(passport).toHaveValue('C4FZ2X9P1');

  // The rest of the sheet is typed the same way; here the test fills it through the API to stay short.
  const sheet = await (await api.get(`/api/v1/properties/${pid}/form-c/${formId}`)).json();
  const sample: Record<string, string> = { date: '2026-01-15', time: '14:30', number: '2', sex: 'Female', yesno: 'No', text: 'Berlin' };
  const values: Record<string, string> = {};
  for (const f of sheet.fields as SheetField[]) if (f.required && !f.value) values[f.key] = sample[f.kind];
  values['passportExpiry'] = '2031-01-14'; values['visaExpiry'] = '2027-01-14'; values['visaType'] = 'e-Tourist'; values['visaNumber'] = 'VJ1234567';
  const saved = await api.put(`/api/v1/properties/${pid}/form-c/${formId}`, { data: { values, version: sheet.version } });
  expect(saved.status(), await saved.text()).toBe(200);
  expect((await saved.json()).missing).toEqual([]);
  await rec.reload();
  await expect(field('Type of visa')).toHaveValue('e-Tourist');

  // Copy puts a date on the clipboard the way the portal wants it.
  await recCtx.grantPermissions(['clipboard-read', 'clipboard-write']);
  await rec.getByRole('button', { name: 'Copy Valid until' }).first().click();
  await said(rec, 'Valid until copied: 14/01/2031');
  await shot(rec, '10-form-c-sheet');

  await rec.getByTestId('form-c-file').click();
  const d = await dialog(rec);
  const ref = d.getByRole('textbox', { name: 'FRRO acknowledgement number' });
  await ref.fill('AB');
  await d.getByRole('button', { name: 'Mark as filed' }).click();
  await expect(d.locator('mat-error')).toContainText('3 to 40 characters');
  await ref.fill(`FRRO-${runId}`);
  await shot(rec, '11-form-c-mark-filed');
  await d.getByRole('button', { name: 'Mark as filed' }).click();
  await said(rec, 'Form C marked as filed.');
  await expect(rec.getByTestId('form-c-filed')).toContainText(`FRRO number FRRO-${runId}`);
  await expect(rec.getByTestId('form-c-filed')).toContainText(`by ${reception.name}`);
  await expect(field('Passport number')).toBeDisabled();
  await expect(rec.getByRole('button', { name: 'Reopen to correct' })).toBeVisible();
  await shot(rec, '12-form-c-filed');
});

test('S30-06 The filed form is on the Filed list with its FRRO number, and the register downloads', async () => {
  await rec.goto('/form-c');
  await expect(rec.locator(`tr[data-guest="${guest.full}"]`)).toHaveCount(0);      // nothing left to file for her
  await rec.getByRole('button', { name: 'Filed', exact: true }).click();
  const row = rec.locator(`tr[data-guest="${guest.full}"]`);
  await expect(row).toContainText(`FRRO-${runId}`);

  const [file] = await Promise.all([rec.waitForEvent('download'), rec.getByTestId('form-c-register').click()]);
  expect(file.suggestedFilename()).toMatch(/^form-c-register-.*\.csv$/);
  const text = (await (await file.createReadStream()).toArray()).join('');
  expect(text).toContain('Guest,Nationality,Passport (last 4)');
  expect(text).toContain(`${guest.full},Germany,•••• X9P1`);
  expect(text).toContain(`FRRO-${runId}`);
  expect(text).not.toContain('C4FZ2X9P1');                                           // the register never has a full passport number
  await shot(rec, '13-form-c-filed-list');
});

test('S30-07 On a phone the Form C list and sheet fit', async ({ browser }: { browser: Browser }) => {
  const ctx = await browser.newContext(PHONE);
  const p = await ctx.newPage();
  await signIn(p, reception.email, pw);
  await p.goto('/form-c');
  await p.getByRole('button', { name: 'All', exact: true }).click();
  await expect(p.locator(`tr[data-guest="${guest.full}"]`)).toBeVisible();
  await noSideScroll(p);
  await shot(p, '14-phone-form-c-list');
  await p.goto(`/form-c/${formId}`);
  await expect(p.getByTestId('form-c-filed')).toBeVisible();
  await noSideScroll(p);
  await shot(p, '15-phone-form-c-sheet');
  await ctx.close();
});
