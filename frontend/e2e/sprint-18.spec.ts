import { APIRequestContext, Browser, BrowserContext, Page, expect, test } from '@playwright/test';
import { apiAs, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 18 — the same checks as docs/sprints/REVIEWING.md → "Sprint 18", automated: brief §74 up to approval. A new
// housekeeper clocks in on her phone at the resort (refused when she is up the road), takes a break and clocks out; a
// manager records a clock-in for someone whose phone has no signal; she submits her week; the manager approves it, and
// approves last week's timesheets after correcting Ravi's missing clock-out, then locks one for payroll.

test.describe.configure({ mode: 'serial' });

const PHONE = { viewport: { width: 360, height: 780 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 };
let phone: Page;        // Gauri, the new housekeeper, on her phone
let phoneCtx: BrowserContext;
let mgr: Page;          // the manager, on a desktop
let api: APIRequestContext;
let pid = '';
let gate = { latitude: 0, longitude: 0 };
const pw = `Staff-${runId}-2026`;
const gauri = { email: `g18.${runId}@demo.local`, name: `Gauri Gaonkar ${runId}` };
const manager = { email: `m18.${runId}@demo.local`, name: `Mira Manager ${runId}` };

const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text }).last()).toBeVisible();
const noSideScroll = async (page: Page) => expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);
/** "HH:MM" now at the property (Asia/Kolkata), plus some minutes. */
const istTime = (plusMinutes = 0) => new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Kolkata', hour: '2-digit', minute: '2-digit', hour12: false })
  .format(new Date(Date.now() + plusMinutes * 60_000));
const istDate = () => new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Kolkata' }).format(new Date());

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  pid = (await (await api.get('/api/v1/properties')).json())[0].id;
  await createStaff(api, gauri.email, gauri.name, 'HOUSEKEEPER', pid, pw);
  await createStaff(api, manager.email, manager.name, 'MANAGER', pid, pw);
  const fences = await (await api.get(`/api/v1/properties/${pid}/attendance/geofences`)).json();
  gate = { latitude: fences[0].latitude, longitude: fences[0].longitude };

  // HR's part (Sprint 17): Gauri's employee record, linked to her account, on today's roster from a quarter of an hour ago.
  const users = await (await api.get('/api/v1/users')).json();
  const userId = users.find((u: { email: string }) => u.email === gauri.email).id;
  const depts = await (await api.get(`/api/v1/properties/${pid}/departments`)).json();
  const hk = depts.find((d: { code: string }) => d.code === 'HK');
  const emp = await api.post(`/api/v1/properties/${pid}/employees`, { data: {
    firstName: 'Gauri', lastName: `Gaonkar ${runId}`, departmentId: hk.id, employmentType: 'FULL_TIME', joinedOn: '2026-01-05', userId,
  } });
  expect(emp.status(), await emp.text()).toBe(201);
  const start = istTime(-15);
  const end = istTime(-15 + 8 * 60);
  const shift = await api.post(`/api/v1/properties/${pid}/roster/shifts`, { data: {
    employeeId: (await emp.json()).id, workDate: istDate(), departmentId: hk.id, startTime: `${start}:00`, endTime: `${end}:00`, breakMinutes: 30,
  } });
  expect(shift.status(), await shift.text()).toBe(201);

  // Her phone, up the road from the resort to begin with (about 1.1 km east of the gate).
  phoneCtx = await browser.newContext({ ...PHONE, geolocation: { latitude: gate.latitude, longitude: gate.longitude + 0.01, accuracy: 15 }, permissions: ['geolocation'] });
  phone = await phoneCtx.newPage();
  await signIn(phone, gauri.email, pw);
  mgr = await (await browser.newContext({ geolocation: gate, permissions: ['geolocation'] })).newPage();
  await signIn(mgr, manager.email, pw);
});

