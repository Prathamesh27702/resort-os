import { APIRequestContext, Browser, Page, expect, test } from '@playwright/test';
import { addDays, apiAs, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 13 — the same checks as docs/sprints/REVIEWING.md → "Sprint 13", automated, on a phone-sized screen (360 px).
// The demo: a guest checks out and their room shows up on the housekeeper's phone by itself. Two taps (Start, Done) and it
// waits for the supervisor, who sends it back once ("bathroom mirror smeared"), then marks it ready. Later the housekeeper
// finds the AC broken, reports it with a photo and takes the room out of order; maintenance fixes it, and the room goes
// back to cleaning before it can be sold again.

test.describe.configure({ mode: 'serial' });

const PHONE = { viewport: { width: 360, height: 780 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 };
let hk: Page;         // the housekeeper's phone
let sup: Page;        // the supervisor's phone
let mt: Page;         // maintenance, on a phone
let m: Page;          // the manager (the owner here), on a desktop
let api: APIRequestContext;
let pid = '';
let today = '';
let unitId = '';
let place = '';
let ticketId = '';
const pw = `Staff-${runId}-2026`;
const housekeeper = { email: `hk13.${runId}@demo.local`, name: `Hema Housekeeper ${runId}` };
const supervisor = { email: `sup13.${runId}@demo.local`, name: `Sunil Supervisor ${runId}` };
const fixer = { email: `mt13.${runId}@demo.local`, name: `Manoj Maintenance ${runId}` };

// A 2×2 red PNG: the phone shrinks it to a JPEG before sending, like a camera photo.
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP4z8DAwMDAxMDAwMDAAAANHQEDasKb6QAAAABJRU5ErkJggg==', 'base64');

const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text }).last()).toBeVisible();
const card = (page: Page) => page.locator(`article[data-room="${place}"]`);
const roomState = async () => {
  const board = await (await api.get(`/api/v1/properties/${pid}/rooms/status`)).json();
  return (board.rooms as { unitId: string; housekeeping: string; openRepairs: number }[]).find(r => r.unitId === unitId)!;
};

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  const prop = (await (await api.get('/api/v1/properties')).json())[0];
  pid = prop.id;
  today = prop.localDate as string;
  await createStaff(api, housekeeper.email, housekeeper.name, 'HOUSEKEEPER', pid, pw);
  await createStaff(api, supervisor.email, supervisor.name, 'HOUSEKEEPING_SUPERVISOR', pid, pw);
  await createStaff(api, fixer.email, fixer.name, 'MAINTENANCE', pid, pw);

  // A guest stays one night from today, pays, and is checked out by the front desk (Sprint 7, not what this test is about).
  // S13-05 takes this room out of order for tonight only, so it needs to be free tonight and nothing more (earlier sprints'
  // tests book most rooms on later nights).
  const avail = await (await api.get(`/api/v1/properties/${pid}/availability?from=${today}&to=${addDays(today, 1)}&adults=2&children=0`)).json();
  const type = (avail.types as { inventoryMode: string; accommodationTypeId: string; freeRooms: { id: string }[] }[])
    .find(t => t.inventoryMode === 'UNIT' && t.freeRooms.length >= 1)!;
  unitId = type.freeRooms[type.freeRooms.length - 1].id;
  const source = (await (await api.get(`/api/v1/properties/${pid}/reservation-sources`)).json()).find((s: { code: string }) => s.code === 'PHONE').id;
  const guest = (await (await api.get('/api/v1/guests?pageSize=9')).json()).items[8];
  const res = await api.post(`/api/v1/properties/${pid}/reservations`, {
    headers: { 'Idempotency-Key': `e2e-s13-stay-${runId}` },
    data: { checkIn: today, checkOut: addDays(today, 1), rooms: [{ accommodationTypeId: type.accommodationTypeId, unitId, adults: 2, children: 0 }],
            guestIds: [guest.id], sourceId: source, ratePlanId: null, status: 'CONFIRMED', specialRequests: null, note: null },
  });
  expect(res.status(), await res.text()).toBe(201);
  const bookingId = (await res.json()).id;
  const inn = await api.post(`/api/v1/properties/${pid}/reservations/${bookingId}/check-in`,
    { data: { version: null, overrideHousekeeping: true, overrideReason: 'Browser test: room checked by hand' } });
  expect(inn.status(), await inn.text()).toBe(200);
  const folio = (await (await api.get(`/api/v1/properties/${pid}/reservations/${bookingId}/folios`)).json()).find((f: { isPrimary: boolean }) => f.isPrimary);
  const pay = await api.post(`/api/v1/properties/${pid}/payments`, {
    headers: { 'Idempotency-Key': `e2e-s13-pay-${runId}` },
    data: { method: 'UPI', amount: folio.balance, folioId: folio.id, reservationId: null, paymentReference: `UPI13${runId}`, note: null },
  });
  expect(pay.status(), await pay.text()).toBe(201);
  const out = await api.post(`/api/v1/properties/${pid}/reservations/${bookingId}/check-out`, { data: { version: null } });
  expect(out.status(), await out.text()).toBe(200);

  const board = await (await api.get(`/api/v1/properties/${pid}/housekeeping/board`)).json();
  place = (board.rooms as { unitId: string; bedId: string | null; place: string }[]).find(r => r.unitId === unitId && !r.bedId)!.place;

  hk = await (await browser.newContext(PHONE)).newPage();
  await signIn(hk, housekeeper.email, pw);
  sup = await (await browser.newContext(PHONE)).newPage();
  await signIn(sup, supervisor.email, pw);
  mt = await (await browser.newContext(PHONE)).newPage();
  await signIn(mt, fixer.email, pw);
  m = await (await browser.newContext()).newPage();
  await signIn(m, owner.email, owner.password);
});

