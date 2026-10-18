import { APIRequestContext, Browser, Page, expect, test } from '@playwright/test';
import { addDays, apiAs, createStaff, fieldDate, owner, runId, shot, signIn } from './helpers';

// Sprint 07 — the same checks as docs/sprints/REVIEWING.md → "Sprint 07", automated.
// The e2e database starts with the demo data (database/seed/dev). From S006: R00007 (Room 105) is in house and leaves
// today, R00008 (Room 104) is in house for two more nights, and Room 101 is dirty, so today's arrival R00001 (Room 101)
// can't go straight in. Earlier sprint files book rooms tonight (Sprint 05 takes Room 102), so the "choose another room"
// check makes its own booking in whichever room is free tonight.

test.describe.configure({ mode: 'serial' });

let p: Page;          // the owner (can mark rooms ready)
let q: Page;          // a receptionist (checks guests in and out)
let api: APIRequestContext;
let pid = '';
let today = '';
const staffPassword = `Staff-${runId}-2026`;
const reception = { email: `reception7.${runId}@demo.local`, name: `Rita Reception ${runId}` };
const own = { ref: '', room: '' };   // this file's own arrival, in a room that isn't ready

interface FreeType { inventoryMode: string; accommodationTypeId: string; freeRooms: { id: string; name: string }[] }

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  const prop = (await (await api.get('/api/v1/properties')).json())[0];
  pid = prop.id;
  today = prop.localDate as string;
  await createStaff(api, reception.email, reception.name, 'RECEPTIONIST', pid, staffPassword);

  p = await (await browser.newContext()).newPage();
  q = await (await browser.newContext()).newPage();
  await signIn(p, owner.email, owner.password);
  await signIn(q, reception.email, staffPassword);
});

const line = (page: Page, ref: string) => page.locator(`tr[data-ref="${ref}"]`);
const tile = (page: Page, text: string) => page.locator('.tile', { hasText: text });
/** The snack bar with this message (the front desk's messages carry an "Open" button, and an earlier one may linger). */
const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text })).toBeVisible();
const board = (page: Page, place: string) => page.locator(`.room[data-room="${place}"]`);

test('S07-01 The front desk opens on today’s arrivals and says why a room can’t be used yet', async () => {
  await q.goto('/dashboard');
  await q.locator('nav a[href="/front-desk"]').click();
  await expect(q).toHaveURL(/\/front-desk\/arrivals/);
  await expect(q.getByRole('heading', { name: 'Front desk' })).toBeVisible();
  await expect(tile(q, 'Arrivals checked in')).toBeVisible();
  await expect(tile(q, 'In house')).toContainText(/^\s*\d+/);
  const r1 = line(q, 'R00001');
  await expect(r1).toContainText('Room 101');
  await expect(r1.locator('.chip.hk')).toHaveText('Dirty');
  await expect(r1.getByRole('note')).toContainText('Room 101 is dirty');
  await shot(q, '01-arrivals');
});

test('S07-02 A room that isn’t ready: the receptionist checks the guest in anyway, with a reason', async () => {
  await line(q, 'R00001').getByRole('button', { name: /^Check in / }).click();
  const dlg = q.locator('mat-dialog-container');
  await expect(dlg.getByRole('alert')).toContainText('Room 101 is dirty, not ready.');
  await shot(q, '02-not-ready');
  // no reason: the field says what is missing
  await dlg.getByRole('button', { name: 'Check in anyway' }).click();
  await expect(dlg.locator('mat-error')).toContainText('Say why the guest goes in anyway');
  await dlg.getByLabel('Reason to check in anyway').fill('Guest agreed to wait while it is cleaned');
  await dlg.getByRole('button', { name: 'Check in anyway' }).click();
  await said(q, 'Checked in: R00001, Room 101');
  await expect(line(q, 'R00001')).toContainText('Checked in');
});

