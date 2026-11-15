import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';

/** A UPI QR for the property's UPI ID: `qrPng` is a PNG data URI of the `upi://pay` link, for an <img>. */
export interface UpiQr { upiId: string; payeeName: string; amount: number | null; note: string | null; link: string; qrPng: string; }

export interface BillTax { code: string; rate: number; taxableValue: number; amount: number; }
export interface BillLine { name: string; detail: string | null; quantity: number; unitPrice: number; amount: number; foodType: string; }
export interface BillProperty { name: string; legalName: string | null; addressLines: string[]; phone: string | null; gstin: string | null; fssaiLicenceNo: string | null; }
/** A restaurant bill as it prints (Sprint 30). `upi` is there while the bill is open and the property has a UPI ID. */
export interface OrderBill {
  property: BillProperty; orderId: string; number: string; where: string; orderType: string; businessDate: string; printedAt: string;
  covers: number | null; waiterName: string | null; guestName: string | null; lines: BillLine[]; subtotal: number; discountAmount: number;
  discountLabel: string | null; net: number; taxes: BillTax[]; taxAmount: number; total: number; taxNote: string | null; sacCode: string | null;
  status: 'OPEN' | 'SETTLED'; paidBy: string | null; upi: UpiQr | null;
}

export type FormCStatus = 'PENDING' | 'SUBMITTED' | 'NOT_REQUIRED';
export interface FormCListItem {
  id: string; status: FormCStatus; guestId: string; guestName: string; nationality: string | null; reservationId: string; reference: string;
  room: string | null; checkOut: string; arrivedAt: string; dueAt: string; hoursLeft: number; overdue: boolean; missing: number;
  frroReference: string | null; submittedAt: string | null; submittedByName: string | null; notRequiredReason: string | null;
}
export interface FormCList { items: FormCListItem[]; pending: number; overdue: number; hoursToFile: number; portalUrl: string; }
export type FormCKind = 'text' | 'date' | 'time' | 'number' | 'sex' | 'yesno';
export interface FormCField {
  key: string; section: string; label: string; required: boolean; hint: string | null; kind: FormCKind; maxLength: number;
  value: string | null; fromProfile: boolean;
}
export interface FormCSheet {
  id: string; status: FormCStatus; guestId: string; guestName: string; nationality: string | null; reservationId: string; reference: string;
  room: string | null; checkIn: string; checkOut: string; arrivedAt: string; dueAt: string; hoursLeft: number; overdue: boolean;
  fields: FormCField[]; missing: string[]; frroReference: string | null; submittedAt: string | null; submittedByName: string | null;
  notRequiredReason: string | null; portalUrl: string; version: number;
}

/** Typed client for the Sprint 30 endpoints: UPI QR, the printable restaurant bill and Form C. */
@Injectable({ providedIn: 'root' })
export class IndiaApi {
  private http = inject(HttpClient);
  private base = (pid: string) => `/api/v1/properties/${pid}`;

  upiQr(pid: string, amount: number | null, note: string | null) {
    const q = new URLSearchParams();
    if (amount) q.set('amount', String(amount));
    if (note) q.set('note', note);
    return this.http.get<UpiQr>(`${this.base(pid)}/upi-qr?${q}`);
  }
  bill(pid: string, orderId: string) { return this.http.get<OrderBill>(`${this.base(pid)}/orders/${orderId}/bill`); }

  formCList(pid: string, status: FormCStatus | 'ALL') { return this.http.get<FormCList>(`${this.base(pid)}/form-c?status=${status}`); }
  formC(pid: string, id: string) { return this.http.get<FormCSheet>(`${this.base(pid)}/form-c/${id}`); }
  saveFormC(pid: string, s: FormCSheet, values: Record<string, string | null>) {
    return this.http.put<FormCSheet>(`${this.base(pid)}/form-c/${s.id}`, { values, version: s.version });
  }
  submitFormC(pid: string, s: FormCSheet, frroReference: string | null) {
    return this.http.post<FormCSheet>(`${this.base(pid)}/form-c/${s.id}/submit`, { frroReference, version: s.version });
  }
  formCNotRequired(pid: string, s: FormCSheet, reason: string) {
    return this.http.post<FormCSheet>(`${this.base(pid)}/form-c/${s.id}/not-required`, { reason, version: s.version });
  }
  reopenFormC(pid: string, s: FormCSheet) { return this.http.post<FormCSheet>(`${this.base(pid)}/form-c/${s.id}/reopen`, { version: s.version }); }
  register(pid: string, from: string, to: string) {
    return this.http.get(`${this.base(pid)}/form-c/register.csv?from=${from}&to=${to}`, { responseType: 'blob', observe: 'response' });
  }
}

/** "5 h left", "40 min left", "3 h overdue": how long a Form C still has, in words. */
export function deadlineWords(hoursLeft: number): string {
  const abs = Math.abs(hoursLeft);
  const span = abs >= 1 ? `${Math.floor(abs)} h` : `${Math.max(1, Math.round(abs * 60))} min`;
  return hoursLeft > 0 ? `${span} left` : `${span} overdue`;
}

/** How urgent a Form C is, for its colour: late (past 24 hours), soon (6 hours or less) or fine. */
export function urgency(i: { status: FormCStatus; hoursLeft: number }): 'late' | 'soon' | 'fine' | 'done' {
  if (i.status !== 'PENDING') return 'done';
  return i.hoursLeft <= 0 ? 'late' : i.hoursLeft <= 6 ? 'soon' : 'fine';
}

export const FORM_C_STATUS: Record<FormCStatus, string> = { PENDING: 'To file', SUBMITTED: 'Filed', NOT_REQUIRED: 'Not needed' };

/** Paper widths of the thermal printers Indian restaurants use. */
export type PaperWidth = 80 | 58;
const PAPER_KEY = 'ro-receipt-paper';
export function readPaper(): PaperWidth {
  try { return localStorage.getItem(PAPER_KEY) === '58' ? 58 : 80; } catch { return 80; }
}
export function savePaper(w: PaperWidth) {
  try { localStorage.setItem(PAPER_KEY, String(w)); } catch { /* storage blocked: lasts until reload */ }
}
