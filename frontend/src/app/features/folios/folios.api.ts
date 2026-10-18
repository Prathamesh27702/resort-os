import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { ChargeCategory, Folio, FolioItem, FolioListItem, Paged } from '../../core/models';

export type FolioShow = 'in-house' | 'open' | 'all';

export interface PostCharge {
  category: ChargeCategory | null; description: string | null; quantity: number; unitPrice: number; serviceDate: string | null;
  packageId: string | null;
}

/** Typed client for the Sprint 8 folio endpoints. Posting a charge needs an Idempotency-Key, so a retry can't charge twice. */
@Injectable({ providedIn: 'root' })
export class FoliosApi {
  private http = inject(HttpClient);
  private base = (pid: string) => `/api/v1/properties/${pid}`;

  list(pid: string, q: { q?: string; show?: FolioShow; page?: number; pageSize?: number }) {
    let params = new HttpParams();
    for (const [k, v] of Object.entries(q)) if (v !== undefined && v !== '') params = params.set(k, String(v));
    return this.http.get<Paged<FolioListItem>>(`${this.base(pid)}/folios`, { params });
  }
  forReservation(pid: string, reservationId: string) { return this.http.get<FolioListItem[]>(`${this.base(pid)}/reservations/${reservationId}/folios`); }
  get(pid: string, id: string) { return this.http.get<Folio>(`${this.base(pid)}/folios/${id}`); }
  postCharge(pid: string, id: string, body: PostCharge, idempotencyKey: string) {
    return this.http.post<Folio>(`${this.base(pid)}/folios/${id}/charges`, body, { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) });
  }
  reverse(pid: string, id: string, itemId: string, reason: string) {
    return this.http.post<Folio>(`${this.base(pid)}/folios/${id}/charges/${itemId}/reverse`, { reason });
  }
  split(pid: string, id: string, body: { itemIds: string[]; targetFolioId: string | null; newFolioName: string | null }) {
    return this.http.post<{ from: Folio; to: Folio }>(`${this.base(pid)}/folios/${id}/split`, body);
  }
}

export const CATEGORIES: ChargeCategory[] = ['ROOM', 'FOOD', 'BEVERAGE', 'ACTIVITY', 'TRANSPORT', 'EQUIPMENT', 'LAUNDRY', 'OTHER'];
const CATEGORY_LABEL: Record<ChargeCategory, string> = {
  ROOM: 'Room', FOOD: 'Food', BEVERAGE: 'Beverage', ACTIVITY: 'Activity', TRANSPORT: 'Transport', EQUIPMENT: 'Equipment', LAUNDRY: 'Laundry', OTHER: 'Other',
};
export const categoryLabel = (c: ChargeCategory | string) => CATEGORY_LABEL[c as ChargeCategory] ?? c;

/** "2 × ₹650" for a line with a quantity other than 1; '' otherwise. Reversals show the quantity they cancel. */
export function quantityText(i: Pick<FolioItem, 'quantity' | 'unitPrice'>, money: (v: number) => string): string {
  const q = Math.abs(i.quantity);
  return q === 1 ? '' : `${Number(q.toFixed(3))} × ${money(i.unitPrice)}`;
}

/** What happened to a line, in words: reversed (with the reason), moved to another folio, or moved here from one. */
export function lineNote(i: FolioItem): string {
  if (i.movedTo) return `Moved to ${i.movedTo}`;
  if (i.reversedByItemId) return `Reversed: ${i.reversalReason ?? ''}`.trim();
  if (i.itemType === 'REVERSAL' && i.reason) return i.reason;
  if (i.movedFrom) return `Moved from ${i.movedFrom}`;
  return '';
}

/** Lines that can be reversed or moved: charges that still count. */
export function liveCharges(items: FolioItem[]): FolioItem[] { return items.filter(i => i.live); }
