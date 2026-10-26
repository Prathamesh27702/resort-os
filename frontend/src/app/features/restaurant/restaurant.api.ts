import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import {
  Floor, FoodType, InHouseRoom, Kitchen, Kot, KotStatus, Menu, MenuCategory, MenuItem, Order, OrderLineRequest, OrderList, OrderType,
  PaymentMethod, RestaurantTable,
} from '../../core/models';

export interface SaveMenuItem {
  categoryId: string; code: string; name: string; description: string | null; foodType: FoodType; price: number; isActive: boolean; sortOrder: number;
  variants: { id: string | null; name: string; price: number }[];
  modifierGroups: { id: string | null; name: string; minSelect: number; maxSelect: number; modifiers: { id: string | null; name: string; priceDelta: number }[] }[];
  version: number | null;
}

/** Typed client for the Sprint 14 restaurant endpoints. Every change to an order sends its version, so a change on an order
 *  another waiter just saved is refused (reload) instead of overwriting their lines. */
@Injectable({ providedIn: 'root' })
export class RestaurantApi {
  private http = inject(HttpClient);
  private base = (pid: string) => `/api/v1/properties/${pid}`;
  private key = (k: string | null) => (k ? { headers: new HttpHeaders({ 'Idempotency-Key': k }) } : {});

  menu(pid: string, all = false) { return this.http.get<Menu>(`${this.base(pid)}/menu${all ? '?all=true' : ''}`); }
  saveCategory(pid: string, id: string | null, body: { name: string; sortOrder: number; isActive: boolean }) {
    return id ? this.http.put<MenuCategory>(`${this.base(pid)}/menu/categories/${id}`, body) : this.http.post<MenuCategory>(`${this.base(pid)}/menu/categories`, body);
  }
  saveItem(pid: string, id: string | null, body: SaveMenuItem) {
    return id ? this.http.put<MenuItem>(`${this.base(pid)}/menu/items/${id}`, body) : this.http.post<MenuItem>(`${this.base(pid)}/menu/items`, body);
  }
  setAvailable(pid: string, id: string, isAvailable: boolean) {
    return this.http.post<MenuItem>(`${this.base(pid)}/menu/items/${id}/availability`, { isAvailable });
  }
  tables(pid: string) { return this.http.get<RestaurantTable[]>(`${this.base(pid)}/menu/tables`); }
  saveTable(pid: string, id: string | null, body: { code: string; seats: number; area: string | null; sortOrder: number; isActive: boolean }) {
    return id ? this.http.put<RestaurantTable>(`${this.base(pid)}/menu/tables/${id}`, body) : this.http.post<RestaurantTable>(`${this.base(pid)}/menu/tables`, body);
  }

  floor(pid: string) { return this.http.get<Floor>(`${this.base(pid)}/orders/floor`); }
  orders(pid: string, status: string, date: string | null) {
    const q = new URLSearchParams({ status });
    if (date) q.set('date', date);
    return this.http.get<OrderList>(`${this.base(pid)}/orders?${q}`);
  }
  rooms(pid: string) { return this.http.get<InHouseRoom[]>(`${this.base(pid)}/orders/rooms`); }
  order(pid: string, id: string) { return this.http.get<Order>(`${this.base(pid)}/orders/${id}`); }
  create(pid: string, body: { orderType: OrderType; tableId: string | null; unitId: string | null; covers: number | null; note: string | null; items: OrderLineRequest[] | null }, key: string) {
    return this.http.post<Order>(`${this.base(pid)}/orders`, body, this.key(key));
  }
  update(pid: string, o: Order, body: { tableId: string | null; covers: number | null; note: string | null }) {
    return this.http.put<Order>(`${this.base(pid)}/orders/${o.id}`, { ...body, version: o.version });
  }
  addItems(pid: string, o: Order, items: OrderLineRequest[]) {
    return this.http.post<Order>(`${this.base(pid)}/orders/${o.id}/items`, { items, version: o.version });
  }
  updateItem(pid: string, o: Order, itemId: string, quantity: number, note: string | null) {
    return this.http.put<Order>(`${this.base(pid)}/orders/${o.id}/items/${itemId}`, { quantity, note, version: o.version });
  }
  removeItem(pid: string, o: Order, itemId: string) {
    return this.http.delete<Order>(`${this.base(pid)}/orders/${o.id}/items/${itemId}?version=${o.version}`);
  }
  voidItem(pid: string, o: Order, itemId: string, reason: string) {
    return this.http.post<Order>(`${this.base(pid)}/orders/${o.id}/items/${itemId}/void`, { reason, version: o.version });
  }
  send(pid: string, o: Order, note: string | null) { return this.http.post<Order>(`${this.base(pid)}/orders/${o.id}/send`, { note, version: o.version }); }
  serve(pid: string, o: Order, kotId: string) { return this.http.post<Order>(`${this.base(pid)}/orders/${o.id}/kots/${kotId}/serve`, {}); }
  discount(pid: string, o: Order, kind: 'PERCENT' | 'AMOUNT' | null, value: number | null, reason: string | null) {
    return this.http.post<Order>(`${this.base(pid)}/orders/${o.id}/discount`, { kind, value, reason, version: o.version });
  }
  voidOrder(pid: string, o: Order, reason: string) { return this.http.post<Order>(`${this.base(pid)}/orders/${o.id}/void`, { reason, version: o.version }); }
  pay(pid: string, o: Order, method: PaymentMethod, reference: string | null, key: string) {
    return this.http.post<Order>(`${this.base(pid)}/orders/${o.id}/pay`, { method, reference, version: o.version }, this.key(key));
  }
  chargeToRoom(pid: string, o: Order, reservationId: string | null, key: string) {
    return this.http.post<Order>(`${this.base(pid)}/orders/${o.id}/charge-to-room`, { reservationId, version: o.version }, this.key(key));
  }

