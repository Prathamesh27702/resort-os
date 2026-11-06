import { change, freshText, monthEnd, monthOf, pc, points, rs, rsShort, toCsv } from './reports.api';

describe('report helpers', () => {
  it('says money in whole rupees with Indian grouping, and hidden money as a dash', () => {
    expect(rs(123456.6)).toBe('₹1,23,457');
    expect(rs(null)).toBe('—');
    expect(rsShort(4567890)).toBe('₹45.68 L');
    expect(rsShort(31200000)).toBe('₹3.12 Cr');
    expect(rsShort(99999)).toBe('₹99,999');
    expect(pc(62.35)).toBe('62.4 %');
    expect(pc(null)).toBe('—');
  });

  it('compares a period with another: percent change, and points for occupancy', () => {
    expect(change(110, 100)).toBe(10);
    expect(change(90, 120)).toBe(-25);
    expect(change(5, 0)).toBeNull();
    expect(change(null, 100)).toBeNull();
    expect(points(64.2, 60)).toBe(4.2);
    expect(points(null, 60)).toBeNull();
  });

  it('moves between months across the year end', () => {
    expect(monthOf('2026-11-05')).toBe('2026-11-01');
    expect(monthOf('2026-01-31', -1)).toBe('2025-12-01');
    expect(monthOf('2026-11-05', 2)).toBe('2027-01-01');
    expect(monthEnd('2028-02-01')).toBe('2028-02-29');
  });

  it('writes CSV a spreadsheet opens: quotes where needed, empty for hidden figures', () => {
    expect(toCsv(['Source', 'Net'], [['Goa, "Direct"', 1200.5], ['OTA', null]])).toBe('Source,Net\r\n"Goa, ""Direct""",1200.5\r\nOTA,\r\n');
  });

  it('says how fresh the numbers are', () => {
    const now = Date.parse('2026-11-05T10:20:00Z');
    expect(freshText(null, now)).toBe('Not added up yet');
    expect(freshText({ refreshedAt: '2026-11-05T10:16:00Z', from: null, to: null, durationMs: 300, everyMinutes: 15 }, now)).toBe('Updated 4 min ago (every 15 min)');
    expect(freshText({ refreshedAt: '2026-11-05T10:19:50Z', from: null, to: null, durationMs: 300, everyMinutes: 15 }, now)).toBe('Updated just now (every 15 min)');
  });
});
