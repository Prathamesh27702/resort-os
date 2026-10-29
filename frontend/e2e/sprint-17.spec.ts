import { APIRequestContext, Browser, Page, expect, test } from '@playwright/test';
import { addDays, apiAs, createStaff, fieldDate, owner, runId, shot, signIn } from './helpers';

// Sprint 17 — the same checks as docs/sprints/REVIEWING.md → "Sprint 17", automated. The demo: create next week's roster
// (copy this week, add a new receptionist), hit a clash, and see the shortage view highlight what is missing. HR sees
// pay and documents; a manager sees the profile and the roster but never the pay.

test.describe.configure({ mode: 'serial' });

const PHONE = { viewport: { width: 360, height: 780 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 };
let hrPage: Page;       // HR, on a desktop
let mgr: Page;          // the manager, on a desktop
let mgrPhone: Page;     // the manager, on a phone
let api: APIRequestContext;
let pid = '';
let nextMonday = '';
let kavyaId = '';
const pw = `Staff-${runId}-2026`;
const hr = { email: `hr17.${runId}@demo.local`, name: `Hema HR ${runId}` };
const manager = { email: `m17.${runId}@demo.local`, name: `Manoj Manager ${runId}` };
const kavya = `Kavya ${runId}`;

const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text }).last()).toBeVisible();
const noSideScroll = async (page: Page) => expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);
const pick = async (page: Page, select: string, option: string | RegExp) => {
  // Opened from the keyboard: an empty select's own floating label sits over it and takes the click.
  await page.getByRole('dialog').getByRole('combobox', { name: select }).press('Enter');
  await page.getByRole('option', { name: option }).first().click();
};
const shortDay = (iso: string) => {
  const [y, m, d] = iso.split('-').map(Number);
  const t = new Date(Date.UTC(y, m - 1, d));
  return `${['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'][t.getUTCDay()]} ${d} ${['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'][m - 1]}`;
};

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  pid = (await (await api.get('/api/v1/properties')).json())[0].id;
  await createStaff(api, hr.email, hr.name, 'HR', pid, pw);
  await createStaff(api, manager.email, manager.name, 'MANAGER', pid, pw);
  const week = await (await api.get(`/api/v1/properties/${pid}/roster`)).json();
  nextMonday = addDays(week.weekStart, 7);

  hrPage = await (await browser.newContext()).newPage();
  await signIn(hrPage, hr.email, pw);
  mgr = await (await browser.newContext()).newPage();
  await signIn(mgr, manager.email, pw);
  mgrPhone = await (await browser.newContext(PHONE)).newPage();
  await signIn(mgrPhone, manager.email, pw);
});

test('S17-01 Employees: ten people by department, each with a job title and weekly day off', async () => {
  await hrPage.goto('/employees');
  await expect(hrPage.getByRole('heading', { name: 'Employees' })).toBeVisible();
  await expect(hrPage.locator('li[data-employee]')).toHaveCount(10);
  await expect(hrPage.locator('li[data-employee="E00001"]')).toContainText('Asha Naik');
  await expect(hrPage.locator('li[data-employee="E00001"]')).toContainText('Front office · Receptionist · Full time');
  await expect(hrPage.locator('li[data-employee="E00001"]')).toContainText('off on Tuesday');
  await expect(hrPage.locator('li[data-employee="E00005"]')).toContainText('Seasonal');
  await hrPage.getByLabel('Find someone').fill('kamat');
  await expect(hrPage.locator('li[data-employee]')).toHaveCount(1);
  await hrPage.getByLabel('Find someone').fill('');
  await shot(hrPage, '01-employees');
});

