import { Kot, Menu, MenuItem } from '../../core/models';
import { KITCHEN_REFRESH_MS, ageMinutes, kitchenColumns, linePrice, oneTap, pickProblem, searchMenu } from './restaurant.api';

const tikka: MenuItem = {
  id: 'tikka', categoryId: 'c1', code: 'PANEER-TIKKA', name: 'Paneer Tikka', description: null, foodType: 'VEG', price: 280, isAvailable: true,
  isActive: true, sortOrder: 1, version: 1,
  variants: [{ id: 'half', name: 'Half', price: 160 }, { id: 'full', name: 'Full', price: 280 }],
  modifierGroups: [
    { id: 'spice', name: 'Spice level', minSelect: 1, maxSelect: 1, modifiers: [{ id: 'less', name: 'Less spicy', priceDelta: 0 }, { id: 'hot', name: 'Extra spicy', priceDelta: 0 }] },
    { id: 'add', name: 'Add-ons', minSelect: 0, maxSelect: 2, modifiers: [{ id: 'cheese', name: 'Extra cheese', priceDelta: 40 }, { id: 'mint', name: 'Mint chutney', priceDelta: 20 }] },
  ],
};
const naan: MenuItem = { ...tikka, id: 'naan', code: 'BUTTER-NAAN', name: 'Butter Naan', price: 60, variants: [], modifierGroups: [] };

describe('restaurant helpers', () => {
  it('a line costs its portion plus every chosen extra', () => {
    expect(linePrice(tikka, 'half', ['less', 'cheese', 'mint'])).toBe(220);
    expect(linePrice(tikka, 'full', ['less'])).toBe(280);
    expect(linePrice(naan, null, [])).toBe(60);
  });

  it('says what is still to pick before a dish can be added', () => {
    expect(pickProblem(tikka, null, ['less'])).toBe('Choose the portion: Half or Full.');
    expect(pickProblem(tikka, 'full', [])).toBe('Choose the spice level.');
    expect(pickProblem(tikka, 'full', ['less', 'hot'])).toBe('Choose one spice level.');
    expect(pickProblem(tikka, 'full', ['less', 'cheese', 'mint'])).toBeNull();
    expect(pickProblem(naan, null, [])).toBeNull();
  });

  it('a dish with nothing to pick is added with one tap', () => {
    expect(oneTap(naan)).toBeTrue();
    expect(oneTap(tikka)).toBeFalse();
  });

  it('searches the menu by name or code, leaving out dishes switched off', () => {
    const menu: Menu = { categories: [{ id: 'c1', name: 'Starters', sortOrder: 1, isActive: true, items: [tikka, naan, { ...naan, id: 'x', name: 'Old naan', isActive: false }] }] };
    expect(searchMenu(menu, 'naan').map(i => i.id)).toEqual(['naan']);
    expect(searchMenu(menu, 'paneer-t').map(i => i.id)).toEqual(['tikka']);
    expect(searchMenu(menu, '').length).toBe(2);
    expect(searchMenu(null, 'x')).toEqual([]);
  });

  it('the kitchen display has three columns, oldest ticket first, and refreshes every 10 seconds', () => {
    const k = (id: string, status: Kot['status'], sentAt: string) => ({ id, status, sentAt }) as Kot;
    const cols = kitchenColumns([k('a', 'NEW', '2026-10-26T10:05:00Z'), k('b', 'NEW', '2026-10-26T10:01:00Z'), k('c', 'READY', '2026-10-26T09:50:00Z'), k('d', 'PREPARING', '2026-10-26T10:00:00Z')]);
    expect(cols.map(c => c.label)).toEqual(['New', 'Preparing', 'Ready']);
    expect(cols[0].tickets.map(t => t.id)).toEqual(['b', 'a']);
    expect(KITCHEN_REFRESH_MS).toBe(10_000);
    expect(ageMinutes('2026-10-26T10:00:00Z', new Date('2026-10-26T10:16:30Z'))).toBe(16);
    expect(ageMinutes('2026-10-26T10:00:30Z', new Date('2026-10-26T10:00:00Z'))).toBe(0);
  });
});
