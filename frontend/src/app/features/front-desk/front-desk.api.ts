import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { FrontDeskBoardRoom, FrontDeskDashboard, FrontDeskList, FrontDeskStay, Housekeeping, Occupancy, Reservation, ReservationStatus, RoomOption } from '../../core/models';
import { addDays } from '../rooms/rooms.api';

/** Typed client for the Sprint 7 front desk endpoints. Dates default to the property's business date on the server. */
@Injectable({ providedIn: 'root' })
export class FrontDeskApi {
  private http = inject(HttpClient);
  private base = (pid: string) => `/api/v1/properties/${pid}`;

  arrivals(pid: string, date?: string) { return this.http.get<FrontDeskList>(`${this.base(pid)}/front-desk/arrivals`, { params: dateParam(date) }); }
  departures(pid: string, date?: string) { return this.http.get<FrontDeskList>(`${this.base(pid)}/front-desk/departures`, { params: dateParam(date) }); }
  inHouse(pid: string) { return this.http.get<FrontDeskList>(`${this.base(pid)}/front-desk/in-house`); }
  dashboard(pid: string) { return this.http.get<FrontDeskDashboard>(`${this.base(pid)}/front-desk/dashboard`); }

  roomOptions(pid: string, id: string, reservationUnitId: string) {
    return this.http.get<RoomOption[]>(`${this.base(pid)}/reservations/${id}/room-options`, { params: { reservationUnitId } });
  }
  assignRoom(pid: string, id: string, body: { reservationUnitId: string; unitId: string; bedId: string | null; reason: string | null; version: number; overrideHousekeeping: boolean }) {
    return this.http.post<Reservation>(`${this.base(pid)}/reservations/${id}/assign-room`, body);
  }
  checkIn(pid: string, id: string, body: { version: number | null; overrideHousekeeping?: boolean; overrideReason?: string | null }) {
    return this.http.post<Reservation>(`${this.base(pid)}/reservations/${id}/check-in`, body);
  }
  checkOut(pid: string, id: string, body: { version: number | null; reason?: string | null }) {
    return this.http.post<Reservation>(`${this.base(pid)}/reservations/${id}/check-out`, body);
  }
  /** Guest + booking from today + check-in in one call. The key makes a retry return the same stay. */
  walkIn(pid: string, body: unknown, idempotencyKey: string) {
    return this.http.post<Reservation>(`${this.base(pid)}/walk-ins`, body, { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) });
  }
  setHousekeeping(pid: string, unitId: string, body: { bedId: string | null; status: Housekeeping; reason: string | null }) {
    return this.http.put<FrontDeskBoardRoom>(`${this.base(pid)}/rooms/${unitId}/housekeeping`, body);
  }
}

function dateParam(date?: string) { return date ? new HttpParams().set('date', date) : new HttpParams(); }

const HK_LABEL: Record<Housekeeping, string> = {
  DIRTY: 'Dirty', CLEANING: 'Being cleaned', CLEAN: 'Cleaned', INSPECTION: 'To inspect', READY: 'Ready', OUT_OF_ORDER: 'Out of order',
};
export const housekeepingLabel = (h: Housekeeping | string) => HK_LABEL[h as Housekeeping] ?? h;

const OCC_LABEL: Record<Occupancy, string> = {
  VACANT: 'Vacant', OCCUPIED: 'Occupied', ARRIVING: 'Arriving', BLOCKED: 'Blocked', INACTIVE: 'Not in use',
};
export const occupancyLabel = (o: Occupancy | string) => OCC_LABEL[o as Occupancy] ?? o;

/** "Room 101", "Dormitory bed B05". */
export function placeName(r: { unitName: string; bedCode: string | null }): string {
  return r.bedCode ? `${r.unitName} bed ${r.bedCode}` : r.unitName;
}

/** "Room 101", "Room 101, Room 102", "Dormitory · 2 beds". */
export function roomsText(rooms: { unitId: string; unitName: string; bedCode: string | null }[]): string {
  const byUnit = new Map<string, { name: string; beds: number }>();
  for (const r of rooms) {
    const g = byUnit.get(r.unitId) ?? { name: r.unitName, beds: 0 };
    if (r.bedCode) g.beds++;
    byUnit.set(r.unitId, g);
  }
  return [...byUnit.values()].map(g => g.beds ? `${g.name} · ${g.beds} bed${g.beds === 1 ? '' : 's'}` : g.name).join(', ');
}

/** What a front desk row offers next: check in an arrival, check out a departure, or nothing (done or not today). */
export type StayAction = 'check-in' | 'check-out' | null;
export function stayAction(s: Pick<FrontDeskStay, 'status' | 'checkIn' | 'checkOut'>, today: string): StayAction {
  if ((s.status === 'CONFIRMED' || s.status === 'PENDING') && s.checkIn <= today && today < s.checkOut) return 'check-in';
  if (s.status === 'CHECKED_IN') return 'check-out';
  return null;
}

/** The check-in problem is one the desk can fix by choosing another room (or by checking in anyway when not ready). */
export function notReady(s: Pick<FrontDeskStay, 'rooms'>): boolean {
  return s.rooms.some(r => r.housekeeping !== 'READY');
}
export function occupied(s: Pick<FrontDeskStay, 'rooms'>): boolean {
  return s.rooms.some(r => r.occupiedBy !== null);
}

/** Leaving before the booked departure day: the API asks for a reason and releases the nights left. A stay keeps at
 *  least its first night, so leaving on the arrival day of a one-night stay isn't early. */
export function leavingEarly(s: Pick<FrontDeskStay, 'checkIn' | 'checkOut'>, today: string): boolean {
  const earliest = addDays(s.checkIn, 1);
  return (today < earliest ? earliest : today) < s.checkOut;
}

/** The stay as the check-in, room and check-out dialogs need it: from a front desk row or from a booking. */
export interface DeskStay {
  reservationId: string; reference: string; guestName: string; status: ReservationStatus; checkIn: string; checkOut: string;
  version: number; balance: number | null;
  rooms: { reservationUnitId: string; unitName: string; bedCode: string | null; housekeeping: Housekeeping | null }[];
}
export function deskStay(s: FrontDeskStay): DeskStay {
  return { reservationId: s.reservationId, reference: s.reference, guestName: s.guestName, status: s.status, checkIn: s.checkIn,
           checkOut: s.checkOut, version: s.version, balance: s.balance, rooms: s.rooms };
}
/** From a booking: the rooms the guest is (or will be) in, i.e. the current ones that reach the departure day. */
export function deskStayOf(r: Reservation): DeskStay {
  const primary = r.guests.find(g => g.isPrimary) ?? r.guests[0];
  return { reservationId: r.id, reference: r.reference, guestName: primary?.fullName ?? '', status: r.status, checkIn: r.checkIn,
           checkOut: r.checkOut, version: r.version, balance: null,
           rooms: r.units.filter(u => u.current && u.endDate === r.checkOut)
             .map(u => ({ reservationUnitId: u.id, unitName: u.unitName, bedCode: u.bedCode, housekeeping: null })) };
}
