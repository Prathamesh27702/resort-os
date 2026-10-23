import { HttpClient, HttpErrorResponse, HttpHeaders, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, from, map, switchMap, throwError } from 'rxjs';
import {
  CreditNoteType, DocumentLine, DocumentType, Invoice, InvoiceListItem, InvoicePreview, InvoiceStatus, Paged, Receipt,
} from '../../core/models';

export interface IssueInvoice {
  customerName: string | null; customerGstin: string | null; customerAddress: string | null; customerStateCode: string | null; notes: string | null;
}
export interface CreditNoteBody { type: CreditNoteType; reason: string; lines: { lineId: string; amount: number | null }[] | null; }
export interface DebitNoteBody { reason: string; folioItemIds: string[] | null; }
export type PdfKind = 'invoices' | 'credit-notes' | 'debit-notes' | 'receipts';

/** Typed client for the Sprint 12 invoice endpoints. Issuing an invoice or a note needs an Idempotency-Key, so a double
 *  click or a retry after a lost connection issues one document (and takes one number). */
@Injectable({ providedIn: 'root' })
export class InvoicesApi {
  private http = inject(HttpClient);
  private base = (pid: string) => `/api/v1/properties/${pid}`;
  private key = (k: string) => ({ headers: new HttpHeaders({ 'Idempotency-Key': k }) });

  list(pid: string, q: { q?: string; from?: string; to?: string; reservationId?: string; status?: string; page?: number; pageSize?: number }) {
    let params = new HttpParams();
    for (const [k, v] of Object.entries(q)) if (v !== undefined && v !== '') params = params.set(k, String(v));
    return this.http.get<Paged<InvoiceListItem>>(`${this.base(pid)}/invoices`, { params });
  }
  get(pid: string, id: string) { return this.http.get<Invoice>(`${this.base(pid)}/invoices/${id}`); }
  preview(pid: string, folioId: string) { return this.http.get<InvoicePreview>(`${this.base(pid)}/folios/${folioId}/invoice-preview`); }
  issue(pid: string, folioId: string, body: IssueInvoice, idempotencyKey: string) {
    return this.http.post<Invoice>(`${this.base(pid)}/folios/${folioId}/invoice`, body, this.key(idempotencyKey));
  }
  credit(pid: string, id: string, body: CreditNoteBody, idempotencyKey: string) {
    return this.http.post<Invoice>(`${this.base(pid)}/invoices/${id}/credit-notes`, body, this.key(idempotencyKey));
  }
  debit(pid: string, id: string, body: DebitNoteBody, idempotencyKey: string) {
    return this.http.post<Invoice>(`${this.base(pid)}/invoices/${id}/debit-notes`, body, this.key(idempotencyKey));
  }
  /** A payment's receipt: issued the first time it is asked for, the same one after that. */
  receipt(pid: string, paymentId: string) { return this.http.post<Receipt>(`${this.base(pid)}/payments/${paymentId}/receipt`, {}); }

  /** Downloads a document's PDF, saved under its number (BEACH-2627-00001.pdf). */
  pdf(pid: string, kind: PdfKind, id: string, fallbackName: string): Observable<string> {
    return this.http.get(`${this.base(pid)}/${kind}/${id}/pdf`, { responseType: 'blob', observe: 'response' }).pipe(
      map((r: HttpResponse<Blob>) => {
        const name = fileName(r.headers.get('Content-Disposition')) ?? fallbackName;
        save(r.body!, name);
        return name;
      }),
      catchError((e: unknown) => from(jsonError(e)).pipe(switchMap(err => throwError(() => err)))),
    );
  }
}

/** The file name in a Content-Disposition header (filename*=UTF-8''… or filename=…). */
export function fileName(header: string | null): string | null {
  if (!header) return null;
  const star = /filename\*=UTF-8''([^;]+)/i.exec(header);
  if (star) return decodeURIComponent(star[1]);
  const plain = /filename="?([^";]+)"?/i.exec(header);
  return plain ? plain[1] : null;
}

/** "BEACH/2627/00001" → "BEACH-2627-00001.pdf", as the server names it. */
export const pdfName = (number: string) => number.replace(/\//g, '-') + '.pdf';

function save(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** A failed blob request carries the API's JSON error as a Blob: read it back so toProblem shows the real reason. */
async function jsonError(e: unknown): Promise<unknown> {
  if (!(e instanceof HttpErrorResponse) || !(e.error instanceof Blob)) return e;
  try {
    return new HttpErrorResponse({ error: JSON.parse(await e.error.text()), status: e.status, statusText: e.statusText, url: e.url ?? undefined });
  } catch {
    return e;
  }
}

const STATUS_LABEL: Record<InvoiceStatus, string> = {
  ISSUED: 'Issued', PARTLY_CREDITED: 'Partly credited', CREDITED: 'Credited', CANCELLED: 'Cancelled',
};
export const invoiceStatusLabel = (s: InvoiceStatus) => STATUS_LABEL[s] ?? s;
export const invoiceStatusClass = (s: InvoiceStatus) =>
  ({ ISSUED: 'ok', PARTLY_CREDITED: 'wait', CREDITED: 'off', CANCELLED: 'back' } as Record<InvoiceStatus, string>)[s] ?? '';
export const STATUSES: InvoiceStatus[] = ['ISSUED', 'PARTLY_CREDITED', 'CREDITED', 'CANCELLED'];

/** "Tax invoice", "Bill of supply" … in words. */
export const documentLabel = (t: DocumentType) =>
  ({ INVOICE: 'Tax invoice', BILL_OF_SUPPLY: 'Bill of supply', CREDIT_NOTE: 'Credit note', DEBIT_NOTE: 'Debit note', RECEIPT: 'Receipt' } as Record<DocumentType, string>)[t] ?? t;

/** "CGST 2.5% · SGST 2.5%" for a line. */
export const lineTaxText = (l: Pick<DocumentLine, 'taxes'>) => l.taxes.map(t => `${t.code} ${Number(t.rate)}%`).join(' · ');

/** GSTIN format (15 characters: state code, PAN, entity, Z, check). The server also checks the check digit. */
export const GSTIN_PATTERN = /^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$/;

/** The GST a credit of `amount` (taxable value) takes off a line: each tax at its own rate, rounded to the paisa, as the
 *  server works it out (crediting everything left takes exactly what is left, which can differ by a paisa). */
export function creditTax(line: Pick<DocumentLine, 'taxes'>, amount: number): number {
  if (amount <= 0) return 0;
  return round(line.taxes.reduce((s, t) => s + round(amount * Number(t.rate) / 100), 0));
}
const round = (v: number) => Math.round((v + Number.EPSILON) * 100) / 100;