test('S07-03 Departures: check out today’s guest; the room is marked dirty, then ready again', async () => {
  await q.locator('.tabs a', { hasText: 'Departures' }).click();
  await expect(q).toHaveURL(/\/front-desk\/departures/);
  await line(q, 'R00007').getByRole('button', { name: /^Check out / }).click();
  const dlg = q.locator('mat-dialog-container');
  await expect(dlg).toContainText('Room 105');
  await expect(dlg).toContainText('Nothing owed');
  await shot(q, '03-check-out');
  await dlg.getByRole('button', { name: 'Check out' }).click();
  await said(q, 'Checked out: R00007');
  await expect(line(q, 'R00007')).toContainText('Left');

  await p.goto('/front-desk/rooms');
  await expect(board(p, 'Room 105').locator('.chip.hk')).toHaveText('Dirty');
  await expect(board(p, 'Room 101')).toContainText('R00001');
  await shot(p, '04-rooms-board');
  await board(p, 'Room 105').getByRole('button', { name: 'Mark ready' }).click();
  await said(p, 'Room 105: Ready');
  await expect(board(p, 'Room 105').locator('.chip.hk')).toHaveText('Ready');
});

test('S07-04 Choose another room: the guest is moved to a ready room and checked in in one go', async () => {
  // a one-night booking tonight in a free room, which housekeeping hasn't finished yet
  const avail = await (await api.get(`/api/v1/properties/${pid}/availability?from=${today}&to=${addDays(today, 1)}&adults=1&children=0`)).json();
  const type = (avail.types as FreeType[]).find(t => t.inventoryMode === 'UNIT' && t.freeRooms.length >= 2)
            ?? (avail.types as FreeType[]).find(t => t.inventoryMode === 'UNIT' && t.freeRooms.length >= 1)!;
  const room = type.freeRooms[0];
  const source = (await (await api.get(`/api/v1/properties/${pid}/reservation-sources`)).json()).find((s: { code: string }) => s.code === 'PHONE').id;
  const guest = (await (await api.get('/api/v1/guests?pageSize=5')).json()).items[3];
  const res = await api.post(`/api/v1/properties/${pid}/reservations`, {
    headers: { 'Idempotency-Key': `e2e-s07-own-${runId}` },
    data: { checkIn: today, checkOut: addDays(today, 1), rooms: [{ accommodationTypeId: type.accommodationTypeId, unitId: room.id, adults: 1, children: 0 }],
            guestIds: [guest.id], sourceId: source, ratePlanId: null, status: 'CONFIRMED', specialRequests: null, note: null },
  });
  expect(res.status(), await res.text()).toBe(201);
  own.ref = (await res.json()).reference;
  own.room = room.name;
  const dirty = await api.put(`/api/v1/properties/${pid}/rooms/${room.id}/housekeeping`, { data: { bedId: null, status: 'CLEANING', reason: null } });
  expect(dirty.status(), await dirty.text()).toBe(200);

  await q.goto('/front-desk/arrivals');
  await line(q, own.ref).getByRole('button', { name: /^Check in / }).click();
  const problem = q.locator('mat-dialog-container');
  await expect(problem.getByRole('alert')).toContainText(`${own.room} is being cleaned, not ready.`);
  await problem.getByRole('button', { name: 'Choose another room' }).click();

  const dlg = q.locator('mat-dialog-container', { hasText: 'Assign a room for' });
  await dlg.locator('mat-select').click();
  const target = q.locator('mat-option:not(.mdc-list-item--disabled)', { hasText: '· Ready' }).first();
  const targetName = ((await target.textContent()) ?? '').split('·')[0].trim();
  await target.click();
  await shot(q, '05-choose-room');
  await dlg.getByRole('button', { name: 'Assign room' }).click();
  await said(q, `Checked in: ${own.ref}, ${targetName}`);
});

