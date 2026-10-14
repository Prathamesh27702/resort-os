import { Browser, Page, expect, test } from '@playwright/test';
import { addDays, apiAs, createStaff, owner, runId, shot, signIn, snack } from './helpers';

// Sprint 04 — the same checks as docs/sprints/REVIEWING.md → "Sprint 04", automated.
// The throwaway e2e database starts with the demo rates from database/seed/dev/S003__rates.sql:
// BAR (default), Standard room ₹3,500 a night and ₹4,200 on Friday/Saturday nights, and two packages.

test.describe.configure({ mode: 'serial' });

let p: Page;
let browserRef: Browser;
let thursday = '';                 // next week's Thursday at the property, so the stay crosses into the weekend
const staffPassword = `Staff-${runId}-2026`;
const reception = { email: `reception4.${runId}@demo.local`, name: `Rekha Reception ${runId}` };

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  browserRef = browser;
  p = await (await browser.newContext()).newPage();
  await signIn(p, owner.email, owner.password);
  const { api } = await apiAs(owner.email, owner.password);
  const prop = (await (await api.get('/api/v1/properties')).json())[0];
  const today = prop.localDate as string;
  const dow = new Date(today + 'T00:00:00Z').getUTCDay();
  thursday = addDays(today, ((4 - dow + 7) % 7) + 7);
  await createStaff(api, reception.email, reception.name, 'RECEPTIONIST', prop.id, staffPassword);
});

const bulk = () => p.locator('form[aria-label="Set prices"]');
const quoteForm = () => p.locator('form[aria-label="Quote"]');
const cell = (typeCode: string, date: string) => p.locator(`td[data-cell="${typeCode}|${date}"]`);

/** Like choose(), for selects whose empty label covers them (Playwright sees the label, not the select, under the click). */
async function pick(select: string, option: string | RegExp) {
  await p.locator(select).click({ force: true });
  await p.getByRole('option', { name: option }).click();
}

async function showWeekOf(date: string) {
  await p.getByLabel('First night shown').fill(date);
  await p.getByLabel('First night shown').dispatchEvent('change');
}

test('S04-01 Rates shows the BAR grid with the weekend uplift', async () => {
  await p.goto('/settings/rates');
  await expect(p.locator('nav a', { hasText: /Rates$/ })).toBeVisible();
  await expect(p.locator('mat-select[aria-label="Rate plan"]')).toContainText('Best available rate (BAR) — default');
  await showWeekOf(thursday);
  await expect(cell('STANDARD_ROOM', thursday)).toContainText('₹3,500');
  await expect(cell('STANDARD_ROOM', addDays(thursday, 1))).toContainText('₹4,200');
  await expect(cell('DELUXE_ROOM', addDays(thursday, 2))).toContainText('₹6,000');
  await expect(cell('DORMITORY', thursday)).toContainText('₹800');
  // Every price sits exactly under its night's header (the grid is one table, so columns line up)
  const headers = p.locator('table.rate-grid thead th');
  for (const [i, date] of [thursday, addDays(thursday, 1), addDays(thursday, 6)].entries()) {
    const head = await headers.nth([1, 2, 7][i]).boundingBox();
    for (const type of ['DELUXE_ROOM', 'STANDARD_ROOM', 'DORMITORY']) {
      const box = await cell(type, date).boundingBox();
      expect(Math.abs(box!.x - head!.x), `${type} ${date} under its header`).toBeLessThan(1);
      expect(Math.abs(box!.width - head!.width), `${type} ${date} as wide as its header`).toBeLessThan(1);
    }
  }
  await shot(p, '01-rate-grid');
});

test('S04-02 set weekend prices for the Standard room', async () => {
  await pick('mat-select[formcontrolname=accommodationTypeIds]', 'Standard room');
  await p.keyboard.press('Escape');
  await bulk().locator('input[formcontrolname=firstNight]').fill(thursday);
  await bulk().locator('input[formcontrolname=lastNight]').fill(addDays(thursday, 13));
  await bulk().getByRole('button', { name: 'Weekend (Fri, Sat)' }).click();
  await bulk().locator('input[formcontrolname=price]').fill('4500');
  await bulk().locator('input[formcontrolname=extraAdult]').fill('1000');
  await bulk().locator('input[formcontrolname=extraChild]').fill('500');
  await shot(p, '02-set-weekend-prices');
  await bulk().getByRole('button', { name: 'Apply prices' }).click();
  expect(await snack(p)).toBe('Prices saved in Best available rate: 4 changed');
  await expect(cell('STANDARD_ROOM', addDays(thursday, 1))).toContainText('₹4,500');
  await expect(cell('STANDARD_ROOM', addDays(thursday, 2))).toContainText('₹4,500');
  await expect(cell('STANDARD_ROOM', addDays(thursday, 3))).toContainText('₹3,500');   // Sunday stays a weekday
});

