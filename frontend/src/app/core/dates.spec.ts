import { IsoDateAdapter, dayText, parseDay, pickerError } from './dates';

describe('date fields', () => {
  const a = new IsoDateAdapter();

  it('shows dates as "15 Oct 2026" and keeps the ISO value', () => {
    expect(a.format('2026-10-15', 'input')).toBe('15 Oct 2026');
    expect(a.format('2026-10-15', 'monthYear')).toBe('Oct 2026');
    expect(a.format('2026-10-15', 'a11y')).toBe('Thursday, 15 October 2026');
    expect(dayText('2026-01-05')).toBe('5 Jan 2026');
  });

  it('reads what people type', () => {
    expect(parseDay('15 Oct 2026')).toBe('2026-10-15');
    expect(parseDay('15 october, 2026')).toBe('2026-10-15');
    expect(parseDay('15-10-2026')).toBe('2026-10-15');
    expect(parseDay('5/1/2026')).toBe('2026-01-05');
    expect(parseDay('2026-10-15')).toBe('2026-10-15');
    expect(parseDay('31 Feb 2026')).toBeNull();
    expect(parseDay('tomorrow')).toBeNull();
    expect(a.parse('')).toBeNull();
    expect(a.isValid(a.parse('31-02-2026')!)).toBeFalse();
  });

  it('does calendar arithmetic on ISO strings', () => {
    expect(a.addCalendarDays('2026-10-31', 1)).toBe('2026-11-01');
    expect(a.addCalendarMonths('2026-01-31', 1)).toBe('2026-02-28');
    expect(a.addCalendarYears('2028-02-29', 1)).toBe('2029-02-28');
    expect(a.getNumDaysInMonth('2028-02-10')).toBe(29);
    expect(a.getDayOfWeek('2026-10-15')).toBe(4);
    expect(a.createDate(2026, 9, 15)).toBe('2026-10-15');
    expect(a.deserialize('')).toBeNull();
  });

  it('says why a typed date was refused', () => {
    expect(pickerError({ matDatepickerParse: { text: 'abc' } })).toBe('Enter a date like 15 Oct 2026.');
    expect(pickerError({ matDatepickerMin: { min: '2026-10-15', actual: '2026-10-01' } })).toBe('Choose 15 Oct 2026 or later.');
    expect(pickerError({ matDatepickerMax: { max: '2026-10-15', actual: '2026-11-01' } })).toBe('Choose 15 Oct 2026 or earlier.');
    expect(pickerError({ required: true })).toBeNull();
  });
});
