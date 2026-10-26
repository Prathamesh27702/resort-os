import { APIRequestContext, Browser, Page, expect, test } from '@playwright/test';
import { apiAs, createStaff, owner, runId, shot, signIn } from './helpers';

// Sprint 15 — the same checks as docs/sprints/REVIEWING.md → "Sprint 15", automated. The demo: sell 3 Paneer Tikka
// (Full) → the kitchen's stock goes down by their recipe (0.6 kg paneer …) → the food-cost report shows the dish at a
// 37.5 % food cost and the highest and lowest margins. Wastage and counts need a reason, said under the field.

test.describe.configure({ mode: 'serial' });

const PHONE = { viewport: { width: 360, height: 780 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 };
let m: Page;          // the manager (the owner here), on a desktop
let k: Page;          // the kitchen, on a phone
let api: APIRequestContext;
let pid = '';
let kitchenId = '';
let paneerBefore = 0;
let soldBefore = 0;
let orderNumber = '';
const pw = `Staff-${runId}-2026`;
const cook = { email: `k15.${runId}@demo.local`, name: `Kiran Kitchen ${runId}` };

interface Item { id: string; code: string; name: string; onHand: number; levels: { locationId: string; onHand: number }[]; }
interface Line { name: string; sold: number; foodCostPercent: number | null; marginPercent: number | null; }

const said = (page: Page, text: string | RegExp) => expect(page.locator('mat-snack-bar-container', { hasText: text }).last()).toBeVisible();
const item = (page: Page, code: string) => page.locator(`li[data-item="${code}"]`);
const noSideScroll = async (page: Page) => expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);
const inKitchen = (i: Item) => i.levels.find(l => l.locationId === kitchenId)?.onHand ?? 0;
const stock = async () => (await (await api.get(`/api/v1/properties/${pid}/inventory`)).json()) as { items: Item[]; locations: { id: string; isKitchen: boolean }[] };
const fullTikka = async () => ((await (await api.get(`/api/v1/properties/${pid}/inventory/food-cost`)).json()).lines as Line[])
  .find(l => l.name === 'Paneer Tikka (Full)')!;

test.beforeAll(async ({ browser }: { browser: Browser }) => {
  ({ api } = await apiAs(owner.email, owner.password));
  pid = (await (await api.get('/api/v1/properties')).json())[0].id;
  await createStaff(api, cook.email, cook.name, 'KITCHEN', pid, pw);
  const s = await stock();
  kitchenId = s.locations.find(l => l.isKitchen)!.id;
  paneerBefore = inKitchen(s.items.find(i => i.code === 'PANEER')!);
  soldBefore = (await fullTikka()).sold;

  m = await (await browser.newContext()).newPage();
  await signIn(m, owner.email, owner.password);
  k = await (await browser.newContext(PHONE)).newPage();
  await signIn(k, cook.email, pw);
});

test('S15-01 Stock by store: the sample inventory, with fresh cream marked to reorder', async () => {
  await m.goto('/inventory');
  await expect(m.getByRole('heading', { name: 'Inventory' })).toBeVisible();
  await expect(m.getByText(/Stock worth ₹/)).toBeVisible();
  await expect(item(m, 'PANEER')).toContainText('₹400/kg');
  await expect(item(m, 'PANEER')).toContainText('KITCHEN');
  await expect(item(m, 'CREAM')).toContainText('Reorder (at 2 l)');
  await expect(m.getByRole('heading', { name: 'Dairy' })).toBeVisible();
  await shot(m, '01-stock');
});

