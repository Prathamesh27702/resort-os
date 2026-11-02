import { HttpErrorResponse } from '@angular/common/http';
import { ActivityBooking } from '../../core/models';
import { showError } from '../leave/leave.api';
import { EQUIPMENT_STATUS, rupees, seats, settlementText } from './activities.api';

const booking = (b: Partial<ActivityBooking>): ActivityBooking => ({
  id: 'b', code: 'AB00001', sessionId: 's', activityName: 'Kayaking', date: '2026-11-02', startTime: '07:00', reservationId: 'r',
  reservationReference: 'R00008', room: 'Room 104', guestName: 'Asha Rao', seats: 2, unitPrice: 800, amount: 1600, settlement: 'ROOM_CHARGE',
  complimentaryReason: null, folioNumber: 'F00001', note: null, status: 'BOOKED', cancelKind: null, cancelReason: null, cancelledAt: null, refund: null,
  movedToCode: null, movedFromCode: null, equipment: [], actions: [], createdAt: '', by: null, version: 1, ...b,
});

describe('activities helpers', () => {
  it('says money and seats in words', () => {
    expect(rupees(800)).toBe('₹800');
    expect(rupees(1500.5)).toBe('₹1,500.5');
    expect(seats(1)).toBe('1 seat');
    expect(seats(6)).toBe('6 seats');
    expect(EQUIPMENT_STATUS.MAINTENANCE).toBe('In maintenance');
  });

  it('says where a booking\'s money went, also after the weather', () => {
    expect(settlementText(booking({}))).toBe("₹1,600 on R00008's bill");
    expect(settlementText(booking({ settlement: 'COMPLIMENTARY', complimentaryReason: 'Staff guide', reservationId: null }))).toBe('complimentary: Staff guide');
    expect(settlementText(booking({ settlement: 'FREE', amount: 0 }))).toBe('free');
    expect(settlementText(booking({ status: 'CANCELLED', refund: 'REVERSED' }))).toBe("₹1,600 taken off R00008's bill");
    expect(settlementText(booking({ status: 'CANCELLED', refund: 'MOVED', movedToCode: 'AB00005' }))).toBe('moved to AB00005');
  });

  it('shows a full session in words, not a details object', () => {
    let said = '';
    showError(null, new HttpErrorResponse({ status: 409, error: { code: 'ACTIVITY_FULL', message: 'Kayaking at 07:00 on Mon 2 Nov is full (6 of 6 seats booked). Choose another session.', details: { seatsLeft: 0 } } }),
      m => (said = m));
    expect(said).toBe('Kayaking at 07:00 on Mon 2 Nov is full (6 of 6 seats booked). Choose another session.');
  });
});
