import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { HousekeepingBoard, HousekeepingRoom, HousekeepingTask } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { housekeepingLabel } from '../front-desk/front-desk.api';
import { BoardFilter, HousekeepingApi, filterRooms, primaryAction, statusClass, taskTypeLabel } from './housekeeping.api';
import { NewTaskDialog, TaskSheet, TaskSheetData } from './task-sheet';

/**
 * The housekeeping board (Sprint 13, brief §53), built for a phone held in one hand: one card per room or dorm bed, rooms
 * a guest arrives in today first, and one big button at the bottom of each card — Start, Done, or (for a supervisor)
 * Mark ready. Tap the card for its checklist, to assign it, send it back or report a problem.
 */
@Component({
  selector: 'app-housekeeping',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page hk">
      <div class="head">
        <div>
          <h1>Housekeeping</h1>
          <p class="mute">{{ longDate() }}@if (board()?.inspectionRequired) { · cleaned rooms wait for inspection }</p>
        </div>
        <div class="tools">
          @if (canAssign()) {
            <button mat-stroked-button (click)="newTask()"><mat-icon>add</mat-icon>Task</button>
            <a mat-button routerLink="/housekeeping/checklists">Checklists</a>
          }
          <button mat-icon-button (click)="load()" aria-label="Refresh"><mat-icon>refresh</mat-icon></button>
        </div>
      </div>

      @if (board(); as b) {
        <div class="filters" role="tablist" aria-label="Which rooms">
          <button role="tab" [attr.aria-selected]="filter() === 'mine'" [class.on]="filter() === 'mine'" (click)="filter.set('mine')">
            My rooms <b>{{ count('mine') }}</b></button>
          <button role="tab" [attr.aria-selected]="filter() === 'todo'" [class.on]="filter() === 'todo'" (click)="filter.set('todo')">
            Not ready <b>{{ count('todo') }}</b></button>
          <button role="tab" [attr.aria-selected]="filter() === 'inspect'" [class.on]="filter() === 'inspect'" (click)="filter.set('inspect')">
            To inspect <b>{{ b.summary.toInspect }}</b></button>
          <button role="tab" [attr.aria-selected]="filter() === 'all'" [class.on]="filter() === 'all'" (click)="filter.set('all')">
            All <b>{{ b.rooms.length }}</b></button>
        </div>
        @if (b.summary.rush) {
          <p class="rush-note"><mat-icon>bolt</mat-icon>{{ b.summary.rush }} room{{ b.summary.rush === 1 ? '' : 's' }} not ready for a guest arriving today — shown first.</p>
        }
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

      <div class="cards">
        @for (r of shown(); track r.place) {
          <article class="card" [class]="'card ' + cls(r.status)" [class.rush]="r.rush" [attr.data-room]="r.place">
            <button class="body" (click)="open(r)" [attr.aria-label]="r.place + ': details'">
              <div class="top">
                <b class="code">{{ r.bedCode ? r.unitCode + ' · ' + r.bedCode : r.unitCode }}</b>
                <span [class]="'state ' + cls(r.status)">{{ hk(r.status) }}</span>
              </div>
              <div class="meta">
                {{ r.typeName }}
                @if (r.rush) { · <b class="hot">Guest arrives today</b> }
                @else if (r.arrivalToday) { · Arrival today }
                @if (r.departureToday) { · {{ r.occupied ? 'Leaves today' : 'Guest left today' }} }
                @else if (r.occupied) { · Guest in room }
              </div>
              @if (r.task; as t) {
                <div class="task">
                  {{ typeLabel(t.taskType) }} · {{ t.assignedToName ?? 'Not assigned' }}
                  @if (t.checklist.length) { · {{ doneCount(t) }}/{{ t.checklist.length }} }
                  @if (t.failedInspections && t.status === 'OPEN' && t.inspectionNote) { <br /><span class="back">Sent back: {{ t.inspectionNote }}</span> }
                  @if (t.status === 'IN_PROGRESS' && t.startedAt) { <br /><span class="mute">Started {{ time(t.startedAt) }} by {{ t.startedByName }}</span> }
                  @if (t.status === 'CLEANED' && t.finishedAt) { <br /><span class="mute">Cleaned {{ time(t.finishedAt) }} by {{ t.finishedByName }}</span> }
                </div>
              }
              @if (r.repair; as rep) {
                <div class="repair"><mat-icon>build</mat-icon>{{ rep.number }} {{ rep.title }}
                  @if (rep.outOfOrder && rep.expectedReadyOn) { · until {{ day(rep.expectedReadyOn) }} }</div>
              }
            </button>
            @if (action(r); as a) {
              <button mat-flat-button class="go" [disabled]="busy() === r.task!.id" (click)="act(r, a.action)">{{ a.label }}</button>
            }
          </article>
        } @empty {
          @if (board()) {
            <p class="empty">
              @switch (filter()) {
                @case ('mine') { Nothing for you right now. Rooms appear here when a guest checks out or your supervisor gives you one. }
                @case ('todo') { Every room is ready. }
                @case ('inspect') { No rooms waiting for inspection. }
                @default { No rooms set up yet. }
              }
            </p>
          } @else if (!error()) { <p class="empty">Loading…</p> }
        }
      </div>
    </div>
  `,
  styles: `
    .hk { max-width: 1100px; padding-bottom: 80px; }
    .head { display: flex; justify-content: space-between; align-items: flex-start; gap: 8px; margin-bottom: 12px; }
    .head p { margin: 2px 0 0; }
    .tools { display: flex; align-items: center; gap: 4px; flex-wrap: wrap; justify-content: flex-end; }
    .mute { color: #5b6b6e; }
    .filters { display: flex; gap: 6px; overflow-x: auto; padding-bottom: 4px; margin-bottom: 8px; scrollbar-width: none; }
    .filters button { flex: 0 0 auto; border: 1px solid #d9e0de; background: #fff; border-radius: 999px; padding: 8px 14px; font: inherit;
                      min-height: 40px; color: #1b2a2e; cursor: pointer; }
    .filters button b { margin-left: 4px; }
    .filters button.on { background: #0e3b43; color: #fff; border-color: #0e3b43; }
    .rush-note { display: flex; align-items: center; gap: 6px; margin: 4px 0 12px; color: #8a5a00; font-size: .9rem; }
    .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 12px; }
    .card { display: flex; flex-direction: column; background: #fff; border: 1px solid #d9e0de; border-left: 6px solid #a9b6b3;
            border-radius: 12px; overflow: hidden; }
    .card.rush { box-shadow: 0 0 0 2px #e0a526 inset; }
    .card.dirty { border-left-color: #b3261e; } .card.cleaning { border-left-color: #d29a2c; }
    .card.clean, .card.inspection { border-left-color: #1f6f8b; } .card.ready { border-left-color: #2e6b3c; }
    .card.out-of-order { border-left-color: #5b6b6e; background: #f5f7f6; }
    .body { all: unset; display: block; padding: 12px 14px 10px; cursor: pointer; }
    .body:focus-visible { outline: 2px solid #8a2449; outline-offset: -2px; }
    .top { display: flex; justify-content: space-between; align-items: center; gap: 8px; }
    .code { font-size: 1.5rem; letter-spacing: -0.01em; }
    .state { font-size: .8rem; font-weight: 600; padding: 2px 10px; border-radius: 999px; background: #eef2f1; white-space: nowrap; }
    .state.dirty { background: #fbe4e2; color: #8c1d18; } .state.cleaning { background: #fbefd8; color: #8a5a00; }
    .state.clean, .state.inspection { background: #e0eff5; color: #134e63; } .state.ready { background: #e1f2e8; color: #1d5b36; }
    .state.out-of-order { background: #e3e7e6; color: #33474a; }
    .meta { color: #5b6b6e; font-size: .9rem; margin-top: 2px; }
    .hot { color: #8a5a00; }
    .task { margin-top: 6px; font-size: .9rem; }
    .back { color: #b3261e; }
    .repair { display: flex; align-items: center; gap: 4px; margin-top: 6px; font-size: .85rem; color: #5b3a00; }
    .repair mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .go { margin: 0 12px 12px; min-height: 52px; font-size: 1.05rem; border-radius: 10px; }
    .empty { color: #5b6b6e; grid-column: 1 / -1; }
    .err { color: #b3261e; }
    @media (max-width: 640px) {
      .cards { grid-template-columns: 1fr; gap: 10px; }
      h1 { font-size: 1.35rem; }
    }
  `,
})
export class HousekeepingPage {
  private api = inject(HousekeepingApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);

  board = signal<HousekeepingBoard | null>(null);
  error = signal('');
  busy = signal<string | null>(null);
  filter = signal<BoardFilter>('mine');
  hk = housekeepingLabel;
  cls = statusClass;
  typeLabel = taskTypeLabel;
  action = primaryAction;

  private pid = computed(() => this.ctx.current()?.id ?? '');
  canAssign = computed(() => this.auth.can('housekeeping.assign', this.pid()));
  shown = computed(() => filterRooms(this.board()?.rooms ?? [], this.filter(), this.auth.me()?.userId ?? null));
  longDate = computed(() => this.board() ? inZone(this.board()!.today + 'T00:00:00Z', 'UTC', { weekday: 'long', day: 'numeric', month: 'long' }) : '');

  constructor() {
    let first = true;
    effect(() => {
      if (!this.pid()) return;
      this.load();
      // A supervisor starts on what needs inspecting; everyone else on their own rooms.
      if (first && this.auth.can('housekeeping.inspect', this.pid()) && !this.auth.can('housekeeping.update', this.pid())) this.filter.set('inspect');
      first = false;
    });
    const t = setInterval(() => { if (document.visibilityState === 'visible' && !this.busy()) this.load(); }, 60_000);
    inject(DestroyRef).onDestroy(() => clearInterval(t));
  }

  load() {
    this.api.board(this.pid()).subscribe({
      next: b => {
        this.board.set(b);
        this.error.set('');
        // Nothing of mine but rooms to clean or inspect: show those instead of an empty list.
        if (this.filter() === 'mine' && !filterRooms(b.rooms, 'mine', this.auth.me()?.userId ?? null).length && this.canAssign())
          this.filter.set(b.summary.toInspect ? 'inspect' : 'todo');
      },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  count(f: BoardFilter) { return filterRooms(this.board()?.rooms ?? [], f, this.auth.me()?.userId ?? null).length; }
  doneCount(t: HousekeepingTask) { return t.checklist.filter(l => l.done).length; }
  time(iso: string) { return inZone(iso, this.ctx.current()?.timezone ?? 'Asia/Kolkata', { hour: '2-digit', minute: '2-digit', hour12: false }); }
  day(iso: string) { return inZone(iso + 'T00:00:00Z', 'UTC', { day: 'numeric', month: 'short' }); }

  /** The card's big button: one tap, no dialog. */
  act(r: HousekeepingRoom, action: 'start' | 'finish' | 'inspect') {
    const t = r.task!;
    this.busy.set(t.id);
    const call = action === 'start' ? this.api.start(this.pid(), t)
      : action === 'finish' ? this.api.finish(this.pid(), t, null, null)
        : this.api.inspect(this.pid(), t, 'PASS', null);
    call.subscribe({
      next: done => {
        this.busy.set(null);
        const msg = action === 'start' ? `${r.place}: cleaning started.`
          : action === 'finish' ? (done.status === 'DONE' ? `${r.place} is ready.` : `${r.place} cleaned — waiting for inspection.`)
            : `${r.place} is ready for the next guest.`;
        this.snack.open(msg, undefined, { duration: 2500 });
        this.load();
      },
      error: e => {
        this.busy.set(null);
        this.snack.open(toProblem(e).detail, 'OK', { duration: 8000 });
        this.load();
      },
    });
  }

  open(r: HousekeepingRoom) {
    const data: TaskSheetData = { propertyId: this.pid(), room: r, canAssign: this.canAssign() };
    this.dialog.open(TaskSheet, { data, width: '560px', maxWidth: '100vw', autoFocus: false, panelClass: 'sheet' })
      .afterClosed().subscribe(changed => { if (changed) this.load(); });
  }

  newTask() {
    this.dialog.open(NewTaskDialog, { data: { propertyId: this.pid(), rooms: this.board()?.rooms ?? [] }, width: '480px', maxWidth: '100vw' })
      .afterClosed().subscribe(t => { if (t) { this.snack.open(`Task added for ${t.place}.`, undefined, { duration: 2500 }); this.load(); } });
  }
}