test('S15-02 Recipes: Paneer Tikka (Full) costs ₹105 of its ₹280, a 37.5 % food cost', async () => {
  await m.getByRole('radio', { name: 'Recipes' }).click();
  await expect(m).toHaveURL(/view=recipes/);
  const full = m.locator('article[data-dish="Paneer Tikka (Full)"]');
  await expect(full).toContainText('₹105');
  await expect(full).toContainText('food cost 37.5 %');
  await expect(full).toContainText('margin 62.5 %');
  await expect(full).toContainText('200 g Paneer');
  await expect(m.locator('article[data-dish="Masala Omelette"]')).toContainText('No recipe');

  await full.getByRole('button').first().click();
  const d = m.getByRole('dialog');
  await expect(d.getByRole('heading', { name: 'Recipe: Paneer Tikka (Full)' })).toBeVisible();
  await expect(d.locator('.sum')).toHaveText('Cost ₹105 · food cost 37.5 % · margin ₹175');
  // A quantity in a unit of another kind is refused under its field, saying which units fit.
  await d.getByRole('combobox', { name: 'Unit 1' }).click();
  await expect(m.getByRole('option')).toHaveText(['kg', 'g']);
  await m.keyboard.press('Escape');
  await shot(m, '02-recipe');
  await d.getByRole('button', { name: 'Cancel' }).click();
});

test('S15-03 Sell 3 Paneer Tikka: serving them takes 0.6 kg of paneer from the kitchen', async () => {
  // The order goes through the restaurant (Sprint 14): taken, sent, ready, served, paid.
  const menu = await (await api.get(`/api/v1/properties/${pid}/menu`)).json();
  const tikka = menu.categories.flatMap((c: { items: unknown[] }) => c.items).find((i: { code: string }) => i.code === 'PANEER-TIKKA');
  const full = tikka.variants.find((v: { name: string }) => v.name === 'Full').id;
  const less = tikka.modifierGroups[0].modifiers[0].id;
  let res = await api.post(`/api/v1/properties/${pid}/orders`, {
    headers: { 'Idempotency-Key': `e2e-s15-${runId}` },
    data: { orderType: 'TAKEAWAY', tableId: null, unitId: null, covers: null, note: null, items: [{ menuItemId: tikka.id, variantId: full, modifierIds: [less], quantity: 3, note: null }] },
  });
  expect(res.status(), await res.text()).toBe(201);
  let order = await res.json();
  orderNumber = order.number;
  order = await (await api.post(`/api/v1/properties/${pid}/orders/${order.id}/send`, { data: { note: null, version: order.version } })).json();
  const kot = order.kots[0];
  expect((await api.post(`/api/v1/properties/${pid}/kitchen/kots/${kot.id}/ready`, { data: { version: null } })).status()).toBe(200);
  // Ready but not served: nothing used yet.
  expect(inKitchen((await stock()).items.find(i => i.code === 'PANEER')!)).toBe(paneerBefore);
  res = await api.post(`/api/v1/properties/${pid}/orders/${order.id}/kots/${kot.id}/serve`);
  expect(res.status(), await res.text()).toBe(200);
  order = await res.json();
  res = await api.post(`/api/v1/properties/${pid}/orders/${order.id}/pay`, { headers: { 'Idempotency-Key': `e2e-s15-pay-${runId}` }, data: { method: 'UPI', reference: 'UPI-E2E-15', version: order.version } });
  expect(res.status(), await res.text()).toBe(200);

  // 3 × 200 g = 0.6 kg paneer less in the kitchen.
  const after = Math.round((paneerBefore - 0.6) * 1000) / 1000;
  await m.getByRole('radio', { name: 'Stock' }).click();
  await expect(item(m, 'PANEER')).toContainText(`KITCHEN ${after} kg`);
  expect(inKitchen((await stock()).items.find(i => i.code === 'PANEER')!)).toBe(after);

  // The ledger shows what was used and why: the order, its ticket and the dish.
  await item(m, 'PANEER').getByRole('button', { name: 'Ledger of Paneer' }).click();
  await expect(m).toHaveURL(/view=ledger/);
  const used = m.locator('ul.ledger li[data-type="CONSUMPTION"]').first();
  await expect(used).toContainText('Used (served)');
  await expect(used).toContainText(`${orderNumber} · ${kot.number} · 3× Paneer Tikka (Full)`);
  await expect(used).toContainText('-0.6 kg');
  await shot(m, '03-ledger');
});

