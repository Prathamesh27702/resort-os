import { Component, DestroyRef, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Subject, debounceTime, distinctUntilChanged, switchMap, tap } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { CanDirective } from '../../shared/can.directive';
import { GuestListItem, Paged } from '../../core/models';
import { toProblem } from '../../core/http';
import { GuestsApi, countryName, formatPhone } from './guests.api';

/** Guest list and search. Typing a name, phone (any part, any format) or email searches as you type. */
@Component({
  selector: 'app-guests',
  imports: [FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSlideToggleModule, CanDirective],
  template: `
    <div class="page">
      <div class="page-head">
        <div><h1>Guests</h1><p>One profile per person, shared by all your properties. Search by name, phone or email.</p></div>
        <a mat-flat-button routerLink="/guests/new" *appCan="'guest.create'">Add guest</a>
      </div>

      <div class="search">
        <mat-form-field appearance="outline" class="q">
          <mat-icon matPrefix>search</mat-icon>
          <mat-label>Search guests</mat-label>
          <input matInput [ngModel]="q()" (ngModelChange)="search($event)" placeholder="e.g. 98200 or Iyer or riya@" autocomplete="off" />
        </mat-form-field>
        <mat-slide-toggle [ngModel]="includeInactive()" (ngModelChange)="toggleInactive($event)">Show merged and anonymised</mat-slide-toggle>
      </div>

      <div class="panel">
        <table class="data">
          <thead><tr><th>Name</th><th>Phone</th><th class="hide-sm">Email</th><th class="hide-sm">From</th></tr></thead>
          <tbody>
            @for (g of result()?.items ?? []; track g.id) {
              <tr class="clickable" (click)="open(g.id)" tabindex="0" (keydown.enter)="open(g.id)">
                <td>
                  <b>{{ g.fullName }}</b>
                  @if (g.isVip) { <span class="chip vip">VIP</span> }
                  @if (g.status === 'MERGED') { <span class="chip off">Merged</span> }
                  @if (g.status === 'ANONYMISED') { <span class="chip off">Anonymised</span> }
                  @if (g.companyName) { <br /><small class="mute">{{ g.companyName }}</small> }
                </td>
                <td class="num">{{ phone(g.phone) }}</td>
                <td class="hide-sm">{{ g.email }}</td>
                <td class="hide-sm">{{ from(g) }}</td>
              </tr>
            } @empty {
              <tr><td colspan="4" class="empty">
                @if (loading()) { Searching… }
                @else if (error()) { <span class="err" role="alert">{{ error() }}</span> }
                @else if (q()) { No guest matches “{{ q() }}”. <a routerLink="/guests/new" *appCan="'guest.create'">Add a new guest</a> }
                @else { No guests yet. Add the first one, or they will be added as bookings arrive. }
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
    .search { display: flex; align-items: center; gap: 16px; flex-wrap: wrap; }
    .q { flex: 1; min-width: 260px; max-width: 520px; }
    .mute { color: #5b6b6e; }
    .vip { background: #fbefd8; color: #8a5a00; margin-left: 6px; }
    .chip.off { margin-left: 6px; }
    .empty { color: #5b6b6e; padding: 20px 16px; }
    .err { color: #b3261e; }
    .pager { display: flex; align-items: center; justify-content: flex-end; gap: 8px; padding: 8px 12px; border-top: 1px solid #edf1f0; }
  `,
})
export class GuestsPage {
  private api = inject(GuestsApi);
  private router = inject(Router);

  q = signal('');
  includeInactive = signal(false);
  result = signal<Paged<GuestListItem> | null>(null);
  loading = signal(true);
  error = signal('');
  phone = formatPhone;

  private query$ = new Subject<{ q: string; page: number; includeInactive: boolean }>();

  constructor() {
    this.query$.pipe(
      debounceTime(250),
      distinctUntilChanged((a, b) => JSON.stringify(a) === JSON.stringify(b)),
      tap(() => { this.loading.set(true); this.error.set(''); }),
      switchMap(x => this.api.list(x.q, { page: x.page, includeInactive: x.includeInactive })),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe({
      next: r => { this.result.set(r); this.loading.set(false); },
      error: e => { this.error.set(toProblem(e).detail); this.loading.set(false); },
    });
    this.query$.next({ q: '', page: 1, includeInactive: false });
  }

  search(q: string) { this.q.set(q); this.result.set(null); this.query$.next({ q, page: 1, includeInactive: this.includeInactive() }); }
  toggleInactive(on: boolean) { this.includeInactive.set(on); this.query$.next({ q: this.q(), page: 1, includeInactive: on }); }
  go(page: number) { this.query$.next({ q: this.q(), page, includeInactive: this.includeInactive() }); }
  open(id: string) { this.router.navigateByUrl(`/guests/${id}`); }
  /** "Pune", "Berlin, Germany", "Germany" — the country only when it isn't India. */
  from(g: GuestListItem) {
    const country = g.nationality && g.nationality !== 'IN' ? countryName(g.nationality) : '';
    return [g.city, country].filter(Boolean).join(', ');
  }
}
