import { FolioItemTax } from '../../../core/models';
import { fullDay, pct, periodPhrase, periodText, slabsText, taxText } from './gst.api';

const tax = (code: FolioItemTax['code'], rate: number, amount: number): FolioItemTax => ({ code, rate, taxableValue: 0, amount, taxRuleId: 'r1' });

describe('GST helpers (Sprint 9)', () => {
  it('writes rates without trailing zeros', () => {
    expect(pct(5)).toBe('5%');
    expect(pct(12.5)).toBe('12.5%');
    expect(pct(2.25)).toBe('2.25%');
  });

  it('describes a flat rule and a slab rule in words, in order of their limits', () => {
    expect(slabsText([{ upTo: null, rate: 5, inputTaxCredit: false }])).toBe('5%');
    expect(slabsText([{ upTo: null, rate: 18, inputTaxCredit: true }, { upTo: 7500, rate: 5, inputTaxCredit: false }]))
      .toBe('Up to ₹7,500: 5% · Above ₹7,500: 18% with ITC');
  });

  it('shows a line’s GST as the total rate: CGST + SGST is GST at twice the half', () => {
    expect(taxText([])).toBe('');
    expect(taxText([tax('CGST', 2.5, 36.25), tax('SGST', 2.5, 36.25)])).toBe('GST 5%');
    expect(taxText([tax('IGST', 18, 180)])).toBe('IGST 18%');
    expect(taxText([tax('CGST', 2.5, 125), tax('SGST', 2.5, 125), tax('CGST', 9, 810), tax('SGST', 9, 810)])).toBe('GST 5% + 18%');
  });

  it('writes periods with the year, open-ended or closed', () => {
    expect(fullDay('2025-09-22')).toMatch(/^22 Sept? 2025$/);
    expect(periodText('2025-09-22', null)).toBe(`From ${fullDay('2025-09-22')}`);
    expect(periodText('2022-07-18', '2025-09-21')).toBe(`${fullDay('2022-07-18')} – ${fullDay('2025-09-21')}`);
    expect(periodPhrase('2026-11-01', null)).toBe(`from ${fullDay('2026-11-01')}`);
  });
});
