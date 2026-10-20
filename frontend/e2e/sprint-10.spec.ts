import { APIRequestContext, Browser, Page, expect, test } from '@playwright/test';
import { addDays, apiAs, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 10 — the same checks as docs/sprints/REVIEWING.md → "Sprint 10", automated.
// The demo: a receptionist opens the cash drawer, a guest pays part of the bill in cash and the rest by UPI, the manager
// refunds part of the cash, the receptionist closes the drawer ₹50 short, and the manager approves the variance.
// The demo data (database/seed/dev/S009) leaves no shift open, so this file opens the first one of the day.

test.describe.configure({ mode: 'serial' });

let r: Page;          // the receptionist
let m: Page;          // the manager (the owner here)
let api: APIRequestContext;
let pid = '';
let today = '';
let folioId = '';
let cashPaymentId = '';
let owed = 0;
const staffPassword = `Staff-${runId}-2026`;
const reception = { email: `reception10.${runId}@demo.local`, name: `Rekha Reception ${runId}` };

const money = (v: number) => new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', minimumFractionDigits: 2 }).format(v);
const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text })).toBeVisible();

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  const prop = (await (await api.get('/api/v1/properties')).json())[0];
  pid = prop.id;
  today = prop.localDate as string;
  await createStaff(api, reception.email, reception.name, 'RECEPTIONIST', pid, staffPassword);

  // a guest in house for two nights, so the bill has room nights on it
  const avail = await (await api.get(`/api/v1/properties/${pid}/availability?from=${today}&to=${addDays(today, 2)}&adults=1&children=0`)).json();
  const type = (avail.types as { inventoryMode: string; accommodationTypeId: string; freeRooms: { id: string }[] }[])
    .find(t => t.inventoryMode === 'UNIT' && t.freeRooms.length >= 1)!;
  const source = (await (await api.get(`/api/v1/properties/${pid}/reservation-sources`)).json()).find((s: { code: string }) => s.code === 'PHONE').id;
  const guest = (await (await api.get('/api/v1/guests?pageSize=8')).json()).items[6];
  const res = await api.post(`/api/v1/properties/${pid}/reservations`, {
    headers: { 'Idempotency-Key': `e2e-s10-stay-${runId}` },
    data: { checkIn: today, checkOut: addDays(today, 2), rooms: [{ accommodationTypeId: type.accommodationTypeId, unitId: type.freeRooms[0].id, adults: 1, children: 0 }],
            guestIds: [guest.id], sourceId: source, ratePlanId: null, status: 'CONFIRMED', specialRequests: null, note: null },
  });
  expect(res.status(), await res.text()).toBe(201);
  const booked = await res.json();
  const inn = await api.post(`/api/v1/properties/${pid}/reservations/${booked.id}/check-in`,
    { data: { version: null, overrideHousekeeping: true, overrideReason: 'Browser test: room checked by hand' } });
  expect(inn.status(), await inn.text()).toBe(200);
  const folio = (await (await api.get(`/api/v1/properties/${pid}/reservations/${booked.id}/folios`)).json()).find((f: { isPrimary: boolean }) => f.isPrimary);
  folioId = folio.id;
  owed = folio.balance;
  expect(owed).toBeGreaterThan(1000);

  r = await (await browser.newContext()).newPage();
  await signIn(r, reception.email, staffPassword);
  m = await (await browser.newContext()).newPage();
  await signIn(m, owner.email, owner.password);
});

test('S10-01 The receptionist opens the cash drawer on the front desk with the float counted in it', async () => {
  await r.goto('/front-desk');
  const drawer = r.getByTestId('drawer');
  await expect(drawer).toContainText('Front desk drawer closed');
  await drawer.getByRole('button', { name: 'Open drawer' }).click();
  const dlg = r.locator('mat-dialog-container');
  await dlg.getByLabel('Cash in the drawer (₹)').fill('5000');
  await dlg.getByRole('button', { name: 'Open drawer' }).click();
  await said(r, `Drawer open with ${money(5000)}`);
  await expect(drawer).toContainText('Front desk drawer open');
  await expect(r.getByTestId('drawer-expected')).toHaveText(money(5000));
  await shot(r, '01-drawer-open');
});