test('S04-03 a quote for Room 103, 2 adults, 3 nights matches the grid', async () => {
  await pick('mat-select[formcontrolname=target]', /^Room 103/);
  await quoteForm().locator('input[formcontrolname=from]').fill(thursday);
  await quoteForm().locator('input[formcontrolname=nights]').fill('3');
  await quoteForm().locator('input[formcontrolname=adults]').fill('2');
  await quoteForm().getByRole('button', { name: /Get quote/ }).click();
  const result = p.locator('[aria-label="Quote result"]');
  await expect(result.locator('tbody tr')).toHaveCount(4);         // 3 nights + total
  await expect(result.locator('tbody tr').nth(0)).toContainText('₹3,500');
  await expect(result.locator('tbody tr').nth(1)).toContainText('₹4,500');
  await expect(result.locator('tbody tr').nth(2)).toContainText('₹4,500');
  await expect(result.locator('[data-total]')).toHaveText('₹12,500');
  await result.scrollIntoViewIfNeeded();
  await shot(p, '03-quote-room-103');
});

test('S04-04 errors say why, under the field they concern', async () => {
  await bulk().locator('input[formcontrolname=price]').fill('-5');
  await bulk().getByRole('button', { name: 'Apply prices' }).click();
  await expect(bulk().locator('mat-error')).toContainText("Price can't be negative.");

  // Standard rooms sleep 2: the API's reason is shown as it is
  await quoteForm().locator('input[formcontrolname=adults]').fill('3');
  await quoteForm().getByRole('button', { name: /Get quote/ }).click();
  await expect(quoteForm().getByRole('alert')).toHaveText('Standard room sleeps at most 2 guests; this quote has 3 (3 adults).');
  await shot(p, '04-error-says-why');
});

test('S04-05 create a package and show its revenue split', async () => {
  await p.goto('/settings/packages');
  await expect(p.locator('table.data tbody tr', { hasText: 'Couple getaway' })).toContainText('₹14,999');
  await shot(p, '05-packages');

  await p.getByRole('link', { name: 'Add package' }).click();
  await p.locator('input[formcontrolname=name]').fill(`Monsoon escape ${runId}`);
  await p.locator('input[formcontrolname=code]').fill(`MONSOON_${runId}`);
  await pick('mat-select[formcontrolname=accommodationTypeId]', 'Deluxe room');
  await p.locator('input[formcontrolname=nights]').fill('2');
  await p.locator('input[formcontrolname=price]').fill('9999.99');
  const parts = p.locator('.part');
  await parts.nth(0).locator('input[formcontrolname=description]').fill('2 nights in a Deluxe room');
  await parts.nth(0).locator('input[formcontrolname=sharePercent]').fill('60');
  await p.getByRole('button', { name: 'Add part' }).click();
  await parts.nth(1).locator('input[formcontrolname=description]').fill('Breakfast and dinner');
  await parts.nth(1).locator('input[formcontrolname=sharePercent]').fill('25.5');
  await p.getByRole('button', { name: 'Add package' }).click();
  await expect(p.locator('[data-split-error]')).toHaveText('The shares add up to 85.5%; they must add up to exactly 100%.');

  await p.getByRole('button', { name: 'Add part' }).click();         // pre-filled with the 14.5 % left
  await pick('.part:nth-child(3) mat-select[formcontrolname=category]', 'Activity');
  await parts.nth(2).locator('input[formcontrolname=description]').fill('Sunset kayak trip');
  await expect(parts.nth(0).locator('.amount')).toHaveText('₹5,999.99');
  await expect(parts.nth(1).locator('.amount')).toHaveText('₹2,550.00');
  await expect(parts.nth(2).locator('.amount')).toHaveText('₹1,450.00');
  await expect(p.locator('.split-foot')).toContainText('Amounts: ₹9,999.99');
  await shot(p, '06-package-split');
  await p.getByRole('button', { name: 'Add package' }).click();
  expect(await snack(p)).toBe(`Monsoon escape ${runId} added`);
  const row = p.locator('table.data tbody tr', { hasText: `Monsoon escape ${runId}` });
  await expect(row).toContainText('Room ₹5,999.99');
  await expect(row).toContainText('Food ₹2,550');
  await expect(row).toContainText('Activity ₹1,450');
});

test('S04-06 a receptionist sees rates and can quote, but not change prices', async () => {
  const r = await (await browserRef.newContext()).newPage();
  await signIn(r, reception.email, staffPassword);
  await r.goto('/settings/rates');
  await expect(r.locator('table.rate-grid')).toBeVisible();
  await expect(r.locator('form[aria-label="Set prices"]')).toHaveCount(0);
  await expect(r.getByRole('button', { name: 'Add rate plan' })).toHaveCount(0);
  await expect(r.locator('form[aria-label="Quote"]')).toBeVisible();
  await r.close();
});
