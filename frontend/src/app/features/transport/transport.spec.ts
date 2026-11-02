import { HttpErrorResponse } from '@angular/common/http';
import { TripPassenger } from '../../core/models';
import { showError } from '../leave/leave.api';
import { PASSENGER_STATUS, TRIP_STATUS, fareText } from './transport.api';

const passenger = (p: Partial<TripPassenger>): TripPassenger => ({
  id: 'p', code: 'TP00002', tripId: 't', routeName: 'Beach shuttle', date: '2026-11-03', departTime: '08:00', reservationId: 'r',
  reservationReference: 'R00008', room: 'Room 104', guestName: 'Asha Rao', seats: 3, unitPrice: 500, amount: 1500, settlement: 'ROOM_CHARGE',
  complimentaryReason: null, folioNumber: 'F00001', note: null, status: 'BOOKED', boardedAt: null, boardedBy: null, cancelReason: null, cancelledAt: null,
  refund: null, actions: [], createdAt: '', by: null, version: 1, ...p,
});

describe('transport helpers', () => {
  it('says where a passenger\'s fare went', () => {
    expect(fareText(passenger({}))).toBe("₹1,500 on R00008's bill");
    expect(fareText(passenger({ settlement: 'COMPLIMENTARY', complimentaryReason: 'Agent visit', reservationId: null }))).toBe('complimentary: Agent visit');
    expect(fareText(passenger({ settlement: 'FREE', amount: 0 }))).toBe('free');
    expect(fareText(passenger({ status: 'CANCELLED', refund: 'REVERSED' }))).toBe("₹1,500 taken off R00008's bill");
    expect(fareText(passenger({ status: 'CANCELLED', refund: 'NONE', settlement: 'COMPLIMENTARY' }))).toBe('nothing was charged');
  });

  it('names trip and passenger states in words', () => {
    expect(TRIP_STATUS.DEPARTED).toBe('On the road');
    expect(PASSENGER_STATUS.BOARDED).toBe('On board');
    expect(PASSENGER_STATUS.NO_SHOW).toBe('No-show');
  });

  it('shows a full trip in words, not a details object', () => {
    let said = '';
    showError(null, new HttpErrorResponse({ status: 409, error: { code: 'TRIP_FULL', message: 'Beach shuttle at 08:00 on Tue 3 Nov is full (12 of 12 seats booked). Choose another trip.', details: { seatsLeft: 0 } } }),
      m => (said = m));
    expect(said).toBe('Beach shuttle at 08:00 on Tue 3 Nov is full (12 of 12 seats booked). Choose another trip.');
  });
});
