import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { CashDrawer, CashDrawerSession, DrawerStatus, Payment, PaymentList, PaymentMethod, PaymentStatus } from '../../core/models';

export interface TakePayment {
  method: PaymentMethod; amount: number; folioId: string | null; reservationId: string | null; paymentReference: string | null; note: string | null;
}
export interface RefundBody { amount: number; reason: string; method: PaymentMethod | null; folioId: string | null; reference: string | null; }

/** Typed client for the Sprint 10 payment and cash drawer endpoints. Taking a payment and refunding need an
 *  Idempotency-Key, so a double click or a retry can't take or give back money twice. */
@Injectable({ providedIn: 'root' })
export class PaymentsApi {
  private http = inject(HttpClient);
  private base = (pid: string) => `/api/v1/properties/${pid}`;
  private key = (k: string) => ({ headers: new HttpHeaders({ 'Idempotency-Key': k }) });

  list(pid: string, q: { q?: string; method?: string; status?: string; from?: string; to?: string; reservationId?: string; page?: number; pageSize?: number }) {
    let params = new HttpParams();
    for (const [k, v] of Object.entries(q)) if (v !== undefined && v !== '') params = params.set(k, String(v));
    return this.http.get<PaymentList>(`${this.base(pid)}/payments`, { params });
  }
  get(pid: string, id: string) { return this.http.get<Payment>(`${this.base(pid)}/payments/${id}`); }
  take(pid: string, body: TakePayment, idempotencyKey: string) { return this.http.post<Payment>(`${this.base(pid)}/payments`, body, this.key(idempotencyKey)); }
  allocate(pid: string, id: string, allocations: { folioId: string; amount: number }[]) {
    return this.http.post<Payment>(`${this.base(pid)}/payments/${id}/allocate`, { allocations });
  }
  refund(pid: string, id: string, body: RefundBody, idempotencyKey: string) {
    return this.http.post<Payment>(`${this.base(pid)}/payments/${id}/refund`, body, this.key(idempotencyKey));
  }
  void(pid: string, id: string, reason: string) { return this.http.post<Payment>(`${this.base(pid)}/payments/${id}/void`, { reason }); }

  drawer(pid: string) { return this.http.get<CashDrawer>(`${this.base(pid)}/cash-drawer`); }
  session(pid: string, id: string) { return this.http.get<CashDrawerSession>(`${this.base(pid)}/cash-drawer/sessions/${id}`); }
  openDrawer(pid: string, openingFloat: number, note: string | null) {
    return this.http.post<CashDrawerSession>(`${this.base(pid)}/cash-drawer/open`, { openingFloat, note });
  }
  closeDrawer(pid: string, countedAmount: number, note: string | null) {
    return this.http.post<CashDrawerSession>(`${this.base(pid)}/cash-drawer/close`, { countedAmount, note });
  }
  approveVariance(pid: string, id: string, note: string) {
    return this.http.post<CashDrawerSession>(`${this.base(pid)}/cash-drawer/sessions/${id}/approve-variance`, { note });
  }
}

export const METHODS: PaymentMethod[] = ['CASH', 'UPI', 'CARD', 'BANK_TRANSFER'];
const METHOD_LABEL: Record<PaymentMethod, string> = { CASH: 'Cash', UPI: 'UPI', CARD: 'Card', BANK_TRANSFER: 'Bank transfer' };
export const methodLabel = (m: PaymentMethod | string) => METHOD_LABEL[m as PaymentMethod] ?? m;

const STATUS_LABEL: Record<PaymentStatus, string> = {
  PENDING: 'Pending', AUTHORIZED: 'Authorised', CAPTURED: 'Received', FAILED: 'Failed', REFUNDED: 'Refunded',
  PARTIALLY_REFUNDED: 'Part refunded', CANCELLED: 'Voided',
};
export const paymentStatusLabel = (s: PaymentStatus | string) => STATUS_LABEL[s as PaymentStatus] ?? s;
export function paymentStatusClass(s: PaymentStatus | string): string {
  return s === 'CAPTURED' ? 'ok' : s === 'CANCELLED' || s === 'FAILED' ? 'off' : s === 'PENDING' || s === 'AUTHORIZED' ? 'wait' : 'back';
}

/** What the reference field is called for a method, and what goes in it. */
export function referenceHint(m: PaymentMethod | ''): { label: string; hint: string } {
  switch (m) {
    case 'UPI': return { label: 'UPI transaction id', hint: 'The 12-digit UTR from the guest’s app, if they show it' };
    case 'CARD': return { label: 'Approval code', hint: 'From the terminal slip. Never type the card number: card details are never stored' };
    case 'BANK_TRANSFER': return { label: 'Bank reference (UTR)', hint: 'From the bank statement or the guest’s transfer slip' };
    default: return { label: 'Reference', hint: 'Optional' };
  }
}

/** The variance a count would give (counted − expected; negative = short), and what to call it. */
export function varianceOf(expected: number, counted: number | null): { amount: number; words: string } | null {
  if (counted === null || Number.isNaN(counted)) return null;
  const amount = Math.round((counted - expected) * 100) / 100;
  return { amount, words: amount === 0 ? 'Balanced' : amount < 0 ? 'Short' : 'Over' };
}

const DRAWER_LABEL: Record<DrawerStatus, string> = { OPEN: 'Open', VARIANCE_PENDING: 'Waiting for approval', CLOSED: 'Closed' };
export const drawerStatusLabel = (s: DrawerStatus) => DRAWER_LABEL[s];

/** True when a text holds 13–19 digits in a row (spaces and dashes inside ignored) that pass the Luhn check: what a card
 *  number looks like. The form refuses it before it is sent; the API refuses it too. */
export function looksLikeCardNumber(text: string | null | undefined): boolean {
  if (!text) return false;
  let run = '';
  for (const c of text + '!') {
    if (c >= '0' && c <= '9') { run += c; continue; }
    if ((c === ' ' || c === '-') && run.length > 0) continue;
    if (run.length >= 13 && run.length <= 19 && luhn(run)) return true;
    run = '';
  }
  return false;
}

export function luhn(digits: string): boolean {
  let sum = 0;
  for (let i = 0; i < digits.length; i++) {
    let d = Number(digits[digits.length - 1 - i]);
    if (i % 2 === 1) { d *= 2; if (d > 9) d -= 9; }
    sum += d;
  }
  return sum % 10 === 0;
}
