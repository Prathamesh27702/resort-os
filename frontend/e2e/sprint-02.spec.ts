import { Browser, Page, expect, test } from '@playwright/test';
import { addDays, apiAs, choose, createStaff, owner, runId, signIn, snack } from './helpers';

// Sprint 02 — the same checks as docs/sprints/REVIEWING.md → "Sprint 02", automated.
// Runs on the throwaway e2e database, which starts with the demo rooms from database/seed/dev.

test.describe.configure({ mode: 'serial' });

let p: Page;
let today = '';

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  p = await (await browser.newContext()).newPage();
  await signIn(p, owner.email, owner.password);
});

/** Opens the block dialog from the Rooms page (for one room, or the toolbar button). */
async function openBlock(room?: string) {
  await p.goto('/rooms');
  await expect(p.getByText('Dormitory bed').first()).toBeVisible();
  if (room) await p.locator('tr', { hasText: room }).first().getByRole('button', { name: new RegExp(`^Block`) }).click();
  else await p.locator('.actions').getByRole('button', { name: 'Block' }).click();
  await expect(p.locator('app-block-dialog')).toBeVisible();
  // The dialog opens on the property's own "today" (IST), which may differ from this computer's date.
  if (!today) today = await p.locator('input[formcontrolname=startDate]').inputValue();
}

async function fillBlock(start: string, end: string, reason: string) {
  await p.locator('input[formcontrolname=startDate]').fill(start);
  await p.locator('input[formcontrolname=endDate]').fill(end);
  await p.locator('input[formcontrolname=reason]').fill(reason);
}

const tile = (code: string) => p.locator('.tile', { has: p.locator('.code', { hasText: new RegExp(`^${code}$`) }) });

test('S02-01 Today has "Set up rooms" ticked; menu has Rooms, Room status, Room types', async () => {
  await p.goto('/dashboard');
  await expect(p.locator('li.done', { hasText: 'Set up rooms' })).toBeVisible();
  for (const item of ['Rooms', 'Room status', 'Room types']) await expect(p.locator('nav a', { hasText: new RegExp(`^\\S*${item}$`) })).toBeVisible();
});

test('S02-02 Rooms shows the demo rooms grouped by type', async () => {
  await p.goto('/rooms');
  await expect(p.locator('.page-head p')).toHaveText('6 rooms and 10 dorm beds at Palm Grove Beach Resort');
  await expect(p.locator('section.group', { hasText: 'Deluxe room' })).toContainText('up to 3 guests per room');
  await expect(p.locator('section.group', { hasText: 'Standard room' })).toContainText('4 rooms');
  await expect(p.locator('section.group', { hasText: 'Dormitory bed' })).toContainText('10 beds');
  await expect(p.locator('.chip.warn')).toHaveCount(0);
});

test('S02-03 block Room 103 for 2 nights with a reason', async () => {
  await openBlock('103');
  await fillBlock(today, addDays(today, 2), 'AC compressor replacement');
  await expect(p.locator('app-block-dialog .nights')).toContainText('2 nights');
  await p.locator('app-block-dialog').getByRole('button', { name: 'Block' }).click();
  expect(await snack(p)).toBe('Room 103 blocked for 2 nights');
  await expect(p.locator('tr', { hasText: '103' }).first().locator('.chip')).toHaveText('Blocked');
  await expect(p.locator('section.group', { hasText: 'Blocks' })).toContainText('AC compressor replacement');
});

test('S02-04 a second, overlapping block on 103 is refused', async () => {
  await openBlock('103');
  await fillBlock(addDays(today, 1), addDays(today, 2), 'Painting');
  await p.locator('app-block-dialog').getByRole('button', { name: 'Block' }).click();
  await expect(p.locator('app-block-dialog [role=alert]'))
    .toContainText('Room 103 is already blocked for the nights of');
  await expect(p.locator('app-block-dialog [role=alert]')).toContainText('(AC compressor replacement)');
  await p.locator('app-block-dialog').getByRole('button', { name: 'Cancel' }).click();
});

test('S02-05 Room status: 103 blocked tonight, free again in 2 nights (end date not included)', async () => {
  await p.goto('/rooms/status');
  await expect(tile('103')).toHaveClass(/BLOCKED/);
  await expect(tile('103')).toContainText('Free from');
  await p.getByRole('button', { name: 'Next night' }).click();
  await p.getByRole('button', { name: 'Next night' }).click();
  await expect(tile('103').locator('.state')).toHaveText('Available');
});

test('S02-06 dorm bed B10 taken out of use → 9 dorm beds', async () => {
  await p.goto('/rooms');
  await p.locator('tr', { hasText: 'DORM' }).first().click();
  const b10 = p.locator('tr', { hasText: 'B10' });
  await b10.locator('input[type=checkbox]').uncheck();
  await b10.getByRole('button', { name: 'Save' }).click();
  expect(await snack(p)).toBe('Bed B10 saved');
  await p.goto('/rooms');
  await expect(p.locator('.page-head p')).toHaveText('6 rooms and 9 dorm beds at Palm Grove Beach Resort');
});

