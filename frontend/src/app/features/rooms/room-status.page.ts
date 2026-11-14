import { MatDatepickerModule } from '@angular/material/datepicker';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { toProblem } from '../../core/http';
import { RoomStatusBoard } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { housekeepingLabel } from '../front-desk/front-desk.api';
import { RoomsApi, addDays, shortDate } from './rooms.api';

/** Read-only board of every room on one night: blocks, and for today each room's cleaning state and open repairs (Sprint 13). */
@Component({
  selector: 'app-room-status',
  imports: [MatDatepickerModule, RouterLink, MatButtonModule, MatIconModule, MatTooltipModule],
  template: `
    <div class="page wide">
      <div class="page-head">
        <div>
          <a routerLink="/rooms" class="back">Rooms</a>
          <h1>Room status</h1>
          <p>Night of {{ longDate() }}.@if (date() === today()) { Cleaning state is today's; <a routerLink="/housekeeping">open housekeeping</a>. }</p>
        </div>
        <div class="nav">
          <button mat-icon-button (click)="shift(-1)" aria-label="Previous night"><mat-icon>chevron_left</mat-icon></button>
          <input [matDatepicker]="dp19" [value]="date()" (dateChange)="date.set($event.value || today())" aria-label="Date" /><mat-datepicker-toggle [for]="dp19" /><mat-datepicker #dp19 />
          <button mat-icon-button (click)="shift(1)" aria-label="Next night"><mat-icon>chevron_right</mat-icon></button>
          @if (date() !== today()) { <button mat-button (click)="date.set(today())">Today</button> }
        </div>
      </div>

      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      <div class="legend">
        <span><i class="key AVAILABLE" aria-hidden="true"></i>Available</span><span><i class="key BLOCKED" aria-hidden="true"></i>Blocked</span>
        <span><i class="key PARTLY_BLOCKED" aria-hidden="true"></i>Some beds blocked</span><span><i class="key INACTIVE" aria-hidden="true"></i>Not in use</span>
      </div>
      <div class="grid">
        @for (r of board()?.rooms ?? []; track r.unitId) {
          <a class="tile" [class]="'tile ' + r.status" [routerLink]="['/rooms', r.unitId]">
            <div class="top"><b class="code">{{ r.code }}</b><span class="type">{{ r.typeName }}</span></div>
            <div class="state">
              @switch (r.status) {
                @case ('BLOCKED') { Blocked }
                @case ('PARTLY_BLOCKED') { {{ r.blockedBeds }} of {{ r.activeBeds }} beds blocked }
                @case ('INACTIVE') { Not in use }
                @default { {{ r.inventoryMode === 'BED' ? r.activeBeds + ' beds available' : 'Available' }} }
              }
            </div>
            @if (r.housekeeping; as hk) { <div [class]="'hk ' + hk">{{ hkLabel(hk) }}</div> }
            @if (r.openRepairs) { <div class="repairs"><mat-icon inline>build</mat-icon>{{ r.openRepairs }} open repair{{ r.openRepairs === 1 ? '' : 's' }}</div> }
            @if (r.block; as b) {
              <div class="why" [matTooltip]="b.reason">{{ b.reason }}</div>
              <div class="until">Free from {{ shortDate(b.endDate) }}</div>
            }
            @if (r.inventoryMode === 'BED' && r.beds.length) {
              <div class="beds">
                @for (bed of r.beds; track bed.bedId) { <i class="dot" [class]="'dot ' + bed.status" [matTooltip]="bed.code + ': ' + bed.status"></i> }
              </div>
            }
          </a>
        } @empty {
          @if (board()) { <p class="mute">No rooms set up yet. <a routerLink="/rooms">Add rooms</a>.</p> }
        }
      </div>
    </div>
  `,
  styles: `
    .wide { max-width: 1200px; }
    .back { color: var(--ro-mute); text-decoration: none; font-size: 0.9rem; } .back::before { content: '‹ '; }
    .nav { display: flex; align-items: center; gap: var(--space-1); }
    .nav input { font: inherit; padding: 6px var(--space-2); border: 1px solid var(--ro-line); border-radius: 6px; background: var(--ro-paper); }
    .legend { display: flex; gap: var(--space-4); flex-wrap: wrap; color: var(--ro-mute); font-size: .85rem; margin-bottom: var(--space-3); }
    .legend span { display: inline-flex; align-items: center; gap: 6px; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(170px, 1fr)); gap: var(--space-3); }
    .tile { display: flex; flex-direction: column; gap: var(--space-1); min-height: 104px; padding: var(--space-3) 14px; border-radius: var(--ro-radius);
            background: var(--ro-paper); border: 1px solid var(--ro-line); border-left-width: 5px; color: var(--ro-ink); text-decoration: none; }
    .tile:hover { box-shadow: 0 1px 6px var(--ro-shadow); }
    .tile.AVAILABLE { border-left-color: var(--ro-ok-mid); }
    .tile.BLOCKED { border-left-color: var(--ro-rust); background: var(--ro-warn-wash); }
    .tile.PARTLY_BLOCKED { border-left-color: var(--ro-warn-bright); }
    .tile.INACTIVE { border-left-color: var(--ro-outline); color: var(--ro-mute); background: var(--ro-wash); }
    .top { display: flex; justify-content: space-between; align-items: baseline; gap: var(--space-2); }
    .code { font-size: 1.35rem; letter-spacing: -0.01em; }
    .type { font-size: .78rem; color: var(--ro-mute); text-align: right; }
    .state { font-weight: 500; }
    .until { font-size: .8rem; color: var(--ro-mute); }
    .hk { display: inline-block; font-size: .75rem; font-weight: 600; padding: 1px var(--space-2); border-radius: 999px; margin-top: var(--space-1); background: var(--ro-sunk); }
    .hk.DIRTY { background: var(--ro-off-bg); color: var(--ro-error-strong); } .hk.CLEANING { background: var(--ro-warn-bg); color: var(--ro-warn); }
    .hk.CLEAN, .hk.INSPECTION { background: var(--ro-info-bg); color: var(--ro-info); } .hk.READY { background: var(--ro-ok-bg); color: var(--ro-ok); }
    .hk.OUT_OF_ORDER { background: var(--ro-line-mid); color: var(--ro-ink-soft); }
    .repairs { font-size: .8rem; color: var(--ro-warn-strong); margin-top: 2px; }
    .why { font-size: .8rem; color: var(--ro-warn-strong); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .beds { display: flex; flex-wrap: wrap; gap: var(--space-1); margin-top: auto; }
    .dot { display: inline-block; width: 10px; height: 10px; border-radius: 50%; background: var(--ro-ok-mid); }
    .dot.BLOCKED { background: var(--ro-rust); } .dot.PARTLY_BLOCKED { background: var(--ro-warn-bright); } .dot.INACTIVE { background: var(--ro-faint); }
    .key { display: inline-block; width: 4px; height: 14px; border-radius: 2px; background: var(--ro-ok-mid); }
    .key.BLOCKED { background: var(--ro-rust); } .key.PARTLY_BLOCKED { background: var(--ro-warn-bright); } .key.INACTIVE { background: var(--ro-faint); }
    .err { color: var(--ro-error); } .mute { color: var(--ro-mute); }
    @media (max-width: 640px) { .tile { min-height: 0; } .grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } .code { font-size: 1.15rem; } }
  `,
})
export class RoomStatusPage {
  private api = inject(RoomsApi);
  private ctx = inject(PropertyContext);
  shortDate = shortDate;
  hkLabel = housekeepingLabel;

  today = computed(() => this.ctx.current()?.localDate ?? '');
  date = signal('');
  board = signal<RoomStatusBoard | null>(null);
  error = signal('');
  longDate = computed(() => this.date()
    ? inZone(this.date() + 'T00:00:00Z', 'UTC', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }) : '');

  constructor() {
    effect(() => { if (!this.date() && this.today()) this.date.set(this.today()); });
    effect(() => {
      const pid = this.ctx.current()?.id, d = this.date();
      if (!pid || !d) return;
      this.api.status(pid, d).subscribe({
        next: b => { this.board.set(b); this.error.set(''); },
        error: e => this.error.set(toProblem(e).detail),
      });
    });
  }

  shift(days: number) { this.date.set(addDays(this.date(), days)); }
}
