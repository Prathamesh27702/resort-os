import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Availability, Paged, Reservation, ReservationListItem, ReservationSource, ReservationStatus } from '../../core/models';

/** Typed client for the Sprint 5 availability and reservation endpoints. Every call is for one property. */
@Injectable({ providedIn: 'root' })
export class ReservationsApi {
  private http = inject(HttpClient);
  private base = (pid: string) => `/api/v1/properties/${pid}`;

  sources(pid: string) { return this.http.get<ReservationSource[]>(`${this.base(pid)}/reservation-sources`); }

  availability(pid: string, q: { from: string; to: string; adults: number; children: number }) {
    return this.http.get<Availability>(`${this.base(pid)}/availability`, { params: new HttpParams({ fromObject: { ...q } }) });
  }

  list(pid: string, q: { q?: string; status?: string; from?: string; to?: string; page?: number; pageSize?: number } = {}) {
    let params = new HttpParams();
    for (const [k, v] of Object.entries(q)) if (v !== undefined && v !== null && v !== '') params = params.set(k, String(v));
    return this.http.get<Paged<ReservationListItem>>(`${this.base(pid)}/reservations`, { params });
  }
  get(pid: string, id: string) { return this.http.get<Reservation>(`${this.base(pid)}/reservations/${id}`); }

  /** The key makes the call safe to retry: the same key and body return the same booking instead of a second one. */
  create(pid: string, body: unknown, idempotencyKey: string) {
    return this.http.post<Reservation>(`${this.base(pid)}/reservations`, body,
      { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) });
  }
  update(pid: string, id: string, body: unknown) { return this.http.put<Reservation>(`${this.base(pid)}/reservations/${id}`, body); }
  confirm(pid: string, id: string) { return this.http.post<Reservation>(`${this.base(pid)}/reservations/${id}/confirm`, {}); }
  cancel(pid: string, id: string, reason: string) { return this.http.post<Reservation>(`${this.base(pid)}/reservations/${id}/cancel`, { reason }); }
  noShow(pid: string, id: string, reason: string) { return this.http.post<Reservation>(`${this.base(pid)}/reservations/${id}/no-show`, { reason }); }
  addNote(pid: string, id: string, note: string) { return this.http.post<Reservation>(`${this.base(pid)}/reservations/${id}/notes`, { note }); }
}

const STATUS_LABEL: Record<ReservationStatus, string> = {
  INQUIRY: 'Enquiry', PENDING: 'Pending', CONFIRMED: 'Confirmed', CHECKED_IN: 'Checked in', CHECKED_OUT: 'Checked out',
  CANCELLED: 'Cancelled', NO_SHOW: 'No-show', REFUNDED: 'Refunded',
};
export const statusLabel = (s: ReservationStatus | string) => STATUS_LABEL[s as ReservationStatus] ?? s;

/** Status chip colour class: live bookings green/amber, finished grey, lost red. */
export function statusClass(s: ReservationStatus | string): string {
  switch (s) {
    case 'CONFIRMED': case 'CHECKED_IN': return 'ok';
    case 'PENDING': case 'INQUIRY': return 'wait';
    case 'CANCELLED': case 'NO_SHOW': return 'off';
    default: return '';
  }
}

/** "2 adults, 1 child" */
export function guestsText(adults: number, children: number): string {
  return `${adults} adult${adults === 1 ? '' : 's'}` + (children > 0 ? `, ${children} child${children === 1 ? '' : 'ren'}` : '');
}

/** A new random Idempotency-Key. */
export function newKey(): string {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}-${Math.random().toString(36).slice(2)}`;
}