test('S13-01 A check-out puts the room on the housekeeper\'s phone, dirty, with one big Start button', async () => {
  await hk.goto('/housekeeping');
  await expect(hk.getByRole('heading', { name: 'Housekeeping' })).toBeVisible();
  await expect(card(hk)).toBeVisible();
  await expect(card(hk).locator('.state')).toHaveText('Dirty');
  await expect(card(hk)).toContainText('Departure clean');
  await expect(card(hk).getByRole('button', { name: 'Start' })).toBeVisible();
  // Built for a phone: nothing scrolls sideways.
  expect(await hk.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);
  await shot(hk, '01-housekeeper-board');
});

test('S13-02 Two taps: Start, then Done; the room waits for the supervisor to inspect it', async () => {
  await card(hk).getByRole('button', { name: 'Start' }).click();
  await said(hk, `${place}: cleaning started.`);
  await expect(card(hk).locator('.state')).toHaveText('Being cleaned');
  await expect(card(hk)).toContainText(`by ${housekeeper.name}`);
  await card(hk).getByRole('button', { name: 'Done' }).click();
  await said(hk, `${place} cleaned — waiting for inspection.`);
  await expect(card(hk).locator('.state')).toHaveText('To inspect');
  expect((await roomState()).housekeeping).toBe('CLEAN');
  await shot(hk, '02-cleaned');
});

test('S13-03 The supervisor sends it back with what to redo; the reason is said under the field when missing', async () => {
  await sup.goto('/housekeeping');
  await sup.getByRole('tab', { name: /To inspect/ }).click();
  await card(sup).locator('button.body').click();
  const sheet = sup.getByRole('dialog');
  await expect(sheet.getByRole('checkbox')).toHaveCount(7);
  await sheet.getByRole('button', { name: 'Send back' }).click();
  await expect(sheet.locator('mat-form-field', { hasText: 'What needs redoing' }).locator('mat-error'))
    .toHaveText('Say what needs redoing (at least 3 characters), e.g. Bathroom mirror smeared.');
  await shot(sup, '03-send-back-error');
  await sheet.getByLabel(/What needs redoing/).fill('Bathroom mirror smeared');
  await sheet.getByRole('button', { name: 'Send back' }).click();
  await expect(sheet).toBeHidden();

  await hk.reload();
  await expect(card(hk).locator('.state')).toHaveText('Dirty');
  await expect(card(hk)).toContainText('Sent back: Bathroom mirror smeared');
  await shot(hk, '04-sent-back');
});

test('S13-04 Cleaned again and marked ready: the room is ready to sell on the room status board', async () => {
  await card(hk).getByRole('button', { name: 'Start' }).click();
  await said(hk, 'cleaning started');
  await card(hk).getByRole('button', { name: 'Done' }).click();
  await said(hk, 'waiting for inspection');

  await sup.reload();
  await sup.getByRole('tab', { name: /To inspect/ }).click();
  await card(sup).getByRole('button', { name: 'Mark ready' }).click();
  await said(sup, `${place} is ready for the next guest.`);
  expect((await roomState()).housekeeping).toBe('READY');

  await m.goto('/rooms/status');
  const tile = m.locator(`a.tile[href="/rooms/${unitId}"]`);
  await expect(tile.locator('.hk')).toHaveText('Ready');
  await shot(m, '05-room-status-ready');
});

