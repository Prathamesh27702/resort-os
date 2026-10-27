import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { dayText } from '../../core/dates';
import { toProblem } from '../../core/http';
import { PurchaseOrder, SupplierInvoice } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { InventoryApi, qty } from '../inventory/inventory.api';
import { ReasonData, ReasonDialog } from '../restaurant/order-dialogs';
import { rupees, rupeesExact } from '../settings/rates/rates.api';
import { BillDialog, OrderDialog, PaySupplierDialog, ReceiveDialog } from './purchasing-dialogs';
import { BILL_STATUS_LABEL, ORDER_STATUS_LABEL, PAY_METHOD_LABEL, PurchasingApi, orderActions, todayAt } from './purchasing.api';

/**
 * One purchase order (Sprint 16): its lines with what has come in and what is still to come, the approval (over the limit
 * a second person approves), each delivery, the supplier's bills with their GST and the payments. The buttons are the
 * steps this person can take now. Every step sends the order's version, so a change someone else made first is refused
 * with the reason and the page reloads. Works at 360 px.
 */
@Component({
  selector: 'app-purchase-order',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page po">
      <a mat-button routerLink="/inventory/purchases" class="back"><mat-icon>arrow_back</mat-icon>Purchases</a>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      @if (order(); as o) {
        <div class="head">
          <h1>{{ o.number }} <span class="sup">{{ o.supplierName }}</span></h1>
          <span [class]="'status ' + o.status" data-testid="po-status">{{ label[o.status] }}</span>
        </div>
        <p class="mute">
          {{ o.supplierGstin ? 'GSTIN ' + o.supplierGstin : 'Unregistered supplier (no GST)' }} · {{ o.supplierState }} · deliver to {{ o.locationName }}
          · raised {{ dayText(o.orderDate) }}@if (o.createdByName) { by {{ o.createdByName }} }@if (o.requestNumber) { from {{ o.requestNumber }} }
          @if (o.expectedDate) { · expected {{ dayText(o.expectedDate) }} }
        </p>
        @if (o.note) { <p class="note">“{{ o.note }}”</p> }

        @switch (o.status) {
          @case ('DRAFT') {
            @if (o.rejectedReason) { <p class="banner warn" data-testid="rejected">Sent back: {{ o.rejectedReason }}. Change it and submit again.</p> }
            <p class="banner">{{ o.needsApproval ? 'Over the ' + rupees(o.approvalLimit) + ' limit: once submitted, someone else with purchase approval must approve it.'
              : 'Within the ' + rupees(o.approvalLimit) + ' limit: it is approved as soon as it is submitted.' }}</p>
          }
          @case ('PENDING_APPROVAL') {
            <p class="banner warn" data-testid="waiting">Waiting for approval: {{ rupees(o.total) }} is over the {{ rupees(o.approvalLimit) }} limit.
              Submitted@if (o.submittedByName) { by {{ o.submittedByName }} } {{ when(o.submittedAt!) }}.
              @if (o.submittedBy === me() && can('purchase.approve')) { You submitted it, so someone else must approve it. }</p>
          }
          @case ('CANCELLED') { <p class="banner off">Cancelled: {{ o.endReason }}</p> }
          @case ('CLOSED') { <p class="banner off">Closed, the rest is not coming: {{ o.endReason }}</p> }
          @default {
            <p class="banner ok">Approved {{ when(o.approvedAt!) }}@if (o.approvedByName) { by {{ o.approvedByName }} } @else { on submit (within the limit) }@if (o.approvalNote) { — {{ o.approvalNote }} }</p>
          }
        }

        <div class="acts">
          @for (a of actions(); track a) {
            @switch (a) {
              @case ('edit') { <button mat-stroked-button (click)="edit()" [disabled]="busy()"><mat-icon>edit</mat-icon>Edit</button> }
              @case ('submit') { <button mat-flat-button (click)="submit()" [disabled]="busy()" data-testid="submit"><mat-icon>send</mat-icon>Submit</button> }
              @case ('approve') { <button mat-flat-button (click)="approve()" [disabled]="busy()" data-testid="approve"><mat-icon>verified</mat-icon>Approve</button> }
              @case ('reject') { <button mat-stroked-button (click)="reason('reject')" [disabled]="busy()">Send back</button> }
              @case ('receive') { <button mat-flat-button (click)="receive()" [disabled]="busy()" data-testid="receive"><mat-icon>inventory</mat-icon>Goods received</button> }
              @case ('bill') { <button mat-flat-button (click)="bill()" [disabled]="busy()" data-testid="bill"><mat-icon>receipt_long</mat-icon>Record bill</button> }
              @case ('close') { <button mat-stroked-button (click)="reason('close')" [disabled]="busy()">Close (rest not coming)</button> }
              @case ('cancel') { <button mat-button (click)="reason('cancel')" [disabled]="busy()">Cancel order</button> }
            }
          }
        </div>

        <section>
          <h2>Lines</h2>
          <div class="table" role="table" aria-label="Order lines">
            <div class="tr th" role="row"><span role="columnheader" class="dn">Item</span><span role="columnheader">Ordered</span><span role="columnheader">Price</span>
              <span role="columnheader">GST</span><span role="columnheader">Amount</span><span role="columnheader">Received</span><span role="columnheader">To come</span>
              <span role="columnheader">Billed</span></div>
            @for (l of o.lines; track l.id) {
              <div class="tr" role="row" [attr.data-line]="l.itemName">
                <span role="cell" class="dn">{{ l.itemName }}</span>
                <span role="cell" data-l="Ordered">{{ qty(l.quantity, l.unit) }}</span>
                <span role="cell" data-l="Price">{{ rupeesExact(l.unitPrice) }}/{{ l.unit }}</span>
                <span role="cell" data-l="GST">{{ l.gstRate }} %</span>
                <span role="cell" data-l="Amount">{{ rupeesExact(l.total) }}</span>
                <span role="cell" data-l="Received">{{ qty(l.received, l.unit) }}</span>
                <span role="cell" data-l="To come" [class.due]="l.remaining > 0 && o.status !== 'CLOSED' && o.status !== 'CANCELLED'">{{ qty(l.remaining, l.unit) }}</span>
                <span role="cell" data-l="Billed">{{ qty(l.billed, l.unit) }}</span>
              </div>
            }
          </div>
          <p class="tot">Before GST {{ rupeesExact(o.subtotal) }} · {{ o.intraState ? 'CGST + SGST' : 'IGST' }} {{ rupeesExact(o.taxTotal) }} · <b>Total {{ rupeesExact(o.total) }}</b></p>
        </section>

        @if (o.receipts.length) {
          <section>
            <h2>Goods received</h2>
            <ul class="list">
              @for (g of o.receipts; track g.id) {
                <li [attr.data-grn]="g.number"><b>{{ g.number }}</b> {{ dayText(g.receivedOn) }} into {{ g.locationName }}@if (g.challanNumber) { · challan {{ g.challanNumber }} }
                  <small>{{ grnLines(g.lines) }} · {{ rupees(g.value) }} before GST@if (g.byName) { · {{ g.byName }} }</small></li>
              }
            </ul>
          </section>
        }

        @if (o.invoices.length) {
          <section>
            <h2>Supplier bills</h2>
            <ul class="list">
              @for (i of o.invoices; track i.id) {
                <li [attr.data-bill]="i.number">
                  <div class="bl"><b>{{ i.number }}</b> bill {{ i.supplierNumber }} of {{ dayText(i.invoiceDate) }} · due {{ dayText(i.dueDate) }}
                    <small>Before GST {{ rupeesExact(i.taxableValue) }} · @if (i.intraState) { CGST {{ rupeesExact(i.cgst) }} · SGST {{ rupeesExact(i.sgst) }} } @else { IGST {{ rupeesExact(i.igst) }} }
                      · <b>{{ rupeesExact(i.total) }}</b>@if (i.inputTax > 0) { · input tax {{ i.itcEligible ? 'claimable' : 'not claimable (no regular GST registration)' }} }</small>
                    @for (l of i.lines; track l.orderItemId) { @if (l.priceDiffers) { <small class="warn">{{ l.itemName }} billed at {{ rupeesExact(l.unitPrice) }}, ordered at {{ rupeesExact(l.orderPrice) }}</small> } }
                    @for (p of i.payments; track p.id) { <small class="mute">{{ p.number }} {{ dayText(p.paidOn) }} · {{ rupeesExact(p.amount) }} by {{ method[p.method] }}@if (p.reference) { ({{ p.reference }}) }</small> }
                  </div>
                  <span class="right"><small [class]="'st ' + i.status + (i.overdue ? ' late' : '')">{{ i.overdue ? 'Overdue' : billLabel[i.status] }}</small>
                    @if (i.balance > 0) { <small>{{ rupees(i.balance) }} left</small> }
                    @if (i.balance > 0 && can('supplier_invoice.manage')) { <button mat-stroked-button (click)="pay(i)" [attr.aria-label]="'Pay ' + i.number">Pay</button> }</span>
                </li>
              }
            </ul>
          </section>
        }
      }
    </div>
  `,
  styles: `
    .po { max-width: 980px; padding-bottom: 60px; }
    .head { display: flex; justify-content: space-between; align-items: baseline; gap: 8px; flex-wrap: wrap; }
    h1 { margin: 4px 0; } .sup { font-weight: 400; color: #34484b; }
    .mute { color: #5b6b6e; } .err { color: #b3261e; } .note { color: #34484b; margin: 0 0 6px; } .warn { color: #8a5a00; } .late { color: #b3261e; }
    .status { padding: 2px 10px; border-radius: 12px; background: #eef2f1; font-size: .85rem; }
    .status.PENDING_APPROVAL { background: #fff3d6; } .status.APPROVED, .status.PARTLY_RECEIVED { background: #e3f0fa; } .status.RECEIVED { background: #e3f4e5; }
    .banner { background: #f4f7f6; border-radius: 10px; padding: 8px 12px; margin: 6px 0; } .banner.warn { background: #fff3d6; } .banner.ok { background: #e3f4e5; }
    .banner.off { background: #eef0f0; color: #5b6b6e; }
    .acts { display: flex; gap: 8px; flex-wrap: wrap; margin: 8px 0; }
    section { background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 4px 14px 10px; margin-top: 12px; }
    h2 { font-size: 1.02rem; margin: 8px 0; }
    .table { display: flex; flex-direction: column; }
    .tr { display: grid; grid-template-columns: 2fr repeat(7, 1fr); gap: 6px; padding: 6px 0; border-bottom: 1px solid #eef2f1; font-variant-numeric: tabular-nums; }
    .tr span:not(.dn) { text-align: right; } .th { font-weight: 600; font-size: .85rem; color: #5b6b6e; } .due { color: #8a5a00; font-weight: 600; }
    .tot { text-align: right; font-variant-numeric: tabular-nums; margin: 6px 0 0; }
    ul { list-style: none; margin: 0; padding: 0; }
    .list li { display: flex; gap: 10px; padding: 8px 0; border-bottom: 1px solid #eef2f1; flex-wrap: wrap; }
    .list small { display: block; } .bl { flex: 1; min-width: 0; }
    .right { display: flex; flex-direction: column; align-items: flex-end; gap: 2px; }
    .st.UNPAID { color: #b3261e; } .st.PART_PAID { color: #8a5a00; } .st.PAID { color: #2e7d32; }
    @media (max-width: 640px) {
      h1 { font-size: 1.3rem; }
      .th { display: none; } .tr { grid-template-columns: 1fr 1fr; } .tr .dn { grid-column: 1 / -1; font-weight: 600; }
      .tr span[data-l]::before { content: attr(data-l) ': '; color: #5b6b6e; float: left; }
      .right { align-items: flex-start; }
    }
  `,
})
export class PurchaseOrderPage {
  id = input.required<string>();
  private api = inject(PurchasingApi);
  private inventoryApi = inject(InventoryApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);

  order = signal<PurchaseOrder | null>(null);
  error = signal('');
  busy = signal(false);
  rupees = rupees;
  rupeesExact = rupeesExact;
  qty = qty;
  dayText = dayText;
  label = ORDER_STATUS_LABEL;
  billLabel = BILL_STATUS_LABEL;
  method = PAY_METHOD_LABEL;
  private pid = computed(() => this.ctx.current()?.id ?? '');
  me = computed(() => this.auth.me()?.userId ?? null);
  can = (p: string) => this.auth.can(p, this.pid());
  actions = computed(() => { const o = this.order(); return o ? orderActions(o, p => this.can(p), this.me()) : []; });
  private today = () => todayAt(this.ctx.current()!.timezone);

  constructor() {
    effect(() => { if (this.pid() && this.id()) this.load(); });
  }

  load() {
    this.api.order(this.pid(), this.id()).subscribe({ next: o => { this.order.set(o); this.error.set(''); }, error: e => this.error.set(toProblem(e).detail) });
  }

  when(at: string) { return new Date(at).toLocaleString('en-IN', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false }); }
  grnLines(lines: { itemName: string; unit: string; quantity: number }[]) { return lines.map(l => `${qty(l.quantity, l.unit)} ${l.itemName}`).join(', '); }

  edit() {
    const o = this.order()!;
    forkJoin([this.inventoryApi.get(this.pid()), this.api.suppliers(this.pid())]).subscribe({
      next: ([inventory, suppliers]) => this.dialog.open(OrderDialog, {
        data: { propertyId: this.pid(), inventory, suppliers, order: o, request: null, propertyState: this.ctx.current()?.gstStateCode ?? null }, maxWidth: '100vw',
      }).afterClosed().subscribe((x?: PurchaseOrder) => { if (x) this.order.set(x); }),
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  submit() { this.run(this.api.step(this.pid(), this.id(), 'submit', null, this.order()!.version), o => o.status === 'APPROVED' ? `${o.number} approved (within the limit). It can be received now.` : `${o.number} sent for approval.`); }
  approve() { this.run(this.api.step(this.pid(), this.id(), 'approve', null, this.order()!.version), o => `${o.number} approved.`); }

  reason(step: 'reject' | 'cancel' | 'close') {
    const o = this.order()!;
    const words = {
      reject: { title: `Send ${o.number} back`, intro: 'It goes back to draft with your reason, for the person who raised it to change.', button: 'Send back', placeholder: 'Too much for one week' },
      cancel: { title: `Cancel ${o.number}`, intro: 'Nothing has come in on it. Say why it is cancelled; its request (if any) opens again.', button: 'Cancel order', placeholder: 'Ordered by phone instead' },
      close: { title: `Close ${o.number}`, intro: 'Part of it came in and the rest is not coming. Say why.', button: 'Close order', placeholder: 'Supplier out of stock' },
    }[step];
    const data: ReasonData<PurchaseOrder> = { ...words, run: r => this.api.withReason(this.pid(), o.id, step, r, o.version) };
    this.dialog.open(ReasonDialog, { data, maxWidth: '100vw' }).afterClosed().subscribe((x?: PurchaseOrder) => { if (x) this.order.set(x); });
  }

  receive() {
    this.inventoryApi.get(this.pid()).subscribe({
      next: inventory => this.dialog.open(ReceiveDialog, { data: { propertyId: this.pid(), order: this.order()!, inventory }, maxWidth: '100vw' })
        .afterClosed().subscribe((x?: PurchaseOrder) => {
          if (!x) return;
          this.order.set(x);
          const g = x.receipts[x.receipts.length - 1];
          this.snack.open(`${g.number} saved: ${this.grnLines(g.lines)} into ${g.locationName}.${x.status === 'RECEIVED' ? ' Everything has come in.' : ''}`, 'OK', { duration: 6000 });
        }),
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  bill() {
    const o = this.order()!;
    this.api.suppliers(this.pid()).subscribe({
      next: list => this.dialog.open(BillDialog, {
        data: { propertyId: this.pid(), order: o, today: this.today(), termsDays: list.find(s => s.id === o.supplierId)?.paymentTermsDays ?? 0 }, maxWidth: '100vw',
      }).afterClosed().subscribe((b?: SupplierInvoice) => {
        if (!b) return;
        this.snack.open(`${b.number} recorded: ${rupees(b.total)}, due ${dayText(b.dueDate)}.`, 'OK', { duration: 6000 });
        this.load();
      }),
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  pay(i: SupplierInvoice) {
    this.dialog.open(PaySupplierDialog, { data: { propertyId: this.pid(), invoice: i, today: this.today() }, maxWidth: '100vw' }).afterClosed()
      .subscribe((b?: SupplierInvoice) => {
        if (!b) return;
        this.snack.open(b.status === 'PAID' ? `${b.number} is paid in full.` : `${b.number}: ${rupees(b.balance)} left to pay.`, 'OK', { duration: 5000 });
        this.load();
      });
  }

  /** A refused step says why; a change someone else made first reloads the order. */
  private run(call: ReturnType<PurchasingApi['step']>, done: (o: PurchaseOrder) => string) {
    this.busy.set(true);
    call.subscribe({
      next: o => { this.busy.set(false); this.order.set(o); this.error.set(''); this.snack.open(done(o), 'OK', { duration: 5000 }); },
      error: e => { this.busy.set(false); const p = toProblem(e); this.error.set(p.detail); if (p.code === 'CONCURRENCY_CONFLICT') this.load(); },
    });
  }
}