test('S07-05 Walk-in: a guest with no booking is booked from today and checked in in one go', async () => {
  await q.goto('/front-desk/arrivals');
  await q.getByRole('link', { name: 'Walk-in' }).click();
  await expect(q).toHaveURL(/\/front-desk\/walk-in/);
  // leaving today is refused, with the reason under the field
  await q.getByLabel('Leaving on').fill('');
  await q.getByLabel('Leaving on').fill(fieldDate(today));
  await q.getByRole('button', { name: 'Find a room' }).click();
  await expect(q.locator('mat-form-field', { hasText: 'Leaving on' }).locator('mat-error')).toContainText('Leaving must be after today');
  await q.getByLabel('Leaving on').fill('');
  await q.getByLabel('Leaving on').fill(fieldDate(addDays(today, 1)));
  await q.getByRole('button', { name: 'Find a room' }).click();
  await q.getByRole('button', { name: 'Choose Dormitory bed' }).click();

  await q.getByLabel('First name').fill(`Walkin${runId}`);
  await q.getByLabel('Last name').fill('Traveller');
  await q.getByLabel('Phone', { exact: true }).fill(`9${String(Date.now()).slice(-9)}`);
  await q.getByRole('button', { name: 'Use this new guest' }).click();
  await expect(q.locator('dl.summary')).toContainText(`Walkin${runId} Traveller`);
  await shot(q, '06-walk-in');
  await q.getByRole('button', { name: 'Check in', exact: true }).click();
  await said(q, /Checked in: R\d{5} · Dormitory bed B\d\d/);
  await expect(q).toHaveURL(/\/reservations\/[0-9a-f-]{36}/);
  await expect(q.locator('h1 .chip')).toHaveText('Checked in');
  await expect(q.getByText('Walk-in', { exact: true })).toBeVisible();
  await shot(q, '07-walk-in-booking');
});

test('S07-06 In house: leaving early needs a reason, and the nights left are released', async () => {
  await q.goto('/front-desk/in-house');
  await expect(line(q, 'R00008')).toContainText('Room 104');
  const link = await line(q, 'R00008').locator('a').first().getAttribute('href');
  await line(q, 'R00008').getByRole('button', { name: 'Check out' }).click();
  const dlg = q.locator('mat-dialog-container');
  await expect(dlg).toContainText('The nights left are released');
  await dlg.getByRole('button', { name: 'Check out' }).click();
  await expect(dlg.locator('mat-error')).toContainText('Say why the guest is leaving early');
  await dlg.getByLabel('Why are they leaving early?').fill('Family emergency');
  await dlg.getByRole('button', { name: 'Check out' }).click();
  await said(q, 'Checked out: R00008');

  await q.goto(link!);
  await expect(q.getByText(/Left early on .* Reason: Family emergency/)).toBeVisible();
  await expect(q.locator('h1 .chip')).toHaveText('Checked out');
  await shot(q, '08-left-early');
});

test('S07-07 A booking can be checked in from its own page too', async () => {
  // R00004 (dormitory, tonight) is still waiting
  await q.goto('/reservations');
  await q.getByLabel('Search').fill('R00004');
  await expect(q.locator('table.data tbody tr')).toHaveCount(1);
  await q.locator('table.data tbody tr', { hasText: 'R00004' }).click();
  await q.getByRole('button', { name: 'Check in' }).click();
  await said(q, 'R00004 checked in');
  await expect(q.locator('h1 .chip')).toHaveText('Checked in');
  await expect(q.locator('dl')).toContainText('Checked in');
});

test('S07-08 Staff without the permission can’t check guests in', async ({ browser }: { browser: Browser }) => {
  const hk = { email: `hk7.${runId}@demo.local`, name: `Hari Housekeeping ${runId}` };
  await createStaff(api, hk.email, hk.name, 'HOUSEKEEPER', pid, staffPassword);
  const h = await (await browser.newContext()).newPage();
  await signIn(h, hk.email, staffPassword);
  await expect(h.locator('nav a[href="/front-desk"]')).toHaveCount(0);
  const hkApi = (await apiAs(hk.email, staffPassword)).api;
  const list = await hkApi.get(`/api/v1/properties/${pid}/front-desk/arrivals`);
  expect(list.status()).toBe(403);
  const r = (await (await api.get(`/api/v1/properties/${pid}/reservations?q=R00002`)).json()).items[0];
  const res = await hkApi.post(`/api/v1/properties/${pid}/reservations/${r.id}/check-in`, { data: { version: null } });
  expect(res.status()).toBe(403);
});

test('S07-09 On a phone the lists stay readable', async () => {
  await q.setViewportSize({ width: 390, height: 844 });
  await q.goto('/front-desk/in-house');
  await expect(line(q, own.ref)).toBeVisible();
  await shot(q, '09-phone');
  await q.setViewportSize({ width: 1280, height: 860 });
});
