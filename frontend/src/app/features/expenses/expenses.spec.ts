import { gstSplit, inr, monthShort, spread, varianceText, yearLabel, yearOf } from './expenses.api';

describe('expense and budget helpers', () => {
  it('knows the Indian financial year', () => {
    expect(yearOf('2026-11-05')).toBe(2026);
    expect(yearOf('2027-03-31')).toBe(2026);
    expect(yearOf('2026-04-01')).toBe(2026);
    expect(yearLabel(2026)).toBe('2026-27');
    expect(yearLabel(2099)).toBe('2099-00');
    expect(monthShort('2026-04-01')).toBe('Apr');
    expect(monthShort('2027-03-01', true)).toBe('Mar 2027');
  });

  it('spreads a yearly budget in whole rupees with March taking the rest, as the API does', () => {
    expect(spread(120000)).toEqual(Array(12).fill(10000));
    const s = spread(100000);
    expect(s[0]).toBe(8333);
    expect(s[11]).toBe(8337);
    expect(s.reduce((a, b) => a + b, 0)).toBe(100000);
  });

  it('splits GST like the API: halves in the state, IGST from another', () => {
    expect(gstSplit(180.01, '30ABCDE1234F1ZD', '30')).toEqual({ cgst: 90, sgst: 90.01, igst: 0 });
    expect(gstSplit(900, '27PQRSX6789K1ZS', '30')).toEqual({ cgst: 0, sgst: 0, igst: 900 });
  });

  it('says money and variance in words with Indian grouping', () => {
    expect(inr(246000)).toBe('₹2,46,000');
    expect(varianceText(-400)).toBe('₹400 over');
    expect(varianceText(24300)).toBe('₹24,300 under');
    expect(varianceText(0)).toBe('on budget');
  });
});
