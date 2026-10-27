import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import {
  PurchaseOrder, PurchaseOrderList, PurchaseOrderStatus, PurchaseRequest, PurchaseRequestStatus, Supplier, SupplierInvoice, SupplierInvoiceList,
  SupplierInvoiceStatus, SupplierPaymentMethod,
} from '../../core/models';

export interface SaveSupplier {
  code: string; name: string; gstin: string | null; stateCode: string | null; contactName: string | null; phone: string | null; email: string | null;
  address: string | null; paymentTermsDays: number; note: string | null; isActive: boolean; version: number | null;
}
export interface SaveOrder {
  supplierId: string; locationId: string; requestId: string | null; expectedDate: string | null; note: string | null;
  lines: { itemId: string; quantity: number; unitPrice: number; gstRate: number }[]; version: number | null;
}
export interface Receive { receivedOn: string | null; locationId: string | null; challanNumber: string | null; note: string | null; lines: { orderItemId: string; quantity: number }[]; }
export interface RecordBill {
  supplierNumber: string; invoiceDate: string; dueDate: string | null; note: string | null;
  lines: { orderItemId: string; quantity: number; unitPrice: number; gstRate: number }[];
}
export interface PaySupplier { amount: number; paidOn: string | null; method: SupplierPaymentMethod; reference: string | null; note: string | null; version: number | null; }

/** Typed client for the Sprint 16 purchasing endpoints. */
@Injectable({ providedIn: 'root' })
export class PurchasingApi {
  private http = inject(HttpClient);
  private base = (pid: string) => `/api/v1/properties/${pid}/purchasing`;

  suppliers(pid: string) { return this.http.get<Supplier[]>(`${this.base(pid)}/suppliers`); }
  saveSupplier(pid: string, id: string | null, body: SaveSupplier) {
    return id ? this.http.put<Supplier>(`${this.base(pid)}/suppliers/${id}`, body) : this.http.post<Supplier>(`${this.base(pid)}/suppliers`, body);
  }

  requests(pid: string, status?: PurchaseRequestStatus | null) {
    return this.http.get<PurchaseRequest[]>(`${this.base(pid)}/requests${status ? `?status=${status}` : ''}`);
  }
  createRequest(pid: string, body: { locationId: string; neededBy: string | null; note: string | null; items: { itemId: string; quantity: number; note: string | null }[] }) {
    return this.http.post<PurchaseRequest>(`${this.base(pid)}/requests`, body);
  }
  cancelRequest(pid: string, id: string, reason: string, version: number | null) {
    return this.http.post<PurchaseRequest>(`${this.base(pid)}/requests/${id}/cancel`, { reason, version });
  }

  orders(pid: string, status?: PurchaseOrderStatus | null) {
    return this.http.get<PurchaseOrderList>(`${this.base(pid)}/orders${status ? `?status=${status}` : ''}`);
  }
  order(pid: string, id: string) { return this.http.get<PurchaseOrder>(`${this.base(pid)}/orders/${id}`); }
  saveOrder(pid: string, id: string | null, body: SaveOrder) {
    return id ? this.http.put<PurchaseOrder>(`${this.base(pid)}/orders/${id}`, body) : this.http.post<PurchaseOrder>(`${this.base(pid)}/orders`, body);
  }
  step(pid: string, id: string, step: 'submit' | 'approve', note: string | null, version: number | null) {
    return this.http.post<PurchaseOrder>(`${this.base(pid)}/orders/${id}/${step}`, { note, version });
  }
  withReason(pid: string, id: string, step: 'reject' | 'cancel' | 'close', reason: string, version: number | null) {
    return this.http.post<PurchaseOrder>(`${this.base(pid)}/orders/${id}/${step}`, { reason, version });
  }
  receive(pid: string, id: string, body: Receive) { return this.http.post<PurchaseOrder>(`${this.base(pid)}/orders/${id}/receipts`, body); }
  recordBill(pid: string, id: string, body: RecordBill) { return this.http.post<SupplierInvoice>(`${this.base(pid)}/orders/${id}/invoices`, body); }

  invoices(pid: string, status?: SupplierInvoiceStatus | 'OPEN' | null) {
    return this.http.get<SupplierInvoiceList>(`${this.base(pid)}/invoices${status ? `?status=${status}` : ''}`);
  }
  pay(pid: string, id: string, body: PaySupplier, key: string) {
    return this.http.post<SupplierInvoice>(`${this.base(pid)}/invoices/${id}/payments`, body, { headers: new HttpHeaders({ 'Idempotency-Key': key }) });
  }
}

export const ORDER_STATUS_LABEL: Record<PurchaseOrderStatus, string> = {
  DRAFT: 'Draft', PENDING_APPROVAL: 'Waiting for approval', APPROVED: 'Approved', PARTLY_RECEIVED: 'Partly received', RECEIVED: 'Received',
  CLOSED: 'Closed', CANCELLED: 'Cancelled',
};
export const REQUEST_STATUS_LABEL: Record<PurchaseRequestStatus, string> = { OPEN: 'Open', ORDERED: 'Ordered', CANCELLED: 'Cancelled' };
export const BILL_STATUS_LABEL: Record<SupplierInvoiceStatus, string> = { UNPAID: 'Unpaid', PART_PAID: 'Part paid', PAID: 'Paid' };
export const PAY_METHOD_LABEL: Record<SupplierPaymentMethod, string> = { CASH: 'Cash (from the drawer)', BANK_TRANSFER: 'Bank transfer', UPI: 'UPI', CHEQUE: 'Cheque' };
/** The GST rates a purchase line can carry (percent). */
export const GST_RATES = [0, 0.25, 3, 5, 12, 18, 28, 40];

/** A line's value before GST and its GST, as the server works them out (to the paisa; CGST + SGST halves rounded each). */
export function lineTotals(quantity: number, unitPrice: number, gstRate: number, intraState: boolean): { taxable: number; tax: number } {
  const round = (v: number) => Math.round((v + Number.EPSILON) * 100) / 100;
  const taxable = round(quantity * unitPrice);
  const tax = intraState ? 2 * round(taxable * gstRate / 200) : round(taxable * gstRate / 100);
  return { taxable, tax };
}

/** The steps a person can take on an order now, given its status and their permissions. */
export function orderActions(o: PurchaseOrder, can: (p: string) => boolean, me: string | null): string[] {
  const out: string[] = [];
  if (o.status === 'DRAFT' && can('purchase.create')) out.push('edit', 'submit');
  if (o.status === 'PENDING_APPROVAL' && can('purchase.approve')) { if (o.submittedBy !== me) out.push('approve'); out.push('reject'); }
  if ((o.status === 'APPROVED' || o.status === 'PARTLY_RECEIVED') && can('grn.create')) out.push('receive');
  if (o.lines.some(l => l.toBill > 0) && o.status !== 'CANCELLED' && can('supplier_invoice.manage')) out.push('bill');
  if (o.status === 'PARTLY_RECEIVED' && can('purchase.create')) out.push('close');
  if ((o.status === 'DRAFT' || o.status === 'PENDING_APPROVAL' || o.status === 'APPROVED') && can('purchase.create')) out.push('cancel');
  return out;
}

/** Puts the API's "Lines[k]" back on the form row it came from (lines left empty are not sent). */
export function remapLines(errors: Record<string, string>, rows: number[]): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(errors)) out[k.replace(/^lines\[(\d+)\]/, (_, i) => `lines[${rows[+i] ?? i}]`)] = v;
  return out;
}

/** Today's date (YYYY-MM-DD) at the property. */
export function todayAt(tz: string): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: tz, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
}
