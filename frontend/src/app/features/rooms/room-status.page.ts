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
  imports: [RouterLink, MatButtonModule, MatIconModule, MatTooltipModule],
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
          <input type="date" [value]="date()" (change)="date.set($any($event.target).value || today())" aria-label="Date" />
          <button mat-icon-button (click)="shift(1)" aria-label="Next night"><mat-icon>chevron_right</mat-icon></button>
          @if (date() !== today()) { <button mat-button (click)="date.set(today())">Today</button> }
        </div>
      </div>

      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      <div class="legend">
        <span><i class="dot AVAILABLE"></i>Available</span><span><i class="dot BLOCKED"></i>Blocked</span>
        <span><i class="dot PARTLY_BLOCKED"></i>Some beds blocked</span><span><i class="dot INACTIVE"></i>Not in use</span>
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
    .back { color: #5b6b6e; text-decoration: none; font-size: 0.9rem; } .back::before { content: '‹ '; }
    .nav { display: flex; align-items: center; gap: 4px; }
    .nav input { font: inherit; padding: 6px 8px; border: 1px solid #d9e0de; border-radius: 6px; background: #fff; }
    .legend { display: flex; gap: 16px; flex-wrap: wrap; color: #5b6b6e; font-size: .85rem; margin-bottom: 12px; }
    .legend span { display: inline-flex; align-items: center; gap: 6px; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(170px, 1fr)); gap: 12px; }
    .tile { display: flex; flex-direction: column; gap: 4px; min-height: 104px; padding: 12px 14px; border-radius: 10px;
            background: #fff; border: 1px solid #d9e0de; border-left-width: 5px; color: #1b2a2e; text-decoration: none; }
    .tile:hover { box-shadow: 0 1px 6px rgba(14,59,67,.12); }
    .tile.AVAILABLE { border-left-color: #2e6b3c; }
    .tile.BLOCKED { border-left-color: #b45309; background: #fffaf0; }
    .tile.PARTLY_BLOCKED { border-left-color: #d29a2c; }
    .tile.INACTIVE { border-left-color: #a9b6b3; color: #5b6b6e; background: #f5f7f6; }
    .top { display: flex; justify-content: space-between; align-items: baseline; gap: 8px; }
    .code { font-size: 1.35rem; letter-spacing: -0.01em; }
    .type { font-size: .78rem; color: #5b6b6e; text-align: right; }
    .state { font-weight: 500; }
    .until { font-size: .8rem; color: #5b6b6e; }
    .hk { display: inline-block; font-size: .75rem; font-weight: 600; padding: 1px 8px; border-radius: 999px; margin-top: 4px; background: #eef2f1; }
    .hk.DIRTY { background: #fbe4e2; color: #8c1d18; } .hk.CLEANING { background: #fbefd8; color: #8a5a00; }
    .hk.CLEAN, .hk.INSPECTION { background: #e0eff5; color: #134e63; } .hk.READY { background: #e1f2e8; color: #1d5b36; }
    .hk.OUT_OF_ORDER { background: #e3e7e6; color: #33474a; }
    .repairs { font-size: .8rem; color: #5b3a00; margin-top: 2px; }
    .why { font-size: .8rem; color: #7a4a00; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .beds { display: flex; flex-wrap: wrap; gap: 4px; margin-top: auto; }
    .dot { display: inline-block; width: 10px; height: 10px; border-radius: 50%; background: #2e6b3c; }
    .dot.BLOCKED { background: #b45309; } .dot.PARTLY_BLOCKED { background: #d29a2c; } .dot.INACTIVE { background: #a9b6b3; }
    .err { color: #b3261e; } .mute { color: #5b6b6e; }
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
