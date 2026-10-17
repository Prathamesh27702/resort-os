import { APIRequestContext, Browser, Locator, Page, expect, test } from '@playwright/test';
import { addDays, apiAs, createStaff, owner, runId, shot, signIn, snack } from './helpers';

// Sprint 06 — the same checks as docs/sprints/REVIEWING.md → "Sprint 06", automated.
// The e2e database starts with the demo data (database/seed/dev): bookings R00001–R00004 and, from S005, Room 106
// blocked for maintenance from tomorrow for two nights ("Plumbing repair").
// Earlier sprint files book rooms for the next few days, so this file makes its own bookings two weeks ahead.

test.describe.configure({ mode: 'serial' });

let p: Page;          // the owner
let q: Page;          // a receptionist in a second browser
let api: APIRequestContext;
let pid = '';
let today = '';
let day = '';         // the first night of this file's bookings (a Monday)
const staffPassword = `Staff-${runId}-2026`;
const reception = { email: `reception6.${runId}@demo.local`, name: `Rita Reception ${runId}` };
const refs: Record<string, string> = {};

interface Room { id: string; code: string; accommodationTypeId: string }

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  const prop = (await (await api.get('/api/v1/properties')).json())[0];
  pid = prop.id;
  today = prop.localDate as string;
  // the Monday two weeks ahead, so every booking below sits inside one week view
  day = addDays(today, 14 - (new Date(today + 'T00:00:00Z').getUTCDay() + 6) % 7);
  await createStaff(api, reception.email, reception.name, 'RECEPTIONIST', pid, staffPassword);

  const rooms: Room[] = await (await api.get(`/api/v1/properties/${pid}/rooms`)).json();
  const room = (code: string) => rooms.find(r => r.code === code)!;
  const source = (await (await api.get(`/api/v1/properties/${pid}/reservation-sources`)).json()).find((s: { code: string }) => s.code === 'PHONE').id;
  const guests = (await (await api.get('/api/v1/guests?pageSize=5')).json()).items;
  const book = async (key: string, code: string, guest: number, from: string, to: string) => {
    const res = await api.post(`/api/v1/properties/${pid}/reservations`, {
      headers: { 'Idempotency-Key': `e2e-s06-${key}-${runId}` },
      data: { checkIn: from, checkOut: to, rooms: [{ accommodationTypeId: room(code).accommodationTypeId, unitId: room(code).id, adults: 2, children: 0 }],
              guestIds: [guests[guest].id], sourceId: source, ratePlanId: null, status: 'CONFIRMED', specialRequests: null, note: null },
    });
    expect(res.status(), await res.text()).toBe(201);
    refs[key] = (await res.json()).reference;
  };
  await book('move', '101', 0, day, addDays(day, 2));        // will move 101 → 102
  await book('blocked', '105', 1, day, addDays(day, 3));     // will try 105 → 104 (blocked)
  await book('desk', '103', 2, addDays(day, 2), addDays(day, 4));
  const block = await api.post(`/api/v1/properties/${pid}/blocks`, {
    data: { unitId: room('104').id, bedId: null, blockType: 'MAINTENANCE', startDate: addDays(day, 1), endDate: addDays(day, 4), reason: 'Bathroom tiles' },
  });
  expect(block.status(), await block.text()).toBe(201);

  p = await (await browser.newContext()).newPage();
  q = await (await browser.newContext()).newPage();
  await signIn(p, owner.email, owner.password);
  await signIn(q, reception.email, staffPassword);
});

const row = (page: Page, label: string) => page.locator(`.row[data-row="${label}"]`);
const bar = (page: Page, ref: string) => page.locator(`.bar[data-ref="${ref}"]`).first();

/** Drags a booking straight up or down onto another row (the calendar only moves bookings between rooms). */
async function dragTo(page: Page, from: Locator, to: Locator) {
  const a = (await from.boundingBox())!;
  const b = (await to.boundingBox())!;
  const x = a.x + Math.min(30, a.width / 2);
  await page.mouse.move(x, a.y + a.height / 2);
  await page.mouse.down();
  await page.mouse.move(x, a.y + a.height / 2 + 8, { steps: 4 });     // past the drag threshold
  await page.mouse.move(x, b.y + b.height / 2, { steps: 12 });
  await page.mouse.up();
}

test('S06-01 Calendar shows this week: bookings by room and the maintenance block', async () => {
  await p.goto('/dashboard');
  await p.locator('nav a[href="/reservations/calendar"]').click();
  await expect(p).toHaveURL(/\/reservations\/calendar/);
  await expect(p.getByRole('heading', { name: 'Calendar', exact: true })).toBeVisible();
  await expect(p.locator('.day.today')).toHaveCount(1);
  // the demo booking in Room 101 tonight
  await expect(row(p, 'Room 101').locator('.bar', { hasText: 'R00001' })).toBeVisible();
  // the dormitory group (two beds) is drawn on bed rows
  await expect(p.locator('.row[data-row^="Dormitory · "] .bar', { hasText: 'R00004' })).toHaveCount(2);
  await shot(p, '01-calendar-week');
});

