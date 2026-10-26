import { APIRequestContext, Browser, Page, expect, test } from '@playwright/test';
import { addDays, apiAs, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 14 — the same checks as docs/sprints/REVIEWING.md → "Sprint 14", automated. The demo: a waiter takes an order on
// a phone (360 px), the kitchen sees the ticket appear on its display by itself, starts it and marks it ready, the waiter
// serves it and charges the bill to a guest's room: ₹1,250 of food lands on the guest's folio as one line. A second waiter
// changing the same order at the same moment is refused and sees the first waiter's change.

test.describe.configure({ mode: 'serial' });

const PHONE = { viewport: { width: 360, height: 780 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 };
const TABLET = { viewport: { width: 1280, height: 800 }, hasTouch: true };
let w1: Page;         // the waiter's phone
let w2: Page;         // a second waiter's phone
let k: Page;          // the kitchen display (a tablet)
let m: Page;          // the manager (the owner here), on a desktop
let api: APIRequestContext;
let waiterApi: APIRequestContext;
let pid = '';
let table = '';
let orderId = '';
let orderNumber = '';
let kotNumber = '';
let room = { unitCode: '', unitName: '', reservationId: '', reference: '', guestName: '' };
const pw = `Staff-${runId}-2026`;
const waiter = { email: `w14.${runId}@demo.local`, name: `Wasim Waiter ${runId}` };
const waiter2 = { email: `w14b.${runId}@demo.local`, name: `Wendy Waiter ${runId}` };
const cook = { email: `k14.${runId}@demo.local`, name: `Kavya Kitchen ${runId}` };

const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text }).last()).toBeVisible();
const line = (page: Page, name: string) => page.locator(`li[data-line="${name}"]`);
const ticket = (page: Page) => page.locator(`article[data-kot="${kotNumber}"]`);
const dish = (page: Page, code: string) => page.locator(`button[data-dish="${code}"]`);
const section = (page: Page, name: string) => page.locator('.cats').getByRole('tab', { name });
const noSideScroll = async (page: Page) => expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  const prop = (await (await api.get('/api/v1/properties')).json())[0];
  pid = prop.id;
  const today = prop.localDate as string;
  await createStaff(api, waiter.email, waiter.name, 'WAITER', pid, pw);
  await createStaff(api, waiter2.email, waiter2.name, 'WAITER', pid, pw);
  await createStaff(api, cook.email, cook.name, 'KITCHEN', pid, pw);
  ({ api: waiterApi } = await apiAs(waiter.email, pw));

  // A guest in house to charge the bill to: R00008 (Room 104) in the demo data, unless an earlier sprint's test checked
  // them out; then any guest in house; if nobody is, one is booked for tonight and checked in (Sprint 7, not this test).
  let rooms = await (await api.get(`/api/v1/properties/${pid}/orders/rooms`)).json() as typeof room[];
  if (!rooms.length) {
    const avail = await (await api.get(`/api/v1/properties/${pid}/availability?from=${today}&to=${addDays(today, 1)}&adults=2&children=0`)).json();
    const type = (avail.types as { inventoryMode: string; accommodationTypeId: string; freeRooms: { id: string }[] }[])
      .find(t => t.inventoryMode === 'UNIT' && t.freeRooms.length >= 1)!;
    const source = (await (await api.get(`/api/v1/properties/${pid}/reservation-sources`)).json()).find((s: { code: string }) => s.code === 'PHONE').id;
    const guest = (await (await api.get('/api/v1/guests?pageSize=9')).json()).items[7];
    const res = await api.post(`/api/v1/properties/${pid}/reservations`, {
      headers: { 'Idempotency-Key': `e2e-s14-stay-${runId}` },
      data: { checkIn: today, checkOut: addDays(today, 1), rooms: [{ accommodationTypeId: type.accommodationTypeId, unitId: type.freeRooms[0].id, adults: 2, children: 0 }],
              guestIds: [guest.id], sourceId: source, ratePlanId: null, status: 'CONFIRMED', specialRequests: null, note: null },
    });
    expect(res.status(), await res.text()).toBe(201);
    const inn = await api.post(`/api/v1/properties/${pid}/reservations/${(await res.json()).id}/check-in`,
      { data: { version: null, overrideHousekeeping: true, overrideReason: 'Browser test: room checked by hand' } });
    expect(inn.status(), await inn.text()).toBe(200);
    rooms = await (await api.get(`/api/v1/properties/${pid}/orders/rooms`)).json();
  }
  room = rooms.find(r => r.reference === 'R00008') ?? rooms[0];

  // A free table, T4 when it is free.
  const floor = await (await api.get(`/api/v1/properties/${pid}/orders/floor`)).json();
  const free = (floor.tables as { code: string; order: unknown }[]).filter(t => !t.order);
  table = (free.find(t => t.code === 'T4') ?? free[0]).code;

  w1 = await (await browser.newContext(PHONE)).newPage();
  await signIn(w1, waiter.email, pw);
  w2 = await (await browser.newContext(PHONE)).newPage();
  await signIn(w2, waiter2.email, pw);
  k = await (await browser.newContext(TABLET)).newPage();
  await signIn(k, cook.email, pw);
  m = await (await browser.newContext()).newPage();
  await signIn(m, owner.email, owner.password);
});

