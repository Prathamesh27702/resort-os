import { Browser, BrowserContext, Page, expect, test } from '@playwright/test';
import { apiAs, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 22 — the same checks as docs/sprints/REVIEWING.md → "Sprint 22", automated: brief §40, §41. The owner adds a
// department of this run's own (so the demo budget's numbers don't move) and plans its budget, ₹12,000 for the year. The
// manager, on his phone, records ₹1,180 paid to a plumber on a GST bill with a photo of the receipt: it waits for approval
// and the budget doesn't count it yet. The owner approves it; only its ₹1,000 taxable value counts (the GST comes back as
// input credit), and the budget shows the department's spending against its plan.

test.describe.configure({ mode: 'serial' });

const PHONE = { viewport: { width: 360, height: 780 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 };
let mgr: Page;          // the manager, on his phone
let mgrCtx: BrowserContext;
let own: Page;          // the owner, on a desktop
let ownCtx: BrowserContext;
let pid = '';
const pw = `Staff-${runId}-2026`;
const manager = { email: `m22.${runId}@demo.local`, name: `Manoj Manager ${runId}` };
const centreCode = `S${runId}`.toUpperCase();
const centreName = `Spa ${runId}`;
const what = `Plumber: spa shower ${runId}`;
const goa = '30ABCDE1234F1ZD';   // a made-up GSTIN in Goa (the demo property's state) with a valid check digit

// A real 4×4 PNG, so the phone's photo shrinking (canvas) works on it.
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAIAAAAmkwkpAAAAEklEQVR4nGP4z8CAB+GTG8HSALfKY52fTcuYAAAAAElFTkSuQmCC', 'base64');

const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text }).last()).toBeVisible();
const noSideScroll = async (page: Page) => expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);
const dialog = async (page: Page) => {
  const d = page.locator('mat-dialog-container');
  await expect(d).toHaveClass(/mdc-dialog--open/);
  await expect(d).not.toHaveClass(/mdc-dialog--opening/);
  await expect.poll(() => d.evaluate(el => el.contains(document.activeElement))).toBe(true);
  return d;
};
/** Months of this financial year so far, April included (November → 8). */
const monthsSoFar = () => {
  const m = new Date(Date.now() + 5.5 * 3600_000).getUTCMonth() + 1;
  return m >= 4 ? m - 3 : m + 9;
};
const centreRow = (page: Page) => page.locator(`[data-centre="${centreCode}"]`);
const inr = (n: number) => '₹' + n.toLocaleString('en-IN');

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  const { api } = await apiAs(owner.email, owner.password);
  pid = (await (await api.get('/api/v1/properties')).json())[0].id;
  await createStaff(api, manager.email, manager.name, 'MANAGER', pid, pw);
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

test('S22-01 the owner adds a department and plans its budget for the year', async () => {
  await own.goto('/expenses');
  await own.getByRole('button', { name: 'Categories and departments' }).click();
  await own.getByRole('button', { name: 'New department' }).click();
  const d = await dialog(own);
  await d.getByLabel('Code').fill(centreCode);
  await d.getByLabel('Name').fill(centreName);
  await d.getByRole('button', { name: 'Save' }).click();
  await said(own, `${centreName} saved`);
  await expect(own.getByTestId('centres')).toContainText(centreName);

  await own.goto('/budgets');
  await expect(own.getByTestId('departments')).toContainText('Housekeeping');
  const row = centreRow(own);
  await expect(row).toContainText('No budget');
  await row.getByRole('button', { name: 'Plan' }).click();
  const p = await dialog(own);
  await p.getByLabel('For the whole year').fill('12000');
  await expect(p).toContainText('₹1,000 a month');
  await p.getByRole('button', { name: 'Save' }).click();
  await said(own, `${centreName}: ₹12,000`);
  await expect(row).toContainText('On track');
  await expect(row).toContainText(inr(1000 * monthsSoFar()));
  await shot(own, '01-budget-planned');
});

