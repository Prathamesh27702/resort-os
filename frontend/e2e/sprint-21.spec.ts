import { APIRequestContext, Browser, BrowserContext, Page, expect, test } from '@playwright/test';
import { apiAs, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 21 — the same checks as docs/sprints/REVIEWING.md → "Sprint 21", automated: brief §47, §73. The transport manager
// adds a vehicle and puts a beach trip on with a driver. The receptionist, on her phone, books 3 seats for a staying guest:
// ₹1,500 goes on the guest's bill. The manager downloads the passenger manifest. The driver opens Beach transport on his
// phone, sees the trip and its passengers (the manifest), ticks them as they board and marks the trip as left. The trip is
// tonight at 23:30 (so the driver can leave it today); after 23:00 IST it is tomorrow and the "left" step is skipped.

test.describe.configure({ mode: 'serial' });

const PHONE = { viewport: { width: 360, height: 780 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 };
let desk: Page;         // the receptionist, on her phone
let deskCtx: BrowserContext;
let mgr: Page;          // the transport manager, on a desktop
let mgrCtx: BrowserContext;
let drv: Page;          // the driver, on his phone
let drvCtx: BrowserContext;
let api: APIRequestContext;
let pid = '';
const pw = `Staff-${runId}-2026`;
const reception = { email: `r21.${runId}@demo.local`, name: `Rekha Desk ${runId}` };
const manager = { email: `m21.${runId}@demo.local`, name: `Mohan Transport ${runId}` };
const driver = { email: `d21.${runId}@demo.local`, name: `Santosh Driver ${runId}` };

// Our own vehicle and route, so earlier runs and the demo data (and their trips) don't change the numbers.
const vehicleCode = `V${runId}`.toUpperCase();
const vehicleName = `Van ${runId}`;
const routeCode = `R${runId}`.toUpperCase();
const routeName = `Shuttle ${runId}`;

const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text }).last()).toBeVisible();
const noSideScroll = async (page: Page) => expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);
const istNow = () => new Date(Date.now() + 5.5 * 3600_000);
const istDate = () => istNow().toISOString().slice(0, 10);
const plus = (iso: string, n: number) => {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
};
/** Tonight, unless it is already past 23:00 in India. */
const tonight = istNow().getUTCHours() < 23;
const tripDay = tonight ? istDate() : plus(istDate(), 1);
/** The open dialog, once its opening animation and first focus are done (typing earlier can land in the wrong field). */
const dialog = async (page: Page) => {
  const d = page.locator('mat-dialog-container');
  await expect(d).toHaveClass(/mdc-dialog--open/);
  await expect(d).not.toHaveClass(/mdc-dialog--opening/);
  await expect.poll(() => d.evaluate(el => el.contains(document.activeElement))).toBe(true);
  return d;
};
const trip = (page: Page) => page.locator(`li[data-trip="${routeCode}-23:30"]`);
const toTripDay = async (page: Page) => { if (!tonight) await page.getByRole('button', { name: 'Next day' }).click(); };

interface Stay { reservationId: string; reference: string; guestName: string }
let stays: Stay[] = [];
let passengerCode = '';

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  pid = (await (await api.get('/api/v1/properties')).json())[0].id;
  await createStaff(api, reception.email, reception.name, 'RECEPTIONIST', pid, pw);
  await createStaff(api, manager.email, manager.name, 'MANAGER', pid, pw);
  await createStaff(api, driver.email, driver.name, 'DRIVER', pid, pw);

  const route = await api.post(`/api/v1/properties/${pid}/transport/routes`, {
    data: { code: routeCode, name: routeName, origin: 'Resort gate', destination: 'Tarkarli beach', distanceKm: 8, durationMinutes: 20, price: 500, sortOrder: 99 },
  });
  expect(route.status(), await route.text()).toBe(201);

  // Guests staying on the trip's day (the demo stays), who can have a seat put on their bill.
  stays = await (await api.get(`/api/v1/properties/${pid}/transport/stays?date=${tripDay}`)).json();
  expect(stays.length, 'the demo data has someone staying on the trip day').toBeGreaterThan(0);

  mgrCtx = await browser.newContext({ acceptDownloads: true });
  mgr = await mgrCtx.newPage();
  await signIn(mgr, manager.email, pw);
  deskCtx = await browser.newContext(PHONE);
  desk = await deskCtx.newPage();
  await signIn(desk, reception.email, pw);
  drvCtx = await browser.newContext(PHONE);
  drv = await drvCtx.newPage();
  await signIn(drv, driver.email, pw);
});

test.afterAll(async () => {
  await drvCtx?.close();
  await deskCtx?.close();
  await mgrCtx?.close();
});

