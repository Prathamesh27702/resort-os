import { APIRequestContext, Browser, Page, expect, test } from '@playwright/test';
import { addDays, apiAs, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 12 — the same checks as docs/sprints/REVIEWING.md → "Sprint 12", automated.
// The demo: a guest who paid by UPI checks out and the GST invoice and the receipt are issued by themselves; a minibar
// found later goes on a debit note; a dinner charged in full is partly credited; then the company asks for the invoice in
// its name, so the accountant cancels it with a credit note and issues a B2B invoice with the company's GSTIN (a typing
// mistake in the GSTIN is caught under the field). Nothing issued is ever edited.
// The demo property is registered for GST in Goa (database/seed/dev/S008), so a company from Maharashtra still pays
// CGST and SGST: a hotel stay's place of supply is where the hotel is.

test.describe.configure({ mode: 'serial' });

let r: Page;          // the receptionist
let m: Page;          // the accountant (the owner here)
let api: APIRequestContext;
let pid = '';
let today = '';
let bookingId = '';
let folioId = '';
let paymentId = '';
let invoiceId = '';
let invoiceNumber = '';
let paid = 0;
const staffPassword = `Staff-${runId}-2026`;
const reception = { email: `reception12.${runId}@demo.local`, name: `Rohan Reception ${runId}` };
const NUMBER = /^[A-Z0-9]{1,5}\/\d{4}\/\d{5}$/;

const money = (v: number) => new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', minimumFractionDigits: 2 }).format(v);
const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text })).toBeVisible();
const charge = async (description: string, category: string, unitPrice: number) => {
  const res = await api.post(`/api/v1/properties/${pid}/folios/${folioId}/charges`, {
    headers: { 'Idempotency-Key': `e2e-s12-${description}-${runId}` },
    data: { category, description, quantity: 1, unitPrice, serviceDate: null, packageId: null },
  });
  expect(res.status(), await res.text()).toBe(201);
  return res.json();
};

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  const prop = (await (await api.get('/api/v1/properties')).json())[0];
  pid = prop.id;
  today = prop.localDate as string;
  await createStaff(api, reception.email, reception.name, 'RECEPTIONIST', pid, staffPassword);

  // one night from today, checked in, with a dinner on the bill, paid in full by UPI
  const avail = await (await api.get(`/api/v1/properties/${pid}/availability?from=${today}&to=${addDays(today, 1)}&adults=2&children=0`)).json();
  const type = (avail.types as { inventoryMode: string; accommodationTypeId: string; freeRooms: { id: string }[] }[])
    .find(t => t.inventoryMode === 'UNIT' && t.freeRooms.length >= 1)!;
  const source = (await (await api.get(`/api/v1/properties/${pid}/reservation-sources`)).json()).find((s: { code: string }) => s.code === 'PHONE').id;
  const guest = (await (await api.get('/api/v1/guests?pageSize=8')).json()).items[7];
  const res = await api.post(`/api/v1/properties/${pid}/reservations`, {
    headers: { 'Idempotency-Key': `e2e-s12-stay-${runId}` },
    data: { checkIn: today, checkOut: addDays(today, 1), rooms: [{ accommodationTypeId: type.accommodationTypeId, unitId: type.freeRooms[0].id, adults: 2, children: 0 }],
            guestIds: [guest.id], sourceId: source, ratePlanId: null, status: 'CONFIRMED', specialRequests: null, note: null },
  });
  expect(res.status(), await res.text()).toBe(201);
  bookingId = (await res.json()).id;
  const inn = await api.post(`/api/v1/properties/${pid}/reservations/${bookingId}/check-in`,
    { data: { version: null, overrideHousekeeping: true, overrideReason: 'Browser test: room checked by hand' } });
  expect(inn.status(), await inn.text()).toBe(200);
  folioId = (await (await api.get(`/api/v1/properties/${pid}/reservations/${bookingId}/folios`)).json()).find((f: { isPrimary: boolean }) => f.isPrimary).id;
  const folio = await charge('Dinner for two at the beach shack', 'FOOD', 2000);
  paid = folio.balance;
  const pay = await api.post(`/api/v1/properties/${pid}/payments`, {
    headers: { 'Idempotency-Key': `e2e-s12-pay-${runId}` },
    data: { method: 'UPI', amount: paid, folioId, reservationId: null, paymentReference: `UPI${runId}`, note: null },
  });
  expect(pay.status(), await pay.text()).toBe(201);
  paymentId = (await pay.json()).id;

  r = await (await browser.newContext()).newPage();
  await signIn(r, reception.email, staffPassword);
  m = await (await browser.newContext()).newPage();
  await signIn(m, owner.email, owner.password);
});

