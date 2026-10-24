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
import { InvoiceListItem, InvoiceStatus, Paged } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { addDays, shortDate } from '../rooms/rooms.api';
import { rupeesExact } from '../settings/rates/rates.api';
import { InvoicesApi, STATUSES, documentLabel, invoiceStatusClass, invoiceStatusLabel } from './invoices.api';

type Period = 'today' | 'week' | 'month' | 'all';

/** GST invoices (Sprint 12): every invoice and bill of supply issued at the property, newest first, with what credit
 *  notes took off them. Search by invoice number, booking, folio, customer or GSTIN. */
@Component({
  selector: 'app-invoices',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule],
  template: `
    <div class="page wide">
      <div class="page-head">
        <div><h1>Invoices</h1><p>GST invoices issued at check-out or from a guest's bill. An invoice is never changed: it is corrected with a
          credit note, and charges after it go on a debit note.</p></div>
      </div>

      <div class="filters">
        <mat-form-field appearance="outline" class="q">
          <mat-icon matPrefix>search</mat-icon>
          <mat-label>Search</mat-label>
          <input matInput [ngModel]="q()" (ngModelChange)="setQ($event)" placeholder="e.g. BEACH/2627/00012, R00008, Acme or a GSTIN" autocomplete="off" />
        </mat-form-field>
        <mat-form-field appearance="outline" class="s">
          <mat-label>Status</mat-label>
          <mat-select [ngModel]="status()" (ngModelChange)="setStatus($event)">
            <mat-option value="">Any status</mat-option>
            @for (s of statuses; track s) { <mat-option [value]="s">{{ statusLabel(s) }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-button-toggle-group [ngModel]="period()" (ngModelChange)="setPeriod($event)" aria-label="When">
          <mat-button-toggle value="today">Today</mat-button-toggle>
          <mat-button-toggle value="week">7 days</mat-button-toggle>
          <mat-button-toggle value="month">30 days</mat-button-toggle>
          <mat-button-toggle value="all">All</mat-button-toggle>
        </mat-button-toggle-group>
      </div>

      <div class="panel">
        <table class="data">
          <thead><tr><th>Invoice</th><th>Billed to</th><th class="hide-sm">Booking</th><th class="right hide-sm">GST</th><th class="right">Total</th></tr></thead>
          <tbody>
            @for (i of result()?.items ?? []; track i.id) {
              <tr class="clickable" (click)="open(i.id)" tabindex="0" (keydown.enter)="open(i.id)" [attr.data-invoice]="i.number">
                <td><b class="num">{{ i.number }}</b><span [class]="'chip status ' + statusClass(i.status)">{{ statusLabel(i.status) }}</span>
                  <br /><small class="mute">{{ shortDate(i.invoiceDate) }}@if (i.documentType !== 'INVOICE') { · {{ docLabel(i.documentType) }} }
                  @if (i.creditNotes) { · {{ i.creditNotes }} credit note{{ i.creditNotes === 1 ? '' : 's' }} }
                  @if (i.debitNotes) { · {{ i.debitNotes }} debit note{{ i.debitNotes === 1 ? '' : 's' }} }</small></td>
                <td><b>{{ i.customerName }}</b><br /><small class="mute num">{{ i.customerGstin ?? 'B2C' }}</small></td>
                <td class="hide-sm num">{{ i.reference }}<br /><small class="mute">{{ i.folioNumber }}</small></td>
                <td class="right num hide-sm">{{ money(i.taxTotal) }}</td>
                <td class="right num"><b [class.void]="i.status === 'CANCELLED'">{{ money(i.total) }}</b>
                  @if (i.credited) { <br /><small class="bad">− {{ money(i.credited) }} credited</small> }</td>
              </tr>
            } @empty {
              <tr><td colspan="5" class="empty">
                @if (loading()) { Loading… }
                @else if (error()) { <span class="err" role="alert">{{ error() }}</span> }
                @else if (q()) { No invoice matches “{{ q() }}”. }
                @else { No invoices {{ periodText() }}. An invoice is issued when a guest checks out, or from the bill (Folios → the bill → Issue invoice). }
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
    .q { flex: 1; min-width: 220px; max-width: 460px; }
    .s { width: 180px; }
    .mute { color: #5b6b6e; }
    .bad { color: #b3261e; }
    .right { text-align: right; }
    .void { text-decoration: line-through; color: #7a8a8c; }
    .chip.status { margin-left: 8px; }
    .chip.ok { background: #e1f2e8; color: #1d5b36; }
    .chip.back { background: #f6e3ec; color: #8a2449; }
    .chip.off { background: #eceff0; color: #5b6b6e; }
    .chip.wait { background: #fbefd8; color: #8a5a00; }
    .empty { color: #5b6b6e; padding: 20px 16px; }
    .err { color: #b3261e; }
    .pager { display: flex; align-items: center; justify-content: flex-end; gap: 8px; padding: 8px 12px; border-top: 1px solid #edf1f0; }
    @media (max-width: 760px) { .s { width: 100%; } }
  `,
})
export class InvoicesPage {
  private api = inject(InvoicesApi);
  private props = inject(PropertyContext);
  private router = inject(Router);

  q = signal('');
  status = signal<InvoiceStatus | ''>('');
  period = signal<Period>('month');
  page = signal(1);
  result = signal<Paged<InvoiceListItem> | null>(null);
  loading = signal(true);
  error = signal('');

  statuses = STATUSES;
  statusLabel = invoiceStatusLabel;
  statusClass = invoiceStatusClass;
  docLabel = documentLabel;
  money = rupeesExact;
  shortDate = shortDate;

  private query$ = new Subject<void>();

  constructor() {
    this.query$.pipe(
      debounceTime(200),
      tap(() => { this.loading.set(true); this.error.set(''); }),
      switchMap(() => this.api.list(this.props.current()!.id, { q: this.q().trim(), status: this.status(), ...this.range(), page: this.page(), pageSize: 50 })),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe({
      next: r => { this.result.set(r); this.loading.set(false); },
      error: e => { this.error.set(toProblem(e).detail); this.loading.set(false); },
    });
    effect(() => { if (this.props.current()) this.query$.next(); });
  }

  /** The invoice days asked for, at the property (a search looks at every day). */
  private range(): { from?: string; to?: string } {
    const today = this.props.current()!.localDate;
    if (this.q().trim() || this.period() === 'all') return {};
    if (this.period() === 'week') return { from: addDays(today, -6), to: today };
    if (this.period() === 'month') return { from: addDays(today, -29), to: today };
    return { from: today, to: today };
  }

  periodText() { return { today: 'today', week: 'in the last 7 days', month: 'in the last 30 days', all: 'yet' }[this.period()]; }

  setQ(q: string) { this.q.set(q); this.page.set(1); this.result.set(null); this.query$.next(); }
  setStatus(s: InvoiceStatus | '') { this.status.set(s); this.page.set(1); this.query$.next(); }
  setPeriod(p: Period) { this.period.set(p); this.page.set(1); this.query$.next(); }
  go(page: number) { this.page.set(page); this.query$.next(); }
  open(id: string) { this.router.navigate(['/invoices', id]); }
}