test('S21-01 the manager adds a van and puts a trip on with the driver', async () => {
  await mgr.goto('/transport');
  await mgr.getByRole('radio', { name: 'Vehicles and routes' }).click();
  await expect(mgr.getByTestId('routes')).toContainText(routeName);
  await mgr.getByRole('button', { name: 'New vehicle' }).click();
  const v = await dialog(mgr);
  await v.getByLabel('Code').fill(vehicleCode);
  await v.getByLabel('Name', { exact: true }).fill(vehicleName);
  await v.getByLabel('Registration number').fill('MH');
  await v.getByLabel('Passenger seats').fill('12');
  await v.getByRole('button', { name: 'Save' }).click();
  // A wrong registration is refused under its field with the reason.
  await expect(v.locator('mat-error', { hasText: 'registration number is 4–12' })).toBeVisible();
  await v.getByLabel('Registration number').fill(`MH 07 ${runId.slice(0, 2)} ${(Date.now() % 9000) + 1000}`.toUpperCase());
  await v.getByRole('button', { name: 'Save' }).click();
  await said(mgr, `${vehicleName} saved`);
  await expect(mgr.getByTestId('vehicles')).toContainText(vehicleName);

  await mgr.getByRole('radio', { name: 'Trips', exact: true }).click();
  await toTripDay(mgr);
  await mgr.getByRole('button', { name: 'Put trips on' }).click();
  const t = await dialog(mgr);
  await t.getByRole('combobox', { name: 'Route' }).click();
  await mgr.getByRole('option', { name: routeName }).click();
  await t.getByRole('combobox', { name: 'Vehicle' }).click();
  await mgr.getByRole('option', { name: vehicleName }).click();
  await t.getByRole('combobox', { name: 'Driver' }).click();
  await mgr.getByRole('option', { name: driver.name }).click();
  await t.getByLabel('From').fill(tripDay);
  await t.getByLabel('To').fill(tripDay);
  await t.getByLabel('Departure times').fill('23:30');
  await t.getByRole('button', { name: 'Put on' }).click();
  await said(mgr, /1 trip put on/);
  await expect(trip(mgr)).toContainText('12 left');
  await expect(trip(mgr)).toContainText(driver.name);
  await shot(mgr, '01-trip-put-on');
});

test('S21-02 the receptionist books 3 seats on her phone and ₹1,500 goes on the guest\'s bill', async () => {
  await desk.goto('/transport');
  await toTripDay(desk);
  const t = trip(desk);
  await t.getByRole('button').first().click();
  await t.getByRole('button', { name: 'Book', exact: true }).click();
  const d = await dialog(desk);
  const stay = stays[0];
  await d.getByRole('combobox', { name: 'Guest staying' }).click();
  await desk.getByRole('option', { name: stay.reference }).click();
  await d.getByLabel('Seats').fill('3');
  await d.getByLabel('Note for the driver (optional)').fill('Pick up at the cottages');
  await expect(d.getByTestId('trip-total')).toContainText('₹1,500');
  await shot(desk, '02-book-on-phone');
  await noSideScroll(desk);
  await d.getByRole('button', { name: 'Book', exact: true }).click();
  await said(desk, /bill/i);
  await expect(t).toContainText('9 left');
  const p = t.locator('li[data-passenger]').first();
  await expect(p).toContainText('3 seats');
  await expect(p).toContainText(`₹1,500 on ${stay.reference}'s bill`);
  passengerCode = (await p.getAttribute('data-passenger'))!;

  // On the guest's bill: one TRANSPORT line of 3 × ₹500 with the passenger's number.
  const seatsBooked = await (await api.get(`/api/v1/properties/${pid}/transport/bookings?reservationId=${stay.reservationId}`)).json();
  const mine = seatsBooked.find((x: { code: string }) => x.code === passengerCode);
  expect(mine.settlement).toBe('ROOM_CHARGE');
  expect(mine.amount).toBe(1500);
  expect(mine.folioNumber).toBeTruthy();
  // The receptionist can't tick boarding or mark the trip as left.
  await expect(t.getByRole('button', { name: 'On board' })).toHaveCount(0);
  await expect(t.getByRole('button', { name: 'Left', exact: true })).toHaveCount(0);
});

test('S21-03 the manager downloads the passenger manifest', async () => {
  await mgr.goto('/transport');
  await toTripDay(mgr);
  const t = trip(mgr);
  await t.getByRole('button').first().click();
  await expect(t.locator(`li[data-passenger="${passengerCode}"]`)).toBeVisible();
  const download = mgr.waitForEvent('download');
  await t.getByRole('button', { name: 'Manifest' }).click();
  const file = await download;
  expect(file.suggestedFilename()).toBe(`manifest-${routeCode}-${tripDay.replace(/-/g, '')}-2330.pdf`);
  await shot(mgr, '03-trip-with-passengers');
});

test('S21-04 the driver sees the trip and its passengers on his phone, ticks them as they board and leaves', async () => {
  await drv.goto('/transport');
  // A driver starts on his own trips.
  await expect(drv.getByRole('radio', { name: 'My trips' })).toBeChecked();
  await toTripDay(drv);
  const card = drv.locator(`section[data-my-trip="${routeCode}-23:30"]`);
  await expect(card).toContainText(routeName);
  const p = card.locator(`li[data-passenger="${passengerCode}"]`);
  await expect(p).toContainText('Pick up at the cottages');
  await expect(card.getByTestId('on-board')).toContainText('0/3');
  await shot(drv, '04-driver-manifest');
  await noSideScroll(drv);

  await p.getByRole('button', { name: /^Tick / }).click();
  await said(drv, 'is on board');
  await expect(card.getByTestId('on-board')).toContainText('3/3');
  if (!tonight) return;   // the trip is tomorrow: it can't leave today
  await card.getByRole('button', { name: 'Left', exact: true }).click();
  await said(drv, /left with 3 on board/);
  await expect(card).toContainText('On the road');
  await expect(card.getByRole('button', { name: 'Arrived' })).toBeVisible();
  await shot(drv, '05-driver-left');

  // The desk sees it on the road, with no "Book" any more.
  await desk.reload();
  const t = trip(desk);
  await expect(t).toContainText('3 on board');
  await t.getByRole('button').first().click();
  await expect(t.getByRole('button', { name: 'Book', exact: true })).toHaveCount(0);
});
