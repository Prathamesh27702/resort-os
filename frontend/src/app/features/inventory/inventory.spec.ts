import { InventoryItem, StockUnit } from '../../core/models';
import { KIND_LABEL, lineCost, marginBand, pct, qty, unitsLike } from './inventory.api';

const units: StockUnit[] = [
  { id: 'kg', code: 'kg', name: 'Kilogram', dimension: 'MASS', factor: 1000 },
  { id: 'g', code: 'g', name: 'Gram', dimension: 'MASS', factor: 1 },
  { id: 'l', code: 'l', name: 'Litre', dimension: 'VOLUME', factor: 1000 },
  { id: 'ml', code: 'ml', name: 'Millilitre', dimension: 'VOLUME', factor: 1 },
  { id: 'pcs', code: 'pcs', name: 'Piece', dimension: 'COUNT', factor: 1 },
];
const paneer = { id: 'p', unitId: 'kg', unitCost: 400 } as InventoryItem;

describe('inventory helpers', () => {
  it('a recipe line costs its quantity in the item’s unit at the item’s cost', () => {
    expect(lineCost(200, units[1], paneer, units)).toBe(80);       // 200 g of ₹400/kg
    expect(lineCost(0.2, units[0], paneer, units)).toBe(80);
    expect(lineCost(200, units[3], paneer, units)).toBeNull();     // ml of a kg item: not of its kind
    expect(lineCost(0, units[1], paneer, units)).toBeNull();
  });

  it('offers only units of the item’s kind', () => {
    expect(unitsLike(units, 'kg').map(u => u.code)).toEqual(['kg', 'g']);
    expect(unitsLike(units, 'pcs').map(u => u.code)).toEqual(['pcs']);
    expect(unitsLike(units, 'nope')).toEqual([]);
  });

  it('shows quantities and percentages without trailing zeros', () => {
    expect(qty(1.25, 'kg')).toBe('1.25 kg');
    expect(qty(4.4000001, 'kg')).toBe('4.4 kg');
    expect(qty(-0.6, 'kg')).toBe('-0.6 kg');
    expect(pct(37.5)).toBe('37.5 %');
    expect(pct(null)).toBe('—');
  });

  it('colours a margin: under 60 % low, 70 % and up good', () => {
    expect(marginBand(62.5)).toBe('mid');
    expect(marginBand(85)).toBe('good');
    expect(marginBand(40.8)).toBe('low');
    expect(marginBand(null)).toBe('none');
    expect(KIND_LABEL.WASTAGE).toBe('Wastage');
  });
});
