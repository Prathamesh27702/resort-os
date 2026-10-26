import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { toProblem } from '../../core/http';
import { OrderList } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { rupees } from '../settings/rates/rates.api';
import { RestaurantApi } from './restaurant.api';

/** One day's restaurant orders (Sprint 14): open, settled (paid or charged to a room) and voided, with the day's totals. */
@Component({
  selector: 'app-restaurant-orders',
  imports: [FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule],
  template: `
    <div class="page list">
      <a mat-button routerLink="/restaurant" class="back"><mat-icon>arrow_back</mat-icon>Floor</a>
      <h1>Restaurant orders</h1>
      <div class="bar">
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Day</mat-label>
          <input matInput type="date" [ngModel]="date()" (ngModelChange)="date.set($event || null)" />
        </mat-form-field>
        <div class="filters" role="tablist" aria-label="Which orders">
          @for (s of statuses; track s.value) {
            <button role="tab" [class.on]="status() === s.value" [attr.aria-selected]="status() === s.value" (click)="status.set(s.value)">{{ s.label }}</button>
          }
        </div>
      </div>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      @if (list(); as l) {
        <p class="totals">Settled <b>{{ rupees(l.settledTotal) }}</b> · still open <b>{{ rupees(l.openTotal) }}</b></p>
        <ul>
          @for (o of l.items; track o.id) {
            <li>
              <a [routerLink]="['/restaurant/orders', o.id]" [attr.data-order]="o.number">
                <b>{{ o.number }}</b>
                <span>{{ o.where }}@if (o.guestName) { · {{ o.guestName }} }</span>
                <span class="amt">{{ rupees(o.total) }}</span>
                <small [class]="'st ' + o.status">{{ state(o.status, o.settlement) }}</small>
                <small class="mute">{{ o.lines }} line{{ o.lines === 1 ? '' : 's' }} · {{ time(o.createdAt) }}@if (o.openedByName) { · {{ o.openedByName }} }</small>
              </a>
            </li>
          } @empty { <li class="mute">No orders on this day.</li> }
        </ul>
      } @else if (!error()) { <p class="mute">Loading…</p> }
    </div>
  `,
  styles: `
    .list { max-width: 760px; }
    .back { margin-left: -8px; }
    .bar { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
    .filters { display: flex; gap: 6px; overflow-x: auto; scrollbar-width: none; }
    .filters button { flex: 0 0 auto; border: 1px solid #d9e0de; background: #fff; border-radius: 999px; padding: 8px 14px; font: inherit; min-height: 40px; cursor: pointer; }
    .filters button.on { background: #0e3b43; color: #fff; border-color: #0e3b43; }
    .totals { margin: 12px 0 8px; }
    .mute { color: #5b6b6e; } .err { color: #b3261e; }
    ul { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 8px; }
    li a { display: grid; grid-template-columns: auto 1fr auto; gap: 2px 10px; background: #fff; border: 1px solid #d9e0de; border-radius: 12px;
           padding: 12px; color: inherit; text-decoration: none; }
    .amt { font-weight: 600; text-align: right; }
    small { grid-column: 1 / -1; }
    .st { font-weight: 600; color: #8a5a00; } .st.SETTLED { color: #1d5b36; } .st.VOIDED { color: #5b6b6e; }
  `,
})
export class OrdersPage {
  private api = inject(RestaurantApi);
  private ctx = inject(PropertyContext);
  list = signal<OrderList | null>(null);
  error = signal('');
  date = signal<string | null>(null);
  status = signal('ALL');
  rupees = rupees;
  statuses = [{ value: 'ALL', label: 'All' }, { value: 'OPEN', label: 'Open' }, { value: 'SETTLED', label: 'Settled' }, { value: 'VOIDED', label: 'Voided' }];
  private pid = computed(() => this.ctx.current()?.id ?? '');

  constructor() {
    effect(() => {
      if (!this.pid()) return;
      this.api.orders(this.pid(), this.status(), this.date()).subscribe({
        next: l => { this.list.set(l); this.error.set(''); },
        error: e => this.error.set(toProblem(e).detail),
      });
    });
  }

  state(s: string, how: string | null) {
    if (s === 'OPEN') return 'Open';
    if (s === 'VOIDED') return 'Voided';
    return how === 'ROOM_CHARGE' ? 'Charged to room' : 'Paid';
  }
  time(iso: string) { return inZone(iso, this.ctx.current()?.timezone ?? 'Asia/Kolkata', { hour: '2-digit', minute: '2-digit', hour12: false }); }
}
