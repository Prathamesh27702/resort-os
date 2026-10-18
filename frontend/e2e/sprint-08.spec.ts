import { APIRequestContext, Browser, Page, expect, test } from '@playwright/test';
import { addDays, apiAs, choose, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 08 — the same checks as docs/sprints/REVIEWING.md → "Sprint 08", automated.
// The demo data (database/seed/dev/S007) gives R00008 a bill with a room night, dinner and an airport pick-up. This file
// also books its own two-night stay tonight and checks it in through the API, so its checks don't depend on what the
// earlier sprint files did to the demo guests (Sprint 07 checks R00008 out early).

test.describe.configure({ mode: 'serial' });

let p: Page;          // the owner (can reverse)
let q: Page;          // a receptionist (posts charges and moves lines, can't reverse)
let api: APIRequestContext;
let pid = '';
let today = '';
const staffPassword = `Staff-${runId}-2026`;
const reception = { email: `reception8.${runId}@demo.local`, name: `Rekha Reception ${runId}` };
const own = { id: '', ref: '', guest: '', folioId: '', nights: 0 };

interface FreeType { inventoryMode: string; accommodationTypeId: string; freeRooms: { id: string; name: string }[] }

// Sprint 9: the demo property is GST-registered, so food carries 5% GST (₹1,450 + ₹72.50); transport and activities have no
// tax category, so they carry none
const dinner = 1450 + 72.5;
const money = (v: number) => new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', minimumFractionDigits: 2 }).format(v);
const row = (page: Page, desc: string) => page.locator(`tr[data-line="${desc}"]`);
const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text })).toBeVisible();

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  const prop = (await (await api.get('/api/v1/properties')).json())[0];
  pid = prop.id;
  today = prop.localDate as string;
  await createStaff(api, reception.email, reception.name, 'RECEPTIONIST', pid, staffPassword);

  // this file's own guest: two nights from tonight, checked in (the room night goes on the bill at check-in)
  const avail = await (await api.get(`/api/v1/properties/${pid}/availability?from=${today}&to=${addDays(today, 2)}&adults=1&children=0`)).json();
  const type = (avail.types as FreeType[]).find(t => t.inventoryMode === 'UNIT' && t.freeRooms.length >= 1)!;
  const source = (await (await api.get(`/api/v1/properties/${pid}/reservation-sources`)).json()).find((s: { code: string }) => s.code === 'PHONE').id;
  const guest = (await (await api.get('/api/v1/guests?pageSize=6')).json()).items[5];
  const res = await api.post(`/api/v1/properties/${pid}/reservations`, {
    headers: { 'Idempotency-Key': `e2e-s08-own-${runId}` },
    data: { checkIn: today, checkOut: addDays(today, 2), rooms: [{ accommodationTypeId: type.accommodationTypeId, unitId: type.freeRooms[0].id, adults: 1, children: 0 }],
            guestIds: [guest.id], sourceId: source, ratePlanId: null, status: 'CONFIRMED', specialRequests: null, note: null },
  });
  expect(res.status(), await res.text()).toBe(201);
  const booked = await res.json();
  own.id = booked.id;
  own.ref = booked.reference;
  const inn = await api.post(`/api/v1/properties/${pid}/reservations/${own.id}/check-in`,
    { data: { version: null, overrideHousekeeping: true, overrideReason: 'Browser test: room checked by hand' } });
  expect(inn.status(), await inn.text()).toBe(200);
  const folios = await (await api.get(`/api/v1/properties/${pid}/reservations/${own.id}/folios`)).json();
  const main = folios.find((f: { isPrimary: boolean }) => f.isPrimary);
  own.folioId = main.id;
  own.guest = main.guestName;
  own.nights = main.balance;
  expect(own.nights).toBeGreaterThan(0);

  p = await (await browser.newContext()).newPage();
  q = await (await browser.newContext()).newPage();
  await signIn(p, owner.email, owner.password);
  await signIn(q, reception.email, staffPassword);
});

