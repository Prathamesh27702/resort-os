import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { Payment } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { rupeesExact } from '../settings/rates/rates.api';
import { AllocateDialog, RefundDialog, VoidPaymentDialog } from './payment-dialogs';
import { PaymentsApi, methodLabel, paymentStatusClass, paymentStatusLabel } from './payments.api';

/**
 * One payment (Sprint 10): how it was paid, the bills it is on, what is still on account, its refunds and, if it was
 * entered by mistake, its void. Nothing is edited or deleted: a refund and a void are new records with their reasons.
 */
@Component({
  selector: 'app-payment',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page">
      @if (p(); as p) {
        <div class="page-head">
          <div>
            <a routerLink="/payments" class="back">Payments</a>
            <h1><span class="num">{{ p.number }}</span> · {{ money(p.amount) }} {{ methodLabel(p.method) }}
              <span [class]="'chip ' + statusClass(p.status)" data-testid="status">{{ statusLabel(p.status) }}</span></h1>
            <p>{{ p.guestName }} · <a [routerLink]="['/reservations', p.reservationId]" class="num">{{ p.reference }}</a> ·
              taken {{ time(p.receivedAt) }}@if (p.receivedByName) { by {{ p.receivedByName }} }</p>
          </div>
          <div class="head-actions">
            @if (canCreate() && p.onAccount > 0 && hasOwedFolio()) { <button mat-flat-button (click)="allocate()" [disabled]="busy()">Put on a bill</button> }
            @if (canRefund() && p.refundable > 0) { <button mat-stroked-button (click)="refund()" [disabled]="busy()"><mat-icon>undo</mat-icon>Refund</button> }
            @if (canVoid() && p.canVoid) { <button mat-button color="warn" (click)="voidIt()" [disabled]="busy()">Void</button> }
          </div>
        </div>

        @if (p.voidReason) { <p class="note" role="note">Voided {{ time(p.voidedAt!) }}@if (p.voidedByName) { by {{ p.voidedByName }} }: {{ p.voidReason }}.
          It counts for nothing on the bill.</p> }

        <div class="sums">
          <div><span>Received</span><b class="num">{{ money(p.amount) }}</b></div>
          <div><span>On bills</span><b class="num">{{ money(p.allocated) }}</b></div>
          <div><span>On account</span><b class="num" data-testid="on-account">{{ money(p.onAccount) }}</b></div>
          <div><span>Refunded</span><b class="num bad" data-testid="refunded">{{ money(p.refunded) }}</b></div>
          <div><span>Can still be refunded</span><b class="num">{{ money(p.refundable) }}</b></div>
        </div>
        <dl class="facts">
          @if (p.paymentReference) { <dt>Reference</dt><dd>{{ p.paymentReference }}</dd> }
          @if (p.note) { <dt>Note</dt><dd>{{ p.note }}</dd> }
          @if (p.method === 'CASH') { <dt>Cash drawer</dt><dd>Taken into the drawer shift open at the time</dd> }
          @if (!p.canVoid && p.cantVoidReason && p.status !== 'CANCELLED') { <dt>Void</dt><dd class="mute">{{ p.cantVoidReason }}</dd> }
        </dl>

        <h2>Bills it paid</h2>
        <div class="panel">
          <table class="data">
            <thead><tr><th>Folio</th><th class="hide-sm">When</th><th class="right">Amount</th><th class="right">Refunded</th></tr></thead>
            <tbody>
              @for (a of p.allocations; track a.id) {
                <tr><td><a [routerLink]="['/folios', a.folioId]" class="num"><b>{{ a.folioNumber }}</b></a> · {{ a.folioName }}</td>
                  <td class="hide-sm">{{ time(a.allocatedAt) }}</td>
                  <td class="right num">{{ money(a.amount) }}</td><td class="right num">{{ a.refunded ? money(a.refunded) : '' }}</td></tr>
              } @empty {
                <tr><td colspan="4" class="empty">Not on a bill yet: the money is on account for {{ p.reference }}.</td></tr>
              }
            </tbody>
          </table>
        </div>

        @if (p.refunds.length) {
          <h2>Refunds</h2>
          <div class="panel">
            <table class="data">
              <thead><tr><th>Refund</th><th class="hide-sm">From</th><th class="right">Amount</th></tr></thead>
              <tbody>
                @for (r of p.refunds; track r.id) {
                  <tr [attr.data-refund]="r.id"><td><b>{{ r.reason }}</b><br /><small class="mute">{{ methodLabel(r.method) }} · {{ time(r.refundedAt) }}
                    @if (r.refundedByName) { · {{ r.refundedByName }} }@if (r.reference) { · {{ r.reference }} }</small></td>
                    <td class="hide-sm">{{ r.folioNumber ?? 'On account' }}</td>
                    <td class="right num bad">− {{ money(r.amount) }}</td></tr>
                }
              </tbody>
            </table>
          </div>
        }
        @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      } @else if (error()) {
        <div class="page-head"><div><a routerLink="/payments" class="back">Payments</a><h1>Payment</h1></div></div>
        <p class="err" role="alert">{{ error() }}</p>
      } @else { <p class="mute">Loading…</p> }
    </div>
  `,
  styles: `
    .head-actions { display: flex; gap: 8px; flex-wrap: wrap; }
    h2 { margin: 20px 0 8px; }
    .note { background: #f3e3e3; color: #7a1f1f; border-radius: 8px; padding: 10px 12px; }
    .sums { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 12px; background: #fff; border: 1px solid #d9e0de; border-radius: 10px;
            padding: 12px 16px; margin-bottom: 12px; }
    .sums div { display: flex; flex-direction: column; }
    .sums span { color: #5b6b6e; font-size: .8rem; }
    .sums b { font-size: 1.15rem; color: #0e3b43; }
    .facts { display: grid; grid-template-columns: auto 1fr; gap: 4px 16px; margin: 0 0 8px; }
    .facts dt { color: #5b6b6e; }
    .facts dd { margin: 0; }
    .chip.ok { background: #e1f2e8; color: #1d5b36; }
    .chip.back { background: #f6e3ec; color: #8a2449; }
    .chip.off { background: #eceff0; color: #5b6b6e; }
    .chip.wait { background: #fbefd8; color: #8a5a00; }
    .mute { color: #5b6b6e; }
    .bad, .sums b.bad { color: #b3261e; }
    .right { text-align: right; }
    .empty { color: #5b6b6e; padding: 16px; }
    .err { color: #b3261e; }
    @media (max-width: 760px) { .sums { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
  `,
})
export class PaymentPage {
  id = input.required<string>();
  private api = inject(PaymentsApi);
  private props = inject(PropertyContext);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  private auth = inject(AuthService);

  p = signal<Payment | null>(null);
  error = signal('');
  busy = signal(false);
  canCreate = computed(() => { this.auth.me(); return this.auth.can('payment.create', this.props.current()?.id); });
  canRefund = computed(() => { this.auth.me(); return this.auth.can('payment.refund', this.props.current()?.id); });
  canVoid = computed(() => { this.auth.me(); return this.auth.can('payment.void', this.props.current()?.id); });
  hasOwedFolio = computed(() => (this.p()?.folios ?? []).some(f => f.balance > 0));

  money = rupeesExact;
  methodLabel = methodLabel;
  statusLabel = paymentStatusLabel;
  statusClass = paymentStatusClass;
  private pid = () => this.props.current()!.id;

  constructor() {
    effect(() => { const id = this.id(); if (this.props.current()) this.load(id); });
  }

  time(iso: string) {
    return inZone(iso, this.props.current()!.timezone, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false });
  }

  private load(id: string) {
    this.error.set('');
    this.api.get(this.pid(), id).subscribe({ next: p => this.p.set(p), error: e => this.error.set(toProblem(e).detail) });
  }

  refund() { this.after(this.dialog.open(RefundDialog, { data: { propertyId: this.pid(), payment: this.p()! } }), p => `Refunded. ${rupeesExact(p.refundable)} can still be refunded`); }
  voidIt() { this.after(this.dialog.open(VoidPaymentDialog, { data: { propertyId: this.pid(), payment: this.p()! } }), p => `${p.number} voided`); }
  allocate() { this.after(this.dialog.open(AllocateDialog, { data: { propertyId: this.pid(), payment: this.p()! } }), p => `${p.number} is on the bill`); }

  private after(ref: { afterClosed(): import('rxjs').Observable<Payment | undefined> }, message: (p: Payment) => string) {
    ref.afterClosed().subscribe(p => {
      if (!p) return;
      this.p.set(p);
      this.snack.open(message(p), undefined, { duration: 3000 });
    });
  }
}
