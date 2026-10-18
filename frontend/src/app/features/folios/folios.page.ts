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
import { toProblem } from '../../core/http';
import { FolioListItem, Paged } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { statusClass, statusLabel } from '../reservations/reservations.api';
import { shortDate } from '../rooms/rooms.api';
import { rupeesExact } from '../settings/rates/rates.api';
import { FolioShow, FoliosApi } from './folios.api';

/** Bills (Sprint 8): one per stay, more when a bill is split. Starts with the guests in house; search by folio number,
 *  booking reference or guest. */
@Component({
  selector: 'app-folios',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatInputModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div><h1>Folios</h1><p>The running bill of every stay. Search by folio number, booking reference, guest name or phone.</p></div>
      </div>

      <div class="filters">
        <mat-form-field appearance="outline" class="q">
          <mat-icon matPrefix>search</mat-icon>
          <mat-label>Search</mat-label>
          <input matInput [ngModel]="q()" (ngModelChange)="setQ($event)" placeholder="e.g. F00012, R00008 or Iyer" autocomplete="off" />
        </mat-form-field>
        <mat-button-toggle-group [ngModel]="show()" (ngModelChange)="setShow($event)" aria-label="Which folios">
          <mat-button-toggle value="in-house">In house</mat-button-toggle>
          <mat-button-toggle value="open">In house and arriving</mat-button-toggle>
          <mat-button-toggle value="all">All</mat-button-toggle>
        </mat-button-toggle-group>
      </div>

      <div class="panel">
        <table class="data">
          <thead><tr><th>Folio</th><th>Guest</th><th class="hide-sm">Stay</th><th class="right">Balance</th></tr></thead>
          <tbody>
            @for (f of result()?.items ?? []; track f.id) {
              <tr class="clickable" (click)="open(f.id)" tabindex="0" (keydown.enter)="open(f.id)" [attr.data-folio]="f.number">
                <td><b class="num">{{ f.number }}</b> @if (!f.isPrimary) { <span class="chip">{{ f.name }}</span> }
                  <br /><small class="mute">{{ f.lines }} line{{ f.lines === 1 ? '' : 's' }}</small></td>
                <td><b>{{ f.guestName }}</b> <span [class]="'chip ' + statusClass(f.reservationStatus)">{{ statusLabel(f.reservationStatus) }}</span>
                  <br /><small class="mute num">{{ f.reference }} · {{ f.rooms }}</small></td>
                <td class="hide-sm num">{{ shortDate(f.checkIn) }} – {{ shortDate(f.checkOut) }}</td>
                <td class="right num"><b [class.owed]="f.balance > 0">{{ rupees(f.balance) }}</b></td>
              </tr>
            } @empty {
              <tr><td colspan="4" class="empty">
                @if (loading()) { Loading… }
                @else if (error()) { <span class="err" role="alert">{{ error() }}</span> }
                @else if (q()) { No folio matches “{{ q() }}”. }
                @else if (show() === 'in-house') { Nobody is checked in, so there are no open bills. Choose All to see earlier ones. }
                @else { No folios yet. A folio opens when a booking is confirmed. }
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
    .filters { display: flex; align-items: center; gap: 16px; flex-wrap: wrap; margin-bottom: 8px; }
    .q { flex: 1; min-width: 240px; max-width: 460px; }
    .mute { color: #5b6b6e; }
    .right { text-align: right; }
    .owed { color: #b3261e; }
    .chip.ok { background: #e1f2e8; color: #1d5b36; }
    .chip.wait { background: #fbefd8; color: #8a5a00; }
    .empty { color: #5b6b6e; padding: 20px 16px; }
    .err { color: #b3261e; }
    .pager { display: flex; align-items: center; justify-content: flex-end; gap: 8px; padding: 8px 12px; border-top: 1px solid #edf1f0; }
  `,
})
export class FoliosPage {
  private api = inject(FoliosApi);
  private props = inject(PropertyContext);
  private router = inject(Router);

  q = signal('');
  show = signal<FolioShow>('in-house');
  page = signal(1);
  result = signal<Paged<FolioListItem> | null>(null);
  loading = signal(true);
  error = signal('');

  shortDate = shortDate;
  rupees = rupeesExact;
  statusLabel = statusLabel;
  statusClass = statusClass;

  private query$ = new Subject<void>();

  constructor() {
    this.query$.pipe(
      debounceTime(200),
      tap(() => { this.loading.set(true); this.error.set(''); }),
      switchMap(() => this.api.list(this.props.current()!.id, { q: this.q().trim(), show: this.show(), page: this.page(), pageSize: 50 })),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe({
      next: r => { this.result.set(r); this.loading.set(false); },
      error: e => { this.error.set(toProblem(e).detail); this.loading.set(false); },
    });
    effect(() => { if (this.props.current()) this.query$.next(); });
  }

  setQ(q: string) { this.q.set(q); this.page.set(1); this.result.set(null); this.query$.next(); }
  setShow(v: FolioShow) { this.show.set(v); this.page.set(1); this.query$.next(); }
  go(page: number) { this.page.set(page); this.query$.next(); }
  open(id: string) { this.router.navigate(['/folios', id]); }
}