test('S14-01 The waiter\'s phone shows the floor: free tables, and the demo order already in the kitchen', async () => {
  await w1.goto('/restaurant');
  await expect(w1.getByRole('heading', { name: 'Restaurant' })).toBeVisible();
  await expect(w1.locator(`button[data-table="${table}"]`)).toContainText('Free');
  // The demo data's O00001 at T2 is with the kitchen.
  await expect(w1.locator('button[data-table="T2"]')).toContainText('O00001');
  // A waiter has no menu editor and no kitchen display buttons to start or finish tickets.
  await expect(w1.getByRole('link', { name: 'Menu' })).toHaveCount(0);
  await noSideScroll(w1);
  await shot(w1, '01-floor');
});

test('S14-02 Tap a free table, add dishes; a dish with portions says what to choose first', async () => {
  await w1.locator(`button[data-table="${table}"]`).click();
  await expect(w1).toHaveURL(/\/restaurant\/orders\/[0-9a-f-]{36}$/);
  orderId = w1.url().split('/').pop()!;
  orderNumber = (await w1.locator('h1').textContent())!.trim().split(' ')[0];
  expect(orderNumber).toMatch(/^O\d{5}$/);
  await expect(w1.locator('h1')).toContainText(`Table ${table}`);

  // Paneer Tikka has Half / Full and a spice level: Add without choosing says so, in words.
  await section(w1, 'Starters').click();
  await dish(w1, 'PANEER-TIKKA').click();
  const d = w1.getByRole('dialog');
  await d.getByRole('button', { name: /^Add/ }).click();
  await expect(d.getByRole('alert')).toHaveText('Choose the portion: Half or Full.');
  await d.getByRole('radio', { name: /Full/ }).check();
  await d.getByRole('button', { name: /^Add/ }).click();
  await expect(d.getByRole('alert')).toHaveText('Choose the spice level.');
  await d.getByRole('checkbox', { name: /Less spicy/ }).check();
  await d.getByRole('button', { name: 'One more' }).click();
  await expect(d.getByRole('button', { name: /^Add/ })).toHaveText(/₹560/);
  await shot(w1, '02-choose-portion');
  await d.getByRole('button', { name: /^Add/ }).click();
  await said(w1, '2× Paneer Tikka added.');

  await section(w1, 'Mains').click();
  await dish(w1, 'DAL-MAKHANI').click();
  await said(w1, '1× Dal Makhani added.');
  await dish(w1, 'VEG-BIRYANI').click();
  await said(w1, '1× Veg Biryani added.');
  await section(w1, 'Breads and rice').click();
  await dish(w1, 'BUTTER-NAAN').click();
  await said(w1, '1× Butter Naan added.');
  await expect(line(w1, 'Paneer Tikka')).toContainText('Less spicy');
  await expect(line(w1, 'Butter Naan')).toContainText('Not sent yet');
  await noSideScroll(w1);
});