test('S13-05 The housekeeper reports a broken AC with a photo and takes the room out of order', async () => {
  await hk.reload();
  await hk.getByRole('tab', { name: /All/ }).click();
  await card(hk).locator('button.body').click();
  await hk.getByRole('dialog').getByRole('button', { name: 'Report a problem' }).click();
  await expect(hk).toHaveURL(/\/maintenance\/new\?unitId=/);
  await expect(hk.getByRole('heading', { name: 'Report a problem' })).toBeVisible();

  // Nothing typed: the reason shows under the field.
  await hk.getByRole('button', { name: 'Send report' }).click();
  await expect(hk.locator('mat-form-field', { hasText: 'What is wrong' }).locator('mat-error'))
    .toHaveText('Say what is wrong in 3–120 characters, e.g. AC not cooling.');

  await hk.getByLabel('What is wrong').fill('AC not cooling');
  await hk.locator('mat-select[formcontrolname=category]').click();
  await hk.getByRole('option', { name: 'AC' }).click();
  await hk.locator('mat-select[formcontrolname=priority]').click();
  await hk.getByRole('option', { name: 'High' }).click();
  await hk.getByRole('switch', { name: /out of order/ }).click();
  await expect(hk.getByLabel('Usable again on')).toHaveValue(addDays(today, 2));
  await hk.getByLabel('Usable again on').fill(addDays(today, 1));
  await hk.getByTestId('photo-input').setInputFiles({ name: 'ac.png', mimeType: 'image/png', buffer: PNG });
  await expect(hk.locator('figure img')).toHaveCount(1);
  await shot(hk, '06-report-problem');
  await hk.getByRole('button', { name: 'Send report' }).click();

  await expect(hk).toHaveURL(/\/maintenance\/[0-9a-f-]{36}$/);
  ticketId = hk.url().split('/').pop()!;
  await said(hk, /M\d{5,} reported\./);
  await expect(hk.getByRole('heading', { level: 1 })).toContainText('AC not cooling');
  await expect(hk.locator('dd', { hasText: 'Out of order' })).toBeVisible();
  await expect(hk.locator('.photos img')).toHaveCount(1);
  const st = await roomState();
  expect(st.housekeeping).toBe('OUT_OF_ORDER');
  expect(st.openRepairs).toBe(1);
  await shot(hk, '07-ticket-reported');
});

test('S13-06 Maintenance works it and resolves it; the room goes back to cleaning, not straight to sale', async () => {
  await mt.goto('/maintenance');
  const row = mt.locator('a.row', { hasText: 'AC not cooling' }).first();
  await expect(row).toContainText('High');
  await expect(row).toContainText('Out of order');
  await shot(mt, '08-maintenance-list');
  await row.click();
  await expect(mt).toHaveURL(new RegExp(`/maintenance/${ticketId}$`));

  await mt.getByRole('button', { name: 'Start work' }).click();
  await expect(mt.locator('mat-form-field', { hasText: 'Note' }).locator('mat-hint')).toHaveText('Say what was done (at least 3 characters).');
  await mt.getByLabel('Note').fill('Gas leak found at the outdoor unit');
  await mt.getByRole('button', { name: 'Start work' }).click();
  await said(mt, 'Update added.');
  await expect(mt.locator('.st')).toHaveText('In progress');

  await mt.getByLabel('Note').fill('Leak brazed and gas refilled; cooling to 18 °C');
  await mt.getByRole('button', { name: 'Resolved' }).click();
  await said(mt, 'Resolved.');
  await expect(mt.locator('.st')).toHaveText('Resolved');
  await expect(mt.locator('.timeline')).toContainText('Leak brazed');
  await shot(mt, '09-ticket-resolved');

  expect((await roomState()).housekeeping).toBe('DIRTY');
  await hk.goto('/housekeeping');
  await expect(card(hk)).toContainText('Clean after repair');
  await expect(card(hk).getByRole('button', { name: 'Start' })).toBeVisible();
});

test('S13-07 The manager checks the repair and closes the ticket', async () => {
  await m.goto(`/maintenance/${ticketId}`);
  await m.getByRole('button', { name: 'Close ticket' }).click();
  await said(m, 'Closed.');
  await expect(m.locator('.st')).toHaveText('Closed');
  // A closed ticket takes no more steps.
  await expect(m.getByRole('button', { name: 'Resolved' })).toHaveCount(0);
  await shot(m, '10-ticket-closed');
});
