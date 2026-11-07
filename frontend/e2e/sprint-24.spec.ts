import { APIRequestContext, Browser, BrowserContext, Page, expect, test } from '@playwright/test';
import { addDays, apiAs, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 24 — the same checks as docs/sprints/REVIEWING.md → "Sprint 24", automated: brief §50. A booking made as confirmed
// sends the guest a WhatsApp and an email confirmation (the built-in test WhatsApp and email: no keys in the browser tests).
// The booking page shows them; the receptionist plays WhatsApp's part (delivered, read). The day-before reminder job, run by
// hand twice, sends once. The seeded message that failed is sent again. The owner's template change with a variable the
// message can't fill in is refused under the text. The owner's bell has a line that goes to the Messages page. On a phone
// the Messages page fits.

test.describe.configure({ mode: 'serial' });

const PHONE = { viewport: { width: 360, height: 780 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 };
let api: APIRequestContext;
let pid: string;
let rec: Page;
let recCtx: BrowserContext;
let own: Page;
let ownCtx: BrowserContext;
const pw = `Staff-${runId}-2026`;
const reception = { email: `r24.${runId}@demo.local`, name: `Rhea Reception ${runId}` };
const booking = { id: '', ref: '', guest: `Isha ${runId}` };

interface FreeType { inventoryMode: string; accommodationTypeId: string; freeRooms: { id: string; name: string }[] }

const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text }).last()).toBeVisible();
const noSideScroll = async (page: Page) => expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);
const dialog = async (page: Page) => {
  const d = page.locator('mat-dialog-container');
  await expect(d).toHaveClass(/mdc-dialog--open/);
  await expect(d).not.toHaveClass(/mdc-dialog--opening/);
  return d;
};

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  const prop = (await (await api.get('/api/v1/properties')).json())[0];
  pid = prop.id;
  const tomorrow = addDays(prop.localDate as string, 1);
  await createStaff(api, reception.email, reception.name, 'RECEPTIONIST', pid, pw);

  // This file's own guest, with a mobile number and an email, booked as confirmed for tomorrow (so the day-before reminder finds it).
  const digits = String(Date.now()).slice(-8);
  const g = await api.post('/api/v1/guests', { data: {
    firstName: booking.guest, lastName: 'Kulkarni', phone: `+9198${digits}`, email: `isha.${runId}@example.com`, isVip: false, ignoreDuplicates: true,
  } });
  expect(g.status(), await g.text()).toBe(201);
  const guest = await g.json();
  const avail = await (await api.get(`/api/v1/properties/${pid}/availability?from=${tomorrow}&to=${addDays(tomorrow, 2)}&adults=2&children=0`)).json();
  const type = (avail.types as FreeType[]).find(t => t.inventoryMode === 'UNIT' && t.freeRooms.length >= 1)!;
  const source = (await (await api.get(`/api/v1/properties/${pid}/reservation-sources`)).json()).find((s: { code: string }) => s.code === 'PHONE').id;
  const res = await api.post(`/api/v1/properties/${pid}/reservations`, {
    headers: { 'Idempotency-Key': `e2e-s24-${runId}` },
    data: { checkIn: tomorrow, checkOut: addDays(tomorrow, 2), rooms: [{ accommodationTypeId: type.accommodationTypeId, unitId: type.freeRooms[0].id, adults: 2, children: 0 }],
            guestIds: [guest.id], sourceId: source, ratePlanId: null, status: 'CONFIRMED', specialRequests: null, note: null },
  });
  expect(res.status(), await res.text()).toBe(201);
  ({ id: booking.id, reference: booking.ref } = await res.json());

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

test('S24-01 a confirmed booking sends the guest a WhatsApp and an email confirmation, shown on the booking', async () => {
  await rec.goto(`/reservations/${booking.id}`);
  const panel = rec.getByTestId('booking-messages');
  const wa = panel.locator('[data-template="booking_confirmation"]', { hasText: 'WhatsApp' });
  const mail = panel.locator('[data-template="booking_confirmation"]', { hasText: 'Email' });
  // The outbox is sent within a few seconds by the dispatcher; Refresh until it is.
  await expect(async () => {
    await panel.getByRole('button', { name: 'Refresh messages' }).click();
    await expect(wa).toHaveAttribute('data-status', 'SENT', { timeout: 1500 });
    await expect(mail).toHaveAttribute('data-status', 'SENT', { timeout: 1500 });
  }).toPass({ timeout: 45_000 });
  await shot(rec, '01-booking-messages');
});

test('S24-02 WhatsApp says delivered, then read; the message shows each step', async () => {
  const panel = rec.getByTestId('booking-messages');
  await panel.locator('[data-template="booking_confirmation"]', { hasText: 'WhatsApp' }).click();
  const d = await dialog(rec);
  await expect(d.getByTestId('detail-body')).toContainText(`Hello ${booking.guest}, your booking ${booking.ref}`);
  await expect(d.getByTestId('detail-status')).toHaveText('Sent');
  await d.getByTestId('fake-status').getByRole('button', { name: 'Delivered' }).click();
  await expect(d.getByTestId('detail-status')).toHaveText('Delivered');
  await d.getByTestId('fake-status').getByRole('button', { name: 'Read' }).click();
  await expect(d.getByTestId('detail-status')).toHaveText('Read');
  await expect(d.getByTestId('steps').locator('li')).toHaveCount(4);   // waiting, sent, delivered, read
  await shot(rec, '02-message-read');
  await d.getByRole('button', { name: 'Close' }).click();
  await expect(rec.getByTestId('booking-messages').locator('[data-template="booking_confirmation"]', { hasText: 'WhatsApp' }))
    .toHaveAttribute('data-status', 'READ');
});