test('S10-02 Part of the bill in cash, a card number refused, the rest by UPI: the balance is nil', async () => {
  await r.goto(`/folios/${folioId}`);
  await expect(r.getByTestId('balance')).toHaveText(money(owed));
  await r.getByRole('button', { name: 'Take payment' }).click();
  let dlg = r.locator('mat-dialog-container');
  await dlg.getByRole('radio', { name: 'Cash' }).click();
  await dlg.getByLabel('Amount (₹)').fill('1000');
  await shot(r, '02-take-cash');
  await dlg.getByRole('button', { name: `Take ${money(1000)}` }).click();
  await said(r, `${money(1000)} paid`);
  await expect(r.getByTestId('balance')).toHaveText(money(owed - 1000));
  const cashLine = r.locator('tr[data-payment-line]').first();
  await expect(cashLine).toContainText('Payment · Cash');
  cashPaymentId = (await cashLine.getByRole('link').getAttribute('href'))!.split('/').pop()!;

  // a card number typed into the reference is refused before it is sent, under the field
  await r.getByRole('button', { name: 'Take payment' }).click();
  dlg = r.locator('mat-dialog-container');
  await dlg.getByRole('radio', { name: 'Card' }).click();
  await dlg.getByLabel('Approval code').fill('4111 1111 1111 1111');
  await dlg.getByRole('button', { name: /^Take / }).click();
  await expect(dlg.locator('mat-form-field', { hasText: 'Approval code' }).locator('mat-error'))
    .toContainText('This looks like a card number, and card details are never stored');
  await shot(r, '03-card-number-refused');

  // the rest by UPI, with the UTR from the guest's app
  await dlg.getByRole('radio', { name: 'UPI' }).click();
  await dlg.getByLabel('UPI transaction id').fill('428915736012');
  await expect(dlg.getByLabel('Amount (₹)')).toHaveValue(String(owed - 1000));
  await dlg.getByRole('button', { name: `Take ${money(owed - 1000)}` }).click();
  await said(r, `${money(owed - 1000)} paid`);
  await expect(r.getByTestId('balance')).toHaveText(money(0));
  await expect(r.locator('tr[data-payment-line]')).toHaveCount(2);
  await shot(r, '04-bill-paid');
});

test('S10-03 The manager refunds ₹200 of the cash with a reason; the receptionist can not refund', async () => {
  await r.goto(`/payments/${cashPaymentId}`);
  await expect(r.getByTestId('status')).toHaveText('Received');
  await expect(r.getByRole('button', { name: 'Refund' })).toHaveCount(0);

  await m.goto(`/payments/${cashPaymentId}`);
  await m.getByRole('button', { name: 'Refund' }).click();
  const dlg = m.locator('mat-dialog-container');
  await dlg.getByLabel('Amount (₹)').fill('200');
  await dlg.getByRole('button', { name: 'Refund' }).click();
  await expect(dlg.locator('mat-form-field', { hasText: 'Reason' }).locator('mat-error'))
    .toContainText('Say why the money is refunded (at least 3 characters)');
  await dlg.getByLabel('Reason').fill('Guest cancelled the airport transfer');
  await shot(m, '05-refund');
  await dlg.getByRole('button', { name: 'Refund' }).click();
  await said(m, `Refunded. ${money(800)} can still be refunded`);
  await expect(m.getByTestId('status')).toHaveText('Part refunded');
  await expect(m.getByTestId('refunded')).toHaveText(money(200));
  await expect(m.locator('tr[data-refund]')).toContainText('Guest cancelled the airport transfer');

  await m.goto(`/folios/${folioId}`);
  await expect(m.getByTestId('balance')).toHaveText(money(200));
  await expect(m.locator('tr[data-payment-line$="-refund"]')).toContainText(`− ${money(200)}`);
  await shot(m, '06-bill-with-refund');
});

