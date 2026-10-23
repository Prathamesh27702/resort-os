import { Component, DestroyRef, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Subject, debounceTime, switchMap, tap } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { toProblem } from '../../core/http';
import { PaymentList, PaymentMethod } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { addDays } from '../rooms/rooms.api';
import { rupeesExact } from '../settings/rates/rates.api';
import { CashDrawerPanel } from './cash-drawer-panel';
import { ALL_METHODS, PaymentsApi, methodLabel, paymentStatusClass, paymentStatusLabel } from './payments.api';

type Period = 'today' | 'yesterday' | 'week' | 'all';

/** Money taken (Sprint 10): every payment with what became of it, what the day adds up to by method, and the cash
 *  drawer. Search by payment number, booking reference, payment reference or guest. */
@Component({
  selector: 'app-payments',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, CashDrawerPanel],
  template: `
    <div class="page wide">
      <div class="page-head">
        <div><h1>Payments</h1><p>Cash, UPI, card and bank transfers taken at the property, refunds and the cash drawer. Take a payment from a
          guest's bill.</p></div>
      </div>

      <app-cash-drawer-panel />

      <div class="filters">
        <mat-form-field appearance="outline" class="q">
          <mat-icon matPrefix>search</mat-icon>
          <mat-label>Search</mat-label>
          <input matInput [ngModel]="q()" (ngModelChange)="setQ($event)" placeholder="e.g. P00012, R00008 or Iyer" autocomplete="off" />
        </mat-form-field>
        <mat-form-field appearance="outline" class="m">
          <mat-label>Method</mat-label>
          <mat-select [ngModel]="method()" (ngModelChange)="setMethod($event)">
            <mat-option value="">All methods</mat-option>
            @for (m of methods; track m) { <mat-option [value]="m">{{ methodLabel(m) }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-button-toggle-group [ngModel]="period()" (ngModelChange)="setPeriod($event)" aria-label="When">
          <mat-button-toggle value="today">Today</mat-button-toggle>
          <mat-button-toggle value="yesterday">Yesterday</mat-button-toggle>
          <mat-button-toggle value="week">7 days</mat-button-toggle>
          <mat-button-toggle value="all">All</mat-button-toggle>
        </mat-button-toggle-group>
      </div>

      @if (result(); as r) {
        <div class="totals" aria-label="Totals">
          <div class="net"><span>Taken, less refunds</span><b class="num" data-testid="net">{{ money(r.net) }}</b>
            <small class="mute">{{ money(r.received) }} in · {{ money(r.refundedTotal) }} refunded</small></div>
          @for (m of r.byMethod; track m.method) {
            <div class="by" [attr.data-method]="m.method"><span>{{ methodLabel(m.method) }} · {{ m.count }}</span><b class="num">{{ money(m.net) }}</b>
              @if (m.refunded) { <small class="mute">{{ money(m.refunded) }} refunded</small> }</div>
          }
        </div>
      }

      <div class="panel">
        <table class="data">
          <thead><tr><th>Payment</th><th>Guest</th><th class="hide-sm">Taken</th><th class="right">Amount</th></tr></thead>
          <tbody>
            @for (p of result()?.items ?? []; track p.id) {
              <tr class="clickable" (click)="open(p.id)" tabindex="0" (keydown.enter)="open(p.id)" [attr.data-payment]="p.number">
                <td><b class="num">{{ p.number }}</b> <span [class]="'chip ' + statusClass(p.status)">{{ statusLabel(p.status) }}</span>
                  <br /><small class="mute">{{ methodLabel(p.method) }}@if (p.paymentReference) { · {{ p.paymentReference }} }</small></td>
                <td><b>{{ p.guestName }}</b><br /><small class="mute num">{{ p.reference }}@if (p.folios) { · {{ p.folios }} }
                  @if (p.onAccount > 0) { · {{ money(p.onAccount) }} on account }</small></td>
                <td class="hide-sm">{{ time(p.receivedAt) }}<br /><small class="mute">{{ p.receivedByName ?? '' }}</small></td>
                <td class="right num"><b [class.void]="p.status === 'CANCELLED'">{{ money(p.amount) }}</b>
                  @if (p.refunded) { <br /><small class="bad">− {{ money(p.refunded) }} refunded</small> }</td>
              </tr>
            } @empty {
              <tr><td colspan="4" class="empty">
                @if (loading()) { Loading… }
                @else if (error()) { <span class="err" role="alert">{{ error() }}</span> }
                @else if (q()) { No payment matches “{{ q() }}”. }
                @else { No payments {{ periodText() }}. Payments are taken from a guest's bill (Folios → the bill → Take payment). }
              </td></tr>
            }
          </tbody>
        </table>
        @if (result(); as r) {
          @if (r.total > r.pageSize) {
            <div class="pager">
              <span class="mute">{{ (r.page - 1) * r.pageSize + 1 }}–{{ (r.page - 1) * r.pageSize + r.items.length }} of {{ r.total }}</span>
              <button mat-button [disabled]="r.page === 1" (click)="go(r.page - 1)">Previous</button>
              <button mat-button [disabled]="r.page * r.pageSize >= r.total" (click)="go(r.page + 1)">Next</button>
            </div>
          }
        }
      </div>
    </div>
  `,
  styles: `
    .wide { max-width: 1100px; }
    .filters { display: flex; align-items: center; gap: 12px 16px; flex-wrap: wrap; margin-bottom: 8px; }
    .q { flex: 1; min-width: 220px; max-width: 420px; }
    .m { width: 170px; }
    .totals { display: flex; gap: 12px; flex-wrap: wrap; margin-bottom: 12px; }
    .totals > div { display: flex; flex-direction: column; background: #fff; border: 1px solid #d9e0de; border-radius: 10px; padding: 10px 14px; min-width: 130px; }
    .totals span { color: #5b6b6e; font-size: .8rem; }
    .totals b { font-size: 1.15rem; color: #0e3b43; }
    .totals .net b { font-size: 1.4rem; }
    .mute { color: #5b6b6e; }
    .bad { color: #b3261e; }
    .right { text-align: right; }
    .void { text-decoration: line-through; color: #7a8a8c; }
    .chip.ok { background: #e1f2e8; color: #1d5b36; }
    .chip.back { background: #f6e3ec; color: #8a2449; }
    .chip.off { background: #eceff0; color: #5b6b6e; }
    .chip.wait { background: #fbefd8; color: #8a5a00; }
    .empty { color: #5b6b6e; padding: 20px 16px; }
    .err { color: #b3261e; }
    .pager { display: flex; align-items: center; justify-content: flex-end; gap: 8px; padding: 8px 12px; border-top: 1px solid #edf1f0; }
    @media (max-width: 760px) { .m { width: 100%; } .totals > div { flex: 1; } }
  `,
})
export class PaymentsPage {
  private api = inject(PaymentsApi);
  private props = inject(PropertyContext);
  private router = inject(Router);

