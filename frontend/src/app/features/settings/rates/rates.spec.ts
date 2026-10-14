import { TestBed } from '@angular/core/testing';
import { FormBuilder } from '@angular/forms';
import { controlPath } from '../../../core/http';
import { rupees, splitByPercent } from './rates.api';
import { componentGroup, moneyError, packageForm, pricesForm, splitError } from './rates.forms';

describe('package split (same rule as the API)', () => {
  it('gives leftover paise to the largest remainders and adds up exactly', () => {
    expect(splitByPercent(999.99, [33.33, 33.33, 33.34])).toEqual([333.3, 333.3, 333.39]);
    expect(splitByPercent(14999, [70, 15, 5, 10])).toEqual([10499.3, 2249.85, 749.95, 1499.9]);
    expect(splitByPercent(0.01, [33.33, 33.33, 33.34])).toEqual([0, 0, 0.01]);   // largest share has the largest remainder
  });

  it('never loses or gains a paisa', () => {
    let seed = 7;
    const rnd = (n: number) => { seed = (seed * 16807) % 2147483647; return seed % n; };
    for (let run = 0; run < 2000; run++) {
      const paise = 1 + rnd(9_999_999);
      const cuts = [...new Set(Array.from({ length: rnd(6) }, () => 1 + rnd(9_999)))].sort((a, b) => a - b);
      const bounds = [0, ...cuts, 10_000];
      const shares = bounds.slice(1).map((b, i) => (b - bounds[i]) / 100);
      const parts = splitByPercent(paise / 100, shares)!;
      expect(Math.round(parts.reduce((s, x) => s + x * 100, 0))).toBe(paise);
    }
  });

  it('returns null until the shares add up to 100', () => {
    expect(splitByPercent(4100, [80, 15.5])).toBeNull();
  });
});

describe('rates forms', () => {
  const fb = () => TestBed.inject(FormBuilder);

  it('explains money errors the way the API does', () => {
    const f = pricesForm(fb(), '2026-10-15');
    f.controls.price.setValue(-1);
    expect(moneyError(f.controls.price, 'Price')).toBe("Price can't be negative.");
    f.controls.price.setValue(10.005);
    expect(moneyError(f.controls.price, 'Price')).toBe('Price can have at most 2 decimal places (paise).');
    f.controls.price.setValue(3500);
    expect(f.controls.price.valid).toBeTrue();
  });

  it('refuses a last night before the first night', () => {
    const f = pricesForm(fb(), '2026-10-15');
    f.patchValue({ accommodationTypeIds: ['t'], price: 100, firstNight: '2026-10-15', lastNight: '2026-10-14' });
    expect(f.hasError('lastBeforeFirst')).toBeTrue();
    f.patchValue({ lastNight: '2026-10-15' });
    expect(f.valid).toBeTrue();
  });

  it('says why a package split is wrong', () => {
    const f = packageForm(fb());
    const parts = f.controls.components;
    expect(splitError(parts)).toBe('Add at least one component; a package always includes the room.');
    parts.push(componentGroup(fb(), { category: 'FOOD', description: 'Breakfast', sharePercent: 100 }));
    expect(splitError(parts)).toBe('Add a ROOM component: every package includes the stay.');
    parts.at(0).controls.category.setValue('ROOM');
    parts.at(0).controls.sharePercent.setValue(80);
    parts.push(componentGroup(fb(), { category: 'FOOD', description: 'Breakfast', sharePercent: 15.5 }));
    expect(splitError(parts)).toBe('The shares add up to 95.5%; they must add up to exactly 100%.');
    parts.at(1).controls.sharePercent.setValue(20);
    expect(parts.valid).toBeTrue();
  });
});

describe('formatting and field paths', () => {
  it('shows rupees with Indian grouping', () => {
    expect(rupees(350000)).toBe('₹3,50,000');
    expect(rupees(10499.3)).toBe('₹10,499.30');
  });

  it('maps API field names inside lists to form paths', () => {
    expect(controlPath('Components[0].SharePercent')).toBe('components.0.sharePercent');
    expect(controlPath('Price')).toBe('price');
  });
});
