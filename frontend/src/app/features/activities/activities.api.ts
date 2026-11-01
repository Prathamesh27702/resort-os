import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  Activity, ActivityBooking, ActivityDay, ActivitySessionDetail, ActivitySession, ActivityStay, CreateSessionsResult, EquipmentAssignment, EquipmentBoard,
  EquipmentItem, EquipmentItemHistory, EquipmentStatus, EquipmentType, WeatherCancelResult,
} from '../../core/models';

export interface SaveActivity {
  code: string; name: string; description: string | null; durationMinutes: number; capacity: number; price: number; meetingPoint: string | null;
  weatherDependent: boolean; equipmentTypeId: string | null; sortOrder: number; isActive: boolean; version: number | null;
}
export interface CreateSessions { activityId: string; fromDate: string; toDate: string; times: string[]; capacity: number | null; price: number | null; }
export interface BookActivity {
  sessionId: string; seats: number; reservationId: string | null; guestName: string | null; complimentary: boolean; complimentaryReason: string | null;
  note: string | null;
}
export interface IssueEquipment {
  activityBookingId: string | null; reservationId: string | null; issuedTo: string | null; equipmentIds: string[] | null; equipmentTypeId: string | null;
  quantity: number | null; reserveOnly: boolean;
}

export const EQUIPMENT_STATUS: Record<EquipmentStatus, string> = {
  AVAILABLE: 'Available', RESERVED: 'Reserved', ISSUED: 'Issued', DAMAGED: 'Damaged', MAINTENANCE: 'In maintenance', LOST: 'Lost', RETIRED: 'Retired',
};

/** "₹800", "₹1,500.50". */
export function rupees(n: number): string {
  return '₹' + n.toLocaleString('en-IN', { maximumFractionDigits: 2 });
}

/** "1 seat", "4 seats". */
export function seats(n: number): string { return `${n} ${n === 1 ? 'seat' : 'seats'}`; }

/** What happened to a booking's money, in words. */
export function settlementText(b: ActivityBooking): string {
  if (b.status === 'CANCELLED') {
    if (b.refund === 'MOVED') return `moved to ${b.movedToCode}`;
    if (b.refund === 'REVERSED') return `${rupees(b.amount)} taken off ${b.reservationReference}'s bill`;
    return 'nothing was charged';
  }
  if (b.settlement === 'ROOM_CHARGE') return `${rupees(b.amount)} on ${b.reservationReference}'s bill`;
  if (b.settlement === 'COMPLIMENTARY') return `complimentary: ${b.complimentaryReason}`;
  return 'free';
}

@Injectable({ providedIn: 'root' })
export class ActivitiesApi {
  private http = inject(HttpClient);
  private url = (pid: string) => `/api/v1/properties/${pid}/activities`;
  private eq = (pid: string) => `/api/v1/properties/${pid}/equipment`;

  activities(pid: string) { return this.http.get<Activity[]>(this.url(pid)); }
  saveActivity(pid: string, id: string | null, body: SaveActivity): Observable<Activity> {
    return id ? this.http.put<Activity>(`${this.url(pid)}/${id}`, body) : this.http.post<Activity>(this.url(pid), body);
  }
  day(pid: string, date: string | null) {
    return this.http.get<ActivityDay>(`${this.url(pid)}/sessions`, { params: date ? { date } : {} });
  }
  session(pid: string, id: string) { return this.http.get<ActivitySessionDetail>(`${this.url(pid)}/sessions/${id}`); }
  createSessions(pid: string, body: CreateSessions) { return this.http.post<CreateSessionsResult>(`${this.url(pid)}/sessions`, body); }
  updateSession(pid: string, s: ActivitySession, capacity: number, price: number, note: string | null) {
    return this.http.put<ActivitySession>(`${this.url(pid)}/sessions/${s.id}`, { capacity, price, note, version: s.version });
  }
  weatherCancel(pid: string, s: ActivitySession, reason: string, moves: { bookingId: string; toSessionId: string }[]) {
    return this.http.post<WeatherCancelResult>(`${this.url(pid)}/sessions/${s.id}/weather-cancel`, { reason, moves, version: s.version });
  }
  stays(pid: string, date: string) { return this.http.get<ActivityStay[]>(`${this.url(pid)}/stays`, { params: { date } }); }
  /** One booking attempt: the key is kept by the dialog, so a retried click replays instead of booking twice. */
  book(pid: string, body: BookActivity, key: string) {
    return this.http.post<ActivityBooking>(`${this.url(pid)}/bookings`, body, { headers: { 'Idempotency-Key': key } });
  }
  cancel(pid: string, b: ActivityBooking, reason: string) {
    return this.http.post<ActivityBooking>(`${this.url(pid)}/bookings/${b.id}/cancel`, { reason, version: b.version });
  }

  board(pid: string) { return this.http.get<EquipmentBoard>(this.eq(pid)); }
  saveType(pid: string, id: string | null, body: { code: string; name: string; sortOrder: number; isActive: boolean; version: number | null }) {
    return id ? this.http.put<EquipmentType>(`${this.eq(pid)}/types/${id}`, body) : this.http.post<EquipmentType>(`${this.eq(pid)}/types`, body);
  }
  addItems(pid: string, body: { equipmentTypeId: string; tag: string | null; prefix: string | null; count: number | null; note: string | null }) {
    return this.http.post<EquipmentItem[]>(`${this.eq(pid)}/items`, body);
  }
  item(pid: string, id: string) { return this.http.get<EquipmentItemHistory>(`${this.eq(pid)}/items/${id}`); }
  setStatus(pid: string, item: EquipmentItem, status: EquipmentStatus, note: string | null) {
    return this.http.post<EquipmentItem>(`${this.eq(pid)}/items/${item.id}/status`, { status, note, version: item.version });
  }
  issue(pid: string, body: IssueEquipment) { return this.http.post<EquipmentAssignment[]>(`${this.eq(pid)}/issue`, body); }
  handOver(pid: string, a: EquipmentAssignment) { return this.http.post<EquipmentAssignment>(`${this.eq(pid)}/assignments/${a.id}/issue`, { version: a.version }); }
  release(pid: string, a: EquipmentAssignment) { return this.http.post<EquipmentAssignment>(`${this.eq(pid)}/assignments/${a.id}/release`, { version: a.version }); }
  returnItem(pid: string, a: EquipmentAssignment, condition: 'GOOD' | 'DAMAGED' | 'LOST', note: string | null) {
    return this.http.post<EquipmentAssignment>(`${this.eq(pid)}/assignments/${a.id}/return`, { condition, note, version: a.version });
  }
}
