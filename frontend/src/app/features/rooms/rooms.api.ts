import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { AccommodationType, Amenity, Bed, Room, RoomStatusBoard, UnitBlock } from '../../core/models';

/** Typed client for the Sprint 2 accommodation endpoints. Every call is for one property. */
@Injectable({ providedIn: 'root' })
export class RoomsApi {
  private http = inject(HttpClient);
  private base = (pid: string) => `/api/v1/properties/${pid}`;

  amenities(pid: string) { return this.http.get<Amenity[]>(`${this.base(pid)}/amenities`); }

  types(pid: string) { return this.http.get<AccommodationType[]>(`${this.base(pid)}/accommodation-types`); }
  type(pid: string, id: string) { return this.http.get<AccommodationType>(`${this.base(pid)}/accommodation-types/${id}`); }
  createType(pid: string, body: unknown) { return this.http.post<AccommodationType>(`${this.base(pid)}/accommodation-types`, body); }
  updateType(pid: string, id: string, body: unknown) { return this.http.put<AccommodationType>(`${this.base(pid)}/accommodation-types/${id}`, body); }

  rooms(pid: string) { return this.http.get<Room[]>(`${this.base(pid)}/rooms`); }
  room(pid: string, id: string) { return this.http.get<Room>(`${this.base(pid)}/rooms/${id}`); }
  createRoom(pid: string, body: unknown) { return this.http.post<Room>(`${this.base(pid)}/rooms`, body); }
  updateRoom(pid: string, id: string, body: unknown) { return this.http.put<Room>(`${this.base(pid)}/rooms/${id}`, body); }

  createBed(pid: string, roomId: string, body: unknown) { return this.http.post<Bed>(`${this.base(pid)}/rooms/${roomId}/beds`, body); }
  updateBed(pid: string, bedId: string, body: unknown) { return this.http.put<Bed>(`${this.base(pid)}/beds/${bedId}`, body); }

  status(pid: string, date?: string) {
    const params = date ? new HttpParams().set('date', date) : undefined;
    return this.http.get<RoomStatusBoard>(`${this.base(pid)}/rooms/status`, { params });
  }

  blocks(pid: string, opts: { unitId?: string; includeReleased?: boolean } = {}) {
    let params = new HttpParams();
    if (opts.unitId) params = params.set('unitId', opts.unitId);
    if (opts.includeReleased) params = params.set('includeReleased', true);
    return this.http.get<UnitBlock[]>(`${this.base(pid)}/blocks`, { params });
  }
  createBlock(pid: string, body: unknown) { return this.http.post<UnitBlock>(`${this.base(pid)}/blocks`, body); }
  releaseBlock(pid: string, id: string, reason: string) {
    return this.http.post<UnitBlock>(`${this.base(pid)}/blocks/${id}/release`, { reason });
  }
}

/** Human labels for codes coming from the API. */
export const label = (code: string) => {
  const s = code.toLowerCase().replace(/_/g, ' ');
  return s.charAt(0).toUpperCase() + s.slice(1);
};

/** Adds days to a YYYY-MM-DD date without touching time zones. */
export function addDays(iso: string, days: number): string {
  const [y, m, d] = iso.split('-').map(Number);
  const t = new Date(Date.UTC(y, m - 1, d + days));
  return t.toISOString().slice(0, 10);
}

/** Nights between two YYYY-MM-DD dates (end exclusive). */
export function nightsBetween(start: string, end: string): number {
  const a = Date.parse(start + 'T00:00:00Z'), b = Date.parse(end + 'T00:00:00Z');
  return Math.round((b - a) / 86_400_000);
}

/** "7 Oct" style label for a YYYY-MM-DD date. */
export function shortDate(iso: string): string {
  return new Intl.DateTimeFormat('en-IN', { day: 'numeric', month: 'short', timeZone: 'UTC' }).format(new Date(iso + 'T00:00:00Z'));
}
