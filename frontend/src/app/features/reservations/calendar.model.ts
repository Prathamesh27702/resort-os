import { Calendar, CalendarBlock, CalendarBooking, CalendarRoom, InventoryMode } from '../../core/models';
import { addDays, nightsBetween } from '../rooms/rooms.api';

/**
 * The reservation calendar's pure logic (no Angular): which days a view shows, the rows (a room, or one dorm bed),
 * where a booking or a block sits in the grid, and whether a move would be refused — checked here first so the
 * reason shows at once, and always checked again by the server (which has the last word).
 */

export type CalView = 'day' | 'week' | 'month';

/** One calendar row: a room, or one bed of a dormitory. `first` marks the first row of its room type (group header). */
export interface CalRow {
  key: string; unitId: string; bedId: string | null; label: string; typeId: string; typeName: string; mode: InventoryMode;
  isActive: boolean; maxOccupancy: number; first: boolean;
}

/** Where an item sits: column (1 = first day shown), how many days, and whether it continues past either edge. */
export interface Placement { col: number; span: number; cutStart: boolean; cutEnd: boolean; }

export const rowKey = (unitId: string, bedId: string | null) => (bedId ? `${unitId}:${bedId}` : unitId);

/** [from, to) for a view around a day: the day; its week (Monday to Sunday); its calendar month. */
export function windowFor(view: CalView, anchor: string): { from: string; to: string } {
  if (view === 'day') return { from: anchor, to: addDays(anchor, 1) };
  if (view === 'week') {
    const dow = (new Date(anchor + 'T00:00:00Z').getUTCDay() + 6) % 7;   // Monday = 0
    const from = addDays(anchor, -dow);
    return { from, to: addDays(from, 7) };
  }
  const from = anchor.slice(0, 8) + '01';
  return { from, to: addMonths(from, 1) };
}

/** The anchor one view earlier (-1) or later (+1). */
export function shift(view: CalView, anchor: string, dir: 1 | -1): string {
  if (view === 'day') return addDays(anchor, dir);
  if (view === 'week') return addDays(anchor, 7 * dir);
  return addMonths(anchor.slice(0, 8) + '01', dir);
}

function addMonths(firstOfMonth: string, n: number): string {
  const [y, m] = firstOfMonth.split('-').map(Number);
  const t = new Date(Date.UTC(y, m - 1 + n, 1));
  return t.toISOString().slice(0, 10);
}

/** Every day in [from, to). */
export function daysIn(from: string, to: string): string[] {
  const n = nightsBetween(from, to);
  return Array.from({ length: Math.max(0, n) }, (_, i) => addDays(from, i));
}

/** Rows in the API's order (room type, then room): one per room, one per dorm bed. */
export function buildRows(rooms: CalendarRoom[]): CalRow[] {
  const rows: CalRow[] = [];
  let lastType = '';
  for (const r of rooms) {
    const base = { unitId: r.id, typeId: r.accommodationTypeId, typeName: r.accommodationTypeName, mode: r.inventoryMode, maxOccupancy: r.maxOccupancy };
    if (r.inventoryMode === 'BED') {
      for (const b of r.beds)
        rows.push({ ...base, key: rowKey(r.id, b.id), bedId: b.id, label: `${r.name} · ${b.code}`, isActive: r.isActive && b.isActive,
                    first: lastType !== r.accommodationTypeId && b === r.beds[0] });
    } else {
      rows.push({ ...base, key: rowKey(r.id, null), bedId: null, label: r.name, isActive: r.isActive, first: lastType !== r.accommodationTypeId });
    }
    lastType = r.accommodationTypeId;
  }
  return rows;
}

/** Where [start, end) sits in the window [from, to), or null when it is outside. */
export function place(start: string, end: string, from: string, to: string): Placement | null {
  if (end <= from || start >= to) return null;
  const s = start < from ? from : start;
  const e = end > to ? to : end;
  return { col: nightsBetween(from, s) + 1, span: nightsBetween(s, e), cutStart: start < from, cutEnd: end > to };
}

/** Bookings by row key. */
export function bookingsByRow(bookings: CalendarBooking[]): Map<string, CalendarBooking[]> {
  const m = new Map<string, CalendarBooking[]>();
  for (const b of bookings) {
    const k = rowKey(b.unitId, b.bedId);
    m.set(k, [...(m.get(k) ?? []), b]);
  }
  return m;
}

/** Blocks by row key. A block on a whole dormitory shows on every bed of it. */
export function blocksByRow(rows: CalRow[], blocks: CalendarBlock[]): Map<string, CalendarBlock[]> {
  const m = new Map<string, CalendarBlock[]>();
  for (const row of rows) {
    const mine = blocks.filter(b => b.unitId === row.unitId && (b.bedId === null || b.bedId === row.bedId));
    if (mine.length) m.set(row.key, mine);
  }
  return m;
}

const SHORT = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const dm = (iso: string) => `${+iso.slice(8, 10)} ${SHORT[+iso.slice(5, 7) - 1]}`;

