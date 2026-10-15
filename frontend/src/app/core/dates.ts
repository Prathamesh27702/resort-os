import { Injectable, Provider } from '@angular/core';
import { DateAdapter, MAT_DATE_FORMATS, MatDateFormats } from '@angular/material/core';
import { ValidationErrors } from '@angular/forms';

/**
 * Date fields show and accept "15 Oct 2026" (the format used everywhere else in the app) with a calendar button,
 * while the form value stays the API's ISO date string "2026-10-15". Typing also works: 15 Oct 2026, 15-10-2026,
 * 15/10/2026 or 2026-10-15.
 */

const SHORT = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const LONG = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October',
  'November', 'December'];
const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const ISO = /^(\d{4})-(\d{2})-(\d{2})$/;
const INVALID = 'invalid-date';

const pad = (n: number) => String(n).padStart(2, '0');
const parts = (s: string) => { const [y, m, d] = s.split('-').map(Number); return { y, m: m - 1, d }; };
const utc = (y: number, m: number, d: number) => { const t = new Date(0); t.setUTCFullYear(y, m, d); return t; };
const iso = (t: Date) => `${String(t.getUTCFullYear()).padStart(4, '0')}-${pad(t.getUTCMonth() + 1)}-${pad(t.getUTCDate())}`;

/** ISO string for y / m (0-based) / d, or null when that day doesn't exist (31 Feb). */
function exact(y: number, m: number, d: number): string | null {
  if (m < 0 || m > 11 || d < 1 || y < 1 || y > 9999) return null;
  const t = utc(y, m, d);
  return t.getUTCMonth() === m && t.getUTCDate() === d ? iso(t) : null;
}

/** Reads what a person typed; null when it isn't a date. */
export function parseDay(text: string): string | null {
  const s = text.trim().replace(/,/g, ' ').replace(/\s+/g, ' ');
  let m = ISO.exec(s);
  if (m) return exact(+m[1], +m[2] - 1, +m[3]);
  m = /^(\d{1,2})[-/.](\d{1,2})[-/.](\d{4})$/.exec(s);
  if (m) return exact(+m[3], +m[2] - 1, +m[1]);
  m = /^(\d{1,2})[ -]([a-z]{3,9})[ -](\d{4})$/i.exec(s);
  if (m) {
    const month = SHORT.findIndex(x => m![2].toLowerCase().startsWith(x.toLowerCase()));
    return month < 0 ? null : exact(+m[3], month, +m[1]);
  }
  return null;
}

/** "15 Oct 2026". */
export function dayText(isoDate: string): string {
  const p = parts(isoDate);
  return `${p.d} ${SHORT[p.m]} ${p.y}`;
}

@Injectable()
export class IsoDateAdapter extends DateAdapter<string> {
  getYear(d: string) { return parts(d).y; }
  getMonth(d: string) { return parts(d).m; }
  getDate(d: string) { return parts(d).d; }
  getDayOfWeek(d: string) { const p = parts(d); return utc(p.y, p.m, p.d).getUTCDay(); }
  getMonthNames(style: 'long' | 'short' | 'narrow') {
    return style === 'long' ? LONG : style === 'short' ? SHORT : LONG.map(x => x[0]);
  }
  getDateNames() { return Array.from({ length: 31 }, (_, i) => String(i + 1)); }
  getDayOfWeekNames(style: 'long' | 'short' | 'narrow') {
    return style === 'long' ? DAYS : style === 'short' ? DAYS.map(x => x.slice(0, 3)) : DAYS.map(x => x[0]);
  }
  getYearName(d: string) { return String(parts(d).y); }
  getFirstDayOfWeek() { return 1; }
  getNumDaysInMonth(d: string) { const p = parts(d); return utc(p.y, p.m + 1, 0).getUTCDate(); }
  clone(d: string) { return d; }
  createDate(year: number, month: number, date: number) {
    const r = exact(year, month, date);
    if (!r) throw Error(`Invalid date ${year}-${month + 1}-${date}.`);
    return r;
  }
  today() { const n = new Date(); return iso(utc(n.getFullYear(), n.getMonth(), n.getDate())); }
  parse(value: unknown) {
    if (value == null || value === '') return null;
    return (typeof value === 'string' && parseDay(value)) || INVALID;
  }
  format(d: string, displayFormat: string) {
    if (!this.isValid(d)) throw Error('Cannot format an invalid date.');
    const p = parts(d);
    switch (displayFormat) {
      case 'monthYear': return `${SHORT[p.m]} ${p.y}`;
      case 'monthYearA11y': return `${LONG[p.m]} ${p.y}`;
      case 'a11y': return `${DAYS[this.getDayOfWeek(d)]}, ${p.d} ${LONG[p.m]} ${p.y}`;
      default: return dayText(d);
    }
  }
  addCalendarYears(d: string, years: number) { return this.addCalendarMonths(d, years * 12); }
  addCalendarMonths(d: string, months: number) {
    const p = parts(d);
    const first = utc(p.y, p.m + months, 1);
    const last = utc(first.getUTCFullYear(), first.getUTCMonth() + 1, 0).getUTCDate();
    return iso(utc(first.getUTCFullYear(), first.getUTCMonth(), Math.min(p.d, last)));
  }
  addCalendarDays(d: string, days: number) { const p = parts(d); return iso(utc(p.y, p.m, p.d + days)); }
  toIso8601(d: string) { return d; }
  isDateInstance(obj: unknown) { return typeof obj === 'string'; }
  isValid(d: string) { const m = ISO.exec(d); return !!m && exact(+m[1], +m[2] - 1, +m[3]) === d; }
  invalid() { return INVALID; }
  override deserialize(value: unknown): string | null {
    if (value === '' || value == null) return null;
    return super.deserialize(value);
  }
}

const FORMATS: MatDateFormats = {
  parse: { dateInput: 'input' },
  display: { dateInput: 'input', monthYearLabel: 'monthYear', dateA11yLabel: 'a11y', monthYearA11yLabel: 'monthYearA11y' },
};

export function provideIsoDates(): Provider[] {
  return [{ provide: DateAdapter, useClass: IsoDateAdapter }, { provide: MAT_DATE_FORMATS, useValue: FORMATS }];
}

/** The reason a date field's own checks failed (typed text that isn't a date, or outside min / max), or null. */
export function pickerError(errors: ValidationErrors | null | undefined): string | null {
  if (!errors) return null;
  if (errors['matDatepickerParse']) return 'Enter a date like 15 Oct 2026.';
  if (errors['matDatepickerMin']) return `Choose ${dayText(errors['matDatepickerMin'].min)} or later.`;
  if (errors['matDatepickerMax']) return `Choose ${dayText(errors['matDatepickerMax'].max)} or earlier.`;
  return null;
}
