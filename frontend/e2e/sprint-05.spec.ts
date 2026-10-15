import { Browser, Page, expect, test } from '@playwright/test';
import { addDays, apiAs, createStaff, fieldDate, owner, runId, shot, signIn } from './helpers';

// Sprint 05 — the same checks as docs/sprints/REVIEWING.md → "Sprint 05", automated.
// The throwaway e2e database starts with the demo bookings from database/seed/dev/S004__reservations.sql:
// R00001 has Room 101 (Deluxe) tonight, so Room 102 is the last Deluxe room tonight.

test.describe.configure({ mode: 'serial' });

let p: Page;          // the owner
let q: Page;          // a receptionist in a second browser
let today = '';
const staffPassword = `Staff-${runId}-2026`;
const reception = { email: `reception5.${runId}@demo.local`, name: `Ravi Reception ${runId}` };
const walkIn = { first: 'Neha', last: `Walkin ${runId}`, phone: `98${Date.now().toString().slice(-8)}` };

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  const { api } = await apiAs(owner.email, owner.password);
  const prop = (await (await api.get('/api/v1/properties')).json())[0];
  today = prop.localDate as string;
  await createStaff(api, reception.email, reception.name, 'RECEPTIONIST', prop.id, staffPassword);
  p = await (await browser.newContext()).newPage();
  q = await (await browser.newContext()).newPage();
  await signIn(p, owner.email, owner.password);
  await signIn(q, reception.email, staffPassword);
});

const field = (page: Page, label: string) => page.locator('mat-form-field', { hasText: label });

/** Steps 1–2 of the wizard: dates and guests, then Check availability. */
async function searchStay(page: Page, from: string, to: string, adults: number) {
  await page.goto('/reservations/new');
  await page.getByLabel('Check-in').fill(from);
  await page.getByLabel('Check-out').fill(to);
  await page.getByLabel('Adults').fill(String(adults));
  await page.getByRole('button', { name: 'Check availability' }).click();
}

/** Steps 3–4: pick a returning guest by name, keep the default plan and source, go to the confirm step. */
async function guestAndRate(page: Page, guestSearch: string, guestName: RegExp) {
  await page.getByLabel('Name, phone or email').fill(guestSearch);
  await page.getByRole('button', { name: guestName }).first().click();
  await expect(page.getByText('Total before tax')).toBeVisible();
  await page.getByRole('button', { name: 'Continue' }).click();
}

test('S05-01 Reservations lists the demo bookings, soonest arrival first', async () => {
  await p.goto('/reservations');
  await expect(p.locator('nav a', { hasText: /Reservations$/ })).toBeVisible();
  const rows = p.locator('table.data tbody tr');
  await expect(rows.filter({ hasText: 'R00001' })).toContainText('Room 101');
  await expect(rows.filter({ hasText: 'R00004' })).toContainText('Dormitory · 2 beds');
  await expect(rows.filter({ hasText: 'R00005' })).toHaveCount(0);   // an enquiry, not a booking
  await shot(p, '01-reservations');
  await p.getByRole('radio', { name: 'Enquiries' }).click();
  await expect(rows.filter({ hasText: 'R00005' })).toContainText('Enquiry');
});

test('S05-02 A check-in in the past is refused under the check-in field', async () => {
  await searchStay(p, addDays(today, -1), addDays(today, 1), 2);
  await expect(field(p, 'Check-in').locator('mat-error')).toContainText("Check-in can't be before today");
  await shot(p, '02-error-under-field');
});

test('S05-03 Availability shows one Deluxe room left tonight', async () => {
  await searchStay(p, today, addDays(today, 1), 2);
  const deluxe = p.locator('.type', { hasText: 'Deluxe room' });
  await expect(deluxe).toBeVisible();                                 // the wizard moved on to step 2
  await expect(deluxe).toContainText('1 of 2 rooms free');
  // R00004 holds two dorm beds tonight (the Sprint 02 steps may also have taken a bed out of use or blocked one)
  await expect(p.locator('.type', { hasText: 'Dormitory bed' })).toContainText(/\d+ of (9|10) beds free/);
  await shot(p, '03-availability');
});

