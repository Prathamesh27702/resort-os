import { APIRequestContext, Browser, BrowserContext, Page, expect, test } from '@playwright/test';
import { apiAs, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 19 — the same checks as docs/sprints/REVIEWING.md → "Sprint 19", automated: brief §74 from leave on. A housekeeper
// asks for leave on her phone over a holiday, ending with a half day; the preview counts the days and says what it does to
// the roster; a wrong date is refused under the field. Her manager sees it on the dashboard and approves it: her balance
// goes down, the roster shows the leave and her shift is gone. HR looks at balances and one person's history, adjusts a
// balance with a reason and adds a holiday. HR then makes this month's payroll file: what isn't ready is listed first and
// the file is made only once they confirm.

test.describe.configure({ mode: 'serial' });

const PHONE = { viewport: { width: 360, height: 780 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 };
let phone: Page;        // Gauri, the housekeeper, on her phone
let phoneCtx: BrowserContext;
let mgr: Page;          // her manager, on a desktop
let hr: Page;           // HR, on a desktop
let api: APIRequestContext;
let pid = '';
let empCode = '';
let reqCode = '';
const pw = `Staff-${runId}-2026`;
const gauri = { email: `g19.${runId}@demo.local`, name: `Gauri Gaonkar ${runId}` };
const manager = { email: `m19.${runId}@demo.local`, name: `Mira Manager ${runId}` };
const hrUser = { email: `h19.${runId}@demo.local`, name: `Hema HR ${runId}` };

const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text }).last()).toBeVisible();
const noSideScroll = async (page: Page) => expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);
const istDate = () => new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Kolkata' }).format(new Date());
const plus = (iso: string, n: number) => {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
};
const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
/** "Tue 3 Nov", as the screens write a date. */
const short = (iso: string) => { const d = new Date(`${iso}T00:00:00Z`); return `${DAYS[d.getUTCDay()]} ${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]}`; };

// The leave: Monday to Wednesday three weeks from now. Tuesday is a holiday and she comes back after lunch on Wednesday,
// so it is 1.5 days. She has a shift on the Monday, which comes off the roster when it is approved.
const today = istDate();
const monday = plus(today, ((8 - new Date(`${today}T00:00:00Z`).getUTCDay()) % 7 || 7) + 14);
const holiday = plus(monday, 1);
const lastDay = plus(monday, 2);
const holidayName = `Feast of the resort ${runId}`;

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  pid = (await (await api.get('/api/v1/properties')).json())[0].id;
  await createStaff(api, gauri.email, gauri.name, 'HOUSEKEEPER', pid, pw);
  await createStaff(api, manager.email, manager.name, 'MANAGER', pid, pw);
  await createStaff(api, hrUser.email, hrUser.name, 'HR', pid, pw);

  // HR's part from Sprint 17: Gauri's employee record linked to her account (Sunday off) and her Monday shift.
  const users = await (await api.get('/api/v1/users')).json();
  const userId = users.find((u: { email: string }) => u.email === gauri.email).id;
  const hk = (await (await api.get(`/api/v1/properties/${pid}/departments`)).json()).find((d: { code: string }) => d.code === 'HK');
  const emp = await api.post(`/api/v1/properties/${pid}/employees`, { data: {
    firstName: 'Gauri', lastName: `Gaonkar ${runId}`, departmentId: hk.id, employmentType: 'FULL_TIME', joinedOn: '2026-01-05', weeklyOff: 7, userId,
  } });
  expect(emp.status(), await emp.text()).toBe(201);
  const e = await emp.json();
  empCode = e.code;
  const shift = await api.post(`/api/v1/properties/${pid}/roster/shifts`, { data: {
    employeeId: e.id, workDate: monday, departmentId: hk.id, startTime: '07:00:00', endTime: '15:00:00', breakMinutes: 30,
  } });
  expect(shift.status(), await shift.text()).toBe(201);

  // The resort's own holiday on the Tuesday, and the 4 days of casual leave she brought from the paper register.
  const types = await (await api.get(`/api/v1/properties/${pid}/leave/types`)).json();
  const cl = types.find((t: { code: string }) => t.code === 'CL');
  const h = await api.post(`/api/v1/properties/${pid}/leave/holidays`, { data: { date: holiday, name: holidayName, isActive: true } });
  expect(h.status(), await h.text()).toBe(201);
  const open = await api.post(`/api/v1/properties/${pid}/leave/adjustments`, { data: {
    employeeId: e.id, leaveTypeId: cl.id, days: 4, reason: 'Brought forward from the paper register', opening: true,
  } });
  expect(open.status(), await open.text()).toBe(200);

  phoneCtx = await browser.newContext(PHONE);
  phone = await phoneCtx.newPage();
  await signIn(phone, gauri.email, pw);
  mgr = await (await browser.newContext()).newPage();
  await signIn(mgr, manager.email, pw);
  hr = await (await browser.newContext()).newPage();
  await signIn(hr, hrUser.email, pw);
});

