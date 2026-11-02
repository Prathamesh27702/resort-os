import { HttpClient, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, from, map, switchMap, throwError } from 'rxjs';
import {
  ActivityStay, CancelTripResult, CreateTripsResult, MyTrips, PassengerStatus, TransportDay, TransportRoute, Trip, TripDetail, TripPassenger, TripStatus,
  Vehicle,
} from '../../core/models';
import { fileName, jsonError, save } from '../invoices/invoices.api';

export interface SaveVehicle {
  code: string; name: string; registration: string; seats: number; note: string | null; sortOrder: number; isActive: boolean; version: number | null;
}
export interface SaveRoute {
  code: string; name: string; origin: string; destination: string; distanceKm: number; durationMinutes: number; price: number; sortOrder: number;
  isActive: boolean; version: number | null;
}
export interface CreateTrips {
  routeId: string; vehicleId: string; driverUserId: string | null; fromDate: string; toDate: string; times: string[]; price: number | null;
}
export interface BookTransport {
  tripId: string; seats: number; reservationId: string | null; guestName: string | null; complimentary: boolean; complimentaryReason: string | null;
  note: string | null;
}

export const TRIP_STATUS: Record<TripStatus, string> = { SCHEDULED: 'Scheduled', DEPARTED: 'On the road', COMPLETED: 'Arrived', CANCELLED: 'Cancelled' };
export const PASSENGER_STATUS: Record<PassengerStatus, string> = { BOOKED: 'Booked', BOARDED: 'On board', NO_SHOW: 'No-show', CANCELLED: 'Cancelled' };

/** What happened to a passenger's money, in words. */
export function fareText(p: TripPassenger): string {
  const rupees = (n: number) => '₹' + n.toLocaleString('en-IN', { maximumFractionDigits: 2 });
  if (p.status === 'CANCELLED') return p.refund === 'REVERSED' ? `${rupees(p.amount)} taken off ${p.reservationReference}'s bill` : 'nothing was charged';
  if (p.settlement === 'ROOM_CHARGE') return `${rupees(p.amount)} on ${p.reservationReference}'s bill`;
  if (p.settlement === 'COMPLIMENTARY') return `complimentary: ${p.complimentaryReason}`;
  return 'free';
}

/** Typed client for the Sprint 21 transport endpoints. Booking needs an Idempotency-Key, so a retried click books once. */
@Injectable({ providedIn: 'root' })
export class TransportApi {
  private http = inject(HttpClient);
  private url = (pid: string) => `/api/v1/properties/${pid}/transport`;

  saveVehicle(pid: string, id: string | null, body: SaveVehicle): Observable<Vehicle> {
    return id ? this.http.put<Vehicle>(`${this.url(pid)}/vehicles/${id}`, body) : this.http.post<Vehicle>(`${this.url(pid)}/vehicles`, body);
  }
  saveRoute(pid: string, id: string | null, body: SaveRoute): Observable<TransportRoute> {
    return id ? this.http.put<TransportRoute>(`${this.url(pid)}/routes/${id}`, body) : this.http.post<TransportRoute>(`${this.url(pid)}/routes`, body);
  }
  day(pid: string, date: string | null) { return this.http.get<TransportDay>(`${this.url(pid)}/trips`, { params: date ? { date } : {} }); }
  mine(pid: string, date: string | null) { return this.http.get<MyTrips>(`${this.url(pid)}/my-trips`, { params: date ? { date } : {} }); }
  trip(pid: string, id: string) { return this.http.get<TripDetail>(`${this.url(pid)}/trips/${id}`); }
  createTrips(pid: string, body: CreateTrips) { return this.http.post<CreateTripsResult>(`${this.url(pid)}/trips`, body); }
  updateTrip(pid: string, t: Trip, body: { vehicleId: string; driverUserId: string | null; capacity: number; price: number; note: string | null }) {
    return this.http.put<Trip>(`${this.url(pid)}/trips/${t.id}`, { ...body, version: t.version });
  }
  cancelTrip(pid: string, t: Trip, reason: string) { return this.http.post<CancelTripResult>(`${this.url(pid)}/trips/${t.id}/cancel`, { reason }); }
  depart(pid: string, t: Trip) { return this.http.post<TripDetail>(`${this.url(pid)}/trips/${t.id}/depart`, {}); }
  complete(pid: string, t: Trip) { return this.http.post<TripDetail>(`${this.url(pid)}/trips/${t.id}/complete`, {}); }
  stays(pid: string, date: string) { return this.http.get<ActivityStay[]>(`${this.url(pid)}/stays`, { params: { date } }); }
  /** One booking attempt: the key is kept by the dialog, so a retried click replays instead of booking twice. */
  book(pid: string, body: BookTransport, key: string) {
    return this.http.post<TripPassenger>(`${this.url(pid)}/bookings`, body, { headers: { 'Idempotency-Key': key } });
  }
  cancel(pid: string, p: TripPassenger, reason: string) {
    return this.http.post<TripPassenger>(`${this.url(pid)}/bookings/${p.id}/cancel`, { reason, version: p.version });
  }
  board(pid: string, p: TripPassenger, on: boolean) {
    return this.http.post<TripPassenger>(`${this.url(pid)}/bookings/${p.id}/${on ? 'board' : 'unboard'}`, {});
  }

  /** Downloads the trip's manifest PDF, saved as manifest-BEACH-20261103-0800.pdf. */
  manifest(pid: string, t: Trip): Observable<string> {
    return this.http.get(`${this.url(pid)}/trips/${t.id}/manifest.pdf`, { responseType: 'blob', observe: 'response' }).pipe(
      map((r: HttpResponse<Blob>) => {
        const name = fileName(r.headers.get('Content-Disposition')) ?? `manifest-${t.routeCode}.pdf`;
        save(r.body!, name);
        return name;
      }),
      catchError((e: unknown) => from(jsonError(e)).pipe(switchMap(err => throwError(() => err)))),
    );
  }
}