test('S18-01 On her phone, clocking in up the road is refused with how far away she is; at the gate it works', async () => {
  // Her day on the dashboard, with the clock-in button.
  await expect(phone.getByTestId('my-day')).toContainText('Not clocked in');
  await expect(phone.getByTestId('my-day')).toContainText('Today:');
  await phone.getByTestId('my-day').getByRole('link', { name: 'Clock in' }).click();
  await expect(phone).toHaveURL(/\/attendance/);
  await expect(phone.getByTestId('clock-state')).toContainText('Not clocked in');

  await phone.getByRole('button', { name: 'Clock in' }).click();
  await expect(phone.getByTestId('clock-error')).toHaveText(/^You are 1\.1 km from Main gate; clocking in counts within 300 m of it\. Do it at the resort, or ask your manager to record it with a reason\.$/);
  await shot(phone, '01-outside-geofence');

  await phoneCtx.setGeolocation({ latitude: gate.latitude + 0.0002, longitude: gate.longitude, accuracy: 15 });
  await phone.getByRole('button', { name: 'Clock in' }).click();
  await said(phone, /Clocked in at \d\d:\d\d\./);
  await expect(phone.getByTestId('clock-state')).toContainText('At work since');
  await expect(phone.getByTestId('my-events')).toContainText('In');
  await expect(phone.getByTestId('my-events')).toContainText('Main gate');
  await noSideScroll(phone);
  await shot(phone, '02-clocked-in-phone');
});

test('S18-02 She takes a break and clocks out; the day shows each entry and the time worked', async () => {
  await phone.getByRole('button', { name: 'Start break' }).click();
  await expect(phone.getByTestId('clock-state')).toContainText('On a break since');
  await phone.getByRole('button', { name: 'End break' }).click();
  await expect(phone.getByTestId('clock-state')).toContainText('At work since');
  await phone.getByRole('button', { name: 'Clock out' }).click();
  await said(phone, /Clocked out at \d\d:\d\d\. Worked 0 h\./);
  await expect(phone.getByTestId('clock-state')).toContainText('Not clocked in');
  await expect(phone.getByTestId('my-events').locator('li')).toHaveCount(4);
  await expect(phone.getByRole('button', { name: 'Clock in' })).toBeVisible();
  await shot(phone, '03-day-done-phone');
});

test('S18-03 The manager sees the team board and records a clock-in for someone whose phone has no signal', async () => {
  await mgr.goto('/attendance');
  const board = mgr.getByTestId('board');
  await expect(board).toBeVisible();
  await expect(board.locator('li', { hasText: gauri.name.split(' ')[0] }).first()).toContainText('Clocked out');
  // Someone from the demo roster who hasn't clocked in today.
  const row = board.locator('li[data-row^="E000"]').filter({ hasText: /Not in|Not started/ }).first();
  const name = (await row.locator('.nm b').textContent())!.trim();
  await row.getByRole('button', { name: `Record for ${name}` }).click();
  const d = mgr.getByRole('dialog');
  await d.getByLabel('Time').fill(istTime(-5));
  // The reason is required and the refusal says why, under the field.
  await d.getByRole('button', { name: 'Record' }).click();
  await expect(d.locator('mat-error')).toHaveText('Say why, at least 3 characters.');
  await d.getByLabel('Why').fill('Phone had no signal at the beach');
  await d.getByRole('button', { name: 'Record' }).click();
  await said(mgr, new RegExp(`Clock in at \\d\\d:\\d\\d recorded for ${name}\\.`));
  const recorded = board.locator('li[data-row^="E000"]', { hasText: name });
  await expect(recorded).toContainText('At work');
  await expect(recorded).toContainText('manager entry');
  await shot(mgr, '04-team-board');
});

test('S18-04 She submits her week from her phone', async () => {
  await phone.goto('/timesheets');
  await expect(phone.getByTestId('sheet-status')).toHaveText('Draft');
  await expect(phone.getByTestId('timesheet').locator('li[data-day]')).toHaveCount(1);
  await phone.getByLabel('Note for your manager (optional)').fill('First day at the resort');
  await phone.getByRole('button', { name: 'Submit week' }).click();
  await said(phone, 'Submitted. Your manager will approve it or send it back.');
  await expect(phone.getByTestId('sheet-status')).toHaveText('Waiting for approval');
  await noSideScroll(phone);
  await shot(phone, '05-submitted-phone');
});