test('S06-02 Month view and a booking’s details: status, source and payment', async () => {
  await p.goto(`/reservations/calendar?view=month&date=${day}`);
  await expect(p.locator('.row.head .day')).toHaveCount(new Date(Date.UTC(+day.slice(0, 4), +day.slice(5, 7), 0)).getUTCDate());
  await expect(bar(p, refs['move'])).toBeVisible();
  await shot(p, '02-calendar-month');
  await bar(p, refs['move']).click();
  const dlg = p.locator('mat-dialog-container');
  await expect(dlg).toContainText(refs['move']);
  await expect(dlg).toContainText('Confirmed');
  await expect(dlg).toContainText('Phone');
  await expect(dlg).toContainText('No payment recorded');
  await dlg.getByRole('button', { name: 'Close' }).click();
});

test('S06-03 Dragging a booking onto a blocked room is refused with the reason', async () => {
  await p.goto(`/reservations/calendar?view=week&date=${day}`);
  await dragTo(p, bar(p, refs['blocked']), row(p, 'Room 104'));
  const alert = p.locator('.banner[role=alert]');
  await expect(alert).toContainText(`Can't move ${refs['blocked']} to Room 104`);
  await expect(alert).toContainText('it is blocked (Bathroom tiles)');
  await expect(p.locator('mat-dialog-container')).toHaveCount(0);
  await expect(row(p, 'Room 105').locator(`.bar[data-ref="${refs['blocked']}"]`)).toBeVisible();   // nothing moved
  await shot(p, '03-drop-on-blocked-room');
});

test('S06-04 Reception moves a guest from 101 to 102 in two actions; the reason is required', async () => {
  await q.goto(`/reservations/calendar?view=week&date=${day}`);
  // action 1: drag
  await dragTo(q, bar(q, refs['move']), row(q, 'Room 102'));
  const dlg = q.locator('mat-dialog-container');
  await expect(dlg.locator('mat-select')).toContainText('Room 102');
  await dlg.getByRole('button', { name: 'Move' }).click();
  await expect(dlg.locator('mat-error')).toHaveText('Say why the guest is moving (at least 3 characters).');
  await shot(q, '04-move-dialog');
  // action 2: the reason, then Move
  await dlg.getByLabel('Reason').fill('AC not working in 101');
  await dlg.getByRole('button', { name: 'Move' }).click();
  expect(await snack(q)).toBe(`${refs['move']} moved to Room 102`);
  await expect(row(q, 'Room 102').locator(`.bar[data-ref="${refs['move']}"]`)).toBeVisible();
  await expect(row(q, 'Room 101').locator(`.bar[data-ref="${refs['move']}"]`)).toHaveCount(0);
  await shot(q, '05-moved');
});

test('S06-05 The booking and the audit trail show who moved it and why', async () => {
  await p.goto('/reservations');
  await p.getByLabel('Search').fill(refs['move']);
  await p.locator('table.data tbody tr', { hasText: refs['move'] }).click();
  await expect(p.locator('section', { has: p.getByRole('heading', { name: 'Rooms' }) })).toContainText('Room 102');
  await expect(p.getByText('Moved from Room 101 to Room 102')).toBeVisible();
  await expect(p.getByText('Reason: AC not working in 101')).toBeVisible();

  await p.goto('/audit');
  const entry = p.locator('table.data tbody tr', { hasText: 'Reserved room' }).filter({ hasText: 'AC not working in 101' }).first();
  await expect(entry).toContainText(reception.name);
  await shot(p, '06-audit-who-why');
});

test('S06-06 Reception can also move from the booking’s details (no drag), and a clash is explained', async () => {
  await q.goto(`/reservations/calendar?view=week&date=${day}`);
  await bar(q, refs['desk']).click();
  const dlg = q.locator('mat-dialog-container');
  await dlg.locator('mat-select').click();
  await q.getByRole('option', { name: /^Room 105/ }).click();
  await expect(dlg.getByRole('alert')).toContainText(`it is booked on ${refs['blocked']}`);
  await dlg.locator('mat-select').click();
  await q.getByRole('option', { name: /^Room 106/ }).click();
  await dlg.getByLabel('Reason').fill('Guest asked for a quieter room');
  await dlg.getByRole('button', { name: 'Move' }).click();
  expect(await snack(q)).toBe(`${refs['desk']} moved to Room 106`);
});

test('S06-07 On a phone the calendar is a list by day', async () => {
  await p.setViewportSize({ width: 390, height: 844 });
  await p.goto(`/reservations/calendar?view=week&date=${day}`);
  await expect(p.locator('app-calendar-grid')).toHaveCount(0);
  const first = p.locator('section.day').filter({ hasText: refs['blocked'] }).first();
  await expect(first).toContainText('Arrives');
  await expect(p.locator('section.day .line.blocked', { hasText: 'Bathroom tiles' }).first()).toBeVisible();
  await shot(p, '07-phone-list');
  await p.setViewportSize({ width: 1280, height: 860 });
});

test('S06-08 Staff without the permission don’t see the calendar', async ({ browser }: { browser: Browser }) => {
  const hk = { email: `hk6.${runId}@demo.local`, name: `Hari Housekeeping ${runId}` };
  await createStaff(api, hk.email, hk.name, 'HOUSEKEEPER', pid, staffPassword);
  const h = await (await browser.newContext()).newPage();
  await signIn(h, hk.email, staffPassword);
  await expect(h.locator('nav a[href="/reservations/calendar"]')).toHaveCount(0);
  const res = await (await apiAs(hk.email, staffPassword)).api.get(`/api/v1/properties/${pid}/calendar?from=${today}&to=${addDays(today, 7)}`);
  expect(res.status()).toBe(403);
});