test('S17-02 HR adds a receptionist, records her pay and her PAN; opening the number is recorded', async () => {
  await hrPage.goto('/employees');
  await hrPage.getByRole('button', { name: 'Employee', exact: true }).click();
  const d = hrPage.getByRole('dialog');
  await d.getByLabel('First name').fill(kavya);
  await d.getByLabel('Last name').fill('Naik');
  await pick(hrPage, 'Department', 'Front office');
  await pick(hrPage, 'Job title', 'Receptionist');
  await d.getByLabel('Joined on').fill(fieldDate(addDays(nextMonday, -7)));
  await pick(hrPage, 'Weekly day off', 'Thursday');
  await d.getByLabel('Phone', { exact: true }).fill('+91 98230 19999');
  // A mistyped phone number is refused under its field, with the reason.
  await d.getByLabel('Their phone').fill('98-ab');
  await d.getByRole('button', { name: 'Save' }).click();
  await expect(d.locator('mat-error')).toHaveText('Enter the phone number with digits only (spaces and - allowed), e.g. +91 98220 12345, or leave it empty.');
  await d.getByLabel('Their phone').fill('+91 98230 29999');
  await d.getByRole('button', { name: 'Save' }).click();
  await said(hrPage, /added as E00011/);
  await expect(hrPage.getByRole('heading', { name: new RegExp(kavya) })).toBeVisible();
  kavyaId = hrPage.url().split('/').pop()!;

  // Pay (HR has salary access).
  await hrPage.getByTestId('pay').getByRole('button', { name: 'Record pay' }).click();
  const pay = hrPage.getByRole('dialog');
  await pay.getByLabel('Basic (₹)').fill('17000');
  await pay.getByLabel('Allowances (₹)').fill('2500');
  await expect(pay).toContainText('Gross ₹19,500 a month');
  await pay.getByRole('button', { name: 'Record pay' }).click();
  await said(hrPage, 'Pay recorded.');
  await expect(hrPage.getByTestId('pay')).toContainText('₹19,500 a month');

  // PAN: stored encrypted, listed masked; "Show number" decrypts it (an audited read).
  await hrPage.getByTestId('docs').getByRole('button', { name: 'Add a document' }).click();
  const doc = hrPage.getByRole('dialog');
  await doc.getByLabel('Number').fill('ABCPK1234');
  await doc.getByRole('button', { name: 'Save document' }).click();
  await expect(doc.locator('mat-error')).toHaveText('A PAN is 10 characters: 5 letters, 4 digits, 1 letter, e.g. ABCPK1234F.');
  await doc.getByLabel('Number').fill('ABCPK1234F');
  await doc.getByRole('button', { name: 'Save document' }).click();
  await said(hrPage, 'PAN saved.');
  const pan = hrPage.locator('li[data-doc="PAN"]');
  await expect(pan).toContainText('•••• 234F');
  await pan.getByRole('button', { name: 'Show number' }).click();
  await expect(pan.getByTestId('doc-number')).toHaveText('ABCPK1234F');
  await shot(hrPage, '02-profile-hr');
});

test('S17-03 A manager sees the profile on a phone but never the pay or the documents', async () => {
  await mgrPhone.goto(`/employees/${kavyaId}`);
  await expect(mgrPhone.getByRole('heading', { name: new RegExp(kavya) })).toBeVisible();
  await expect(mgrPhone.getByText('Front office · Receptionist · Full time')).toBeVisible();
  await expect(mgrPhone.getByTestId('pay')).toHaveCount(0);
  await expect(mgrPhone.getByTestId('docs')).toHaveCount(0);
  await expect(mgrPhone.locator('body')).not.toContainText('19,500');
  await noSideScroll(mgrPhone);
  await shot(mgrPhone, '03-profile-manager-phone');
  // And the API refuses them too.
  const { api: m } = await apiAs(manager.email, pw);
  expect((await m.get(`/api/v1/properties/${pid}/employees/${kavyaId}/compensation`)).status()).toBe(403);
});