test('S22-02 the manager records a GST bill with a photo of the receipt on his phone', async () => {
  await mgr.goto('/expenses');
  await mgr.getByRole('button', { name: 'Record expense' }).click();
  const d = await dialog(mgr);
  await d.getByLabel('What was it for').fill(what);
  await d.getByLabel('Amount paid (with GST)').fill('1180');
  await d.getByRole('combobox', { name: 'Category' }).press('Enter');
  await mgr.getByRole('option', { name: 'Repairs and maintenance' }).click();
  await d.getByRole('combobox', { name: 'Department' }).press('Enter');
  await mgr.getByRole('option', { name: centreName }).click();
  await d.getByLabel('Paid to').fill('Ganesh Plumbing');
  await d.getByText("It is a GST bill").click();
  await d.getByRole('textbox', { name: "Vendor's GSTIN" }).fill(goa.slice(0, 14) + 'A');
  await d.getByLabel('Bill number').fill(`GP-${runId}`);
  await d.getByLabel('GST on the bill').fill('180');
  await expect(d).toContainText('CGST ₹90 + SGST ₹90; taxable value ₹1,000.');
  await d.locator('[data-testid="receipt-input"]').setInputFiles({ name: 'plumber-bill.png', mimeType: 'image/png', buffer: PNG });
  await expect(d.locator('figure img')).toHaveCount(1);
  await noSideScroll(mgr);
  await d.getByRole('button', { name: 'Record' }).click();
  // A GSTIN with a wrong check digit is refused under its field, with the reason.
  await expect(d.locator('mat-error', { hasText: 'check digit' })).toBeVisible();
  await d.getByRole('textbox', { name: "Vendor's GSTIN" }).fill(goa);
  await shot(mgr, '02-record-on-phone');
  await d.getByRole('button', { name: 'Record' }).click();
  await said(mgr, /recorded: ₹1,180 to Ganesh Plumbing\. It waits for approval/);
  const item = mgr.getByTestId('expenses').locator('li', { hasText: what });
  await expect(item).toContainText('Waiting for approval');
  await expect(item).toContainText('CGST ₹90 + SGST ₹90');
  await expect(item.getByTestId('receipts').locator('img')).toHaveCount(1);
  // The manager records expenses but doesn't approve them.
  await expect(item.getByRole('button', { name: 'Approve' })).toHaveCount(0);
  await shot(mgr, '03-waiting-on-phone');
});

test('S22-03 while it waits, the budget doesn\'t count it', async () => {
  await own.goto('/budgets');
  const row = centreRow(own);
  await expect(row.locator('[data-label="Spent"]')).toContainText('₹0');
  await row.getByRole('button', { name: centreName }).click();
  await expect(row).toContainText('₹1,000 waits for approval');
});

test('S22-04 the owner looks at the receipt and approves it: ₹1,000 counts against the plan', async () => {
  await own.goto('/expenses');
  await expect(own.getByRole('radio', { name: /Waiting/ })).toBeChecked();
  const item = own.getByTestId('expenses').locator('li', { hasText: what });
  await item.getByRole('button').first().click();
  await expect(item.getByTestId('receipts').locator('img')).toHaveCount(1);
  await expect(item).toContainText('claimable as input credit');
  await shot(own, '04-approve');
  await item.getByRole('button', { name: 'Approve' }).click();
  await said(own, `approved: ₹1,000 counts in ${centreName}'s budget`);

  await own.goto('/budgets');
  const row = centreRow(own);
  await expect(row.locator('[data-label="Spent"]')).toContainText('₹1,000');
  await expect(row.locator('[data-label="Variance"]')).toContainText(`${inr(1000 * monthsSoFar() - 1000)} under`);
  await expect(own.getByTestId('months')).toBeVisible();
  await shot(own, '05-budget-vs-actual');
});

test('S22-05 the budget reads on a phone too', async () => {
  await mgr.goto('/budgets');
  const row = centreRow(mgr);
  await expect(row).toContainText('₹1,000');
  await expect(row.getByRole('button', { name: 'Plan' })).toHaveCount(0);   // the manager reads budgets, doesn't plan them
  await noSideScroll(mgr);
  await shot(mgr, '06-budget-on-phone');
});