  kitchen(pid: string) { return this.http.get<Kitchen>(`${this.base(pid)}/kitchen/kots`); }
  kotStep(pid: string, k: Kot, step: 'start' | 'ready') { return this.http.post<Kot>(`${this.base(pid)}/kitchen/kots/${k.id}/${step}`, { version: k.version }); }
}

/** How often the kitchen display asks for new tickets (brief §76: polling, no websockets yet). */
export const KITCHEN_REFRESH_MS = 10_000;

export const FOOD_LABEL: Record<FoodType, string> = { VEG: 'Veg', NON_VEG: 'Non-veg', EGG: 'Egg' };
const KOT_LABEL: Record<KotStatus, string> = { NEW: 'New', PREPARING: 'Preparing', READY: 'Ready', SERVED: 'Served', CANCELLED: 'Cancelled' };
export const kotLabel = (s: KotStatus | string) => KOT_LABEL[s as KotStatus] ?? s;
export const ORDER_TYPE_LABEL: Record<OrderType, string> = { DINE_IN: 'Table', ROOM_SERVICE: 'Room service', TAKEAWAY: 'Takeaway' };

/** What one unit of a dish costs with the chosen portion and choices (the server prices it again from the menu). */
export function linePrice(item: MenuItem, variantId: string | null, modifierIds: string[]): number {
  const base = item.variants.find(v => v.id === variantId)?.price ?? item.price;
  const extras = item.modifierGroups.flatMap(g => g.modifiers).filter(m => modifierIds.includes(m.id)).reduce((s, m) => s + m.priceDelta, 0);
  return base + extras;
}

/** Why a dish can't be added with these picks yet (portion missing, too few or too many choices), or null. */
export function pickProblem(item: MenuItem, variantId: string | null, modifierIds: string[]): string | null {
  if (item.variants.length && !item.variants.some(v => v.id === variantId)) return `Choose the portion: ${item.variants.map(v => v.name).join(' or ')}.`;
  for (const g of item.modifierGroups) {
    const n = g.modifiers.filter(m => modifierIds.includes(m.id)).length;
    if (n < g.minSelect) return g.minSelect === 1 ? `Choose the ${g.name.toLowerCase()}.` : `Choose at least ${g.minSelect} of ${g.name.toLowerCase()}.`;
    if (n > g.maxSelect) return g.maxSelect === 1 ? `Choose one ${g.name.toLowerCase()}.` : `Choose at most ${g.maxSelect} of ${g.name.toLowerCase()}.`;
  }
  return null;
}

/** A dish that can be added with one tap: no portion to choose and no choice that must be made. */
export const oneTap = (item: MenuItem) => item.variants.length === 0 && item.modifierGroups.every(g => g.minSelect === 0);

/** Minutes since a ticket was sent, for the kitchen display ("3 min"). */
export function ageMinutes(sentAt: string, now: Date): number {
  return Math.max(0, Math.floor((now.getTime() - new Date(sentAt).getTime()) / 60_000));
}

/** A ticket waiting longer than this is shown as late on the kitchen display. */
export const LATE_MINUTES = 15;

/** The kitchen display's columns, oldest ticket first in each. */
export function kitchenColumns(tickets: Kot[]): { status: KotStatus; label: string; tickets: Kot[] }[] {
  return (['NEW', 'PREPARING', 'READY'] as KotStatus[]).map(status => ({
    status, label: kotLabel(status), tickets: tickets.filter(t => t.status === status).sort((a, b) => a.sentAt.localeCompare(b.sentAt)),
  }));
}

/** Menu items matching a search (name or code), across sections; empty search = everything. */
export function searchMenu(menu: Menu | null, q: string): MenuItem[] {
  const all = (menu?.categories ?? []).flatMap(c => c.items).filter(i => i.isActive);
  const t = q.trim().toLowerCase();
  return t ? all.filter(i => i.name.toLowerCase().includes(t) || i.code.toLowerCase().includes(t)) : all;
}