test('S10-04 The receptionist closes the drawer ₹50 short and must say why', async () => {
  await r.goto('/front-desk');
  await expect(r.getByTestId('drawer-expected')).toHaveText(money(5800));
  await r.getByTestId('drawer').getByRole('button', { name: 'Close drawer' }).click();
  const dlg = r.locator('mat-dialog-container');
  await expect(dlg.getByTestId('expected')).toHaveText(money(5800));
  await dlg.getByLabel('Cash counted now (₹)').fill('5750');
  await expect(dlg.getByTestId('variance')).toContainText('Short');
  await expect(dlg.getByTestId('variance')).toContainText(money(50));
  await dlg.getByRole('button', { name: 'Close drawer' }).click();
  await expect(dlg.locator('mat-form-field', { hasText: 'Why it is short' }).locator('mat-error'))
    .toContainText('Say why the drawer is short (at least 3 characters)');
  await dlg.getByLabel('Why it is short').fill('Gave too much change to a guest');
  await shot(r, '07-close-short');
  await dlg.getByRole('button', { name: 'Close drawer' }).click();
  await said(r, `Drawer closed ${money(50)} short; waiting for a manager to approve`);
  const pending = r.getByTestId('drawer').locator('[data-session]');
  await expect(pending).toContainText(`${money(50)} short`);
  await expect(pending).toContainText('Waiting for a manager');
  await expect(r.getByTestId('drawer')).toContainText('Front desk drawer closed');
});

test('S10-05 The manager approves the variance; the day adds up by method on Payments', async () => {
  await m.goto('/dashboard');
  await m.locator('nav a[href="/payments"]').click();
  await expect(m).toHaveURL(/\/payments$/);
  const pending = m.getByTestId('drawer').locator('[data-session]');
  await expect(pending).toContainText('Gave too much change to a guest');
  await shot(m, '08-variance-pending');
  await pending.getByRole('button', { name: 'Approve' }).click();
  const dlg = m.locator('mat-dialog-container');
  await dlg.getByLabel('Your note').fill('Counted again with Rekha; ₹50 given as change by mistake');
  await dlg.getByRole('button', { name: 'Approve' }).click();
  await said(m, 'Variance approved; the shift is closed');
  await expect(m.getByTestId('drawer').locator('[data-session]')).toHaveCount(0);

  await expect(m.locator('.by[data-method="CASH"]')).toContainText(money(800));
  await expect(m.locator('.by[data-method="UPI"]')).toContainText(money(owed - 1000));
  await expect(m.locator('tr[data-payment]').first()).toBeVisible();
  await shot(m, '09-payments');
});

test('S10-06 The API refuses a refund over what is left, a refund without a key, and a second shift on one drawer', async () => {
  const over = await api.post(`/api/v1/properties/${pid}/payments/${cashPaymentId}/refund`, {
    headers: { 'Idempotency-Key': `e2e-s10-over-${runId}` }, data: { amount: 900, reason: 'Too much', method: 'UPI' },
  });
  expect(over.status()).toBe(400);
  expect(JSON.stringify(await over.json())).toContain(money(800).replace('.00', ''));

  const noKey = await api.post(`/api/v1/properties/${pid}/payments/${cashPaymentId}/refund`, { data: { amount: 10, reason: 'No key', method: 'UPI' } });
  expect(noKey.status()).toBe(400);

  const open = await api.post(`/api/v1/properties/${pid}/cash-drawer/open`, { data: { openingFloat: 100 } });
  expect(open.status(), await open.text()).toBe(201);
  const again = await api.post(`/api/v1/properties/${pid}/cash-drawer/open`, { data: { openingFloat: 100 } });
  expect(again.status()).toBe(409);
  expect((await again.json()).code).toBe('DRAWER_ALREADY_OPEN');
  const close = await api.post(`/api/v1/properties/${pid}/cash-drawer/close`, { data: { countedAmount: 100 } });
  expect(close.status(), await close.text()).toBe(200);
});

test('S10-07 On a phone the payments list and a payment stay readable', async () => {
  await m.setViewportSize({ width: 390, height: 844 });
  await m.goto('/payments');
  await expect(m.locator('tr[data-payment]').first()).toBeVisible();
  expect(await m.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBeTruthy();
  await shot(m, '10-phone');
  await m.goto(`/payments/${cashPaymentId}`);
  await expect(m.getByTestId('status')).toHaveText('Part refunded');
  expect(await m.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBeTruthy();
  await m.setViewportSize({ width: 1280, height: 860 });
});