test('S15-04 The food-cost report: highest and lowest margins, and Paneer Tikka sold at a 37.5 % food cost', async () => {
  await m.getByRole('radio', { name: 'Food cost' }).click();
  await expect(m.getByRole('region', { name: 'Highest margin' })).toContainText('Butter Naan');
  await expect(m.getByRole('region', { name: 'Lowest margin' })).toContainText('Goan Fish Curry');
  const row = m.locator('[data-line="Paneer Tikka (Full)"]');
  await expect(row).toContainText('37.5 %');
  await expect(row).toContainText('62.5 %');
  const line = await fullTikka();
  expect(line.sold).toBe(soldBefore + 3);
  await expect(row).toContainText(String(line.sold));
  await shot(m, '04-food-cost');

  await k.goto('/inventory?view=cost');
  await expect(k.getByRole('region', { name: 'Lowest margin' })).toContainText('Goan Fish Curry');
  await noSideScroll(k);
  await shot(k, '05-food-cost-phone');

  // A start after the end is refused under the start date, saying why.
  await m.getByLabel('From').fill('2099-01-01');
  await expect(m.locator('mat-hint.err')).toHaveText('The start date must be on or before the end date.');
});

test('S15-05 Wastage needs a reason and can\'t take more than the store has; each refusal is under its field', async () => {
  await m.goto('/inventory');
  await item(m, 'CREAM').getByRole('button', { name: 'Record for Fresh cream' }).click();
  const d = m.getByRole('dialog');
  await d.getByLabel(/^Quantity/).fill('50');
  await d.getByRole('button', { name: 'Save' }).click();
  await expect(d.locator('mat-error')).toHaveText('Say why, in 3–300 characters. Every stock change needs a reason.');
  await d.getByLabel('Reason').fill('Left out of the fridge');
  await d.getByRole('button', { name: 'Save' }).click();
  await expect(d.locator('mat-error')).toHaveText(/^Kitchen has only [\d.]+ l of Fresh cream, so 50 l can't be taken out\.$/);
  await d.getByLabel(/^Quantity/).fill('0.25');
  await shot(m, '06-wastage');
  await d.getByRole('button', { name: 'Save' }).click();
  await said(m, /SA\d{5} saved: 0.25 l Fresh cream — Kitchen now has [\d.]+ l\./);
});

test('S15-06 A count sets stock to what is on the shelf; a difference needs a reason', async () => {
  const paneer = (await stock()).items.find(i => i.code === 'PANEER')!;
  const counted = Math.round((inKitchen(paneer) - 0.1) * 1000) / 1000;
  await m.getByRole('button', { name: 'Count', exact: true }).click();
  const d = m.getByRole('dialog');
  await d.getByRole('spinbutton', { name: 'Paneer counted' }).fill(String(counted));
  await d.getByRole('button', { name: 'Save count' }).click();
  await expect(d.locator('[data-item="PANEER"] mat-error')).toHaveText(/^Paneer: the system has [\d.]+ kg and you counted [\d.]+ kg\. Say why they differ/);
  await d.getByRole('textbox', { name: 'Paneer reason' }).fill('Trimmings not recorded');
  await shot(m, '07-count');
  await d.getByRole('button', { name: 'Save count' }).click();
  await said(m, /Count of Kitchen saved: 1 counted, 1 changed/);
  expect(inKitchen((await stock()).items.find(i => i.code === 'PANEER')!)).toBe(counted);
});

test('S15-07 The kitchen sees stock and recipes but can\'t change them', async () => {
  await k.goto('/inventory');
  await expect(item(k, 'PANEER')).toBeVisible();
  await expect(k.getByRole('button', { name: 'Record', exact: true })).toHaveCount(0);
  await expect(k.getByRole('button', { name: 'Count', exact: true })).toHaveCount(0);
  await noSideScroll(k);
  const { api: kitchenApi } = await apiAs(cook.email, pw);
  const paneer = (await stock()).items.find(i => i.code === 'PANEER')!;
  const res = await kitchenApi.post(`/api/v1/properties/${pid}/inventory/adjustments`,
    { data: { kind: 'WASTAGE', itemId: paneer.id, locationId: kitchenId, toLocationId: null, quantity: 1, unitCost: null, reason: 'Testing' } });
  expect(res.status()).toBe(403);
});
