import { TestBed } from '@angular/core/testing';
import { Calendar, CalendarBooking, CalendarRoom } from '../../core/models';
import { addDays } from '../rooms/rooms.api';
import { CalendarGrid } from './calendar-grid';
import { blocksByRow, buildRows, byDay, daysIn, moveProblem, nightsText, place, shift, windowFor } from './calendar.model';

const room = (code: string, type = 'STD', max = 2): CalendarRoom => ({
  id: `u-${code}`, code, name: `Room ${code}`, accommodationTypeId: `t-${type}`, accommodationTypeName: type === 'DLX' ? 'Deluxe room' : 'Standard room',
  inventoryMode: 'UNIT', maxOccupancy: max, floor: null, isActive: true, beds: [],
});
const dorm: CalendarRoom = {
  id: 'u-DORM', code: 'DORM', name: 'Dormitory', accommodationTypeId: 't-DORM', accommodationTypeName: 'Dormitory bed', inventoryMode: 'BED',
  maxOccupancy: 1, floor: null, isActive: true,
  beds: [{ id: 'b1', code: 'B01', name: 'Bed 1', isActive: true }, { id: 'b2', code: 'B02', name: 'Bed 2', isActive: true }],
};
const booking = (ref: string, unit: string, start: string, end: string, extra: Partial<CalendarBooking> = {}): CalendarBooking => ({
  reservationUnitId: `ru-${ref}`, reservationId: `r-${ref}`, reference: ref, status: 'CONFIRMED', unitId: unit, bedId: null,
  startDate: start, endDate: end, guestName: `Guest ${ref}`, adults: 2, children: 0, sourceName: 'Walk-in', paymentStatus: 'NONE',
  totalAmount: 7000, version: 1, ...extra,
});

describe('calendar windows', () => {
  it('a day, a Monday-to-Sunday week, a calendar month', () => {
    expect(windowFor('day', '2026-10-15')).toEqual({ from: '2026-10-15', to: '2026-10-16' });
    expect(windowFor('week', '2026-10-15')).toEqual({ from: '2026-10-12', to: '2026-10-19' });   // Thu → Mon 12 … Sun 18
    expect(windowFor('week', '2026-10-18')).toEqual({ from: '2026-10-12', to: '2026-10-19' });   // a Sunday stays in its week
    expect(windowFor('month', '2026-02-15')).toEqual({ from: '2026-02-01', to: '2026-03-01' });
    expect(daysIn('2026-10-01', '2026-11-01').length).toBe(31);
  });

  it('moves a view earlier or later', () => {
    expect(shift('day', '2026-10-15', 1)).toBe('2026-10-16');
    expect(shift('week', '2026-10-15', -1)).toBe('2026-10-08');
    expect(shift('month', '2026-01-31', 1)).toBe('2026-02-01');
    expect(shift('month', '2026-01-15', -1)).toBe('2025-12-01');
  });

  it('places a stay in the window and says where it is cut', () => {
    expect(place('2026-10-14', '2026-10-16', '2026-10-12', '2026-10-19')).toEqual({ col: 3, span: 2, cutStart: false, cutEnd: false });
    expect(place('2026-10-10', '2026-10-13', '2026-10-12', '2026-10-19')).toEqual({ col: 1, span: 1, cutStart: true, cutEnd: false });
    expect(place('2026-10-18', '2026-10-25', '2026-10-12', '2026-10-19')).toEqual({ col: 7, span: 1, cutStart: false, cutEnd: true });
    expect(place('2026-10-19', '2026-10-20', '2026-10-12', '2026-10-19')).toBeNull();   // arrives the day after the window
    expect(place('2026-10-10', '2026-10-12', '2026-10-12', '2026-10-19')).toBeNull();   // left the day the window starts
  });

  it('writes nights the way the API does', () => {
    expect(nightsText('2027-01-07', '2027-01-08')).toBe('for the night of 7 Jan 2027');
    expect(nightsText('2027-01-07', '2027-01-09')).toBe('for the nights of 7 Jan – 8 Jan 2027');
    expect(nightsText('2026-12-30', '2027-01-02')).toBe('for the nights of 30 Dec 2026 – 1 Jan 2027');
  });
});

