import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { Invoice, InvoiceNote } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { shortDate } from '../rooms/rooms.api';
import { rupeesExact } from '../settings/rates/rates.api';
import { methodLabel } from '../payments/payments.api';
import { CreditNoteDialog, DebitNoteDialog } from './invoice-dialogs';
import { InvoicesApi, PdfKind, invoiceStatusClass, invoiceStatusLabel, lineTaxText, pdfName } from './invoices.api';

/**
 * One GST invoice (Sprint 12) as printed: supplier and customer with their GSTINs, place of supply, each line with its
 * SAC and taxes, the tax summary, and the credit notes, debit notes and receipts that go with it. Nothing here is ever
 * edited: accountants issue a credit note to take something off (or cancel it to issue it again), and a debit note
 * bills charges posted after it.
 */
@Component({
  selector: 'app-invoice',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page wide">
      @if (i(); as i) {
        <div class="page-head">
          <div>
            <a routerLink="/invoices" class="back">Invoices</a>
            <h1><span class="num">{{ i.number }}</span> · {{ i.customer.name }}
              <span [class]="'chip ' + statusClass(i.status)" data-testid="invoice-status">{{ statusLabel(i.status) }}</span></h1>
            <p>{{ i.title }} · {{ shortDate(i.invoiceDate) }} · <a [routerLink]="['/reservations', i.reservationId]" class="num">{{ i.reference }}</a> ·
              <a [routerLink]="['/folios', i.folioId]" class="num">{{ i.folioNumber }}</a>@if (i.issuedByName) { · issued by {{ i.issuedByName }} }</p>
          </div>
          <div class="head-actions">
            <button mat-flat-button (click)="pdf('invoices', i.id, i.number)" [disabled]="busy()"><mat-icon>download</mat-icon>PDF</button>
            @if (canCredit() && i.status !== 'CANCELLED' && i.outstanding > 0) {
              <button mat-stroked-button (click)="credit()" [disabled]="busy()"><mat-icon>remove_circle_outline</mat-icon>Credit note</button>
            }
            @if (canCredit() && i.status !== 'CANCELLED' && i.unbilledCharges > 0) {
              <button mat-stroked-button (click)="debit()" [disabled]="busy()"><mat-icon>add_circle_outline</mat-icon>Debit note</button>
            }
          </div>
        </div>

        @if (i.status === 'CANCELLED') {
          <p class="note" role="note">Cancelled by credit note {{ cancelNote()?.number }}: {{ cancelNote()?.reason }}. Its charges can be invoiced again
            from the <a [routerLink]="['/folios', i.folioId]">bill</a>.</p>
        } @else if (i.unbilledCharges > 0) {
          <p class="note" role="note">{{ i.unbilledCharges }} charge{{ i.unbilledCharges === 1 ? ' was' : 's were' }} posted to {{ i.folioNumber }} after this
            invoice ({{ money(i.unbilledAmount) }}).@if (canCredit()) { Bill {{ i.unbilledCharges === 1 ? 'it' : 'them' }} with a debit note. }</p>
        }
        @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

        <div class="parties">
          <div><small>Supplier</small><b>{{ i.supplier.name }}</b>@if (i.supplier.tradeName) { <span>{{ i.supplier.tradeName }}</span> }
            <span>{{ i.supplier.address }}</span><span class="num">GSTIN {{ i.supplier.gstin }}</span><span>{{ i.supplier.stateName }}</span></div>
          <div><small>Billed to</small><b data-testid="customer">{{ i.customer.name }}</b>
            @if (i.customer.address) { <span>{{ i.customer.address }}</span> }
            <span class="num" data-testid="customer-gstin">{{ i.customer.gstin ? 'GSTIN ' + i.customer.gstin : 'Unregistered (B2C)' }}</span>
            @if (i.customer.stateName) { <span>{{ i.customer.stateName }}</span> }</div>
          <div><small>Place of supply</small><b>{{ i.placeOfSupplyName }}</b><span>{{ i.isB2B ? 'B2B' : 'B2C' }} · reverse charge: no</span>
            <span>Financial year {{ i.financialYear }}</span></div>
        </div>

        <div class="panel">
          <table class="data" aria-label="Lines">
            <thead><tr><th>#</th><th>Line</th><th class="hide-sm">SAC</th><th class="right">Taxable</th><th class="right hide-sm">GST</th><th class="right">Total</th></tr></thead>
            <tbody>
              @for (l of i.items; track l.id) {
                <tr [attr.data-line]="l.description">
                  <td class="num">{{ l.lineNo }}</td>
                  <td>{{ l.description }}<br /><small class="mute">{{ shortDate(l.serviceDate) }}@if (l.debitNoteNumber) { · on debit note {{ l.debitNoteNumber }} }
                    @if (l.discount) { · {{ money(l.grossAmount) }} less {{ money(l.discount) }} credited before }</small>
                    @if (l.credited) { <br /><small class="why">{{ money(l.credited) }} credited</small> }</td>
                  <td class="hide-sm num">{{ l.hsnSac }}</td>
                  <td class="right num">{{ money(l.taxableValue) }}</td>
                  <td class="right num hide-sm">{{ money(l.taxAmount) }}@if (l.taxes.length) { <br /><small class="mute">{{ taxText(l) }}</small> }</td>
                  <td class="right num"><b>{{ money(l.total) }}</b></td>
                </tr>
              }
            </tbody>
            <tfoot>
              <tr><td colspan="3" class="right">Taxable value</td><td class="right num">{{ money(i.taxableValue) }}</td><td class="hide-sm"></td><td></td></tr>
              @if (i.cgst) { <tr><td colspan="3" class="right">CGST</td><td></td><td class="hide-sm"></td><td class="right num">{{ money(i.cgst) }}</td></tr> }
              @if (i.sgst) { <tr><td colspan="3" class="right">SGST</td><td></td><td class="hide-sm"></td><td class="right num">{{ money(i.sgst) }}</td></tr> }
              @if (i.igst) { <tr><td colspan="3" class="right">IGST</td><td></td><td class="hide-sm"></td><td class="right num">{{ money(i.igst) }}</td></tr> }
              <tr class="grand"><td colspan="3" class="right"><b>Invoice total</b></td><td></td><td class="hide-sm"></td>
                <td class="right num"><b data-testid="invoice-total">{{ money(i.total) }}</b></td></tr>
            </tfoot>
          </table>
          <p class="words">{{ i.amountInWords }}</p>
          @if (i.notes) { <p class="mute words">{{ i.notes }}</p> }
        </div>

        @if (i.creditNotes.length || i.debitNotes.length) {
          <div class="sums">
            <div><span>Billed</span><b class="num">{{ money(i.billed) }}</b></div>
            <div><span>Credited</span><b class="num bad" data-testid="credited">{{ money(i.credited) }}</b></div>
            <div><span>Stands at</span><b class="num" data-testid="outstanding">{{ money(i.outstanding) }}</b></div>
          </div>
        }

        @if (notes().length) {
          <h2>Credit and debit notes</h2>
          <div class="panel">
            <table class="data" aria-label="Credit and debit notes">
              <thead><tr><th>Note</th><th class="hide-sm">Lines</th><th class="right">Amount</th><th></th></tr></thead>
              <tbody>
                @for (n of notes(); track n.id) {
                  <tr [attr.data-note]="n.number">
                    <td><b class="num">{{ n.number }}</b> {{ noteLabel(n) }}<br /><small class="why">{{ n.reason }}</small>
                      <br /><small class="mute">{{ when(n.issuedAt) }}@if (n.issuedByName) { · {{ n.issuedByName }} }</small></td>
                    <td class="hide-sm">@for (l of n.lines; track l.lineNo) { <div>{{ l.description }} <small class="mute num">{{ money(l.total) }}</small></div> }</td>
                    <td class="right num"><b [class.bad]="n.kind === 'CREDIT_NOTE'">{{ n.kind === 'CREDIT_NOTE' ? '− ' : '' }}{{ money(n.total) }}</b>
                      <br /><small class="mute">GST {{ money(n.taxTotal) }}</small></td>
                    <td class="right"><button mat-button (click)="pdf(n.kind === 'CREDIT_NOTE' ? 'credit-notes' : 'debit-notes', n.id, n.number)"
                      [attr.aria-label]="'PDF of ' + n.number"><mat-icon>download</mat-icon>PDF</button></td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }

        <h2>Receipts</h2>
        <div class="panel">
          <table class="data" aria-label="Receipts">
            <thead><tr><th>Receipt</th><th class="hide-sm">Payment</th><th class="right">Amount</th><th></th></tr></thead>
            <tbody>
              @for (r of i.receipts; track r.id) {
                <tr [class.reversed]="r.voided" [attr.data-receipt]="r.number">
                  <td><b class="num">{{ r.number }}</b> · {{ shortDate(r.receiptDate) }}@if (r.voided) { <span class="chip back">Payment voided</span> }</td>
                  <td class="hide-sm"><a [routerLink]="['/payments', r.paymentId]" class="num">{{ r.paymentNumber }}</a> · {{ methodLabel(r.method) }}
                    @if (r.paymentReference) { <small class="mute"> · {{ r.paymentReference }}</small> }</td>
                  <td class="right num"><b>{{ money(r.amount) }}</b></td>
                  <td class="right"><button mat-button (click)="pdf('receipts', r.id, r.number)" [attr.aria-label]="'PDF of ' + r.number"><mat-icon>download</mat-icon>PDF</button></td>
                </tr>
              } @empty {
                <tr><td colspan="4" class="empty">No receipt yet. A receipt is issued for each payment on the booking at check-out, or from the payment.</td></tr>
              }
            </tbody>
          </table>
        </div>
      } @else if (error()) {
        <div class="page-head"><div><a routerLink="/invoices" class="back">Invoices</a><h1>Invoice</h1></div></div>
        <p class="err" role="alert">{{ error() }}</p>
      } @else { <p class="mute">Loading…</p> }
    </div>
  `,
  styles: `
    .wide { max-width: 1100px; }
    h2 { margin: 20px 0 8px; }
    .head-actions { display: flex; gap: 8px; flex-wrap: wrap; }
    .note { background: #fbefd8; color: #6b4600; border-radius: 8px; padding: 10px 12px; }
    .parties { display: grid; grid-template-columns: repeat(3, 1fr); gap: 16px; background: #fff; border: 1px solid #d9e0de; border-radius: 10px;
               padding: 14px 16px; margin-bottom: 16px; }
    .parties > div { display: flex; flex-direction: column; gap: 2px; }
    .parties small { color: #5b6b6e; text-transform: uppercase; font-size: .72rem; letter-spacing: .04em; }
    .sums { display: flex; gap: 12px; flex-wrap: wrap; margin: 16px 0 0; }
    .sums > div { display: flex; flex-direction: column; background: #fff; border: 1px solid #d9e0de; border-radius: 10px; padding: 10px 14px; min-width: 140px; }
    .sums span { color: #5b6b6e; font-size: .8rem; }
    .sums b { font-size: 1.2rem; color: #0e3b43; }
    .words { padding: 8px 16px 12px; margin: 0; font-style: italic; }
    .mute { color: #5b6b6e; }
    .why { color: #8a2449; }
    .bad, .sums b.bad { color: #b3261e; }
    .right { text-align: right; }
    .err { color: #b3261e; }
    .empty { color: #5b6b6e; padding: 20px 16px; }
    .chip { margin-left: 6px; }
    .chip.ok { background: #e1f2e8; color: #1d5b36; }
    .chip.back { background: #f6e3ec; color: #8a2449; }
    .chip.off { background: #eceff0; color: #5b6b6e; }
    .chip.wait { background: #fbefd8; color: #8a5a00; }
    tr.reversed b { text-decoration: line-through; color: #7a8a8c; }
    tfoot td { border-bottom: 0; padding-top: 4px; padding-bottom: 4px; }
    tfoot tr.grand td { border-top: 2px solid #d9e0de; padding-top: 10px; }
    @media (max-width: 760px) {
      .parties { grid-template-columns: 1fr; }
      table.data td, table.data th { padding: 10px 8px; }
    }
  `,
})
export class InvoicePage {
  id = input.required<string>();
  private api = inject(InvoicesApi);
  private props = inject(PropertyContext);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  private auth = inject(AuthService);

  i = signal<Invoice | null>(null);
  error = signal('');
  busy = signal(false);
  canCredit = computed(() => { this.auth.me(); return this.auth.can('credit_note.create', this.props.current()?.id); });
  canCancel = computed(() => { this.auth.me(); return this.auth.can('invoice.cancel', this.props.current()?.id); });
  notes = computed(() => [...(this.i()?.creditNotes ?? []), ...(this.i()?.debitNotes ?? [])].sort((a, b) => a.issuedAt.localeCompare(b.issuedAt)));
  cancelNote = computed(() => this.i()?.creditNotes.find(n => n.noteType === 'CANCEL') ?? null);

  money = rupeesExact;
  shortDate = shortDate;
  statusLabel = invoiceStatusLabel;
  statusClass = invoiceStatusClass;
  taxText = lineTaxText;
  methodLabel = methodLabel;
  private pid = () => this.props.current()!.id;

  constructor() {
    effect(() => { const id = this.id(); if (this.props.current()) this.load(id); });
  }

  noteLabel(n: InvoiceNote) {
    if (n.kind === 'DEBIT_NOTE') return 'Debit note';
    return n.noteType === 'CANCEL' ? 'Credit note · cancels the invoice' : 'Credit note';
  }

  when(iso: string) {
    return inZone(iso, this.props.current()!.timezone, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false });
  }

  private load(id: string) {
    this.error.set('');
    this.api.get(this.pid(), id).subscribe({
      next: i => this.i.set(i),
      error: e => this.error.set(toProblem(e).status === 404
        ? 'This invoice doesn’t exist at this property. Check the property selected at the top.' : toProblem(e).detail),
    });
  }

  pdf(kind: PdfKind, id: string, number: string) {
    this.error.set('');
    this.busy.set(true);
    this.api.pdf(this.pid(), kind, id, pdfName(number)).subscribe({
      next: () => this.busy.set(false),
      error: e => { this.busy.set(false); this.error.set(toProblem(e).detail); },
    });
  }

  credit() {
    this.dialog.open(CreditNoteDialog, { data: { propertyId: this.pid(), invoice: this.i()!, canCancel: this.canCancel() }, autoFocus: 'first-tabbable' })
      .afterClosed().subscribe((r: Invoice | undefined) => {
        if (!r) return;
        this.i.set(r);
        const n = r.creditNotes[r.creditNotes.length - 1];
        this.snack.open(`Credit note ${n?.number ?? ''} issued: ${rupeesExact(n?.total ?? 0)}`, undefined, { duration: 4000 });
      });
  }

  debit() {
    this.dialog.open(DebitNoteDialog, { data: { propertyId: this.pid(), invoice: this.i()! } })
      .afterClosed().subscribe((r: Invoice | undefined) => {
        if (!r) return;
        this.i.set(r);
        const n = r.debitNotes[r.debitNotes.length - 1];
        this.snack.open(`Debit note ${n?.number ?? ''} issued: ${rupeesExact(n?.total ?? 0)}`, undefined, { duration: 4000 });
      });
  }
}