/** "for the night of 7 Jan 2027", "for the nights of 7 Jan – 8 Jan 2027" — the API's wording. */
export function nightsText(start: string, end: string): string {
  const last = addDays(end, -1);
  if (last === start) return `for the night of ${dm(start)} ${start.slice(0, 4)}`;
  return start.slice(0, 4) === last.slice(0, 4)
    ? `for the nights of ${dm(start)} – ${dm(last)} ${last.slice(0, 4)}`
    : `for the nights of ${dm(start)} ${start.slice(0, 4)} – ${dm(last)} ${last.slice(0, 4)}`;
}

const overlaps = (a: { startDate: string; endDate: string }, b: { startDate: string; endDate: string }) =>
  a.startDate < b.endDate && b.startDate < a.endDate;

/** Statuses whose room can be changed from the calendar (the API's rule; checked-in moves come with the front desk). */
export const MOVABLE = ['PENDING', 'CONFIRMED'];

/**
 * Why moving booking `b` to `target` would be refused, in the same words as the API, or null if it looks fine.
 * Only knows what is in the window loaded, so the server checks again.
 */
export function moveProblem(b: CalendarBooking, target: CalRow, cal: Calendar): string | null {
  const isBed = b.bedId !== null;
  const room = cal.rooms.find(r => r.id === target.unitId);
  const roomName = room?.name ?? target.label;
  if (!MOVABLE.includes(b.status))
    return b.status === 'CHECKED_IN'
      ? `${b.reference} is checked in. Moving an in-house guest mid-stay comes with the front desk (Sprint 7).`
      : `${b.reference} is ${b.status.replace('_', ' ').toLowerCase()}, so its room can't be changed.`;
  if (!target.isActive)
    return `${target.label} is inactive, so guests can't be moved into it.`;
  if (isBed && target.mode !== 'BED')
    return `This guest has a dormitory bed; ${roomName} is a private room. Move them to a bed in a dormitory.`;
  if (!isBed && target.mode === 'BED')
    return `${roomName} is a dormitory, sold by the bed. Move this room booking to another room.`;
  const guests = b.adults + b.children;
  if (!isBed && guests > target.maxOccupancy)
    return `${roomName} (${target.typeName}) sleeps at most ${target.maxOccupancy}; this room has ${guests} guests. Choose a bigger room.`;

  const what = isBed ? `Bed ${room?.beds.find(x => x.id === target.bedId)?.code ?? ''} in ${roomName}` : roomName;
  const nights = nightsText(b.startDate, b.endDate);
  const kind = isBed ? 'bed' : 'room';
  const booked = cal.bookings.find(x => x.reservationUnitId !== b.reservationUnitId && x.unitId === target.unitId
    && (x.bedId === null || x.bedId === target.bedId) && overlaps(x, b));
  if (booked) return `${what} isn't free ${nights}: it is booked on ${booked.reference}. Choose another ${kind}.`;
  const block = cal.blocks.find(x => x.unitId === target.unitId && (x.bedId === null || x.bedId === target.bedId) && overlaps(x, b));
  if (block) return `${what} isn't free ${nights}: it is blocked (${block.reason}). Choose another ${kind}.`;
  return null;
}

/** For the phone view: what happens in each room on each day of the window. */
export interface DayLine { row: CalRow; booking?: CalendarBooking; block?: CalendarBlock; kind: 'arrives' | 'stays' | 'leaves' | 'blocked'; }

export function byDay(cal: Calendar, rows: CalRow[], days: string[]): { day: string; lines: DayLine[] }[] {
  const byKey = new Map(rows.map(r => [r.key, r]));
  const blocks = blocksByRow(rows, cal.blocks);
  return days.map(day => {
    const lines: DayLine[] = [];
    for (const row of rows) {
      for (const b of cal.bookings) {
        if (rowKey(b.unitId, b.bedId) !== row.key) continue;
        if (b.startDate === day) lines.push({ row, booking: b, kind: 'arrives' });
        else if (b.startDate < day && day < b.endDate) lines.push({ row, booking: b, kind: 'stays' });
        else if (b.endDate === day) lines.push({ row, booking: b, kind: 'leaves' });
      }
      for (const k of blocks.get(row.key) ?? [])
        if (k.startDate <= day && day < k.endDate && (k.bedId !== null || row.bedId === null || row === firstBedRow(rows, row.unitId)))
          lines.push({ row: k.bedId === null && row.bedId !== null ? { ...row, label: row.label.split(' · ')[0] } : row, block: k, kind: 'blocked' });
    }
    const order = { leaves: 0, arrives: 1, stays: 2, blocked: 3 };
    lines.sort((a, b) => order[a.kind] - order[b.kind]);
    return { day, lines: lines.filter(l => byKey.has(l.row.key)) };
  });
}

function firstBedRow(rows: CalRow[], unitId: string): CalRow | undefined {
  return rows.find(r => r.unitId === unitId);
}
