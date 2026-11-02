import { APIRequestContext, Browser, BrowserContext, Page, expect, test } from '@playwright/test';
import { apiAs, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 20 — the same checks as docs/sprints/REVIEWING.md → "Sprint 20", automated: brief §48–49. An activity manager
// adds kayaking (6 seats, ₹800, a life jacket each) and puts sessions on for tomorrow at 07:00 and 09:00. The receptionist,
// on her phone, books 4 seats for a staying guest: the price goes on the guest's bill. The last seats are filled, the
// session shows full and one more booking is refused with the reason. She issues 4 life jackets for the booking; one comes
// back good and one damaged ("Strap torn"), which takes it out of use. The manager then cancels 07:00 for the weather,
// moving one booking to 09:00 and refunding the other on the bill, and sends the damaged jacket to maintenance.

test.describe.configure({ mode: 'serial' });

const PHONE = { viewport: { width: 360, height: 780 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 };
let desk: Page;         // the receptionist, on her phone
let deskCtx: BrowserContext;
let mgr: Page;          // the activity manager, on a desktop
let mgrCtx: BrowserContext;
let api: APIRequestContext;
let pid = '';
const pw = `Staff-${runId}-2026`;
const reception = { email: `r20.${runId}@demo.local`, name: `Rekha Desk ${runId}` };
const manager = { email: `a20.${runId}@demo.local`, name: `Arun Activities ${runId}` };

// Our own activity and our own kind of jacket, so earlier runs and the demo data don't change the numbers.
const code = `K${runId}`.toUpperCase();
const activityName = `Kayaking ${runId}`;
const jacketCode = `J${runId}`.toUpperCase();
const jacketName = `Jacket ${runId}`;

const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text }).last()).toBeVisible();
const noSideScroll = async (page: Page) => expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);
const istDate = () => new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Kolkata' }).format(new Date());
const plus = (iso: string, n: number) => {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
};
const tomorrow = plus(istDate(), 1);
/** The open dialog, once its opening animation and first focus are done (typing earlier can land in the wrong field). */
const dialog = async (page: Page) => {
  const d = page.locator('mat-dialog-container');
  await expect(d).toHaveClass(/mdc-dialog--open/);
  await expect(d).not.toHaveClass(/mdc-dialog--opening/);
  await expect.poll(() => d.evaluate(el => el.contains(document.activeElement))).toBe(true);
  return d;
};
const session = (page: Page, time: string) => page.locator(`li[data-session="${code}-${time}"]`);

interface Stay { reservationId: string; reference: string; guestName: string }
let stays: Stay[] = [];
let firstBooking = '';

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  pid = (await (await api.get('/api/v1/properties')).json())[0].id;
  await createStaff(api, reception.email, reception.name, 'RECEPTIONIST', pid, pw);
  await createStaff(api, manager.email, manager.name, 'ACTIVITY_MANAGER', pid, pw);

  const type = await api.post(`/api/v1/properties/${pid}/equipment/types`, { data: { code: jacketCode, name: jacketName, sortOrder: 99 } });
  expect(type.status(), await type.text()).toBe(201);
  const items = await api.post(`/api/v1/properties/${pid}/equipment/items`, { data: { equipmentTypeId: (await type.json()).id, prefix: jacketCode, count: 6 } });
  expect(items.status(), await items.text()).toBe(201);

  // Guests staying tomorrow (the demo stays), who can have an activity put on their bill.
  stays = await (await api.get(`/api/v1/properties/${pid}/activities/stays?date=${tomorrow}`)).json();
  expect(stays.length, 'the demo data has someone staying tomorrow').toBeGreaterThan(0);

  mgrCtx = await browser.newContext();
  mgr = await mgrCtx.newPage();
  await signIn(mgr, manager.email, pw);
  deskCtx = await browser.newContext(PHONE);
  desk = await deskCtx.newPage();
  await signIn(desk, reception.email, pw);
});

test.afterAll(async () => {
  await deskCtx?.close();
  await mgrCtx?.close();
});