  q = signal('');
  method = signal<PaymentMethod | ''>('');
  period = signal<Period>('today');
  page = signal(1);
  result = signal<PaymentList | null>(null);
  loading = signal(true);
  error = signal('');

  methods = ALL_METHODS;
  methodLabel = methodLabel;
  statusLabel = paymentStatusLabel;
  statusClass = paymentStatusClass;
  money = rupeesExact;

  private query$ = new Subject<void>();

  constructor() {
    this.query$.pipe(
      debounceTime(200),
      tap(() => { this.loading.set(true); this.error.set(''); }),
      switchMap(() => this.api.list(this.props.current()!.id, { q: this.q().trim(), method: this.method(), ...this.range(), page: this.page(), pageSize: 50 })),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe({
      next: r => { this.result.set(r); this.loading.set(false); },
      error: e => { this.error.set(toProblem(e).detail); this.loading.set(false); },
    });
    effect(() => { if (this.props.current()) this.query$.next(); });
  }

  /** The days asked for, at the property (a search looks at every day). */
  private range(): { from?: string; to?: string } {
    const today = this.props.current()!.localDate;
    if (this.q().trim() || this.period() === 'all') return {};
    if (this.period() === 'yesterday') return { from: addDays(today, -1), to: addDays(today, -1) };
    if (this.period() === 'week') return { from: addDays(today, -6), to: today };
    return { from: today, to: today };
  }

  periodText() {
    return { today: 'today', yesterday: 'yesterday', week: 'in the last 7 days', all: 'yet' }[this.period()];
  }

  time(iso: string) {
    return inZone(iso, this.props.current()!.timezone, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false });
  }

  setQ(q: string) { this.q.set(q); this.page.set(1); this.result.set(null); this.query$.next(); }
  setMethod(m: PaymentMethod | '') { this.method.set(m); this.page.set(1); this.query$.next(); }
  setPeriod(p: Period) { this.period.set(p); this.page.set(1); this.query$.next(); }
  go(page: number) { this.page.set(page); this.query$.next(); }
  open(id: string) { this.router.navigate(['/payments', id]); }
}