test('S14-03 Two waiters change the same order at once: the second is refused (409) and sees the first one\'s change', async () => {
  await w2.goto(`/restaurant/orders/${orderId}`);
  await expect(line(w2, 'Butter Naan')).toContainText('1×');
  // The first waiter adds a second naan …
  await line(w1, 'Butter Naan').getByRole('button', { name: 'One more Butter Naan' }).click();
  await expect(line(w1, 'Butter Naan')).toContainText('2×');
  // … while the second waiter, still looking at one naan, taps + too.
  await line(w2, 'Butter Naan').getByRole('button', { name: 'One more Butter Naan' }).click();
  await said(w2, `Someone else changed ${orderNumber} a moment ago (another waiter, or the kitchen). Reload it to see their change, then make yours again.`);
  await expect(line(w2, 'Butter Naan')).toContainText('2×');   // reloaded: two naan, not three
  await shot(w2, '03-second-waiter-refused');
  // Food before GST: 2 × 280 + 240 + 330 + 2 × 60 = ₹1,250.
  await w1.reload();
  await expect(w1.locator('.bill dd').first()).toHaveText('₹1,250.00');
});

test('S14-04 Send to kitchen: the ticket appears on the kitchen display by itself (10 s refresh)', async () => {
  await k.goto('/restaurant/kitchen');
  await expect(k.getByRole('heading', { name: 'Kitchen' })).toBeVisible();
  await expect(k.getByText('Refreshes every 10 seconds')).toBeVisible();

  await w1.getByTestId('send').click();
  await said(w1, 'Sent to the kitchen.');
  kotNumber = (await w1.locator('li[data-kot]').last().getAttribute('data-kot'))!;
  expect(kotNumber).toMatch(/^K\d{5}$/);
  await expect(line(w1, 'Butter Naan')).toContainText(`${kotNumber} · New`);
  // Nothing can be settled while food is in the kitchen.
  await expect(w1.getByTestId('charge')).toHaveCount(0);
  await expect(w1.getByText('The bill can be settled once every kitchen ticket is served.')).toBeVisible();

  // The kitchen display was not reloaded: the ticket arrives with its next refresh.
  await expect(ticket(k)).toBeVisible({ timeout: 15_000 });
  await expect(k.locator('section.col.NEW').locator(`article[data-kot="${kotNumber}"]`)).toBeVisible();
  await expect(ticket(k)).toContainText(`Table ${table}`);
  await expect(ticket(k)).toContainText('2× Paneer Tikka (Full)');
  await expect(ticket(k)).toContainText('Less spicy');
  await expect(ticket(k)).toContainText('2× Butter Naan');
  await shot(k, '04-kitchen-display');
});

test('S14-05 The kitchen starts the ticket and marks it ready; the waiter\'s phone shows Ready by itself', async () => {
  await ticket(k).getByRole('button', { name: 'Start', exact: true }).click();
  await said(k, `${kotNumber} started.`);
  await expect(k.locator('section.col.PREPARING').locator(`article[data-kot="${kotNumber}"]`)).toBeVisible();
  await ticket(k).getByRole('button', { name: 'Ready', exact: true }).click();
  await said(k, `${kotNumber} ready.`);
  await expect(k.locator('section.col.READY').locator(`article[data-kot="${kotNumber}"]`)).toBeVisible();
  await shot(k, '05-kitchen-ready');

  await expect(w1.locator(`li[data-kot="${kotNumber}"]`)).toContainText('Ready', { timeout: 15_000 });
  await w1.locator(`li[data-kot="${kotNumber}"]`).getByRole('button', { name: 'Served' }).click();
  await said(w1, `${kotNumber} served.`);
  await expect(w1.locator(`li[data-kot="${kotNumber}"]`)).toContainText('Served');
});

test('S14-06 A waiter can\'t give a discount or void: no buttons, and the API refuses', async () => {
  await expect(w1.getByRole('button', { name: 'Discount' })).toHaveCount(0);
  await expect(w1.getByRole('button', { name: 'Void order' })).toHaveCount(0);
  await expect(w1.getByRole('button', { name: /^Void / })).toHaveCount(0);
  const order = await (await waiterApi.get(`/api/v1/properties/${pid}/orders/${orderId}`)).json();
  const res = await waiterApi.post(`/api/v1/properties/${pid}/orders/${orderId}/discount`,
    { data: { kind: 'PERCENT', value: 10, reason: 'Friend of the waiter', version: order.version } });
  expect(res.status()).toBe(403);
});

