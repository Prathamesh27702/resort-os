import { Browser, Page, expect, test } from '@playwright/test';
import { apiAs, choose, createStaff, owner, runId, shot, signIn, snack } from './helpers';

// Sprint 03 — the same checks as docs/sprints/REVIEWING.md → "Sprint 03", automated.
// The throwaway e2e database starts with the 20 demo guests from database/seed/dev/S002__guests.sql.

test.describe.configure({ mode: 'serial' });

// A 1×1 PNG, standing in for a photo of a passport page.
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64');

let p: Page;
let browserRef: Browser;
let newGuestUrl = '';
const staffPassword = `Staff-${runId}-2026`;
const reception = { email: `reception.${runId}@demo.local`, name: `Reena Reception ${runId}` };
const waiter = { email: `waiter.${runId}@demo.local`, name: `Vikram Waiter ${runId}` };
const housekeeper = { email: `hk.${runId}@demo.local`, name: `Hema Housekeeping ${runId}` };
// Unique per run so a second run never finds the first run's guest.
const tail = String(Date.now()).slice(-5);
const guestPhone = `98765 ${tail}`;

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  browserRef = browser;
  p = await (await browser.newContext()).newPage();
  await signIn(p, owner.email, owner.password);
  const { api } = await apiAs(owner.email, owner.password);
  const pid = (await (await api.get('/api/v1/properties')).json())[0].id as string;
  await createStaff(api, reception.email, reception.name, 'RECEPTIONIST', pid, staffPassword);
  await createStaff(api, waiter.email, waiter.name, 'WAITER', pid, staffPassword);
  await createStaff(api, housekeeper.email, housekeeper.name, 'HOUSEKEEPER', pid, staffPassword);
});

async function search(q: string) {
  await p.goto('/guests');
  await p.getByLabel('Search guests').fill(q);
}

test('S03-01 Guests lists the 20 demo guests', async () => {
  await p.goto('/guests');
  await expect(p.locator('nav a', { hasText: /Guests$/ })).toBeVisible();
  await expect(p.locator('table.data tbody tr')).toHaveCount(20);
  await shot(p, '01-guest-list');
  await expect(p.locator('tr', { hasText: 'Ananya Iyer' })).toBeVisible();
});

test('S03-02 a returning guest is found by phone in one search, typed any way', async () => {
  await search('90000 00104');
  await expect(p.locator('table.data tbody tr')).toHaveCount(1);
  await expect(p.locator('tbody tr').first()).toContainText('Ananya Iyer');
  await expect(p.locator('tbody tr').first()).toContainText('+91 90000 00104');
  await shot(p, '02-search-by-phone');
  await search('Fernandes');
  await expect(p.locator('tbody tr', { hasText: 'Rohan Fernandes' })).toContainText('VIP');
});

test('S03-03 adding a guest with an existing phone warns about a duplicate first', async () => {
  await p.goto('/guests/new');
  await p.locator('input[formcontrolname=firstName]').fill('Ananya');
  await p.locator('input[formcontrolname=lastName]').fill('Iyer');
  await p.locator('input[formcontrolname=phone]').fill('090000 00104');
  await p.locator('input[formcontrolname=email]').click();          // leaving the phone field checks for duplicates
  await expect(p.locator('.dupes')).toContainText('This guest may already have a profile');
  await expect(p.locator('.dupes')).toContainText('Same phone');
  await expect(p.getByRole('button', { name: 'Create a new profile anyway' })).toBeVisible();
  await shot(p, '03-duplicate-warning');
});

