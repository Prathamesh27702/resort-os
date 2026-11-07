import { Browser, BrowserContext, Page, expect, test } from '@playwright/test';
import { apiAs, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 23 — the same checks as docs/sprints/REVIEWING.md → "Sprint 23", automated: brief §50–55. The dev seed has a year
// of past stays (S020), so the owner's Today shows tonight, the month so far against last month and a year ago, and the
// last twelve months. The month at a glance for last month prints and downloads as CSV. The occupancy report adds up again
// on Refresh now. The owner sets Booking.com's commission and puts it back. The manager, on a phone, sees tonight and the
// next two weeks but no money, and no money tabs in Reports.

test.describe.configure({ mode: 'serial' });

const PHONE = { viewport: { width: 360, height: 780 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 };
let own: Page;
let ownCtx: BrowserContext;
let mgr: Page;
let mgrCtx: BrowserContext;
const pw = `Staff-${runId}-2026`;
const manager = { email: `m23.${runId}@demo.local`, name: `Meera Manager ${runId}` };

const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text }).last()).toBeVisible();
const noSideScroll = async (page: Page) => expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);
const dialog = async (page: Page) => {
  const d = page.locator('mat-dialog-container');
  await expect(d).toHaveClass(/mdc-dialog--open/);
  await expect(d).not.toHaveClass(/mdc-dialog--opening/);
  return d;
};
/** Last month at the property, as the month picker names it ("October 2026"). */
const lastMonth = () => {
  const now = new Date(Date.now() + 5.5 * 3600_000);
  const d = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - 1, 15));
  return d.toLocaleDateString('en-IN', { month: 'long', year: 'numeric', timeZone: 'UTC' });
};

/** The first and last day of last month, as yyyy-mm-dd. */
const lastMonthRange = () => {
  const now = new Date(Date.now() + 5.5 * 3600_000);
  const iso = (d: Date) => d.toISOString().slice(0, 10);
  return [iso(new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - 1, 1))), iso(new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 0)))];
};

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  const { api } = await apiAs(owner.email, owner.password);
  const pid = (await (await api.get('/api/v1/properties')).json())[0].id;
  await createStaff(api, manager.email, manager.name, 'MANAGER', pid, pw);
  // Add the numbers up now (the job does it every 15 minutes), so the test doesn't wait for it.
  expect((await api.post(`/api/v1/properties/${pid}/reports/refresh`, { data: {} })).ok()).toBe(true);
  ownCtx = await browser.newContext();
  own = await ownCtx.newPage();
  await signIn(own, owner.email, owner.password);
  mgrCtx = await browser.newContext(PHONE);
  mgr = await mgrCtx.newPage();
  await signIn(mgr, manager.email, pw);
});

test.afterAll(async () => {
  await mgrCtx?.close();
  await ownCtx?.close();
});

test('S23-01 the owner\'s Today: tonight, the month so far against last month and a year ago, the last 12 months', async () => {
  await own.goto('/dashboard');
  const tonight = own.getByTestId('tonight');
  await expect(tonight).toContainText('Guests in house');
  await expect(tonight.getByTestId('tonight-occupancy')).toHaveText(/\d+(\.\d)? %/);
  await expect(tonight.getByTestId('freshness')).toContainText('Updated');
  const mtd = own.getByTestId('month-to-date');
  for (const k of ['occupancy', 'adr', 'revpar', 'revenue', 'labour']) await expect(mtd.locator(`[data-kpi="${k}"]`)).toBeVisible();
  await expect(mtd.locator('[data-kpi="adr"]')).toContainText('₹');
  await expect(mtd.locator('[data-kpi="adr"]')).toContainText('on a year ago');
  await expect(mtd).toContainText('The last 12 months');
  await expect(mtd).toContainText('Booking sources this month');
  await expect(mtd.getByTestId('budget-card')).toContainText('spent of');
  await shot(own, '01-owner-dashboard');
});