test('S08-01 Folios: find a guest’s bill and see every line with what it is for', async () => {
  await q.goto('/dashboard');
  await q.locator('nav a[href="/folios"]').click();
  await expect(q).toHaveURL(/\/folios/);
  await q.getByRole('radio', { name: 'All', exact: true }).click();
  await q.getByLabel('Search').fill('R00008');
  await expect(q.locator('tr[data-folio]')).toHaveCount(1);
  await shot(q, '01-folios');
  await q.locator('tr[data-folio]').first().click();
  await expect(q).toHaveURL(/\/folios\/[0-9a-f-]{36}/);
  await expect(row(q, 'Dinner at the beach shack')).toContainText(money(1450));
  await expect(row(q, 'Airport pick-up (Goa airport)')).toContainText(money(1200));
  await expect(q.locator('.cats')).toContainText('Food');
  await expect(q.locator('.cats')).toContainText('Transport');
  await shot(q, '02-folio');
});

test('S08-02 The receptionist posts dinner, a transfer and kayaking; a missing description is named under its field', async () => {
  await q.goto(`/reservations/${own.id}`);
  await q.getByTestId('bill').click();
  await expect(q).toHaveURL(new RegExp(`/folios/${own.folioId}`));
  await expect(q.locator('tr[data-line^="Room night"]').first()).toBeVisible();
  await expect(q.getByTestId('balance')).toHaveText(money(own.nights));

  const post = async (category: string, description: string, quantity: string, price: string) => {
    await q.getByRole('button', { name: 'Post charge' }).click();
    const dlg = q.locator('mat-dialog-container');
    await choose(q, 'mat-dialog-container mat-select[formcontrolname=category]', category);
    await dlg.getByLabel('Description').fill(description);
    await dlg.getByLabel('Quantity').fill(quantity);
    await dlg.getByLabel('Price each (₹)').fill(price);
    return dlg;
  };

  // no description: the reason is under the field and nothing is posted
  let dlg = await post('Food', '', '1', '1450');
  await dlg.getByRole('button', { name: 'Post charge' }).click();
  await expect(dlg.locator('mat-form-field', { hasText: 'Description' }).locator('mat-error')).toContainText('Say what the charge is for');
  await dlg.getByLabel('Description').fill('Dinner at the beach shack');
  await shot(q, '03-post-charge');
  await dlg.getByRole('button', { name: 'Post charge' }).click();
  await said(q, `Balance ${money(own.nights + dinner)}`);

  dlg = await post('Transport', 'Airport transfer', '1', '1200');
  await dlg.getByRole('button', { name: 'Post charge' }).click();
  await said(q, `Balance ${money(own.nights + dinner + 1200)}`);

  dlg = await post('Activity', 'Kayaking', '2', '650');
  await expect(dlg).toContainText(money(1300));
  await dlg.getByRole('button', { name: 'Post charge' }).click();
  await said(q, `Balance ${money(own.nights + dinner + 2500)}`);

  await expect(row(q, 'Kayaking')).toContainText(`2 × ${money(650)}`);
  await expect(q.getByTestId('balance')).toHaveText(money(own.nights + dinner + 2500));
  await shot(q, '04-charges');
});

