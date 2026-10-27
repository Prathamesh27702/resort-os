import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import {
  AdjustmentKind, CostedDish, FoodCostReport, Inventory, InventoryCategory, InventoryItem, MovementType, RecipeList, StockAdjustment,
  StockCountResult, StockLocation, StockMovementList, StockUnit,
} from '../../core/models';

export interface SaveStockItem {
  categoryId: string; code: string; name: string; unitId: string; unitCost: number; reorderLevel: number | null; isActive: boolean;
  openingQuantity: number | null; openingLocationId: string | null; version: number | null;
}
export interface Adjust {
  kind: AdjustmentKind; itemId: string; locationId: string; toLocationId: string | null; quantity: number; unitCost: number | null; reason: string;
}

/** Typed client for the Sprint 15 inventory endpoints. */
@Injectable({ providedIn: 'root' })
export class InventoryApi {
  private http = inject(HttpClient);
  private base = (pid: string) => `/api/v1/properties/${pid}/inventory`;

  get(pid: string) { return this.http.get<Inventory>(this.base(pid)); }
  movements(pid: string, q: { itemId?: string | null; type?: string | null; take?: number }) {
    const p = new URLSearchParams();
    if (q.itemId) p.set('itemId', q.itemId);
    if (q.type) p.set('type', q.type);
    if (q.take) p.set('take', String(q.take));
    return this.http.get<StockMovementList>(`${this.base(pid)}/movements?${p}`);
  }
  saveCategory(pid: string, id: string | null, body: { name: string; sortOrder: number; isActive: boolean }) {
    return id ? this.http.put<InventoryCategory>(`${this.base(pid)}/categories/${id}`, body) : this.http.post<InventoryCategory>(`${this.base(pid)}/categories`, body);
  }
  saveLocation(pid: string, id: string | null, body: { code: string; name: string; isKitchen: boolean; sortOrder: number; isActive: boolean }) {
    return id ? this.http.put<StockLocation>(`${this.base(pid)}/locations/${id}`, body) : this.http.post<StockLocation>(`${this.base(pid)}/locations`, body);
  }
  saveItem(pid: string, id: string | null, body: SaveStockItem) {
    return id ? this.http.put<InventoryItem>(`${this.base(pid)}/items/${id}`, body) : this.http.post<InventoryItem>(`${this.base(pid)}/items`, body);
  }
  adjust(pid: string, body: Adjust) { return this.http.post<StockAdjustment>(`${this.base(pid)}/adjustments`, body); }
  count(pid: string, body: { locationId: string; lines: { itemId: string; countedQuantity: number; reason: string | null }[] }) {
    return this.http.post<StockCountResult>(`${this.base(pid)}/counts`, body);
  }
  recipes(pid: string) { return this.http.get<RecipeList>(`${this.base(pid)}/recipes`); }
  saveRecipe(pid: string, body: { menuItemId: string; variantId: string | null; note: string | null; ingredients: { itemId: string; quantity: number; unitId: string }[]; version: number | null }) {
    return this.http.put<CostedDish[]>(`${this.base(pid)}/recipes`, body);
  }
  foodCost(pid: string, from: string | null, to: string | null) {
    const p = new URLSearchParams();
    if (from) p.set('from', from);
    if (to) p.set('to', to);
    return this.http.get<FoodCostReport>(`${this.base(pid)}/food-cost?${p}`);
  }
}

export const KIND_LABEL: Record<AdjustmentKind, string> = {
  RECEIVED: 'Stock received', WASTAGE: 'Wastage', EXPIRED: 'Expired', DAMAGED: 'Damaged', CORRECTION: 'Correction', TRANSFER: 'Transfer to another store',
};

export const MOVEMENT_LABEL: Record<MovementType, string> = {
  OPENING: 'Opening', RECEIVED: 'Received', CONSUMPTION: 'Used (served)', WASTAGE: 'Written off', ADJUSTMENT: 'Correction', COUNT: 'Count',
  TRANSFER_OUT: 'Moved out', TRANSFER_IN: 'Moved in',
};

/** "1.25 kg", "3 pcs", "-0.6 kg": at most three decimals, no trailing zeros. */
export function qty(v: number, unit: string): string {
  return `${Number(v.toFixed(3)).toLocaleString('en-IN', { maximumFractionDigits: 3 })} ${unit}`;
}

/** "37.5 %" or "—". */
export function pct(v: number | null | undefined): string {
  return v === null || v === undefined ? '—' : `${Number(v.toFixed(2)).toLocaleString('en-IN', { maximumFractionDigits: 2 })} %`;
}

/** The units a quantity of an item can be entered in: those of the item's kind (g or kg for an item kept in kg). */
export function unitsLike(units: StockUnit[], unitId: string): StockUnit[] {
  const own = units.find(u => u.id === unitId);
  return own ? units.filter(u => u.dimension === own.dimension) : [];
}

/** The cost of a recipe line as the screen shows it before saving: quantity in its unit, converted to the item's unit, at its cost. */
export function lineCost(quantity: number, unit: StockUnit | undefined, item: InventoryItem | undefined, units: StockUnit[]): number | null {
  if (!unit || !item || !(quantity > 0)) return null;
  const own = units.find(u => u.id === item.unitId);
  if (!own || own.dimension !== unit.dimension) return null;
  const inOwn = Math.round((quantity * unit.factor / own.factor) * 1000) / 1000;
  return Math.round(inOwn * item.unitCost * 100) / 100;
}

/** Margin colour band: under 60 % is low for a restaurant dish, 70 % and up is good. */
export function marginBand(margin: number | null): 'low' | 'mid' | 'good' | 'none' {
  if (margin === null) return 'none';
  return margin < 60 ? 'low' : margin < 70 ? 'mid' : 'good';
}

/** Kinds that take stock out of a store (the quantity entered is positive). */
export const OUT_KINDS: AdjustmentKind[] = ['WASTAGE', 'EXPIRED', 'DAMAGED', 'TRANSFER'];