test('S12-01 Check-out issues the GST invoice and the receipt by itself; the booking links to the invoice', async () => {
  await r.goto(`/reservations/${bookingId}`);
  await r.getByRole('button', { name: 'Check out' }).click();
  const dlg = r.locator('mat-dialog-container');
  await expect(dlg).toContainText('GST invoice is issued with a receipt for each payment');
  await dlg.getByRole('button', { name: 'Check out' }).click();
  await expect(r.locator('h1 .chip')).toHaveText('Checked out');
  const link = r.getByTestId('invoice');
  await expect(link).toHaveCount(1);
  invoiceNumber = (await link.textContent())!.replace('request_quote', '').replace('Invoice', '').trim();
  expect(invoiceNumber).toMatch(NUMBER);
  await shot(r, '01-checked-out');
  await link.click();
  await expect(r.locator('h1')).toContainText(invoiceNumber);
  invoiceId = r.url().split('/').pop()!;
  await expect(r.getByTestId('invoice-status')).toHaveText('Issued');
  await expect(r.getByTestId('customer-gstin')).toHaveText('Unregistered (B2C)');
  await expect(r.getByTestId('invoice-total')).toHaveText(money(paid));
  await expect(r.locator('[data-line]')).toHaveCount(2);
  await expect(r.locator('[data-line^="Dinner"]')).toContainText('CGST 2.5% · SGST 2.5%');
  await expect(r.locator('[data-receipt]')).toHaveCount(1);
  await expect(r.locator('[data-receipt]')).toContainText(money(paid));
  // the receptionist can read and print, but corrections are for accountants
  await expect(r.getByRole('button', { name: 'Credit note' })).toHaveCount(0);
  await shot(r, '02-invoice');
  const download = r.waitForEvent('download');
  await r.getByRole('button', { name: 'PDF' }).first().click();
  expect((await download).suggestedFilename()).toBe(invoiceNumber.replace(/\//g, '-') + '.pdf');
});

test('S12-02 On the bill each charge shows its invoice and can no longer be reversed; a late minibar goes on a debit note', async () => {
  await m.goto(`/folios/${folioId}`);
  await expect(m.getByTestId('invoice')).toContainText(invoiceNumber);
  const dinner = m.locator('[data-line^="Dinner"]');
  await expect(dinner).toContainText(invoiceNumber);
  await expect(dinner.getByRole('button', { name: /Reverse/ })).toHaveCount(0);
  await shot(m, '03-bill-invoiced');

  await charge('Minibar found after check-out', 'BEVERAGE', 400);
  await m.goto(`/invoices/${invoiceId}`);
  await expect(m.getByRole('note')).toContainText('1 charge was posted');
  await m.getByRole('button', { name: 'Debit note' }).click();
  const dlg = m.locator('mat-dialog-container');
  await dlg.getByRole('button', { name: 'Issue debit note' }).click();
  await expect(dlg.locator('mat-error')).toContainText('Say why the debit note is issued');
  await dlg.getByLabel('Reason').fill('Minibar found after check-out');
  await shot(m, '04-debit-note');
  await dlg.getByRole('button', { name: 'Issue debit note' }).click();
  await said(m, /Debit note .* issued/);
  await expect(m.locator('[data-note]')).toHaveCount(1);
  await expect(m.locator('[data-line^="Minibar"]')).toContainText('on debit note');
});

test('S12-03 Part of the dinner is credited: too much is refused under the amount, the rest comes off the bill', async () => {
  await m.getByRole('button', { name: 'Credit note' }).click();
  const dlg = m.locator('mat-dialog-container');
  const dinner = dlg.locator('[data-credit-line^="Dinner"]');
  await dinner.getByRole('checkbox').check();
  await dinner.getByLabel('Amount (₹)').fill('2500');
  await dlg.getByLabel('Reason').fill('One main course was not served');
  await dlg.getByRole('button', { name: 'Issue credit note' }).click();
  await expect(dinner.locator('mat-error')).toContainText(`Enter ₹0.01 to ${money(2000)}`);
  await dinner.getByLabel('Amount (₹)').fill('600');
  await expect(dlg.getByTestId('credit-total')).toHaveText(money(630));
  await shot(m, '05-credit-note');
  await dlg.getByRole('button', { name: 'Issue credit note' }).click();
  await said(m, `issued: ${money(630)}`);
  await expect(m.getByTestId('invoice-status')).toHaveText('Partly credited');
  await expect(m.getByTestId('credited')).toHaveText(money(630));
  await m.goto(`/folios/${folioId}`);
  await expect(m.locator('[data-line^="Credit note"]')).toContainText('One main course was not served');
  await shot(m, '06-bill-credited');
});

test('S12-04 The company wants the invoice: cancel it with a credit note, then issue a B2B one; a wrong GSTIN is caught under the field', async () => {
  await m.goto(`/invoices/${invoiceId}`);
  await m.getByRole('button', { name: 'Credit note' }).click();
  let dlg = m.locator('mat-dialog-container');
  await dlg.getByRole('radio', { name: 'Cancel the invoice' }).click();
  await expect(dlg).toContainText('a new invoice can be issued');
  await dlg.getByLabel('Reason').fill('Company asked for the invoice in its name');
  await dlg.getByRole('button', { name: 'Issue credit note' }).click();
  await said(m, /Credit note .* issued/);
  await expect(m.getByTestId('invoice-status')).toHaveText('Cancelled');
  await expect(m.getByRole('note')).toContainText('Cancelled by credit note');
  await shot(m, '07-cancelled');

  await m.goto(`/folios/${folioId}`);
  await m.getByRole('button', { name: 'Issue invoice' }).click();
  dlg = m.locator('mat-dialog-container');
  await dlg.getByLabel('Customer name').fill('Acme Travels Pvt Ltd');
  await dlg.getByLabel('GSTIN (for a B2B invoice)').fill('27AAPFU0939F1ZX');
  await dlg.getByLabel('Address').fill('Baner Road, Pune');
  await dlg.getByRole('button', { name: 'Issue tax invoice' }).click();
  const gstin = dlg.locator('mat-form-field', { hasText: 'GSTIN' }).locator('mat-error');
  await expect(gstin).toContainText('The last character (check digit) should be V');
  await dlg.getByLabel('GSTIN (for a B2B invoice)').fill('27AAPFU0939F1ZV');
  await expect(dlg.locator('mat-select[formcontrolname=customerStateCode]')).toContainText('Maharashtra');
  await shot(m, '08-issue-b2b');
  await dlg.getByRole('button', { name: 'Issue tax invoice' }).click();
  await said(m, /Tax invoice .* issued/);
  await m.locator('mat-snack-bar-container').getByRole('button', { name: 'Open' }).click();
  await expect(m.getByTestId('customer')).toHaveText('Acme Travels Pvt Ltd');
  await expect(m.getByTestId('customer-gstin')).toHaveText('GSTIN 27AAPFU0939F1ZV');
  await expect(m.locator('.parties')).toContainText('Goa (30)');
  await expect(m.locator('tfoot')).toContainText('CGST');
  await expect(m.locator('tfoot')).not.toContainText('IGST');
  await expect(m.locator('[data-line^="Dinner"]')).toContainText(`less ${money(600)} credited before`);
  await shot(m, '09-b2b-invoice');
});

test('S12-05 The Invoices screen finds both by the company\'s GSTIN; a payment\'s receipt downloads from the payment', async () => {
  await m.goto('/invoices');
  await m.getByLabel('Search').fill('27AAPFU0939F1ZV');
  await expect(m.locator('[data-invoice]')).toHaveCount(1);
  await m.getByLabel('Search').fill(invoiceNumber);
  await expect(m.locator(`[data-invoice="${invoiceNumber}"]`)).toContainText('Cancelled');
  await m.getByLabel('Search').fill('');
  await shot(m, '10-invoices');

  await r.goto(`/payments/${paymentId}`);
  const download = r.waitForEvent('download');
  await r.getByRole('button', { name: 'Receipt' }).click();
  expect((await download).suggestedFilename()).toMatch(/^[A-Z0-9]{1,5}-R\d{4}-\d{5}\.pdf$/);
  await said(r, /Receipt .* downloaded/);
});
