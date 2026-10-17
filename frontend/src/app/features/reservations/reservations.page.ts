import { Component, DestroyRef, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Subject, debounceTime, switchMap, tap } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { toProblem } from '../../core/http';
import { Paged, ReservationListItem } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { CanDirective } from '../../shared/can.directive';
import { shortDate } from '../rooms/rooms.api';
import { rupees } from '../settings/rates/rates.api';
import { guestsText, statusClass, statusLabel } from './reservations.api';
import { ReservationsApi } from './reservations.api';

type View = 'upcoming' | 'enquiries' | 'all';
const VIEW_STATUS: Record<View, string | undefined> = {
  upcoming: 'PENDING,CONFIRMED,CHECKED_IN',
  enquiries: 'INQUIRY',
  all: undefined,
};

/** Reservations at the current property: stays that haven't ended yet, soonest arrival first. Search by guest or reference. */
@Component({
  selector: 'app-reservations',
  imports: [FormsModule, RouterLink, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatInputModule, CanDirective],
  template: `
    <div class="page">
      <div class="page-head">
        <div><h1>Reservations</h1><p>Stays that haven't ended yet, soonest arrival first. Search by guest name, phone or booking reference.</p></div>
        <div class="head-actions">
          <a mat-stroked-button routerLink="/reservations/calendar"><mat-icon>calendar_month</mat-icon>Calendar</a>
          <a mat-flat-button routerLink="/reservations/new" *appCan="'reservation.create'"><mat-icon>add</mat-icon>New booking</a>
        </div>
      </div>

      <div class="filters">
        <mat-form-field appearance="outline" class="q">
          <mat-icon matPrefix>search</mat-icon>
          <mat-label>Search</mat-label>
          <input matInput [ngModel]="q()" (ngModelChange)="setQ($event)" placeholder="e.g. Iyer, 98200 or R00012" autocomplete="off" />
        </mat-form-field>
        <mat-button-toggle-group [ngModel]="view()" (ngModelChange)="setView($event)" aria-label="Which bookings">
          <mat-button-toggle value="upcoming">Booked</mat-button-toggle>
          <mat-button-toggle value="enquiries">Enquiries</mat-button-toggle>
          <mat-button-toggle value="all">All</mat-button-toggle>
        </mat-button-toggle-group>
      </div>

      <div class="panel">
        <table class="data">
          <thead><tr><th>Guest</th><th>Stay</th><th class="hide-sm">Rooms</th><th class="hide-sm">Source</th><th class="right">Total</th></tr></thead>
          <tbody>
            @for (r of result()?.items ?? []; track r.id) {
              <tr class="clickable" (click)="open(r.id)" tabindex="0" (keydown.enter)="open(r.id)">
                <td>
                  <b>{{ r.guestName }}</b> <span [class]="'chip ' + statusClass(r.status)">{{ statusLabel(r.status) }}</span>
                  <br /><small class="mute num">{{ r.reference }} · {{ guestsText(r.adults, r.children) }}</small>
                </td>
                <td class="num">{{ shortDate(r.checkIn) }} – {{ shortDate(r.checkOut) }}<br /><small class="mute">{{ r.nights }} night{{ r.nights === 1 ? '' : 's' }}</small></td>
                <td class="hide-sm">{{ r.rooms }}</td>
                <td class="hide-sm">{{ r.sourceName }}</td>
                <td class="right num">{{ rupees(r.totalAmount) }}</td>
              </tr>
            } @empty {
              <tr><td colspan="5" class="empty">
                @if (loading()) { Loading… }
                @else if (error()) { <span class="err" role="alert">{{ error() }}</span> }
                @else if (q()) { No booking matches “{{ q() }}”. }
                @else { No bookings yet. <a routerLink="/reservations/new" *appCan="'reservation.create'">Make the first one</a> }
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
    .head-actions { display: flex; gap: 8px; flex-wrap: wrap; }
    .filters { display: flex; align-items: center; gap: 16px; flex-wrap: wrap; margin-bottom: 8px; }
    .q { flex: 1; min-width: 240px; max-width: 460px; }
    .mute { color: #5b6b6e; }
    .right { text-align: right; }
    .chip.ok { background: #e1f2e8; color: #1d5b36; }
    .chip.wait { background: #fbefd8; color: #8a5a00; }
    .empty { color: #5b6b6e; padding: 20px 16px; }
    .err { color: #b3261e; }
    .pager { display: flex; align-items: center; justify-content: flex-end; gap: 8px; padding: 8px 12px; border-top: 1px solid #edf1f0; }
  `,
})
export class ReservationsPage {
  private api = inject(ReservationsApi);
  private props = inject(PropertyContext);
  private router = inject(Router);

  q = signal('');
  view = signal<View>('upcoming');
  page = signal(1);
  result = signal<Paged<ReservationListItem> | null>(null);
  loading = signal(true);
  error = signal('');

  shortDate = shortDate;
  rupees = rupees;
  statusLabel = statusLabel;
  statusClass = statusClass;
  guestsText = guestsText;

  private query$ = new Subject<void>();

  constructor() {
    this.query$.pipe(
      debounceTime(200),
      tap(() => { this.loading.set(true); this.error.set(''); }),
      switchMap(() => this.api.list(this.props.current()!.id,
        { q: this.q().trim(), status: VIEW_STATUS[this.view()], page: this.page(), pageSize: 50 })),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe({
      next: r => { this.result.set(r); this.loading.set(false); },
      error: e => { this.error.set(toProblem(e).detail); this.loading.set(false); },
    });
    // reload when the property changes
    effect(() => { if (this.props.current()) this.query$.next(); });
  }

  setQ(q: string) { this.q.set(q); this.page.set(1); this.result.set(null); this.query$.next(); }
  setView(v: View) { this.view.set(v); this.page.set(1); this.query$.next(); }
  go(page: number) { this.page.set(page); this.query$.next(); }
  open(id: string) { this.router.navigateByUrl(`/reservations/${id}`); }
}