test('S20-01 the activity manager adds kayaking and puts sessions on for tomorrow', async () => {
  await mgr.goto('/activities');
  await mgr.getByRole('radio', { name: 'Activities' }).click();
  await mgr.getByRole('button', { name: 'New activity' }).click();
  const d = await dialog(mgr);
  await d.getByLabel('Code').fill(code);
  await d.getByLabel('Name', { exact: true }).fill(activityName);
  await d.getByLabel('Minutes').fill('90');
  await d.getByLabel('Seats a session').fill('0');
  await d.getByLabel('Price a person (₹)').fill('800');
  await d.getByRole('combobox', { name: 'Equipment each person needs' }).click();
  await mgr.getByRole('option', { name: jacketName }).click();
  await d.getByRole('button', { name: 'Save' }).click();
  // A wrong number is refused under its field with the reason.
  await expect(d.locator('mat-error', { hasText: /seat/i })).toBeVisible();
  await d.getByLabel('Seats a session').fill('6');
  await d.getByRole('button', { name: 'Save' }).click();
  await said(mgr, activityName);
  await expect(mgr.getByTestId('activities')).toContainText(activityName);

  await mgr.getByRole('radio', { name: 'Day' }).click();
  await mgr.getByRole('button', { name: 'Put sessions on' }).click();
  const s = await dialog(mgr);
  await s.getByRole('combobox', { name: 'Activity' }).click();
  await mgr.getByRole('option', { name: activityName }).click();
  await s.getByLabel('From').fill(tomorrow);
  await s.getByLabel('To').fill(tomorrow);
  await s.getByLabel('Start times').fill('07:00, 09:00');
  await s.getByRole('button', { name: 'Put on' }).click();
  await said(mgr, /2 sessions? put on/i);
  await mgr.getByRole('button', { name: 'Next day' }).click();
  await expect(session(mgr, '07:00')).toContainText('6 left');
  await expect(session(mgr, '09:00')).toContainText('6 left');
  await shot(mgr, '01-sessions-put-on');
});

test('S20-02 the receptionist books 4 seats on her phone; 07:00 cannot be oversold', async () => {
  await desk.goto('/activities');
  await desk.getByRole('button', { name: 'Next day' }).click();
  const s7 = session(desk, '07:00');
  await s7.getByRole('button').first().click();
  await s7.getByRole('button', { name: 'Book' }).click();
  const d = await dialog(desk);
  const stay = stays[0];
  await d.getByRole('combobox', { name: 'Guest staying' }).click();
  await desk.getByRole('option', { name: stay.reference }).click();
  await d.getByLabel('Seats').fill('4');
  await expect(d.getByTestId('book-total')).toContainText('3,200');
  await shot(desk, '02-book-on-phone');
  await noSideScroll(desk);
  await d.getByRole('button', { name: 'Book' }).click();
  await said(desk, /bill/i);
  await expect(s7).toContainText('2 left');
  const booking = s7.locator('li[data-booking]').first();
  await expect(booking).toContainText('4 seats');
  firstBooking = (await booking.getAttribute('data-booking'))!;

  // The other two seats go to a free guest of the house; then the session is full and a 7th seat is refused.
  const sessions = await (await api.get(`/api/v1/properties/${pid}/activities/sessions?date=${tomorrow}`)).json();
  const at7 = sessions.sessions.find((x: { activityCode: string; startTime: string }) => x.activityCode === code && x.startTime === '07:00');
  const fill = await api.post(`/api/v1/properties/${pid}/activities/bookings`, {
    headers: { 'Idempotency-Key': `s20-fill-${runId}` },
    data: { sessionId: at7.id, seats: 2, guestName: 'Asha Rao', complimentary: true, complimentaryReason: 'Honeymoon welcome' },
  });
  expect(fill.status(), await fill.text()).toBe(201);
  const more = await api.post(`/api/v1/properties/${pid}/activities/bookings`, {
    headers: { 'Idempotency-Key': `s20-more-${runId}` },
    data: { sessionId: at7.id, seats: 1, guestName: 'One more', complimentary: true, complimentaryReason: 'Walk-up' },
  });
  expect(more.status()).toBe(409);
  const problem = await more.json();
  expect(problem.code).toBe('ACTIVITY_FULL');
  expect(problem.message).toContain('is full (6 of 6 seats booked)');

  await desk.reload();
  await desk.getByRole('button', { name: 'Next day' }).click();
  await expect(session(desk, '07:00')).toContainText('full');
  await expect(session(desk, '07:00')).toContainText('6/6');
});

