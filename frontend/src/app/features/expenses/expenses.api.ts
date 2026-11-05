import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  Budget, BudgetReport, BudgetStanding, BudgetSummary, CostCentre, Expense, ExpenseCategory, ExpenseList, ExpenseReceipt, ExpenseSetup, ExpenseStatus,
  ExpensePaymentMethod,
} from '../../core/models';

export interface SaveExpense {
  expenseDate: string; categoryId: string; costCentreId: string; description: string; payee: string; paymentMethod: ExpensePaymentMethod; amount: number;
  paymentReference: string | null; vendorGstin: string | null; billNumber: string | null; gstAmount: number; note: string | null; version: number | null;
}
export interface ExpenseFilter { from?: string; to?: string; status?: string; costCentreId?: string; categoryId?: string; mine?: boolean; }

export const EXPENSE_STATUS: Record<ExpenseStatus, string> = {
  SUBMITTED: 'Waiting for approval', APPROVED: 'Approved', REJECTED: 'Rejected', CANCELLED: 'Cancelled',
};
export const PAYMENT_METHOD: Record<ExpensePaymentMethod, string> = { CASH: 'Cash', UPI: 'UPI', CARD: 'Card', BANK_TRANSFER: 'Bank transfer', CHEQUE: 'Cheque' };
export const STANDING: Record<BudgetStanding, string> = {
  ON_TRACK: 'On track', OVER: 'Over budget', HEADING_OVER: 'Heading over', NO_BUDGET: 'No budget',
};
export const MAX_RECEIPTS = 4;

/** "₹1,23,456" (Indian grouping), paise only when there are some. */
export function inr(n: number): string { return '₹' + n.toLocaleString('en-IN', { maximumFractionDigits: 2 }); }

/** The financial year (April to March) a day falls in: 2026-11-05 → 2026. */
export function yearOf(iso: string): number {
  const [y, m] = iso.split('-').map(Number);
  return m >= 4 ? y : y - 1;
}

/** 2026 → "2026-27". */
export function yearLabel(y: number): string { return `${y}-${String((y + 1) % 100).padStart(2, '0')}`; }

/** "2026-04-01" → "Apr", or "Apr 2026" with the year. */
export function monthShort(iso: string, withYear = false): string {
  const [y, m] = iso.split('-').map(Number);
  const name = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'][m - 1];
  return withYear ? `${name} ${y}` : name;
}

/** A yearly amount over twelve months in whole rupees; March takes what is left (the API does the same). */
export function spread(annual: number): number[] {
  const month = Math.floor(annual / 12);
  const months = Array(12).fill(month);
  months[11] = Math.round((annual - month * 11) * 100) / 100;
  return months;
}

/** The GST split the API will make: halves within the state, IGST from another (by the GSTIN's first two digits). */
export function gstSplit(gst: number, gstin: string | null, stateCode: string | null): { cgst: number; sgst: number; igst: number } {
  const intra = !gstin || !stateCode || gstin.trim().slice(0, 2) === stateCode;
  if (!intra) return { cgst: 0, sgst: 0, igst: gst };
  const cgst = Math.floor(gst * 50) / 100;
  return { cgst, sgst: Math.round((gst - cgst) * 100) / 100, igst: 0 };
}

/** Variance in words: "₹400 over", "₹24,300 under", "on budget". */
export function varianceText(v: number): string { return v === 0 ? 'on budget' : v < 0 ? `${inr(-v)} over` : `${inr(v)} under`; }

/** Typed client for the Sprint 22 expense and budget endpoints. Recording needs an Idempotency-Key. */
@Injectable({ providedIn: 'root' })
export class ExpensesApi {
  private http = inject(HttpClient);
  private x = (pid: string) => `/api/v1/properties/${pid}/expenses`;
  private b = (pid: string) => `/api/v1/properties/${pid}/budgets`;

  setup(pid: string) { return this.http.get<ExpenseSetup>(`${this.x(pid)}/setup`); }
  saveCategory(pid: string, id: string | null, body: Omit<ExpenseCategory, 'id' | 'version'> & { version: number | null }): Observable<ExpenseCategory> {
    return id ? this.http.put<ExpenseCategory>(`${this.x(pid)}/categories/${id}`, body) : this.http.post<ExpenseCategory>(`${this.x(pid)}/categories`, body);
  }
  saveCostCentre(pid: string, id: string | null, body: Omit<CostCentre, 'id' | 'version'> & { version: number | null }): Observable<CostCentre> {
    return id ? this.http.put<CostCentre>(`${this.x(pid)}/cost-centres/${id}`, body) : this.http.post<CostCentre>(`${this.x(pid)}/cost-centres`, body);
  }
  list(pid: string, f: ExpenseFilter) {
    const params: Record<string, string> = {};
    for (const [k, v] of Object.entries(f)) if (v !== undefined && v !== '' && v !== false) params[k] = String(v);
    return this.http.get<ExpenseList>(this.x(pid), { params });
  }
  get(pid: string, id: string) { return this.http.get<Expense>(`${this.x(pid)}/${id}`); }
  /** One recording attempt: the key is kept by the form, so a retried tap replays instead of recording twice. */
  create(pid: string, body: SaveExpense, key: string) { return this.http.post<Expense>(this.x(pid), body, { headers: { 'Idempotency-Key': key } }); }
  update(pid: string, id: string, body: SaveExpense) { return this.http.put<Expense>(`${this.x(pid)}/${id}`, body); }
  approve(pid: string, e: Expense) { return this.http.post<Expense>(`${this.x(pid)}/${e.id}/approve`, { version: e.version }); }
  reject(pid: string, e: Expense, reason: string) { return this.http.post<Expense>(`${this.x(pid)}/${e.id}/reject`, { reason, version: e.version }); }
  cancel(pid: string, e: Expense, reason: string) { return this.http.post<Expense>(`${this.x(pid)}/${e.id}/cancel`, { reason, version: e.version }); }
  addReceipt(pid: string, id: string, file: Blob, fileName: string) {
    const fd = new FormData();
    fd.set('file', file, fileName);
    return this.http.post<ExpenseReceipt>(`${this.x(pid)}/${id}/receipts`, fd);
  }
  receipt(pid: string, id: string, receiptId: string) { return this.http.get(`${this.x(pid)}/${id}/receipts/${receiptId}`, { responseType: 'blob' }); }

  budgets(pid: string) { return this.http.get<BudgetSummary[]>(this.b(pid)); }
  budget(pid: string, id: string) { return this.http.get<Budget>(`${this.b(pid)}/${id}`); }
  createBudget(pid: string, body: { year: number; name: string | null; note: string | null; copyFromId: string | null; increasePercent: number }) {
    return this.http.post<Budget>(this.b(pid), body);
  }
  saveRow(pid: string, b: Budget, costCentreId: string, months: number[]) {
    return this.http.put<Budget>(`${this.b(pid)}/${b.id}/rows`, { costCentreId, months, version: b.version });
  }
  report(pid: string, year: number | null, through: string | null) {
    const params: Record<string, string> = {};
    if (year !== null) params['year'] = String(year);
    if (through) params['through'] = through;
    return this.http.get<BudgetReport>(`${this.b(pid)}/report`, { params });
  }
}