test('S05-04 Two people try to book the last Deluxe room: one gets it, the other is told why', async () => {
  // Both reach the confirm step for the same room before either books.
  for (const [page, guest, name] of [[p, 'Diya', /Diya Patel/], [q, 'Ishita', /Ishita Banerjee/]] as const) {
    await searchStay(page, today, addDays(today, 1), 2);
    await page.getByRole('button', { name: 'Choose Deluxe room' }).click();
    await guestAndRate(page, guest, name);
    await expect(page.locator('dl.summary')).toBeVisible();
    await expect(page.locator('dl.summary')).toContainText('Deluxe room · first free room (Room 102)');
  }
  await shot(p, '04-confirm-step');

  await p.getByRole('button', { name: 'Book' }).click();
  await expect(p.locator('mat-snack-bar-container').last()).toContainText(/Booked: R\d{5}/);
  await expect(p).toHaveURL(/\/reservations\/[0-9a-f-]{36}$/);
  await expect(p.locator('h1')).toContainText('Diya Patel');
  await expect(p.locator('section', { has: p.getByRole('heading', { name: 'Rooms' }) })).toContainText('Room 102');

  await q.getByRole('button', { name: 'Book' }).click();
  await expect(q.getByRole('alert')).toContainText(/^No Deluxe room is free for the night of .*\. Choose another room type or other dates\.$/);
  await shot(q, '05-last-room-taken');
  await q.getByRole('button', { name: "See what's free now" }).click();
  await expect(q.locator('.type', { hasText: 'Deluxe room' })).toContainText('0 of 2 rooms free');
});

test('S05-05 A walk-in guest is added and booked into the dormitory', async () => {
  await searchStay(q, today, addDays(today, 2), 2);
  await q.getByRole('button', { name: 'Choose Dormitory bed' }).click();
  await q.getByRole('textbox', { name: 'First name', exact: true }).fill(walkIn.first);
  await q.getByRole('textbox', { name: 'Last name', exact: true }).fill(walkIn.last);
  await q.getByRole('textbox', { name: 'Phone', exact: true }).fill(walkIn.phone);
  await q.getByRole('button', { name: 'Add guest and continue' }).click();
  await expect(q.getByText('Total before tax')).toBeVisible();
  await expect(q.getByText('2 beds ×').first()).toBeVisible();
  await q.getByRole('button', { name: 'Continue' }).click();
  await expect(q.locator('dl.summary')).toBeVisible();
  await q.getByRole('button', { name: 'Book' }).click();
  await expect(q.locator('mat-snack-bar-container').last()).toContainText(/Booked: R\d{5}/);
  await expect(q.locator('section', { has: q.getByRole('heading', { name: 'Rooms' }) }).locator('tbody tr')).toHaveCount(2);
  await expect(q.locator('section', { has: q.getByRole('heading', { name: 'Rooms' }) })).toContainText(/bed B\d\d/);
});

test('S05-06 The booking page: add a note, change the dates, cancel with a reason', async () => {
  // p is on Diya's booking from S05-04.
  await p.getByLabel('Add a note').fill('Advance of ₹2,000 received by UPI');
  await p.getByRole('button', { name: 'Add note' }).click();
  await expect(p.locator('mat-snack-bar-container').last()).toContainText('Note added');
  await expect(p.locator('.note').first()).toContainText('Advance of ₹2,000 received by UPI');

  // Moving the stay to start yesterday is refused under the field
  await p.getByRole('button', { name: /Change dates or guests/ }).click();
  const dlg = p.locator('mat-dialog-container');
  await dlg.getByLabel('Check-out').fill(fieldDate(today));
  await dlg.getByRole('button', { name: 'Save and re-price' }).click();
  await expect(dlg.locator('mat-form-field', { hasText: 'Check-out' }).locator('mat-error')).toHaveText('The check-out date must be after check-in.');
  // Two nights instead of one: Room 102 is free tomorrow too
  await dlg.getByLabel('Check-out').fill(fieldDate(addDays(today, 2)));
  await dlg.getByLabel('Check-out').blur();
  await shot(p, '06-change-dates');
  await dlg.getByRole('button', { name: 'Save and re-price' }).click();
  await expect(p.locator('mat-snack-bar-container').last()).toContainText(/updated · new total/);
  await expect(p.locator('h1 + p')).toContainText('2 nights');

  await p.getByRole('button', { name: 'Cancel booking' }).click();
  await p.locator('mat-dialog-container').getByLabel('Reason').fill('Guest changed plans');
  await p.locator('mat-dialog-container').getByRole('button', { name: 'Cancel booking' }).click();
  await expect(p.locator('mat-snack-bar-container').last()).toContainText(/cancelled/);
  await expect(p.locator('h1 .chip')).toHaveText('Cancelled');
  await expect(p.locator('section', { hasText: 'History' }).last()).toContainText('“Guest changed plans”');
  await shot(p, '07-cancelled-with-history');
});

test('S05-07 The guest profile lists the stay with its booking reference', async () => {
  const ref = (await p.locator('h1 .num').textContent())!.trim();
  await p.locator('section', { hasText: 'Guests' }).getByRole('link', { name: 'Diya Patel' }).click();
  const stays = p.locator('section', { has: p.getByRole('heading', { name: 'Stays' }) });
  await expect(stays.getByRole('link', { name: ref })).toBeVisible();
  await expect(stays.locator('p', { hasText: ref })).toContainText('Cancelled');
});