test('S03-04 add a new guest with address, GST state and a dietary preference', async () => {
  await p.goto('/guests/new');
  await p.locator('input[formcontrolname=firstName]').fill(`Kavya ${runId}`);
  await p.locator('input[formcontrolname=lastName]').fill('Desai');
  await p.locator('input[formcontrolname=phone]').fill(guestPhone);
  await p.locator('input[formcontrolname=email]').fill(`kavya.${runId}@example.in`);
  await p.locator('input[formcontrolname=city]').fill('Panaji');
  await choose(p, 'mat-select[formcontrolname=stateCode]', /^Goa$/);
  await p.locator('input[formcontrolname=postalCode]').fill('403001');
  await choose(p, '.add-pref mat-select', 'Dietary');
  await p.getByLabel('Preference').fill('Jain food');
  await p.locator('.add-pref').getByRole('button', { name: 'Add' }).click();
  await shot(p, '04-add-guest-form');
  await p.getByRole('button', { name: 'Add guest' }).click();
  expect(await snack(p)).toBe('Guest added');
  await expect(p.locator('h1')).toContainText(`Kavya ${runId} Desai`);
  await expect(p.locator('.addr')).toContainText('Panaji, Goa, 403001');
  await expect(p.getByText('Dietary: Jain food')).toBeVisible();
  newGuestUrl = new URL(p.url()).pathname;
});

test('S03-05 add a passport with a scan; the number is masked until opened, and opening it is audited', async () => {
  await p.goto(newGuestUrl);
  await p.getByRole('button', { name: 'Add document' }).click();
  await choose(p, 'app-document-dialog mat-select[formcontrolname=documentType]', 'Passport');
  await p.locator('app-document-dialog input[formcontrolname=number]').fill('Z 1234 567');
  await p.locator('app-document-dialog input[type=file]').setInputFiles({ name: 'passport.png', mimeType: 'image/png', buffer: PNG });
  await shot(p, '05-add-document');
  await p.getByRole('button', { name: 'Save document' }).click();
  expect(await snack(p)).toBe('Document saved');
  const row = p.locator('tr', { hasText: 'Passport' });
  await expect(row).toContainText('•••• 4567');
  await row.getByRole('button', { name: 'Show number' }).click();
  await expect(row).toContainText('Z1234567');
  await expect(row.getByRole('button', { name: 'View scan' })).toBeVisible();
  await row.scrollIntoViewIfNeeded();
  await shot(p, '06-documents-number-shown');

  await p.goto('/audit');
  await expect(p.locator('table.data')).toContainText('Opened (sensitive) · ID document');
  await shot(p, '07-audit-sensitive-read');
});

test('S03-06 an Aadhaar keeps only its last four digits', async () => {
  await p.goto(newGuestUrl);
  await p.getByRole('button', { name: 'Add document' }).click();
  await choose(p, 'app-document-dialog mat-select[formcontrolname=documentType]', 'Aadhaar');
  await p.locator('app-document-dialog input[formcontrolname=number]').fill('2345 6789 0123');
  await p.getByRole('button', { name: 'Save document' }).click();
  expect(await snack(p)).toBe('Document saved');
  const row = p.locator('tr', { hasText: 'Aadhaar' });
  await expect(row).toContainText('•••• 0123');
  await expect(row.getByRole('button', { name: 'Show number' })).toHaveCount(0);
});

test('S03-07 record the guest’s WhatsApp consent', async () => {
  await p.goto(newGuestUrl);
  const row = p.locator('tr', { hasText: 'Offers on WhatsApp' });
  await row.getByRole('button', { name: 'Yes' }).click();
  expect(await snack(p)).toBe('Consent recorded');
  await expect(row.locator('.chip')).toHaveText('Yes');
  await p.evaluate(() => window.scrollTo(0, 0));
  await shot(p, '08-guest-profile');
});

test('S03-08 a receptionist limited to one property finds the guest and sees ID documents', async () => {
  const r = await (await browserRef.newContext()).newPage();
  await signIn(r, reception.email, staffPassword);
  await r.goto('/guests');
  await r.getByLabel('Search guests').fill(guestPhone.replace(' ', ''));
  await r.locator('tbody tr', { hasText: 'Desai' }).click();
  await expect(r.getByRole('heading', { name: 'ID documents' })).toBeVisible();
  await expect(r.locator('tr', { hasText: 'Passport' })).toContainText('•••• 4567');
  await r.context().close();
});

