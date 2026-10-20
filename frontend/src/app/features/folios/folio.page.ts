import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { Folio, FolioItem, FolioPaymentLine, Payment } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { statusClass, statusLabel } from '../reservations/reservations.api';
import { shortDate } from '../rooms/rooms.api';
import { pct, taxText } from '../settings/gst/gst.api';
import { dayLabel, rupeesExact } from '../settings/rates/rates.api';
import { TakePaymentDialog } from '../payments/payment-dialogs';
import { methodLabel } from '../payments/payments.api';
import { PostChargeDialog, ReverseChargeDialog, SplitFolioDialog, SplitResult } from './folio-dialogs';
import { FoliosApi, categoryLabel, lineNote, liveCharges, quantityText } from './folios.api';

/**
 * One bill (Sprint 8): every line in date order, what it adds up to by category, and the balance. Lines are never edited
 * or deleted: a reversed charge stays on the bill, struck through, next to its reversal and the reason. Staff post
 * charges and move lines to another folio; managers (folio.adjust) reverse. Sprint 10: payments and refunds on the bill
 * are listed under it, and the balance is what is still owed after them.
 */
@Component({
  selector: 'app-folio',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page wide">
      @if (f(); as f) {
        <div class="page-head">
          <div>
            <a routerLink="/folios" class="back">Folios</a>
            <h1><span class="num">{{ f.number }}</span> · {{ f.isPrimary ? f.guestName : f.name }}
              <span [class]="'chip ' + statusClass(f.reservationStatus)">{{ statusLabel(f.reservationStatus) }}</span></h1>
            <p><a [routerLink]="['/reservations', f.reservationId]" class="num">{{ f.reference }}</a> · {{ f.guestName }} · {{ f.rooms }} ·
              {{ dayLabel(f.checkIn) }} – {{ dayLabel(f.checkOut) }}@if (!f.isPrimary) { · {{ f.name }} }</p>
          </div>
          <div class="head-actions">
            @if (canPay()) { <button mat-flat-button (click)="pay()" [disabled]="busy()"><mat-icon>payments</mat-icon>Take payment</button> }
            @if (canPost() && f.canPost) { <button mat-flat-button (click)="post()" [disabled]="busy()"><mat-icon>add</mat-icon>Post charge</button> }
            @if (canPost() && f.canPost && live().length) {
              <button mat-stroked-button (click)="split()" [disabled]="busy()"><mat-icon>call_split</mat-icon>Move lines</button>
            }
          </div>
        </div>

        @if (f.folios.length > 1) {
          <nav class="folios" aria-label="Folios of this stay">
            @for (o of f.folios; track o.id) {
              <a [routerLink]="['/folios', o.id]" [class.on]="o.id === f.id"><b class="num">{{ o.number }}</b> {{ o.name }} · <span class="num">{{ money(o.balance) }}</span></a>
            }
          </nav>
        }
        @if (f.cantPostReason) { <p class="note" role="note">{{ f.cantPostReason }}</p> }

        <div class="sums">
          <div class="balance"><span>Balance</span><b class="num" [class.owed]="f.balance > 0" data-testid="balance">{{ money(f.balance) }}</b>
            <small class="mute">GST included where it applies.@if (f.balance < 0) { Paid more than the bill: refund the difference from the payment. }</small></div>
          <dl>
            <dt>Charges</dt><dd class="num">{{ money(f.charges) }}</dd>
            <dt>Reversals</dt><dd class="num">{{ money(f.reversals) }}</dd>
            <dt>Tax</dt><dd class="num">{{ money(f.tax) }}</dd>
            <dt>Paid</dt><dd class="num">{{ money(f.payments) }}</dd>
          </dl>
          <div class="cats" aria-label="By category">
            @for (c of f.categories; track c.category) {
              @if (c.amount !== 0) { <span class="chip cat">{{ label(c.category) }} <b class="num">{{ money(c.amount) }}</b></span> }
            }
          </div>
        </div>

        @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

        <div class="panel">
          <table class="data">
            <thead><tr><th>Day</th><th>Line</th><th class="hide-sm">For</th><th class="right">Amount</th><th class="right">&nbsp;</th></tr></thead>
            <tbody>
              @for (i of f.items; track i.id) {
                <tr [class.reversed]="!i.live && i.itemType === 'CHARGE'" [class.reversal]="i.itemType === 'REVERSAL'" [attr.data-line]="i.description">
                  <td class="num">{{ shortDate(i.serviceDate) }}</td>
                  <td>
                    <span class="desc">{{ i.description }}</span>
                    @if (qty(i)) { <small class="mute num"> · {{ qty(i) }}</small> }
                    <br /><small class="mute">{{ i.postedByName ?? 'System' }} at {{ time(i.postedAt) }}</small>
                    @if (note(i); as n) { <br /><small [class]="i.itemType === 'REVERSAL' || i.reversedByItemId ? 'why' : 'mute'">{{ n }}</small> }
                  </td>
                  <td class="hide-sm"><span class="chip cat">{{ label(i.category) }}</span></td>
                  <td class="right num"><b>{{ money(i.total) }}</b>
                    @if (i.taxes.length) { <br /><small class="mute" [attr.title]="taxTitle(i)">{{ money(i.amount) }} + {{ tax(i.taxes) }} {{ money(i.taxAmount) }}</small> }</td>
                  <td class="right">
                    @if (canReverse() && i.live) {
                      <button mat-button (click)="reverse(i)" [disabled]="busy()" [attr.aria-label]="'Reverse ' + i.description">Reverse</button>
                    }
                  </td>
                </tr>
              } @empty {
                <tr><td colspan="5" class="empty">Nothing on this bill yet.@if (f.reservationStatus === 'CONFIRMED') { Room nights are added at check-in. }</td></tr>
              }
            </tbody>
            @if (f.items.length) {
              <tfoot><tr><td colspan="3" class="right"><b>Total</b></td><td class="right num"><b>{{ money(f.total) }}</b></td><td></td></tr></tfoot>
            }
          </table>
        </div>

        <h2>Payments</h2>
        <div class="panel">
          <table class="data" aria-label="Payments on this bill">
            <thead><tr><th>Payment</th><th class="hide-sm">When</th><th class="right">Amount</th></tr></thead>
            <tbody>
              @for (p of f.paymentLines; track (p.refundId ?? p.paymentId)) {
                <tr [class.reversed]="p.voided" [class.reversal]="p.kind === 'REFUND'" [attr.data-payment-line]="p.paymentNumber + (p.kind === 'REFUND' ? '-refund' : '')">
                  <td><a [routerLink]="['/payments', p.paymentId]" class="num"><b>{{ p.paymentNumber }}</b></a>
                    {{ p.kind === 'REFUND' ? 'Refund' : 'Payment' }} · {{ methodLabel(p.method) }}@if (p.voided) { · voided }
                    @if (p.detail) { <br /><small [class]="p.kind === 'REFUND' ? 'why' : 'mute'">{{ p.detail }}</small> }</td>
                  <td class="hide-sm">{{ time(p.at) }}@if (p.byName) { <br /><small class="mute">{{ p.byName }}</small> }</td>
                  <td class="right num"><b>{{ lineMoney(p) }}</b></td>
                </tr>
              } @empty {
                <tr><td colspan="3" class="empty">No payment on this bill yet.@if (canPay()) { Use Take payment when the guest pays. }</td></tr>
              }
            </tbody>
          </table>
        </div>
      } @else if (error()) {
        <div class="page-head"><div><a routerLink="/folios" class="back">Folios</a><h1>Folio</h1></div></div>
        <p class="err" role="alert">{{ error() }}</p>
      } @else { <p class="mute">Loading…</p> }
    </div>
  `,
  styles: `
    .wide { max-width: 1100px; }
    h2 { margin: 20px 0 8px; }
    .head-actions { display: flex; gap: 8px; flex-wrap: wrap; }
    .folios { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 12px; }
    .folios a { padding: 6px 12px; border: 1px solid #d9e0de; border-radius: 16px; color: #1b2a2e; text-decoration: none; background: #fff; }
    .folios a.on { border-color: #0e3b43; background: #e6f0f1; }
    .note { background: #fbefd8; color: #6b4600; border-radius: 8px; padding: 10px 12px; }
    .sums { display: grid; grid-template-columns: minmax(200px, 1fr) minmax(220px, 1fr) 2fr; gap: 16px; align-items: start; background: #fff;
            border: 1px solid #d9e0de; border-radius: 10px; padding: 14px 16px; margin-bottom: 16px; }
    .balance { display: flex; flex-direction: column; gap: 2px; }
    .balance span { color: #5b6b6e; font-size: .85rem; }
    .balance b { font-size: 1.8rem; color: #0e3b43; letter-spacing: -0.01em; }
    .balance b.owed { color: #b3261e; }
    dl { display: grid; grid-template-columns: auto 1fr; gap: 2px 16px; margin: 0; }
    dt { color: #5b6b6e; }
    dd { margin: 0; text-align: right; }
    .cats { display: flex; flex-wrap: wrap; gap: 6px; }
    .chip.cat { background: #eef3f2; color: #24423f; }
    .chip.ok { background: #e1f2e8; color: #1d5b36; }
    .chip.wait { background: #fbefd8; color: #8a5a00; }
    .mute { color: #5b6b6e; }
    .why { color: #8a2449; }
    .right { text-align: right; }
    .err { color: #b3261e; }
    .empty { color: #5b6b6e; padding: 20px 16px; }
    tr.reversed .desc, tr.reversed td.num b { text-decoration: line-through; color: #7a8a8c; }
    tr.reversal td.num b { color: #b3261e; }
    tfoot td { border-top: 2px solid #d9e0de; padding-top: 10px; }
    @media (max-width: 760px) {
      .sums { grid-template-columns: 1fr; }
      table.data td, table.data th { padding: 10px 8px; }
    }
  `,
})
export class FolioPage {
  id = input.required<string>();
  private api = inject(FoliosApi);
  private props = inject(PropertyContext);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  private router = inject(Router);
  private auth = inject(AuthService);

  f = signal<Folio | null>(null);
  error = signal('');
  busy = signal(false);
  live = computed(() => liveCharges(this.f()?.items ?? []));
  canPost = computed(() => { this.auth.me(); return this.auth.can('folio.post_charge', this.props.current()?.id); });
  canPay = computed(() => { this.auth.me(); return this.auth.can('payment.create', this.props.current()?.id); });
  canReverse = computed(() => { this.auth.me(); return this.auth.can('folio.adjust', this.props.current()?.id); });

  money = rupeesExact;
  methodLabel = methodLabel;
  /** A refund reads as money going back; a payment as money in. */
  lineMoney = (p: FolioPaymentLine) => (p.kind === 'REFUND' ? '− ' : '') + rupeesExact(Math.abs(p.amount));
  shortDate = shortDate;
  dayLabel = dayLabel;
  statusLabel = statusLabel;
  statusClass = statusClass;
  label = categoryLabel;
  note = lineNote;
  qty = (i: FolioItem) => quantityText(i, rupeesExact);
  tax = taxText;
  /** "CGST 2.5% ₹36.25 · SGST 2.5% ₹36.25" on hover. */
  taxTitle = (i: FolioItem) => i.taxes.map(t => `${t.code} ${pct(t.rate)} ${rupeesExact(t.amount)}`).join(' · ');
  private pid = () => this.props.current()!.id;

  constructor() {
    effect(() => { const id = this.id(); if (this.props.current()) this.load(id); });
  }

  time(iso: string) {
    return inZone(iso, this.props.current()!.timezone, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false });
  }

  private load(id: string) {
    this.error.set('');
    this.api.get(this.pid(), id).subscribe({ next: f => this.f.set(f), error: e => this.error.set(toProblem(e).detail) });
  }

  post() {
    const f = this.f()!;
    this.dialog.open(PostChargeDialog, { data: { propertyId: this.pid(), folio: f, today: this.props.current()!.localDate }, autoFocus: 'first-tabbable' })
      .afterClosed().subscribe((r: Folio | undefined) => {
        if (!r) return;
        this.f.set(r);
        this.snack.open(`Posted to ${r.number}. Balance ${rupeesExact(r.balance)}`, undefined, { duration: 3000 });
      });
  }

  pay() {
    this.dialog.open(TakePaymentDialog, { data: { propertyId: this.pid(), folio: this.f()! }, autoFocus: 'first-tabbable' })
      .afterClosed().subscribe((p: Payment | undefined) => {
        if (!p) return;
        this.load(this.f()!.id);
        this.snack.open(p.onAccount > 0 && p.allocated === 0 ? `${p.number}: ${rupeesExact(p.amount)} taken on account`
          : `${p.number}: ${rupeesExact(p.amount)} paid`, undefined, { duration: 3000 });
      });
  }

  reverse(item: FolioItem) {
    this.dialog.open(ReverseChargeDialog, { data: { propertyId: this.pid(), folio: this.f()!, item } })
      .afterClosed().subscribe((r: Folio | undefined) => {
        if (!r) return;
        this.f.set(r);
        this.snack.open(`Reversed: ${item.description}. Balance ${rupeesExact(r.balance)}`, undefined, { duration: 3000 });
      });
  }

  split() {
    this.dialog.open(SplitFolioDialog, { data: { propertyId: this.pid(), folio: this.f()! } })
      .afterClosed().subscribe((r: SplitResult | undefined) => {
        if (!r) return;
        this.f.set(r.from);
        this.snack.open(`Moved to ${r.to.number} (${r.to.name})`, 'Open', { duration: 5000 })
          .onAction().subscribe(() => this.router.navigate(['/folios', r.to.id]));
      });
  }
}