test('S02-07 one dorm bed blocked; the whole dorm can then not be blocked the same night', async () => {
  await openBlock();
  await choose(p, 'mat-select[formcontrolname=unitId]', /^Dormitory/);
  await choose(p, 'mat-select[formcontrolname=bedId]', /\(B01\)/);
  await fillBlock(today, addDays(today, 1), 'Mattress replacement');
  await p.locator('app-block-dialog').getByRole('button', { name: 'Block' }).click();
  await expect(p.locator('app-block-dialog')).toBeHidden();
  await p.goto('/rooms/status');
  await expect(tile('DORM').locator('.state')).toHaveText('1 of 9 beds blocked');

  await openBlock();
  await choose(p, 'mat-select[formcontrolname=unitId]', /^Dormitory/);
  await fillBlock(today, addDays(today, 1), 'Fumigation');
  await p.locator('app-block-dialog').getByRole('button', { name: 'Block' }).click();
  await expect(p.locator('app-block-dialog [role=alert]')).toContainText('Dormitory (a bed) is already blocked');
  await p.locator('app-block-dialog').getByRole('button', { name: 'Cancel' }).click();
});

test('S02-08 a per-room room (101) has no beds section', async () => {
  await p.goto('/rooms');
  await p.locator('tr', { hasText: '101' }).first().click();
  await expect(p.locator('input[formcontrolname=name]')).toBeVisible();
  await expect(p.getByRole('heading', { name: 'Beds' })).toHaveCount(0);
});

test('S02-09 new room type "Garden cottage" → add room C1', async () => {
  await p.goto('/settings/room-types/new');
  await p.locator('input[formcontrolname=name]').fill('Garden cottage');
  await p.locator('input[formcontrolname=code]').fill('garden cottage');
  await expect(p.locator('input[formcontrolname=code]')).toHaveValue('GARDEN_COTTAGE');
  await p.locator('input[formcontrolname=maxOccupancy]').fill('4');
  await p.locator('mat-checkbox', { hasText: 'Balcony' }).click();
  await p.getByRole('button', { name: 'Add room type' }).click();
  await p.waitForURL(/\/rooms\/new\?type=/);
  await expect(p.locator('mat-select[formcontrolname=accommodationTypeId]')).toContainText('Garden cottage');
  await p.locator('input[formcontrolname=code]').fill('C1');
  await p.locator('input[formcontrolname=name]').fill('Cottage 1');
  await p.getByRole('button', { name: 'Add room' }).click();
  await p.waitForURL(/\/rooms$/);
  await expect(p.locator('section.group', { hasText: 'Garden cottage' })).toContainText('Cottage 1');
});

test('S02-10 a type with rooms in use can\'t be deactivated; after its room is off, it can', async () => {
  const openType = async () => {
    await p.goto('/settings/room-types');
    await p.locator('tr', { hasText: 'Garden cottage' }).click();
    await expect(p.locator('mat-slide-toggle')).toBeVisible();
  };
  await openType();
  await p.locator('mat-slide-toggle button').click();
  await p.getByRole('button', { name: 'Save changes' }).click();
  await expect(p.getByRole('alert')).toHaveText('Deactivate or move the rooms of this type first.');

  await p.goto('/rooms');
  await p.locator('tr', { hasText: 'C1' }).first().click();
  await expect(p.locator('mat-slide-toggle')).toBeVisible();
  await p.locator('mat-slide-toggle button').click();
  await p.getByRole('button', { name: 'Save changes' }).click();
  expect(await snack(p)).toBe('Changes saved');

  await openType();
  await p.locator('mat-slide-toggle button').click();
  await p.getByRole('button', { name: 'Save changes' }).click();
  expect(await snack(p)).toBe('Changes saved');
});

test('S02-11 release the 103 block early with a reason', async () => {
  await p.goto('/rooms');
  await p.locator('tr', { hasText: 'AC compressor replacement' }).getByRole('button', { name: 'Release' }).click();
  await p.locator('app-reason-dialog input').fill('Fixed early');
  await p.locator('app-reason-dialog').getByRole('button', { name: 'Release' }).click();
  expect(await snack(p)).toBe('Block released');
  await expect(p.locator('tr', { hasText: '103' }).first().locator('.chip')).toHaveText('Available');
});

test('S02-12 audit trail has both reasons', async () => {
  await p.goto('/audit');
  const reasons = p.locator('table.data .reason');
  await expect(reasons.filter({ hasText: 'AC compressor replacement' }).first()).toBeVisible();
  await expect(reasons.filter({ hasText: 'Fixed early' }).first()).toBeVisible();
});

test('S02-13 a receptionist can view rooms but not change anything', async ({ browser }) => {
  const { api } = await apiAs(owner.email, owner.password);
  const properties = await (await api.get('/api/v1/properties')).json();
  const email = `front.${runId}@demo.local`, password = `FrontDesk-${runId}-2026`;
  await createStaff(api, email, `Front Desk ${runId}`, 'RECEPTIONIST', properties[0].id, password);

  const r = await (await browser.newContext()).newPage();
  await signIn(r, email, password);
  await r.goto('/rooms');
  await expect(r.getByText('Dormitory bed').first()).toBeVisible();
  await expect(r.getByRole('link', { name: 'Add room' })).toHaveCount(0);
  await expect(r.locator('.actions').getByRole('button', { name: 'Block' })).toHaveCount(0);
  await expect(r.getByRole('button', { name: 'Release' })).toHaveCount(0);
  await expect(r.getByRole('link', { name: 'View type' }).first()).toBeVisible();
  await r.locator('tr', { hasText: '101' }).first().click();
  await expect(r.locator('input[formcontrolname=name]')).toBeDisabled();
  await r.goto('/rooms/new');
  await expect(r).toHaveURL(/\/dashboard/);
});

test('S02-14 phone width: Room status and Rooms fit without sideways scrolling', async () => {
  await p.setViewportSize({ width: 390, height: 844 });
  for (const url of ['/rooms/status', '/rooms']) {
    await p.goto(url);
    await expect(p.locator('h1')).toBeVisible();
    expect(await p.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), url).toBe(true);
  }
  await p.setViewportSize({ width: 1280, height: 860 });
});
