import { APIRequestContext, Browser, Page, expect, test } from '@playwright/test';
import { addDays, apiAs, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 11 — the same checks as docs/sprints/REVIEWING.md → "Sprint 11", automated.
// The demo: a receptionist sends a payment link for an advance on a pending booking; the guest opens it on their phone
// (no sign-in), a first try is declined, the second goes through; Razorpay's webhook records the payment and the booking
// is confirmed by itself; the manager refunds part of it back through Razorpay.
// The browser tests run without Razorpay keys, so the API uses the built-in fake Razorpay and the guest's page shows test
// buttons instead of Razorpay Checkout. Everything after the button (order, webhook, signature, dedupe) is the real code.

test.describe.configure({ mode: 'serial' });

let r: Page;          // the receptionist
let m: Page;          // the manager (the owner here)
let guest: Page;      // the guest, not signed in
let api: APIRequestContext;
let pid = '';
let bookingId = '';
let reference = '';
let total = 0;
let payPath = '';
let paymentId = '';
const staffPassword = `Staff-${runId}-2026`;
const reception = { email: `reception11.${runId}@demo.local`, name: `Ravi Reception ${runId}` };

const money = (v: number) => new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', minimumFractionDigits: 2 }).format(v);
const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text })).toBeVisible();

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  const prop = (await (await api.get('/api/v1/properties')).json())[0];
  pid = prop.id;
  const from = addDays(prop.localDate as string, 25);
  await createStaff(api, reception.email, reception.name, 'RECEPTIONIST', pid, staffPassword);

  // a booking held as PENDING until the guest pays an advance
  const avail = await (await api.get(`/api/v1/properties/${pid}/availability?from=${from}&to=${addDays(from, 2)}&adults=2&children=0`)).json();
  const type = (avail.types as { inventoryMode: string; accommodationTypeId: string; freeRooms: { id: string }[] }[])
    .find(t => t.inventoryMode === 'UNIT' && t.freeRooms.length >= 1)!;
  const source = (await (await api.get(`/api/v1/properties/${pid}/reservation-sources`)).json()).find((s: { code: string }) => s.code === 'PHONE').id;
  const g = (await (await api.get('/api/v1/guests?pageSize=8')).json()).items[5];
  const res = await api.post(`/api/v1/properties/${pid}/reservations`, {
    headers: { 'Idempotency-Key': `e2e-s11-stay-${runId}` },
    data: { checkIn: from, checkOut: addDays(from, 2), rooms: [{ accommodationTypeId: type.accommodationTypeId, unitId: type.freeRooms[0].id, adults: 2, children: 0 }],
            guestIds: [g.id], sourceId: source, ratePlanId: null, status: 'PENDING', specialRequests: null, note: 'Will pay the advance online' },
  });
  expect(res.status(), await res.text()).toBe(201);
  const booked = await res.json();
  bookingId = booked.id;
  reference = booked.reference;
  total = booked.totalAmount;
  expect(total).toBeGreaterThan(2000);

  const staff = await browser.newContext({ permissions: ['clipboard-read', 'clipboard-write'] });
  r = await staff.newPage();
  await signIn(r, reception.email, staffPassword);
  m = await (await browser.newContext()).newPage();
  await signIn(m, owner.email, owner.password);
  guest = await (await browser.newContext({ viewport: { width: 390, height: 844 } })).newPage();
});

test('S11-01 The receptionist sends a payment link for an advance; more than the booking owes is refused under the amount', async () => {
  await r.goto(`/reservations/${bookingId}`);
  const panel = r.getByTestId('payment-links');
  await expect(panel).toContainText('No links sent');
  await expect(r.getByTestId('owed')).toHaveText(money(total));
  await expect(panel.getByTestId('fake-note')).toBeVisible();
  await panel.getByTestId('send-link').click();
  const dlg = r.locator('mat-dialog-container');
  await expect(dlg.getByLabel('Amount (₹)')).toHaveValue(String(total));
  await dlg.getByLabel('Amount (₹)').fill(String(total + 1));
  await dlg.getByRole('button', { name: 'Create link' }).click();
  await expect(dlg.locator('mat-form-field', { hasText: 'Amount' }).locator('mat-error'))
    .toContainText(`The link can be for at most ${money(total)}`);
  await dlg.getByLabel('Amount (₹)').fill('2000');
  await dlg.getByLabel('Note for the guest').fill('Advance to hold your booking');
  await shot(r, '01-send-link');
  await dlg.getByRole('button', { name: 'Create link' }).click();
  await said(r, `Link for ${money(2000)} created and copied`);
  const link = panel.locator('[data-link]');
  await expect(link).toHaveCount(1);
  await expect(link).toContainText('Waiting for the guest');
  await expect(panel).toContainText(`links ask for ${money(2000)}`);
  payPath = new URL(await r.evaluate(() => navigator.clipboard.readText())).pathname;
  expect(payPath).toMatch(/^\/pay\/[A-Za-z0-9_-]{40,}$/);
  await shot(r, '02-link-sent');
});