test('S23-02 the month at a glance for last month, with the P&L, prints and downloads as CSV', async () => {
  await own.getByRole('link', { name: 'Reports' }).first().click();
  await expect(own).toHaveURL(/\/reports/);
  await own.getByRole('combobox', { name: 'Month' }).click();
  await own.getByRole('option', { name: lastMonth() }).click();
  const m = own.getByTestId('month-report');
  await expect(m).toContainText(lastMonth());
  await expect(m.locator('[data-kpi="adr"]')).toContainText('₹');
  await expect(m.locator('[data-kpi="occupancy"]')).toContainText('%');
  await expect(m.locator('[data-line="REV_ROOM"]')).toContainText('₹');
  await expect(m.locator('[data-line="PROFIT"]')).toContainText('Operating profit');
  await expect(m).toContainText('Booking sources');
  await expect(m).toContainText('GST (estimate)');
  await expect(own.getByRole('button', { name: 'Print' })).toBeVisible();
  await shot(own, '02-month-report');
  const [file] = await Promise.all([own.waitForEvent('download'), own.getByTestId('csv').click()]);
  expect(file.suggestedFilename()).toMatch(/-month-\d{4}-\d{2}\.csv$/);
});

test('S23-03 occupancy by month for the last year, and Refresh now adds it up again', async () => {
  await own.getByRole('button', { name: 'Occupancy', exact: true }).click();
  await own.getByRole('radio', { name: 'By month' }).click();
  const t = own.getByTestId('occupancy');
  await expect(t.locator('tbody tr')).toHaveCount(13);   // 12 months and the total
  await expect(t.getByTestId('total')).toContainText('%');
  await own.getByRole('button', { name: 'Refresh now' }).click();
  await said(own, /Added up again in/);
  await shot(own, '03-occupancy-by-month');
});

test('S23-04 booking sources: the owner sets Booking.com\'s commission, the net follows, and puts it back', async () => {
  await own.getByRole('button', { name: 'Booking sources', exact: true }).click();
  // Last month: the seeded history has Booking.com stays then (it ends a week ago).
  const [first, last] = lastMonthRange();
  await own.getByTestId('from').fill(first);
  await own.getByTestId('from').dispatchEvent('change');
  await own.getByTestId('to').fill(last);
  await own.getByTestId('to').dispatchEvent('change');
  const row = own.getByTestId('channels').locator('[data-source="BOOKING_COM"]');
  await expect(row).toContainText('18 %');
  await own.getByRole('button', { name: 'Commission rates' }).click();
  let d = await dialog(own);
  await d.getByLabel('Booking.com commission %').fill('15');
  await d.getByRole('button', { name: 'Save' }).click();
  await said(own, 'Commission rates saved');
  await expect(row).toContainText('15 %');
  await shot(own, '04-booking-sources');
  await own.getByRole('button', { name: 'Commission rates' }).click();
  d = await dialog(own);
  await d.getByLabel('Booking.com commission %').fill('');
  await d.getByRole('button', { name: 'Save' }).click();
  await said(own, 'Commission rates saved');
  await expect(row).toContainText('18 %');
});

test('S23-05 GST, costs and the profit and loss by month', async () => {
  await own.getByRole('button', { name: 'GST', exact: true }).click();
  await expect(own.getByTestId('gst')).toContainText('Total');
  await own.getByRole('button', { name: 'Food and labour cost', exact: true }).click();
  await expect(own.getByTestId('costs')).toContainText('Pay %');
  await own.getByRole('button', { name: 'Profit and loss', exact: true }).click();
  await expect(own.getByTestId('pnl').locator('[data-line="PAY"]')).toBeVisible();
  await shot(own, '05-profit-and-loss');
});

test('S23-06 the manager, on a phone: tonight and the next two weeks, no money', async () => {
  await mgr.goto('/dashboard');
  await expect(mgr.getByTestId('tonight')).toContainText('The next 14 nights');
  await expect(mgr.getByTestId('month-to-date')).toHaveCount(0);
  await noSideScroll(mgr);
  await shot(mgr, '06-manager-today-on-phone');
  await mgr.goto('/reports?tab=occupancy');
  await expect(mgr.getByTestId('occupancy')).toBeVisible();
  await expect(mgr.getByRole('button', { name: 'Revenue', exact: true })).toHaveCount(0);
  await expect(mgr.getByTestId('occupancy')).not.toContainText('ADR');
  await noSideScroll(mgr);
});

test('S23-07 the budget counts supplier bills and pay, and says what each department spent on what', async () => {
  await own.goto('/budgets');
  const fb = own.locator('[data-centre="FB"]');
  await fb.getByRole('button', { name: 'Food and beverage' }).click();
  await expect(fb.getByTestId('split')).toContainText('supplier bills ₹');
  await expect(fb.getByTestId('split')).toContainText('pay ₹');
  await expect(own.getByTestId('footnote')).toContainText('supplier bills');
  await shot(own, '07-budget-with-bills-and-pay');
});