test('S03-09 a waiter can look a guest up but never sees ID documents', async () => {
  const w = await (await browserRef.newContext()).newPage();
  await signIn(w, waiter.email, staffPassword);
  await w.goto(newGuestUrl);
  await expect(w.locator('h1')).toContainText('Desai');
  await expect(w.getByRole('heading', { name: 'ID documents' })).toHaveCount(0);
  await expect(w.getByRole('link', { name: 'Edit' })).toHaveCount(0);
  await shot(w, '09-waiter-no-documents');
  await w.goto('/guests/new');
  await expect(w).toHaveURL(/\/dashboard/);
  await w.context().close();
});

test('S03-10 a housekeeper has no access to guests at all', async () => {
  const h = await (await browserRef.newContext()).newPage();
  await signIn(h, housekeeper.email, staffPassword);
  await expect(h.locator('nav a', { hasText: /Guests$/ })).toHaveCount(0);
  await h.goto('/guests');
  await expect(h).toHaveURL(/\/dashboard/);
  await h.context().close();
});

test('S03-11 merge a duplicate profile into the original', async () => {
  const { api } = await apiAs(owner.email, owner.password);
  const dup = await api.post('/api/v1/guests', { data: {
    firstName: `Kavya ${runId}`, lastName: 'Desai', phone: null, email: `kavya.work.${runId}@example.com`,
    companyName: 'Desai Exports', isVip: false, ignoreDuplicates: true,
  } });
  expect(dup.status(), await dup.text()).toBe(201);

  await p.goto(newGuestUrl);
  await p.getByRole('button', { name: 'More actions' }).click();
  await p.getByRole('menuitem', { name: 'Merge a duplicate into this profile' }).click();
  await p.locator('app-merge-dialog input').first().fill(`kavya.work.${runId}`);
  await p.locator('app-merge-dialog mat-radio-button').first().click();
  await p.locator('app-merge-dialog input[formcontrolname=reason]').fill('Same guest, second profile from a phone booking');
  await shot(p, '10-merge-dialog');
  await p.locator('app-merge-dialog').getByRole('button', { name: 'Merge' }).click();
  expect(await snack(p)).toBe('Profiles merged');
  await expect(p.locator('.page-head p')).toContainText('Desai Exports');      // empty field filled from the duplicate

  await search(`kavya.work.${runId}`);
  await expect(p.locator('tbody')).toContainText('No guest matches');
});

test('S03-12 anonymise the guest: personal data and documents are gone, the profile stays', async () => {
  await p.goto(newGuestUrl);
  await p.getByRole('button', { name: 'More actions' }).click();
  await p.getByRole('menuitem', { name: /Anonymise/ }).click();
  await p.locator('app-reason-dialog input').fill('Guest asked to be forgotten');
  await p.locator('app-reason-dialog').getByRole('button', { name: 'Anonymise' }).click();
  expect(await snack(p)).toBe('Guest anonymised');
  await expect(p.locator('h1')).toContainText('Anonymised guest');
  await expect(p.locator('.banner')).toContainText('Personal data was removed');
  await expect(p.locator('tr', { hasText: 'Passport' })).toContainText('Purged');
  await shot(p, '11-anonymised');

  await search(guestPhone);
  await expect(p.locator('tbody')).toContainText('No guest matches');
});

test('S03-13 phone width: list and profile are usable', async () => {
  await p.setViewportSize({ width: 390, height: 844 });
  await search('Iyer');
  await p.locator('tbody tr', { hasText: 'Ananya Iyer' }).click();
  await expect(p.getByRole('heading', { name: 'Contact' })).toBeVisible();
  await shot(p, '12-phone-profile');
  expect(await p.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await p.setViewportSize({ width: 1280, height: 860 });
});