test('S14-07 Charge to room: ₹1,250 of food lands on the guest\'s folio as one line, with its GST', async () => {
  await w1.getByTestId('charge').click();
  const d = w1.getByRole('dialog');
  await d.locator(`mat-radio-button[data-room="${room.unitCode}"]`).click();
  await shot(w1, '06-charge-to-room');
  await d.getByRole('button', { name: 'Charge to room' }).click();
  await said(w1, 'Charged to the room.');
  await expect(w1.getByTestId('settled')).toContainText(`Charged to ${room.unitName}`);
  await expect(w1.locator('.status')).toHaveText('Settled');
  // A settled order is final: nothing more to add, send or settle.
  await expect(w1.getByRole('heading', { name: 'Add dishes' })).toHaveCount(0);
  await expect(w1.getByTestId('charge')).toHaveCount(0);
  await noSideScroll(w1);
  await shot(w1, '07-charged');

  const order = await (await api.get(`/api/v1/properties/${pid}/orders/${orderId}`)).json();
  expect(order.settlement).toBe('ROOM_CHARGE');
  expect(order.subtotal).toBe(1250);
  const folio = await (await api.get(`/api/v1/properties/${pid}/folios/${order.folioId}`)).json();
  const items = (folio.items as { source: string; description: string; amount: number; taxAmount: number; category: string }[])
    .filter(i => i.source === 'RESTAURANT' && i.description.startsWith(`Restaurant ${orderNumber}`));
  expect(items.length).toBe(1);
  expect(items[0].amount).toBe(1250);
  expect(items[0].category).toBe('FOOD');
  expect(items[0].taxAmount).toBe(order.taxAmount);
  expect(items[0].description).toBe(`Restaurant ${orderNumber} · Table ${table}: 2× Paneer Tikka (Full), 1× Dal Makhani, 1× Veg Biryani, 2× Butter Naan`);

  // The bill shows on the folio page.
  await m.goto(`/folios/${order.folioId}`);
  await expect(m.getByText(`Restaurant ${orderNumber}`)).toBeVisible();

  const again = await waiterApi.post(`/api/v1/properties/${pid}/orders/${orderId}/items`,
    { data: { items: [{ menuItemId: order.items[0].menuItemId, variantId: null, modifierIds: [], quantity: 1, note: null }], version: order.version } });
  expect(again.status()).toBe(422);
  expect((await again.json()).message).toBe(`${orderNumber} is already settled, so it can't be changed. Open a new order for anything more.`);
});

test('S14-08 The manager marks a dish sold out; waiters see it at once', async () => {
  await m.goto('/restaurant/menu');
  await expect(m.getByRole('heading', { name: 'Menu and tables' })).toBeVisible();
  await m.getByRole('switch', { name: 'Bebinca available' }).click();
  await said(m, 'Bebinca is sold out.');
  await shot(m, '08-menu-sold-out');

  await m.goto('/restaurant');
  await m.getByRole('button', { name: 'Takeaway' }).click();
  await expect(m).toHaveURL(/\/restaurant\/orders\//);
  await section(m, 'Desserts').click();
  await expect(dish(m, 'BEBINCA')).toBeDisabled();
  await expect(dish(m, 'BEBINCA')).toContainText('Sold out');
  await dish(m, 'GULAB-JAMUN').click();
  await said(m, '1× Gulab Jamun added.');
});

test('S14-09 A manager voids an order; the reason is asked for under its field', async () => {
  const number = (await m.locator('h1').textContent())!.trim().split(' ')[0];
  await m.getByRole('button', { name: 'Void order' }).click();
  const d = m.getByRole('dialog');
  await d.getByRole('button', { name: 'Void order' }).click();
  await expect(d.locator('mat-error')).toHaveText('Say why (at least 3 characters), e.g. Guest left before ordering.');
  await d.getByLabel('Reason').fill('Guest left before ordering');
  await d.getByRole('button', { name: 'Void order' }).click();
  await said(m, `${number} voided.`);
  await expect(m.locator('.banner.off')).toContainText('Voided: Guest left before ordering');

  // Bebinca back on for the next run.
  await m.goto('/restaurant/menu');
  await m.getByRole('switch', { name: 'Bebinca available' }).click();
  await said(m, 'Bebinca is back on.');
});