test('S08-03 Only a manager reverses, and only with a reason; the reversed line stays on the bill', async () => {
  // the receptionist has no Reverse button, and the API refuses too
  await expect(q.getByRole('button', { name: /^Reverse / })).toHaveCount(0);
  const folio = await (await api.get(`/api/v1/properties/${pid}/folios/${own.folioId}`)).json();
  const transfer = folio.items.find((i: { description: string }) => i.description === 'Airport transfer');
  const recApi = (await apiAs(reception.email, staffPassword)).api;
  const refused = await recApi.post(`/api/v1/properties/${pid}/folios/${own.folioId}/charges/${transfer.id}/reverse`, { data: { reason: 'Wrong guest' } });
  expect(refused.status()).toBe(403);

  await p.goto(`/folios/${own.folioId}`);
  await p.getByRole('button', { name: 'Reverse Airport transfer' }).click();
  const dlg = p.locator('mat-dialog-container');
  await dlg.getByRole('button', { name: 'Reverse' }).click();
  await expect(dlg.locator('mat-error')).toContainText('Say why the charge is reversed');
  await dlg.getByLabel('Reason').fill('Posted to the wrong guest');
  await shot(p, '05-reverse');
  await dlg.getByRole('button', { name: 'Reverse' }).click();
  await said(p, `Reversed: Airport transfer. Balance ${money(own.nights + dinner + 1300)}`);
  await expect(row(p, 'Airport transfer')).toHaveClass(/reversed/);
  await expect(row(p, 'Airport transfer')).toContainText('Reversed: Posted to the wrong guest');
  await expect(row(p, 'Reversal: Airport transfer')).toContainText(money(-1200));
  await expect(p.getByRole('button', { name: 'Reverse Airport transfer' })).toHaveCount(0);
  await shot(p, '06-reversed');
});

test('S08-04 Moving kayaking to a new folio: both bills show it and the stay’s total doesn’t change', async () => {
  await q.reload();
  await q.getByRole('button', { name: 'Move lines' }).click();
  const dlg = q.locator('mat-dialog-container');
  await dlg.getByRole('button', { name: 'Move lines' }).click();
  await expect(dlg.getByRole('alert')).toContainText('Choose at least one line to move.');
  await dlg.getByRole('checkbox', { name: /Kayaking/ }).check();
  await dlg.getByRole('radio', { name: 'A new folio' }).check();
  await dlg.getByLabel('Name of the new folio').fill('Paid by Arjun (friend)');
  await expect(dlg).toContainText(`Moves ${money(1300)} (1 line)`);
  await shot(q, '07-move-lines');
  await dlg.getByRole('button', { name: 'Move lines' }).click();
  await said(q, /Moved to F\d{5} \(Paid by Arjun \(friend\)\)/);
  await expect(q.getByTestId('balance')).toHaveText(money(own.nights + dinner));
  await expect(row(q, 'Kayaking')).toContainText(/Moved to F\d{5}/);

  const other = q.locator('nav.folios a', { hasText: 'Paid by Arjun' });
  await expect(other).toContainText(money(1300));
  await other.click();
  await expect(row(q, 'Kayaking')).toContainText(/Moved from F\d{5}/);
  await expect(q.getByTestId('balance')).toHaveText(money(1300));
  await shot(q, '08-second-folio');
});

test('S08-05 The front desk opens the bill straight from the in-house list', async () => {
  await q.goto('/front-desk/in-house');
  await q.locator(`tr[data-ref="${own.ref}"]`).getByRole('button', { name: `Bill of ${own.guest}` }).click();
  await expect(q).toHaveURL(new RegExp(`/folios/${own.folioId}`));
  await expect(row(q, 'Dinner at the beach shack')).toBeVisible();
});

test('S08-06 Staff without the permission can’t see bills', async ({ browser }: { browser: Browser }) => {
  const hk = { email: `hk8.${runId}@demo.local`, name: `Hema Housekeeping ${runId}` };
  await createStaff(api, hk.email, hk.name, 'HOUSEKEEPER', pid, staffPassword);
  const h = await (await browser.newContext()).newPage();
  await signIn(h, hk.email, staffPassword);
  await expect(h.locator('nav a[href="/folios"]')).toHaveCount(0);
  const hkApi = (await apiAs(hk.email, staffPassword)).api;
  expect((await hkApi.get(`/api/v1/properties/${pid}/folios/${own.folioId}`)).status()).toBe(403);
});

test('S08-07 On a phone the bill stays readable', async () => {
  await q.setViewportSize({ width: 390, height: 844 });
  await q.goto(`/folios/${own.folioId}`);
  await expect(q.getByTestId('balance')).toBeVisible();
  await expect(row(q, 'Dinner at the beach shack')).toBeVisible();
  await shot(q, '09-phone');
  await q.setViewportSize({ width: 1280, height: 860 });
});
