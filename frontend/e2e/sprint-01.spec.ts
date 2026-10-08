import { Browser, Page, expect, test } from '@playwright/test';
import { choose, failedSignIn, owner, runId, signIn, snack } from './helpers';

// Sprint 01 — the same checks as docs/sprints/REVIEWING.md → "Sprint 01", automated.
// Each test.step is one row of that table, so a failure tells you which step broke.

test.describe.configure({ mode: 'serial' });

const riya = { email: `riya.${runId}@demo.local`, name: `Riya Naik ${runId}`, temp: 'Welcome12345', own: `RiyaOwn-${runId}-2026` };
let ownerPage: Page;
let riyaPage: Page;

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ownerPage = await (await browser.newContext()).newPage();
  riyaPage = await (await browser.newContext()).newPage();   // a separate "private window"
});

test('S01-01 owner signs in and sees Today with the setup checklist', async () => {
  const p = ownerPage;
  await signIn(p, owner.email, owner.password);
  await expect(p.getByRole('heading', { level: 1 })).toContainText(/Good (morning|afternoon|evening), Demo/);
  await expect(p.getByText('Palm Grove Beach Resort').first()).toBeVisible();
  await expect(p.locator('.clock .tz')).toHaveText('IST');
  await expect(p.getByText('Add address and GST state')).toBeVisible();
});

test('S01-02 edit the property: address, PIN, phone, email; GST state is Goa', async () => {
  const p = ownerPage;
  await p.getByRole('link', { name: 'Edit property' }).first().click();
  await expect(p.locator('mat-select[formcontrolname=gstStateCode]')).toContainText('Goa (30)');
  await p.locator('input[formcontrolname=addressLine1]').fill('Beach Road, Calangute');
  await p.locator('input[formcontrolname=postalCode]').fill('403516');
  await p.locator('input[formcontrolname=phone]').fill('+91 832 000 0000');
  await p.locator('input[formcontrolname=email]').fill('frontdesk@demo.local');
  await p.getByRole('button', { name: 'Save changes' }).click();
  expect(await snack(p)).toBe('Changes saved');
  await p.goto('/dashboard');
  await expect(p.locator('li.done', { hasText: 'Add address and GST state' })).toBeVisible();
  await expect(p.locator('li.done', { hasText: 'Add guest contact details' })).toBeVisible();
});

test('S01-03 add a receptionist limited to one property', async () => {
  const p = ownerPage;
  await p.goto('/users/new');
  await p.locator('input[formcontrolname=fullName]').fill(riya.name);
  await p.locator('input[formcontrolname=email]').fill(riya.email);
  await p.locator('input[formcontrolname=temporaryPassword]').fill(riya.temp);
  await choose(p, 'mat-form-field:has(mat-label:text-is("Role")) mat-select', 'Receptionist');
  await choose(p, 'mat-form-field:has(mat-label:text-is("Where")) mat-select', 'Palm Grove Beach Resort');
  await p.getByRole('button', { name: 'Create account' }).click();
  expect(await snack(p)).toBe('Account created');
  await p.goto('/users');
  await expect(p.locator('tr', { hasText: riya.email })).toContainText('Awaiting first sign-in');
});

test('S01-04 roles matrix shows 17 roles; salary only for Owner and HR', async () => {
  const p = ownerPage;
  await p.goto('/roles');
  await expect(p.locator('table.matrix thead th.role')).toHaveCount(17);
  const salaryRow = p.locator('table.matrix tbody tr', { has: p.locator('code', { hasText: /^employee_salary\.read$/ }) });
  await expect(salaryRow.locator('.dot')).toHaveCount(2);
  await expect(salaryRow.locator('.dot[aria-label^="Owner has"]')).toHaveCount(1);
});

test('S01-05 audit trail shows the property edit and the new account', async () => {
  const p = ownerPage;
  await p.goto('/audit');
  await expect(p.locator('table.data')).toContainText('Beach Road, Calangute');
  await expect(p.locator('table.data')).toContainText(riya.email);
});

test('S01-06 receptionist must choose her own password at first sign-in', async () => {
  const p = riyaPage;
  await signIn(p, riya.email, riya.temp, /\/account\/password/);
  await expect(p.getByRole('heading', { name: 'Choose your own password' })).toBeVisible();
  await p.locator('input[formcontrolname=currentPassword]').fill(riya.temp);
  await p.locator('input[formcontrolname=newPassword]').fill(riya.own);
  await p.getByRole('button', { name: 'Change password' }).click();
  await p.waitForURL(/\/login/);
  await signIn(p, riya.email, riya.own);
});

test('S01-07 receptionist sees only what her role allows; /users sends her back', async () => {
  const p = riyaPage;
  const nav = p.locator('nav a');
  await expect(nav.filter({ hasText: 'Today' })).toBeVisible();
  await expect(nav.filter({ hasText: 'Properties' })).toBeVisible();
  await expect(nav.filter({ hasText: 'Staff accounts' })).toHaveCount(0);
  await expect(nav.filter({ hasText: 'Audit trail' })).toHaveCount(0);
  await p.goto('/users');
  await expect(p).toHaveURL(/\/dashboard/);
});

test('S01-08 owner deactivates the receptionist with a reason', async () => {
  const p = ownerPage;
  await p.goto('/users');
  await p.locator('tr', { hasText: riya.email }).click();
  await p.getByLabel('Reason (saved in the audit trail)').fill('Testing deactivation');
  await p.getByRole('button', { name: 'Deactivate' }).click();
  expect(await snack(p)).toBe('Account deactivated');
});

test('S01-09 the deactivated receptionist is signed out and cannot sign in', async () => {
  const p = riyaPage;
  await p.goto('/dashboard');
  await p.waitForURL(/\/login/);
  const msg = await failedSignIn(p, riya.email, riya.own);
  expect(msg).toBe('Email or password is incorrect.');
});

test('S01-10 audit trail shows the deactivation with the reason', async () => {
  const p = ownerPage;
  await p.goto('/audit');
  await expect(p.locator('table.data .reason').filter({ hasText: 'Testing deactivation' }).first()).toBeVisible();
});

test('S01-11 phone width: menu collapses behind ☰, page still usable', async () => {
  const p = ownerPage;
  await p.setViewportSize({ width: 390, height: 844 });
  await p.goto('/users');
  await expect(p.getByRole('button', { name: 'Open menu' })).toBeVisible();
  await expect(p.getByRole('heading', { name: 'Staff accounts' })).toBeVisible();
  expect(await p.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await p.setViewportSize({ width: 1280, height: 860 });
});

test('S01-12 five wrong passwords lock the account for 15 minutes', async ({ browser }) => {
  // A throwaway account so the lock doesn't affect anyone else.
  const email = `lock.${runId}@demo.local`;
  const p = ownerPage;
  await p.goto('/users/new');
  await p.locator('input[formcontrolname=fullName]').fill(`Lock Test ${runId}`);
  await p.locator('input[formcontrolname=email]').fill(email);
  await p.locator('input[formcontrolname=temporaryPassword]').fill('Welcome12345');
  await p.getByRole('button', { name: 'Create account' }).click();
  expect(await snack(p)).toBe('Account created');

  const page = await (await browser.newContext()).newPage();
  for (let i = 1; i <= 5; i++) expect(await failedSignIn(page, email, `Wrong-password-${i}`)).toBe('Email or password is incorrect.');
  expect(await failedSignIn(page, email, 'Welcome12345'))
    .toBe('Too many failed attempts. Try again later or ask a manager to reset your password.');
});