test('S18-05 The manager approves her week', async () => {
  await mgr.goto('/timesheets');
  const row = mgr.getByTestId('team').locator('li', { hasText: gauri.name.split(' ')[0] }).filter({ hasText: 'Waiting for approval' });
  await row.getByRole('link').click();
  await expect(mgr.getByTestId('timesheet')).toContainText('First day at the resort');
  await mgr.getByRole('button', { name: 'Approve' }).click();
  await said(mgr, /week is approved\./);
  await expect(mgr.getByTestId('sheet-status')).toHaveText('Approved');
});

test('S18-06 Last week: approving all stops at Ravi\'s missing clock-out; corrected with a reason, approved and locked', async () => {
  await mgr.goto('/timesheets?view=team');
  await expect(mgr.getByTestId('week')).toContainText('Week of');
  await mgr.getByRole('button', { name: 'Previous week' }).click();
  await expect(mgr.getByTestId('team').locator('li[data-row="E00007"]')).toContainText('1 day without a clock-out');
  await mgr.getByRole('button', { name: /Approve all submitted/ }).click();
  await said(mgr, /8 timesheets approved\. 1 need a look first\./);
  await expect(mgr.getByTestId('skipped')).toContainText(/Ravi Kamat: Tue \d+ \w+ has a clock-in at \d\d:\d\d with no clock-out\. Correct that day first\./);
  await shot(mgr, '06-approve-week');

  await mgr.getByTestId('team').locator('li[data-row="E00007"]').getByRole('link').click();
  await expect(mgr.getByTestId('timesheet')).toContainText('Forgot to clock out on Tuesday, left at 22:05.');
  await mgr.getByTestId('timesheet').locator('li[data-day]', { hasText: 'No clock-out' }).getByRole('button', { name: /Correct/ }).click();
  const d = mgr.getByRole('dialog');
  await d.getByLabel('Clock-out').fill('22:05');
  await d.getByLabel('Why').fill('Forgot to clock out, left at 22:05 (kitchen closing log)');
  await d.getByRole('button', { name: 'Save correction' }).click();
  await said(mgr, 'Correction saved. The old times are kept on the timesheet.');
  await expect(mgr.getByTestId('corrections')).toContainText('Forgot to clock out, left at 22:05 (kitchen closing log)');
  await mgr.getByRole('button', { name: 'Approve' }).click();
  await expect(mgr.getByTestId('sheet-status')).toHaveText('Approved');
  await shot(mgr, '07-corrected-approved');
  await mgr.getByRole('button', { name: 'Lock for payroll' }).click();
  await said(mgr, /locked for payroll\./);
  await expect(mgr.getByTestId('sheet-status')).toHaveText('Locked for payroll');
  await expect(mgr.getByRole('button', { name: 'Correct a day' })).toHaveCount(0);
});

test('S18-07 The app can be installed: a manifest with its name and icons, and the phone screens fit 360 px', async () => {
  const manifest = await (await phone.request.get('/manifest.webmanifest')).json();
  expect(manifest.name).toBe('Resort OS');
  expect(manifest.display).toBe('standalone');
  expect(manifest.start_url).toBe('/dashboard');
  expect(manifest.icons.map((i: { sizes: string }) => i.sizes)).toEqual(['192x192', '512x512', '512x512']);
  expect((await phone.request.get('/icons/icon-192.png')).headers()['content-type']).toContain('image/png');
  await expect(phone.locator('link[rel="manifest"]')).toHaveAttribute('href', 'manifest.webmanifest');
  await phone.goto('/dashboard');
  await expect(phone.getByTestId('my-day')).toContainText('This week');
  await noSideScroll(phone);
  await shot(phone, '08-dashboard-phone');
  await phone.goto('/attendance');
  await noSideScroll(phone);
});