test('S20-03 life jackets go out with the booking; one comes back good and one damaged', async () => {
  const s7 = session(desk, '07:00');
  await s7.getByRole('button').first().click();
  const booking = s7.locator(`li[data-booking="${firstBooking}"]`);
  await booking.getByRole('button', { name: 'Equipment' }).click();
  const d = await dialog(desk);
  await expect(d.getByLabel('How many')).toHaveValue('4');
  await d.getByRole('button', { name: 'Issue' }).click();
  await said(desk, /issued/);
  await expect(s7.locator(`li[data-booking="${firstBooking}"]`)).toContainText(jacketCode);

  await desk.goto('/equipment');
  await desk.locator(`[data-kind="${jacketCode}"]`).click();
  const out = desk.getByTestId('out');
  await expect(out.locator('li[data-tag]')).toHaveCount(4);
  await expect(desk.locator(`[data-kind="${jacketCode}"]`)).toContainText('2/6');
  await shot(desk, '03-equipment-out');
  await noSideScroll(desk);

  await out.locator(`li[data-tag="${jacketCode}-01"]`).getByRole('button', { name: 'Return' }).click();
  await (await dialog(desk)).getByRole('button', { name: 'Return' }).click();
  await said(desk, `${jacketCode}-01 is back and available.`);

  await out.locator(`li[data-tag="${jacketCode}-02"]`).getByRole('button', { name: 'Return' }).click();
  const r = await dialog(desk);
  await r.getByRole('radio', { name: 'Damaged' }).click();
  await r.getByRole('button', { name: 'Return' }).click();
  // Damaged needs a reason, said under the field.
  await expect(r.locator('mat-error')).toContainText('Say what happened');
  await r.getByLabel('What happened').fill('Strap torn');
  await r.getByRole('button', { name: 'Return' }).click();
  await said(desk, 'Strap torn');
  await expect(out.locator('li[data-tag]')).toHaveCount(2);

  await desk.getByRole('radio', { name: 'Items' }).click();
  const damaged = desk.getByTestId('items').locator(`li[data-tag="${jacketCode}-02"]`);
  await expect(damaged).toContainText('Damaged');
  await expect(damaged).toContainText('Strap torn');
  await expect(desk.getByTestId('items').locator(`li[data-tag="${jacketCode}-01"]`)).toContainText('Available');
  await shot(desk, '04-returned-damaged');
});

test('S20-04 the manager cancels 07:00 for the weather: one booking moves to 09:00, the other is refunded', async () => {
  await mgr.goto('/activities');
  await mgr.getByRole('button', { name: 'Next day' }).click();
  const s7 = session(mgr, '07:00');
  await s7.getByRole('button').first().click();
  await s7.getByRole('button', { name: 'Cancel for weather' }).click();
  const d = await dialog(mgr);
  await d.getByRole('button', { name: 'Cancel the session' }).click();
  await expect(d.locator('mat-error')).toBeVisible();
  await d.getByLabel('What is the weather doing').fill('Rough sea, red flag');
  // The paid booking is refunded on the bill (the default); the free one moves to 09:00.
  const selects = d.getByRole('combobox');
  await expect(selects).toHaveCount(2);
  await expect(selects.first()).toContainText('Refund');
  await selects.nth(1).click();
  await mgr.getByRole('option', { name: /09:00/ }).click();
  await shot(mgr, '05-weather-cancel');
  await d.getByRole('button', { name: 'Cancel the session' }).click();
  await said(mgr, /refunded|moved/i);
  await expect(s7).toContainText('cancelled');
  await expect(session(mgr, '09:00')).toContainText('2/6');

  // On the guest's bill the charge and its reversal both show; the activity booking says why.
  const b = await (await api.get(`/api/v1/properties/${pid}/activities/bookings?reservationId=${stays[0].reservationId}`)).json();
  const mine = b.find((x: { code: string }) => x.code === firstBooking);
  expect(mine.status).toBe('CANCELLED');
  expect(mine.cancelKind).toBe('WEATHER');
  expect(mine.refund).toBe('REVERSED');
});

test('S20-05 the manager sends the damaged jacket to maintenance', async () => {
  await mgr.goto('/equipment');
  await mgr.getByRole('radio', { name: 'Items' }).click();
  await mgr.locator(`[data-kind="${jacketCode}"]`).click();
  const item = mgr.getByTestId('items').locator(`li[data-tag="${jacketCode}-02"]`);
  await item.getByRole('button', { name: 'Change' }).click();
  const d = await dialog(mgr);
  await d.getByRole('combobox', { name: 'Set to' }).click();
  await mgr.getByRole('option', { name: 'In maintenance' }).click();
  await d.getByLabel('What is wrong, or what was done').fill('Sent for stitching');
  await d.getByRole('button', { name: 'Save' }).click();
  await said(mgr, `${jacketCode}-02 is`);
  await expect(item).toContainText('Sent for stitching');
  await shot(mgr, '06-maintenance');
});
