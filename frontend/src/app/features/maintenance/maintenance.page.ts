import { Component, computed, effect, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { TicketSummary } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { MaintenanceApi, categoryLabel, priorityLabel, ticketStatusClass, ticketStatusLabel } from './maintenance.api';

type ListFilter = 'OPEN' | 'mine' | 'RESOLVED' | 'ALL';

/**
 * Maintenance tickets (Sprint 13, brief §54): what is broken, where, how urgent, and who has it. Urgent and high first.
 * Someone who can only report problems sees the ones they reported.
 */
@Component({
  selector: 'app-maintenance',
  imports: [RouterLink, MatButtonModule, MatIconModule],
  template: `
    <div class="page mt">
      <div class="head">
        <div>
          <h1>Maintenance</h1>
          <p class="mute">@if (canReadAll()) { Every problem reported at this property. } @else { Problems you reported. }</p>
        </div>
        <a mat-flat-button routerLink="/maintenance/new"><mat-icon>add</mat-icon>Report a problem</a>
      </div>

      <div class="filters" role="tablist" aria-label="Which tickets">
        @for (f of filters(); track f.key) {
          <button role="tab" [attr.aria-selected]="filter() === f.key" [class.on]="filter() === f.key" (click)="filter.set(f.key)">{{ f.label }}</button>
        }
      </div>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

      <div class="list">
        @for (t of tickets(); track t.id) {
          <a class="row" [routerLink]="['/maintenance', t.id]" [attr.data-ticket]="t.number">
            <div class="top">
              <b>{{ t.number }}</b>
              <span [class]="'pri ' + t.priority.toLowerCase()">{{ pri(t.priority) }}</span>
              <span [class]="'st ' + stCls(t.status)">{{ st(t.status) }}</span>
            </div>
            <div class="title">{{ t.title }}</div>
            <div class="meta">
              {{ t.place }} · {{ cat(t.category) }}
              @if (t.outOfOrder) { · <b class="ooo">Out of order@if (t.expectedReadyOn) { until {{ day(t.expectedReadyOn) }} }</b> }
              @if (t.photos) { · <mat-icon inline>photo_camera</mat-icon>{{ t.photos }} }
            </div>
            <div class="meta">{{ t.assignedToName ? 'With ' + t.assignedToName : 'Not assigned' }} · reported {{ when(t.createdAt) }}@if (t.reportedByName) { by {{ t.reportedByName }} }</div>
          </a>
        } @empty {
          @if (loaded()) { <p class="empty">@if (filter() === 'OPEN') { Nothing broken right now. } @else { No tickets here. }</p> }
          @else if (!error()) { <p class="empty">Loading…</p> }
        }
      </div>
    </div>
  `,
  styles: `
    .mt { max-width: 900px; }
    .head { display: flex; justify-content: space-between; align-items: flex-start; gap: 8px; flex-wrap: wrap; margin-bottom: 12px; }
    .head p { margin: 2px 0 0; }
    .mute { color: #5b6b6e; }
    .filters { display: flex; gap: 6px; overflow-x: auto; margin-bottom: 10px; scrollbar-width: none; }
    .filters button { flex: 0 0 auto; border: 1px solid #d9e0de; background: #fff; border-radius: 999px; padding: 8px 14px; font: inherit;
                      min-height: 40px; color: #1b2a2e; cursor: pointer; }
    .filters button.on { background: #0e3b43; color: #fff; border-color: #0e3b43; }
    .list { display: flex; flex-direction: column; gap: 8px; }
    .row { display: block; background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 12px 14px; color: inherit; text-decoration: none; }
    .row:hover { border-color: #8fa5a1; }
    .top { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .title { font-size: 1.05rem; margin: 4px 0 2px; }
    .meta { color: #5b6b6e; font-size: .88rem; }
    .pri, .st { font-size: .75rem; font-weight: 600; padding: 2px 8px; border-radius: 999px; background: #eef2f1; }
    .pri.urgent { background: #b3261e; color: #fff; } .pri.high { background: #fbe4e2; color: #8c1d18; }
    .st.resolved, .st.closed { background: #e1f2e8; color: #1d5b36; } .st.on-hold { background: #fbefd8; color: #8a5a00; }
    .st.in-progress, .st.assigned { background: #e0eff5; color: #134e63; }
    .ooo { color: #5b3a00; }
    .empty { color: #5b6b6e; }
    .err { color: #b3261e; }
  `,
})
export class MaintenancePage {
  private api = inject(MaintenanceApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private pid = computed(() => this.ctx.current()?.id ?? '');
  canReadAll = computed(() => this.auth.can('maintenance.read', this.pid()));
  filter = signal<ListFilter>('OPEN');
  tickets = signal<TicketSummary[]>([]);
  loaded = signal(false);
  error = signal('');
  pri = priorityLabel;
  st = ticketStatusLabel;
  stCls = ticketStatusClass;
  cat = categoryLabel;
  filters = computed(() => [
    { key: 'OPEN' as const, label: 'Open' },
    ...(this.canReadAll() ? [{ key: 'mine' as const, label: 'Mine' }] : []),
    { key: 'RESOLVED' as const, label: 'Resolved' },
    { key: 'ALL' as const, label: 'All' },
  ]);

  constructor() {
    effect(() => {
      const pid = this.pid(), f = this.filter();
      if (!pid) return;
      this.loaded.set(false);
      this.api.list(pid, f === 'mine' ? { status: 'OPEN', mine: true } : { status: f }).subscribe({
        next: list => { this.tickets.set(list); this.loaded.set(true); this.error.set(''); },
        error: e => this.error.set(toProblem(e).detail),
      });
    });
  }

  when(iso: string) { return inZone(iso, this.ctx.current()?.timezone ?? 'Asia/Kolkata', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false }); }
  day(iso: string) { return inZone(iso + 'T00:00:00Z', 'UTC', { day: 'numeric', month: 'short' }); }
}
