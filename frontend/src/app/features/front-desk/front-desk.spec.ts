import { Reservation } from '../../core/models';
import { deskStayOf, housekeepingLabel, leavingEarly, notReady, occupied, placeName, roomsText, stayAction } from './front-desk.api';

const room = (unitId: string, unitName: string, bedCode: string | null = null) => ({ unitId, unitName, bedCode });

describe('front desk helpers', () => {
  it('names rooms and dorm beds the way the desk says them', () => {
    expect(placeName(room('u1', 'Room 101'))).toBe('Room 101');
    expect(placeName(room('d', 'Dormitory', 'B05'))).toBe('Dormitory bed B05');
    expect(roomsText([room('u1', 'Room 101'), room('u2', 'Room 102')])).toBe('Room 101, Room 102');
    expect(roomsText([room('d', 'Dormitory', 'B01'), room('d', 'Dormitory', 'B02')])).toBe('Dormitory · 2 beds');
    expect(housekeepingLabel('OUT_OF_ORDER')).toBe('Out of order');
  });

  it('offers check-in from the arrival day until the last night, and check-out to anyone in house', () => {
    const today = '2026-10-17';
    expect(stayAction({ status: 'CONFIRMED', checkIn: today, checkOut: '2026-10-19' }, today)).toBe('check-in');
    expect(stayAction({ status: 'PENDING', checkIn: '2026-10-16', checkOut: '2026-10-19' }, today)).toBe('check-in');   // late arrival
    expect(stayAction({ status: 'CONFIRMED', checkIn: '2026-10-18', checkOut: '2026-10-19' }, today)).toBeNull();      // tomorrow
    expect(stayAction({ status: 'CONFIRMED', checkIn: '2026-10-16', checkOut: today }, today)).toBeNull();             // stay is over
    expect(stayAction({ status: 'CHECKED_IN', checkIn: '2026-10-16', checkOut: '2026-10-16' }, today)).toBe('check-out'); // overdue
    expect(stayAction({ status: 'CHECKED_OUT', checkIn: '2026-10-16', checkOut: today }, today)).toBeNull();
  });

  it('calls a departure early only when nights are left after today (the first night is always kept)', () => {
    expect(leavingEarly({ checkIn: '2026-10-15', checkOut: '2026-10-19' }, '2026-10-17')).toBeTrue();
    expect(leavingEarly({ checkIn: '2026-10-15', checkOut: '2026-10-17' }, '2026-10-17')).toBeFalse();
    expect(leavingEarly({ checkIn: '2026-10-17', checkOut: '2026-10-18' }, '2026-10-17')).toBeFalse();
    expect(leavingEarly({ checkIn: '2026-10-17', checkOut: '2026-10-19' }, '2026-10-17')).toBeTrue();
  });

  it('spots rooms that are not ready or still occupied', () => {
    const r = (housekeeping: 'READY' | 'DIRTY', occupiedBy: string | null = null) =>
      ({ reservationUnitId: 'ru', unitId: 'u', unitCode: '101', unitName: 'Room 101', bedId: null, bedCode: null, typeName: 'Standard room',
         startDate: '2026-10-17', endDate: '2026-10-18', housekeeping, occupiedBy });
    expect(notReady({ rooms: [r('READY')] })).toBeFalse();
    expect(notReady({ rooms: [r('READY'), r('DIRTY')] })).toBeTrue();
    expect(occupied({ rooms: [r('READY', 'R00007')] })).toBeTrue();
  });

  it('takes the rooms a booking will be in on its last night', () => {
    const unit = (id: string, unitName: string, endDate: string, current = true) => ({ id, unitName, bedCode: null, endDate, current });
    const res = {
      id: 'r1', reference: 'R00009', status: 'CHECKED_IN', checkIn: '2026-10-15', checkOut: '2026-10-18', version: 4,
      guests: [{ fullName: 'Asha Rao', isPrimary: true }],
      units: [unit('a', 'Room 101', '2026-10-16'), unit('b', 'Room 102', '2026-10-18'), unit('c', 'Room 103', '2026-10-18', false)],
    } as unknown as Reservation;
    const s = deskStayOf(res);
    expect(s.guestName).toBe('Asha Rao');
    expect(s.rooms.map(x => x.unitName)).toEqual(['Room 102']);
  });
});