test('S11-02 The guest opens the link on a phone without signing in; a declined try says why and the link stays open', async () => {
  await guest.goto(payPath);
  await expect(guest.locator('h1')).toHaveText(money(2000));
  await expect(guest.locator('p.mute').first()).toContainText(`Booking ${reference}`);
  await expect(guest.getByTestId('test-mode')).toBeVisible();
  await expect(guest.locator('nav')).toHaveCount(0);   // no staff menu
  await shot(guest, '03-pay-page');
  await guest.getByRole('radio', { name: 'Card' }).click();
  await guest.getByRole('button', { name: 'Fail the payment (test)' }).click();
  await expect(guest.getByRole('alert')).toContainText('Payment was declined by the bank (test)');
  await expect(guest.getByRole('button', { name: `Pay ${money(2000)} (test)` })).toBeEnabled();
  await shot(guest, '04-declined');
  expect(await guest.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBeTruthy();
});

test('S11-03 The second try goes through: Razorpay\'s webhook records the payment and the page says paid', async () => {
  await guest.getByRole('radio', { name: 'UPI' }).click();
  await guest.getByRole('button', { name: `Pay ${money(2000)} (test)` }).click();
  await expect(guest.locator('[data-status=PAID]')).toContainText(`has received ₹2,000 for booking ${reference}`);
  await shot(guest, '05-paid');
  // opening the link again later still says paid, and can't be paid twice
  await guest.reload();
  await expect(guest.locator('[data-status=PAID]')).toBeVisible();
  await expect(guest.getByRole('button', { name: /Pay / })).toHaveCount(0);
});

test('S11-04 On the booking: confirmed by the payment, the link paid with its payment and what Razorpay sent', async () => {
  await r.goto(`/reservations/${bookingId}`);
  await expect(r.locator('h1 .chip')).toHaveText('Confirmed');
  const link = r.getByTestId('payment-links').locator('[data-link]');
  await expect(link).toContainText('Paid');
  await expect(r.getByTestId('owed')).toHaveText(money(total - 2000));
  await link.getByRole('button', { name: 'History' }).click();
  const events = r.getByTestId('link-events');
  await expect(events).toContainText('payment.failed');
  await expect(events).toContainText('payment.authorized');
  await expect(events).toContainText(`${reference} confirmed`);
  await expect(events).toContainText('changes nothing');   // the later events of the same payment
  await shot(r, '06-booking-confirmed');
  await expect(r.getByText(`Paid online: `)).toBeVisible();   // in the booking's history

  const href = await link.getByRole('link').getAttribute('href');
  paymentId = href!.split('/').pop()!;
  await link.getByRole('link').click();
  await expect(r.getByTestId('status')).toHaveText('Received');
  await expect(r.getByTestId('online')).toContainText('Online (Razorpay), by UPI');
  await expect(r.getByRole('button', { name: 'Void' })).toHaveCount(0);   // money that really came in can only be refunded
});

test('S11-05 The manager refunds ₹500 back through Razorpay', async () => {
  await m.goto(`/payments/${paymentId}`);
  await expect(m.locator('dd', { hasText: 'paid online through Razorpay' })).toBeVisible();
  await m.getByRole('button', { name: 'Refund' }).click();
  const dlg = m.locator('mat-dialog-container');
  await expect(dlg.locator('mat-select')).toContainText('Online (Razorpay)');
  await expect(dlg.locator('[data-online-refund]')).toBeVisible();
  await dlg.getByLabel('Amount (₹)').fill('500');
  await dlg.getByLabel('Reason').fill('Guest changed to a smaller room');
  await shot(m, '07-online-refund');
  await dlg.getByRole('button', { name: 'Refund' }).click();
  await said(m, `Refunded. ${money(1500)} can still be refunded`);
  await expect(m.getByTestId('status')).toHaveText('Part refunded');
  await expect(m.locator('tr[data-refund]')).toContainText('Online (Razorpay)');
  await shot(m, '08-refunded');
});

test('S11-06 The webhook refuses a wrong signature, and a link can be cancelled with a reason', async () => {
  const bad = await api.post('/api/v1/webhooks/razorpay', {
    headers: { 'X-Razorpay-Signature': 'f'.repeat(64), 'X-Razorpay-Event-Id': `evt_e2e_${runId}` },
    data: { event: 'payment.captured', payload: {} },
  });
  expect(bad.status()).toBe(401);
  expect((await bad.json()).code).toBe('BAD_SIGNATURE');

  await r.goto(`/reservations/${bookingId}`);
  const panel = r.getByTestId('payment-links');
  await panel.getByTestId('send-link').click();
  let dlg = r.locator('mat-dialog-container');
  await dlg.getByLabel('Amount (₹)').fill('1000');
  await dlg.getByRole('button', { name: 'Create link' }).click();
  await said(r, `Link for ${money(1000)} created`);
  const open = panel.locator('[data-link]', { hasText: 'Waiting for the guest' });
  await open.getByRole('button', { name: 'Cancel' }).click();
  dlg = r.locator('mat-dialog-container');
  await dlg.getByRole('button', { name: 'Cancel link' }).click();
  await expect(dlg.locator('mat-error')).toContainText('Say why the link is cancelled');
  await dlg.getByLabel('Reason').fill('Guest will pay at the desk');
  await dlg.getByRole('button', { name: 'Cancel link' }).click();
  await said(r, 'cancelled; it can\'t be paid any more');
  await expect(panel.locator('[data-link]', { hasText: 'Cancelled' })).toContainText('Guest will pay at the desk');
});
