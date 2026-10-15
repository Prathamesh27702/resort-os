import { APIRequestContext, Page, expect, request } from '@playwright/test';
import { API_PORT, OWNER_EMAIL } from './settings.mjs';

export const owner = { email: OWNER_EMAIL, password: process.env['E2E_OWNER_PASSWORD']! };

/** Unique per run, so re-running never collides with earlier data. */
export const runId = Date.now().toString(36).slice(-5);

/**
 * Signs in through the real sign-in page. The API allows 10 sign-ins a minute per IP; if a test hits that,
 * this waits for the next minute and tries again (that is the limit working, not a bug).
 */
export async function signIn(page: Page, email: string, password: string, expectUrl = /\/dashboard/) {
  for (let attempt = 0; attempt < 3; attempt++) {
    await page.goto('/login');
    await page.locator('input[formcontrolname=email]').fill(email);
    await page.locator('input[formcontrolname=password]').fill(password);
    await page.getByRole('button', { name: 'Sign in' }).click();
    const outcome = await Promise.race([
      page.waitForURL(expectUrl, { timeout: 15_000 }).then(() => 'ok'),
      page.getByRole('alert').filter({ hasText: /Too many requests/ }).waitFor({ timeout: 15_000 }).then(() => 'limited'),
    ]);
    if (outcome === 'ok') return;
    await page.waitForTimeout(61_000);
  }
  throw new Error(`Could not sign in as ${email}`);
}

/** Types a wrong password once and returns the message shown (used by the lockout check). */
export async function failedSignIn(page: Page, email: string, password: string) {
  await page.goto('/login');
  await page.locator('input[formcontrolname=email]').fill(email);
  await page.locator('input[formcontrolname=password]').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  const alert = page.getByRole('alert');
  await expect(alert).toBeVisible();
  const text = (await alert.textContent())?.trim() ?? '';
  if (/Too many requests/.test(text)) { await page.waitForTimeout(61_000); return failedSignIn(page, email, password); }
  return text;
}

/** The last snack-bar message ("Changes saved", "Room 103 blocked for 2 nights", …). */
export async function snack(page: Page) {
  const s = page.locator('mat-snack-bar-container').last();
  await expect(s).toBeVisible();
  return (await s.textContent())?.trim() ?? '';
}

/**
 * Saves a screenshot for the sprint's review guide when E2E_SCREENSHOTS names a folder, e.g.
 *   E2E_SCREENSHOTS=../docs/sprints/sprint-03-screens npm run e2e -- e2e/sprint-03.spec.ts
 * Does nothing otherwise, so normal runs are unaffected.
 */
export async function shot(page: Page, name: string) {
  const dir = process.env['E2E_SCREENSHOTS'];
  if (!dir) return;
  await page.waitForTimeout(400);   // let snack bars and menus settle
  await page.screenshot({ path: `${dir}/${name}.png` });
}

/** Picks an option in an Angular Material select. */
export async function choose(page: Page, select: string, option: string | RegExp) {
  await page.locator(select).click();
  await page.getByRole('option', { name: option }).click();
}

/** A date field's text ("15 Oct 2026") as YYYY-MM-DD. */
export function isoFromField(text: string) {
  const [d, mon, y] = text.trim().split(/\s+/);
  const m = ['jan', 'feb', 'mar', 'apr', 'may', 'jun', 'jul', 'aug', 'sep', 'oct', 'nov', 'dec'].indexOf(mon.slice(0, 3).toLowerCase());
  return `${y}-${String(m + 1).padStart(2, '0')}-${d.padStart(2, '0')}`;
}

/** YYYY-MM-DD plus N days, without time-zone surprises. */
export function addDays(iso: string, days: number) {
  const [y, m, d] = iso.split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, d + days)).toISOString().slice(0, 10);
}

/** A signed-in API client, used only to prepare data a test isn't about (e.g. a receptionist account). */
export async function apiAs(email: string, password: string): Promise<{ api: APIRequestContext; token: string }> {
  const api = await request.newContext({ baseURL: `http://localhost:${API_PORT}` });
  for (let attempt = 0; attempt < 3; attempt++) {
    const res = await api.post('/api/v1/auth/login', { data: { email, password } });
    if (res.status() === 429) { await new Promise(r => setTimeout(r, 61_000)); continue; }
    expect(res.status(), await res.text()).toBe(200);
    const token = (await res.json()).accessToken as string;
    const authed = await request.newContext({ baseURL: `http://localhost:${API_PORT}`, extraHTTPHeaders: { authorization: `Bearer ${token}` } });
    return { api: authed, token };
  }
  throw new Error(`API sign-in failed for ${email}`);
}

/** Creates a staff account with a temporary password and replaces it with a permanent one (skips the forced change screen). */
export async function createStaff(ownerApi: APIRequestContext, email: string, fullName: string, roleCode: string, propertyId: string | null, password: string) {
  const temp = `Temp-${runId}-12345`;
  const created = await ownerApi.post('/api/v1/users', { data: { email, fullName, temporaryPassword: temp, roles: [{ roleCode, propertyId }] } });
  expect(created.status(), await created.text()).toBe(201);
  const { api } = await apiAs(email, temp);
  let res = await api.post('/api/v1/auth/change-password', { data: { currentPassword: temp, newPassword: password } });
  if (res.status() === 429) {
    await new Promise(r => setTimeout(r, 61_000));
    res = await api.post('/api/v1/auth/change-password', { data: { currentPassword: temp, newPassword: password } });
  }
  expect(res.status(), await res.text()).toBe(204);
}