test('S19-01 On her phone she asks for leave over the holiday; the preview counts 1.5 days and a wrong date is refused under the field', async () => {
  await phone.goto('/leave');
  await expect(phone.getByTestId('my-balances').locator('[data-type="CL"]')).toContainText('4 days');
  await phone.getByRole('button', { name: 'Ask for leave' }).click();
  const d = phone.getByRole('dialog');
  // Casual leave is the first kind, so it is already chosen.
  await expect(d.getByLabel('Kind of leave')).toContainText('Casual leave');
  await d.getByLabel('From').fill(monday);
  await d.getByLabel('To').fill(plus(monday, -1));
  await d.getByLabel('To').blur();
  await expect(d.locator('mat-error', { hasText: 'The last day is before the first day.' })).toBeVisible();

  await d.getByLabel('To').fill(lastDay);
  await d.getByText('Last day: until lunch').click();
  await d.getByLabel('Reason (optional)').fill('Village feast');
  const preview = d.getByTestId('leave-preview');
  await expect(preview).toContainText('1.5 days');
  await expect(preview).toContainText('4 days available, 2.5 days left after');
  await expect(preview).toContainText(`Not counted: ${short(holiday)}: ${holidayName}`);
  await expect(preview).toContainText(`${short(monday)}: Gauri's shift`);
  await expect(preview).toContainText('comes off the roster when this is approved.');
  await noSideScroll(phone);
  await shot(phone, '01-ask-leave-phone');

  await d.getByRole('button', { name: 'Send' }).click();
  await said(phone, /LV\d{5}: 1\.5 days of Casual leave asked for\. Your manager will approve it\./);
  const row = phone.getByTestId('my-requests').locator('li[data-req]').first();
  await expect(row).toContainText('Waiting for approval');
  await expect(row).toContainText(`${short(monday)} – ${short(lastDay)} (morning)`);
  reqCode = (await row.getAttribute('data-req'))!;
  await expect(phone.getByTestId('my-balances').locator('[data-type="CL"]')).toContainText('1.5 days waiting · 2.5 days free');
  await shot(phone, '02-my-leave-phone');
});

test('S19-02 Her manager sees it on the dashboard and approves it; her shift comes off the roster', async () => {
  await mgr.goto('/dashboard');
  await expect(mgr.getByTestId('leave-waiting')).toContainText('waiting for approval');
  await mgr.getByTestId('leave-waiting').getByRole('link', { name: /Look at/ }).click();
  await expect(mgr).toHaveURL(/\/leave\?view=team/);
  const row = mgr.getByTestId('pending').locator(`li[data-req="${reqCode}"]`);
  await expect(row).toContainText(gauri.name);
  await expect(row).toContainText('comes off the roster when this is approved.');
  await shot(mgr, '03-team-pending');

  await row.getByRole('button', { name: 'Approve' }).click();
  const d = mgr.getByRole('dialog');
  await d.getByLabel('Note (optional)').fill('Enjoy the feast');
  await d.getByRole('button', { name: 'Approve' }).click();
  await said(mgr, `${reqCode} approved: 1.5 days off ${gauri.name}'s Casual leave, 1 shift taken off the roster.`);
  const upcoming = mgr.getByTestId('upcoming').locator(`li[data-req="${reqCode}"]`);
  await expect(upcoming).toContainText('Approved');
  await expect(upcoming).toContainText('1 shift taken off the roster');

  // The roster for that week: the leave on Monday and Wednesday, no shift on Monday.
  await mgr.goto('/roster');
  await expect(mgr.getByTestId('week')).toBeVisible();
  for (let i = 0; i < 3; i++) await mgr.getByRole('button', { name: 'Next week' }).click();
  await expect(mgr.getByTestId('week')).toContainText(new Date(`${monday}T00:00:00Z`).getUTCDate().toString());
  await expect(mgr.locator(`[data-leave="${empCode}-${monday}"]`)).toHaveText('CL leave');
  await expect(mgr.locator(`[data-leave="${empCode}-${lastDay}"]`)).toHaveText('½ CL leave');
  const cell = mgr.locator(`[data-leave="${empCode}-${monday}"]`).locator('xpath=ancestor::td[1]');
  await expect(cell).not.toContainText('07:00');
  await cell.scrollIntoViewIfNeeded();
  await shot(mgr, '04-roster-leave');
});