test('S24-03 the day-before reminder, run twice, sends once', async () => {
  await rec.getByRole('link', { name: 'Messages', exact: true }).click();
  await expect(rec).toHaveURL(/\/messages/);
  await rec.getByRole('radio', { name: 'Daily jobs' }).click();
  const job = rec.locator('[data-job="reservation_reminder"]');
  await expect(job).toContainText('Daily at 10:00');
  await job.getByRole('button', { name: 'Run now' }).click();
  await expect(job.getByTestId('run-result')).toContainText(/Day-before reminder: \d+ to do, \d+ messages queued/);
  await shot(rec, '03-reminder-run');
  await job.getByRole('button', { name: 'Run now' }).click();
  await expect(job.getByTestId('run-result')).toContainText(/already done earlier, so nothing was sent twice/);
  await expect(rec.getByTestId('runs')).toContainText(`Day-before reminder · ${booking.ref}`);

  // The reminder reaches this guest once, on each channel.
  await rec.getByRole('radio', { name: 'Messages', exact: true }).click();
  await rec.getByLabel('Guest or booking').fill(booking.ref);
  await rec.getByLabel('Guest or booking').press('Enter');
  await expect(rec.getByTestId('messages').locator('[data-template]', { hasNotText: booking.ref })).toHaveCount(0);
  const rows = rec.getByTestId('messages').locator('[data-template="reservation_reminder"]');
  await expect(rows).toHaveCount(2);
});

test('S24-04 the seeded message WhatsApp couldn\'t deliver says why and is sent again', async () => {
  await rec.getByLabel('Guest or booking').fill('');
  await rec.getByLabel('Guest or booking').press('Enter');
  await rec.getByRole('button', { name: /^Needs a look/ }).click();
  const failed = rec.getByTestId('messages').locator('[data-status="FAILED"]').first();
  await expect(failed).toContainText("WhatsApp couldn't deliver it");
  await shot(rec, '04-needs-a-look');
  await failed.click();
  const d = await dialog(rec);
  await expect(d.getByTestId('detail-reason')).toContainText('the number isn');
  await d.getByTestId('retry').click();
  await expect(d.getByTestId('detail-status')).toHaveText('Sent');
  await d.getByRole('button', { name: 'Close' }).click();
});

test('S24-05 the owner\'s template change with a variable the message can\'t fill in is refused under the text', async () => {
  await own.goto('/messages?tab=templates');
  const card = own.locator('[data-code="booking_confirmation"]');
  await card.locator('[data-channel="EMAIL"]').getByRole('button', { name: 'Change' }).click();
  const d = await dialog(own);
  const body = d.getByTestId('template-body');
  await body.fill('Hello {{guest_name}}, room {{room_number}} is ready for {{reference}}.');
  await expect(d.getByTestId('unknown-vars')).toContainText("{{room_number}} isn't something this message can fill in");
  await d.getByRole('button', { name: 'Save' }).click();
  await expect(d.locator('mat-error')).toContainText("{{room_number}} isn't something this message can fill in. It can use:");
  await shot(own, '05-template-refused');
  await body.fill('Hello {{guest_name}}, your booking {{reference}} at {{property_name}} is confirmed. See you on {{check_in}}.');
  await expect(d.getByTestId('template-preview')).toContainText('is confirmed. See you on');
  await d.getByRole('button', { name: 'Save' }).click();
  await said(own, 'Booking confirmation (Email) saved');
});

test('S24-06 the owner moves the day-before reminder to 9:30 and puts it back', async () => {
  await own.getByRole('radio', { name: 'Daily jobs' }).click();
  const job = own.locator('[data-job="reservation_reminder"]');
  await job.getByRole('button', { name: 'Change' }).click();
  let d = await dialog(own);
  await d.getByLabel('At (property time)').fill('09:30');
  await d.getByRole('button', { name: 'Save' }).click();
  await said(own, 'Day-before reminder: daily at 09:30');
  await expect(job).toContainText('Daily at 09:30');
  await job.getByRole('button', { name: 'Change' }).click();
  d = await dialog(own);
  await d.getByLabel('At (property time)').fill('10:00');
  await d.getByRole('button', { name: 'Save' }).click();
  await expect(job).toContainText('Daily at 10:00');
});

test('S24-07 the owner\'s bell has a line that goes to the Messages page and is then read', async () => {
  await own.goto('/dashboard');
  await own.getByTestId('bell').click();
  const line = own.locator('[data-notification="Messages are on"]');
  await expect(line).toBeVisible();
  await shot(own, '06-bell');
  await line.click();
  await expect(own).toHaveURL(/\/messages$/);
  await own.getByTestId('bell').click();
  await expect(own.locator('[data-notification="Messages are on"]')).not.toHaveClass(/unread/);
  await own.keyboard.press('Escape');
});

test('S24-08 on a phone the receptionist\'s Messages page fits', async ({ browser }) => {
  const ctx = await browser.newContext(PHONE);
  const phone = await ctx.newPage();
  await signIn(phone, reception.email, pw);
  await phone.goto('/messages');
  await expect(phone.getByTestId('messages').locator('[data-template]').first()).toBeVisible();
  await noSideScroll(phone);
  await shot(phone, '07-phone-messages');
  await phone.getByRole('radio', { name: 'Daily jobs' }).click();
  await expect(phone.getByTestId('jobs')).toBeVisible();
  await noSideScroll(phone);
  await ctx.close();
});