test('S17-04 Next week starts empty with every shift short; copying this week fills it and lists the gaps', async () => {
  await mgr.goto('/roster');
  await mgr.getByRole('button', { name: 'Next week' }).click();
  await expect(mgr.getByTestId('week')).toContainText(fieldDate(nextMonday));
  await expect(mgr.getByTestId('shortages')).toContainText('short this week');
  await expect(mgr.locator('button[data-shift]')).toHaveCount(0);

  await mgr.getByRole('button', { name: 'Copy last week' }).click();
  const d = mgr.getByRole('dialog');
  await d.getByRole('button', { name: 'Copy' }).click();
  await expect(d.getByTestId('copied')).toHaveText('56 shifts copied.');
  await d.getByRole('button', { name: 'Done' }).click();
  await expect(mgr.locator('button[data-shift]')).toHaveCount(56);
  // Asha's day off is Tuesday: Front office · Morning that day has 0 of 1.
  await expect(mgr.getByTestId('shortages')).toContainText(`${shortDay(addDays(nextMonday, 1))} Front office · Morning 07:00–15:00: 0 of 1`);
  await shot(mgr, '04-roster-next-week');
});

test('S17-05 Putting Asha on a second shift that overlaps her morning is refused, naming it; allowed with a reason it shows as a warning', async () => {
  await mgr.locator('tr[data-row="E00001"] td').nth(0).getByRole('button', { name: /Add a shift/ }).click();
  const d = mgr.getByRole('dialog');
  await pick(mgr, 'Shift', 'Other times');
  await d.getByLabel('Starts').fill('14:00');
  await d.getByLabel('Ends').fill('22:00');
  await d.getByRole('button', { name: 'Add' }).click();
  await expect(d.getByTestId('clash')).toContainText(`Asha Naik is already on Front office · Morning 07:00–15:00 on`);
  await expect(d.getByTestId('clash')).toContainText('One person can\'t work two shifts at once');
  await shot(mgr, '05-clash');
  await d.getByRole('checkbox', { name: 'Allow the overlap' }).check();
  await d.getByLabel('Why').fill('Handover to the evening desk');
  await d.getByRole('button', { name: 'Add' }).click();
  await said(mgr, /Asha Naik on a shift 14:00–22:00/);
  await expect(mgr.getByTestId('warnings')).toContainText('Asha Naik works two shifts at once');
  await expect(mgr.getByTestId('warnings')).toContainText('Handover to the evening desk');
});

test('S17-06 Tapping a gap fills it: Kavya takes Tuesday morning and the shortage is gone', async () => {
  const gap = `${shortDay(addDays(nextMonday, 1))} Front office · Morning`;
  await mgr.getByTestId('shortages').getByRole('button', { name: `Fill Front office Morning on ${shortDay(addDays(nextMonday, 1))}` }).click();
  const d = mgr.getByRole('dialog');
  await pick(mgr, 'Who', new RegExp(kavya));
  await d.getByRole('button', { name: 'Add' }).click();
  await said(mgr, new RegExp(`${kavya} Naik on Morning 07:00–15:00`));
  await expect(mgr.getByTestId('shortages')).not.toContainText(gap);
  await expect(mgr.locator(`button[data-shift="E00011-${addDays(nextMonday, 1)}"]`)).toContainText('Morning 07:00–15:00');
  await shot(mgr, '06-shortage-filled');
});

test('S17-07 On a phone the roster is one day at a time, with the day\'s gaps marked', async () => {
  await mgrPhone.goto('/roster');
  await mgrPhone.getByRole('button', { name: 'Next week' }).click();
  await mgrPhone.getByRole('tab').nth(2).click();   // Wednesday
  await expect(mgrPhone.locator('li[data-row="E00003"]')).toContainText('day off');   // Maria is off on Wednesday
  await expect(mgrPhone.locator('li[data-row="E00004"]')).toContainText('Day 08:00–17:00');
  await noSideScroll(mgrPhone);
  await shot(mgrPhone, '07-roster-phone');
});
