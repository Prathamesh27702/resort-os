import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import {
  MaintenanceAsset, StaffOption, Ticket, TicketCategory, TicketPhoto, TicketPriority, TicketStatus, TicketSummary,
} from '../../core/models';

export interface CreateTicket {
  title: string; description: string | null; category: TicketCategory; priority: TicketPriority; unitId: string | null; bedId: string | null;
  assetId: string | null; location: string | null; outOfOrder: boolean; expectedReadyOn: string | null;
}
export interface SaveAsset { code: string; name: string; category: TicketCategory; unitId: string | null; location: string | null; isActive: boolean; }

/** Typed client for the Sprint 13 maintenance endpoints. Steps send the ticket's version, so a stale screen is refused. */
@Injectable({ providedIn: 'root' })
export class MaintenanceApi {
  private http = inject(HttpClient);
  private base = (pid: string) => `/api/v1/properties/${pid}/maintenance`;

  /** status: OPEN (default, anything unresolved), ALL, or one status. */
  list(pid: string, opts: { status?: string; mine?: boolean; unitId?: string } = {}) {
    let params = new HttpParams();
    if (opts.status) params = params.set('status', opts.status);
    if (opts.mine) params = params.set('mine', true);
    if (opts.unitId) params = params.set('unitId', opts.unitId);
    return this.http.get<TicketSummary[]>(`${this.base(pid)}/tickets`, { params });
  }
  get(pid: string, id: string) { return this.http.get<Ticket>(`${this.base(pid)}/tickets/${id}`); }
  create(pid: string, body: CreateTicket, idempotencyKey: string) {
    return this.http.post<Ticket>(`${this.base(pid)}/tickets`, body, { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) });
  }
  assign(pid: string, t: Ticket, userId: string | null, note: string | null) {
    return this.http.post<Ticket>(`${this.base(pid)}/tickets/${t.id}/assign`, { userId, note, version: t.version });
  }
  progress(pid: string, t: Ticket, status: 'IN_PROGRESS' | 'ON_HOLD', note: string) {
    return this.http.post<Ticket>(`${this.base(pid)}/tickets/${t.id}/progress`, { status, note, version: t.version });
  }
  resolve(pid: string, t: Ticket, resolution: string) {
    return this.http.post<Ticket>(`${this.base(pid)}/tickets/${t.id}/resolve`, { resolution, version: t.version });
  }
  close(pid: string, t: Ticket, note: string | null) {
    return this.http.post<Ticket>(`${this.base(pid)}/tickets/${t.id}/close`, { note, version: t.version });
  }
  comment(pid: string, id: string, body: string) { return this.http.post<Ticket>(`${this.base(pid)}/tickets/${id}/comments`, { body }); }
  addPhoto(pid: string, id: string, photo: Blob, fileName: string) {
    const fd = new FormData();
    fd.set('file', photo, fileName);
    return this.http.post<TicketPhoto>(`${this.base(pid)}/tickets/${id}/photos`, fd);
  }
  photo(pid: string, id: string, photoId: string) { return this.http.get(`${this.base(pid)}/tickets/${id}/photos/${photoId}`, { responseType: 'blob' }); }
  staff(pid: string) { return this.http.get<StaffOption[]>(`${this.base(pid)}/staff`); }
  assets(pid: string) { return this.http.get<MaintenanceAsset[]>(`${this.base(pid)}/assets`); }
  saveAsset(pid: string, id: string | null, body: SaveAsset) {
    return id ? this.http.put<MaintenanceAsset>(`${this.base(pid)}/assets/${id}`, body) : this.http.post<MaintenanceAsset>(`${this.base(pid)}/assets`, body);
  }
}

export const CATEGORIES: TicketCategory[] = ['AC', 'PLUMBING', 'ELECTRICAL', 'FURNITURE', 'APPLIANCE', 'POOL', 'CIVIL', 'OTHER'];
const CATEGORY_LABEL: Record<TicketCategory, string> = {
  AC: 'AC', PLUMBING: 'Plumbing', ELECTRICAL: 'Electrical', FURNITURE: 'Furniture', APPLIANCE: 'Appliance', POOL: 'Pool',
  CIVIL: 'Building / civil', OTHER: 'Other',
};
export const categoryLabel = (c: TicketCategory | string) => CATEGORY_LABEL[c as TicketCategory] ?? c;

export const PRIORITIES: TicketPriority[] = ['URGENT', 'HIGH', 'NORMAL', 'LOW'];
const PRIORITY_LABEL: Record<TicketPriority, string> = { URGENT: 'Urgent', HIGH: 'High', NORMAL: 'Normal', LOW: 'Low' };
export const priorityLabel = (p: TicketPriority | string) => PRIORITY_LABEL[p as TicketPriority] ?? p;

const STATUS_LABEL: Record<TicketStatus, string> = {
  OPEN: 'Open', ASSIGNED: 'Assigned', IN_PROGRESS: 'In progress', ON_HOLD: 'On hold', RESOLVED: 'Resolved', CLOSED: 'Closed',
};
export const ticketStatusLabel = (s: TicketStatus | string) => STATUS_LABEL[s as TicketStatus] ?? s;
export const ticketStatusClass = (s: TicketStatus | string) => s.toLowerCase().replaceAll('_', '-');

/** The latest date an out-of-order room may be expected back (the server allows at most 90 days). */
export function addDays(isoDate: string, days: number): string {
  const d = new Date(isoDate + 'T00:00:00Z');
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

/** Today's date (YYYY-MM-DD) at the property. */
export function localToday(tz: string, now: Date = new Date()): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: tz, year: 'numeric', month: '2-digit', day: '2-digit' }).format(now);
}