test('S19-03 Her balance has gone down, and she sees the approval on her phone', async () => {
  await phone.goto('/leave');
  await expect(phone.getByTestId('my-balances').locator('[data-type="CL"]')).toContainText('2.5 days');
  const row = phone.getByTestId('my-requests').locator(`li[data-req="${reqCode}"]`);
  await expect(row).toContainText('Approved');
  await expect(row).toContainText(`approved by ${manager.name}`);
  await shot(phone, '05-approved-phone');
});

test('S19-04 HR looks at the balances and her history, adjusts it with a reason, and adds a holiday', async () => {
  await hr.goto('/leave?view=balances');
  const row = hr.getByTestId('balances').locator(`tr[data-emp="${empCode}"]`);
  await expect(row).toContainText('2.5');
  await shot(hr, '06-balances');
  await row.click();
  const d = hr.getByRole('dialog');
  await expect(d.locator('table.ledger')).toContainText(`Taken CL · ${reqCode}`);
  await expect(d.locator('table.ledger')).toContainText('Brought in CL');
  // Without a reason it is refused, under the field.
  await d.getByLabel('Leave').press('Enter');
  await hr.getByRole('option', { name: 'Casual leave' }).click();
  await d.getByLabel('Days').fill('1');
  await d.getByRole('button', { name: 'Adjust' }).click();
  await expect(d.locator('mat-error', { hasText: 'Say why, at least 3 characters.' })).toBeVisible();
  await d.getByLabel('Why').fill('Worked on the holiday in August');
  await d.getByRole('button', { name: 'Adjust' }).click();
  await expect(d.locator('table.ledger tbody tr').first()).toContainText('Adjusted CL');
  await expect(d.locator('table.ledger tbody tr').first()).toContainText('Worked on the holiday in August');
  await expect(d.locator('.bal')).toContainText('3.5 days CL');
  await shot(hr, '07-ledger-adjusted');
  await d.getByRole('button', { name: 'Close' }).click();
  await expect(row).toContainText('3.5');

  // A holiday added on the Holidays view; the same date again is refused under Date.
  await hr.locator('mat-button-toggle', { hasText: 'Holidays' }).click();
  const newDay = plus(monday, 10);
  for (const attempt of [1, 2]) {
    await hr.getByRole('button', { name: 'Add a holiday' }).click();
    const h = hr.getByRole('dialog');
    await h.getByLabel('Date').fill(attempt === 1 ? newDay : holiday);
    await h.getByLabel('Name').fill(`Village fair ${runId}`);
    await h.getByRole('button', { name: 'Save' }).click();
    if (attempt === 1) {
      await said(hr, `Village fair ${runId} on`);
    } else {
      await expect(h.locator('mat-error').first()).toContainText(`is already a holiday: ${holidayName}.`);
      await h.getByRole('button', { name: 'Cancel' }).click();
    }
  }
  if (newDay.slice(0, 4) === today.slice(0, 4)) await expect(hr.getByTestId('holidays')).toContainText(`Village fair ${runId}`);
  await shot(hr, '08-holidays');
});

test('S19-05 HR makes this month\'s payroll file: what is not ready is listed, and the file is made once they confirm', async () => {
  await hr.goto('/payroll');
  await hr.getByLabel('Month').fill(today.slice(0, 7));
  const warnings = hr.getByTestId('payroll-warnings');
  await expect(warnings).toContainText("hasn't ended");
  await expect(hr.getByTestId('payroll-rows').locator('tbody tr').first()).toBeVisible();
  const make = hr.getByRole('button', { name: 'Make the file' });
  await expect(make).toBeDisabled();
  await shot(hr, '09-payroll-preview');

  await warnings.getByText('Make the file anyway').click();
  const download = hr.waitForEvent('download');
  await make.click();
  await said(hr, /Payroll file for \w+ \d{4} made: \d+ people/);
  const file = await download;
  expect(file.suggestedFilename()).toMatch(/^payroll-[a-z0-9]+-\d{4}-\d{2}-\d{8}-\d{4}\.csv$/);
  const first = hr.getByTestId('payroll-files').locator('li').first();
  await expect(first).toContainText('left out');
  await expect(first).toContainText(`by ${hrUser.name}`);
  await shot(hr, '10-payroll-file');
});