describe('calendar rows and moves', () => {
  const rooms = [room('101', 'DLX', 3), room('102', 'DLX', 3), room('103'), room('104'), dorm];
  const cal = (extra: Partial<Calendar> = {}): Calendar => ({ from: '2026-10-12', to: '2026-10-19', today: '2026-10-15', rooms, bookings: [], blocks: [], ...extra });

  it('one row per room, one per dorm bed, with the first of each type marked', () => {
    const rows = buildRows(rooms);
    expect(rows.map(r => r.label)).toEqual(['Room 101', 'Room 102', 'Room 103', 'Room 104', 'Dormitory · B01', 'Dormitory · B02']);
    expect(rows.filter(r => r.first).map(r => r.typeName)).toEqual(['Deluxe room', 'Standard room', 'Dormitory bed']);
  });

  it('a whole-dorm block shows on every bed; a bed block on that bed only', () => {
    const rows = buildRows(rooms);
    const m = blocksByRow(rows, [
      { id: 'k1', unitId: 'u-DORM', bedId: null, blockType: 'MAINTENANCE', startDate: '2026-10-13', endDate: '2026-10-14', reason: 'Pest control' },
      { id: 'k2', unitId: 'u-DORM', bedId: 'b2', blockType: 'OTHER', startDate: '2026-10-16', endDate: '2026-10-17', reason: 'Broken ladder' },
    ]);
    expect(m.get('u-DORM:b1')!.map(k => k.id)).toEqual(['k1']);
    expect(m.get('u-DORM:b2')!.map(k => k.id)).toEqual(['k1', 'k2']);
  });

  it('says why a move would be refused, in the API’s words', () => {
    const rows = buildRows(rooms);
    const r = (label: string) => rows.find(x => x.label === label)!;
    const b = booking('R00001', 'u-101', '2026-10-15', '2026-10-17');
    const c = cal({
      bookings: [b, booking('R00002', 'u-103', '2026-10-16', '2026-10-18')],
      blocks: [{ id: 'k', unitId: 'u-104', bedId: null, blockType: 'MAINTENANCE', startDate: '2026-10-14', endDate: '2026-10-16', reason: 'AC repair' }],
    });
    expect(moveProblem(b, r('Room 102'), c)).toBeNull();
    expect(moveProblem(b, r('Room 103'), c)).toBe(
      "Room 103 isn't free for the nights of 15 Oct – 16 Oct 2026: it is booked on R00002. Choose another room.");
    expect(moveProblem(b, r('Room 104'), c)).toBe(
      "Room 104 isn't free for the nights of 15 Oct – 16 Oct 2026: it is blocked (AC repair). Choose another room.");
    expect(moveProblem({ ...b, adults: 3 }, r('Room 103'), c)).toBe(
      'Room 103 (Standard room) sleeps at most 2; this room has 3 guests. Choose a bigger room.');
    expect(moveProblem(b, r('Dormitory · B01'), c)).toContain('is a dormitory, sold by the bed');
    expect(moveProblem({ ...b, status: 'CHECKED_IN' }, r('Room 102'), c)).toContain('is checked in');
    // departing on the day the other arrives is not a clash
    expect(moveProblem(booking('R00009', 'u-102', '2026-10-13', '2026-10-16'), r('Room 103'), c)).toBeNull();
  });

  it('lists each day’s arrivals, stays, departures and blocks for the phone view', () => {
    const rows = buildRows(rooms);
    const c = cal({
      bookings: [booking('R00001', 'u-101', '2026-10-15', '2026-10-17')],
      blocks: [{ id: 'k', unitId: 'u-DORM', bedId: null, blockType: 'MAINTENANCE', startDate: '2026-10-16', endDate: '2026-10-17', reason: 'Pest control' }],
    });
    const days = byDay(c, rows, daysIn('2026-10-15', '2026-10-18'));
    expect(days.map(d => d.lines.map(l => `${l.kind} ${l.row.label}`))).toEqual([
      ['arrives Room 101'],
      ['stays Room 101', 'blocked Dormitory'],
      ['leaves Room 101'],
    ]);
  });
});

describe('calendar grid', () => {
  beforeEach(() => TestBed.configureTestingModule({ imports: [CalendarGrid] }));

  it('renders 7 rooms × 31 days in under 200 ms', () => {
    const from = '2026-10-01';
    const seven = ['101', '102', '103', '104', '105', '106', '107'].map((c, i) => room(c, i < 2 ? 'DLX' : 'STD'));
    // a busy month: every room booked back to back in 2- and 3-night stays, plus a block
    const bookings: CalendarBooking[] = [];
    seven.forEach((r, i) => {
      let d = i % 3;
      while (d < 31) {
        const n = 2 + ((d + i) % 2);
        bookings.push(booking(`R${String(bookings.length + 1).padStart(5, '0')}`, r.id, addDays(from, d), addDays(from, d + n)));
        d += n;
      }
    });
    const cal: Calendar = {
      from, to: '2026-11-01', today: '2026-10-15', rooms: seven, bookings,
      blocks: [{ id: 'k', unitId: 'u-107', bedId: null, blockType: 'OUT_OF_ORDER', startDate: '2026-10-20', endDate: '2026-10-22', reason: 'Leak' }],
    };
    const fixture = TestBed.createComponent(CalendarGrid);
    fixture.componentRef.setInput('cal', cal);
    fixture.componentRef.setInput('days', daysIn(from, '2026-11-01'));
    fixture.componentRef.setInput('canMove', true);

    const t0 = performance.now();
    fixture.detectChanges();
    const ms = performance.now() - t0;

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelectorAll('.row:not(.head)').length).toBe(7);
    expect(el.querySelectorAll('.cell').length).toBe(7 * 31);
    expect(el.querySelectorAll('.bar').length).toBe(bookings.length);
    expect(el.querySelectorAll('.block').length).toBe(1);
    expect(ms).withContext(`first render took ${ms.toFixed(1)} ms`).toBeLessThan(200);
  });

  it('draws a bar over its nights and reports a click', () => {
    const fixture = TestBed.createComponent(CalendarGrid);
    const b = booking('R00001', 'u-101', '2026-10-14', '2026-10-16');
    fixture.componentRef.setInput('cal', { from: '2026-10-12', to: '2026-10-19', today: '2026-10-15', rooms: [room('101')], bookings: [b], blocks: [] });
    fixture.componentRef.setInput('days', daysIn('2026-10-12', '2026-10-19'));
    let opened: CalendarBooking | null = null;
    fixture.componentInstance.opened.subscribe(x => (opened = x));
    fixture.detectChanges();
    const bar: HTMLElement = fixture.nativeElement.querySelector('.bar');
    expect(bar.style.gridColumn).toBe('4 / span 2');            // label column, then Mon 12, Tue 13 → Wed 14
    expect(bar.textContent).toContain('Guest R00001');
    expect(bar.getAttribute('title')).toContain('No payment recorded');
    expect(bar.classList).toContain('cdk-drag-disabled');      // canMove defaults to false
    bar.click();
    expect(opened!.reference).toBe('R00001');
  });
});
