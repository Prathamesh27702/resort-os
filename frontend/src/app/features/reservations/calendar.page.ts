import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { BreakpointObserver } from '@angular/cdk/layout';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { Subject, map, switchMap, tap } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { Calendar, CalendarBooking } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { CanDirective } from '../../shared/can.directive';
import { label, shortDate } from '../rooms/rooms.api';
import { dayLabel } from '../settings/rates/rates.api';
import { CalendarDrop, CalendarGrid } from './calendar-grid';
import { CalRow, CalView, buildRows, byDay, daysIn, moveProblem, shift, windowFor } from './calendar.model';
import { MoveDialog, MoveDialogData, MoveResult } from './move-dialog';
import { ReservationsApi, statusClass, statusLabel } from './reservations.api';

/** Today's date (YYYY-MM-DD) in a time zone. */
export function todayIn(tz: string): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: tz, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
}

/**
 * The reservation calendar: every room and dorm bed by day, week or month, with bookings (guest, status, source,
 * payment) and blocks. Drag a booking to another row to move the guest; a reason is asked, the server checks, and only
 * then does the calendar change. On a phone the same window is a list by day.
 */
@Component({
  selector: 'app-calendar',
  imports: [FormsModule, RouterLink, MatButtonModule, MatButtonToggleModule, MatDialogModule, MatIconModule, MatSnackBarModule,
            CanDirective, CalendarGrid],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Calendar</h1>
          <p>Every room and dorm bed by day. Click a booking for its details;
            @if (canMove()) { drag it up or down to move the guest to another room. } @else { you can view bookings but not move them. }
          </p>
        </div>
        <div class="head-actions">
          <a mat-stroked-button routerLink="/reservations"><mat-icon>list</mat-icon>List</a>
          <a mat-flat-button routerLink="/reservations/new" *appCan="'reservation.create'"><mat-icon>add</mat-icon>New booking</a>
        </div>
      </div>

      <div class="toolbar">
        <div class="nav">
          <button mat-icon-button (click)="go(-1)" [attr.aria-label]="'Previous ' + view()"><mat-icon>chevron_left</mat-icon></button>
          <button mat-stroked-button (click)="goToday()">Today</button>
          <button mat-icon-button (click)="go(1)" [attr.aria-label]="'Next ' + view()"><mat-icon>chevron_right</mat-icon></button>
          <h2 class="range" aria-live="polite">{{ title() }}</h2>
        </div>
        <mat-button-toggle-group [ngModel]="view()" (ngModelChange)="setView($event)" aria-label="Calendar view">
          <mat-button-toggle value="day">Day</mat-button-toggle>
          <mat-button-toggle value="week">Week</mat-button-toggle>
          <mat-button-toggle value="month">Month</mat-button-toggle>
        </mat-button-toggle-group>
      </div>

      <div class="legend" aria-hidden="true">
        <span class="sw s-confirmed"></span>Confirmed <span class="sw s-pending"></span>Pending <span class="sw s-checked_in"></span>Checked in
        <span class="sw s-checked_out"></span>Checked out <span class="sw blk"></span>Blocked
      </div>

      @if (banner(); as b) {
        <div class="banner" role="alert">
          <mat-icon>block</mat-icon><span>{{ b }}</span>
          <button mat-icon-button (click)="banner.set('')" aria-label="Dismiss"><mat-icon>close</mat-icon></button>
        </div>
      }

      @if (error()) {
        <p class="err" role="alert">{{ error() }} <button mat-button (click)="load()">Try again</button></p>
      } @else if (cal(); as cal) {
        @if (!cal.rooms.length) {
          <div class="panel empty">No rooms yet. <a routerLink="/rooms">Add rooms</a> to see them here.</div>
        } @else if (mobile()) {
          <div class="days">
            @for (d of perDay(); track d.day) {
              <section class="panel day" [class.today]="d.day === cal.today">
                <h3>{{ dayLabel(d.day) }}@if (d.day === cal.today) { <span class="chip ok">Today</span> }</h3>
                @for (l of d.lines; track $index) {
                  @if (l.booking; as b) {
                    <button type="button" class="line" (click)="open(b)">
                      <span class="room">{{ l.row.label }}</span>
                      <span class="who"><b>{{ b.guestName }}</b> <small class="num mute">{{ b.reference }}</small></span>
                      <span class="tag" [class]="'tag ' + l.kind">{{ kindText[l.kind] }}</span>
                      <span [class]="'chip ' + statusClass(b.status)">{{ statusLabel(b.status) }}</span>
                    </button>
                  } @else if (l.block; as k) {
                    <div class="line blocked">
                      <span class="room">{{ l.row.label }}</span><span class="who">{{ label(k.blockType) }}: {{ k.reason }}</span>
                      <span class="tag blocked">Blocked</span>
                    </div>
                  }
                } @empty { <p class="mute nothing">No bookings or blocks.</p> }
              </section>
            }
          </div>
        } @else {
          <app-calendar-grid [cal]="cal" [days]="days()" [canMove]="canMove()" (dropped)="dropped($event)" (opened)="open($event)" />
        }
        @if (loading()) { <p class="mute loading">Loading…</p> }
      } @else {
        <p class="mute">Loading…</p>
      }
    </div>
  `,
  styles: `
    .head-actions { display: flex; gap: 8px; flex-wrap: wrap; }
    .toolbar { display: flex; justify-content: space-between; align-items: center; gap: 12px; flex-wrap: wrap; margin-bottom: 8px; }
    .nav { display: flex; align-items: center; gap: 4px; }
    .range { margin: 0 0 0 8px; font-size: 18px; font-weight: 600; }
    .legend { display: flex; align-items: center; gap: 6px 10px; flex-wrap: wrap; font-size: 12px; color: #5b6b6e; margin-bottom: 10px; }
    .sw { display: inline-block; width: 14px; height: 10px; border-radius: 3px; border: 1px solid transparent; margin-left: 6px; }
    .s-confirmed { background: #d7efe0; border-color: #9fd4b4; }
    .s-pending { background: #fbefd8; border: 1px dashed #efcd8a; }
    .s-checked_in { background: #dbe8fb; border-color: #a7c3ef; }
    .s-checked_out { background: #eceff0; border-color: #d3d9da; }
    .blk { background: repeating-linear-gradient(135deg, #fde3e1 0 3px, #f9d0cc 3px 6px); border-color: #f2b8b2; }
    .banner { display: flex; align-items: center; gap: 8px; padding: 6px 6px 6px 12px; margin-bottom: 10px; border-radius: 8px;
              background: #fdecea; color: #7f1d1d; border: 1px solid #f2b8b2; }
    .banner span { flex: 1; }
    .err { color: #b3261e; }
    .mute { color: #5b6b6e; }
    .empty { padding: 20px 16px; color: #5b6b6e; }
    .loading { margin-top: 8px; }
    .days { display: flex; flex-direction: column; gap: 10px; }
    .day { padding: 10px 12px; }
    .day h3 { margin: 0 0 6px; font-size: 15px; display: flex; gap: 8px; align-items: center; }
    .day.today { border-color: #80cbc4; }
    .line { display: grid; grid-template-columns: 1fr auto; grid-template-areas: "room tag" "who chip"; gap: 2px 8px; width: 100%;
            text-align: left; padding: 8px 0; border: 0; border-top: 1px solid #edf1f0; background: none; font: inherit; color: inherit; }
    .line .room { grid-area: room; font-size: 12px; color: #5b6b6e; }
    .line .who { grid-area: who; }
    .line .tag { grid-area: tag; justify-self: end; font-size: 11px; padding: 1px 6px; border-radius: 10px; background: #eef2f1; }
    .line .chip { grid-area: chip; justify-self: end; }
    .tag.arrives { background: #d7efe0; color: #14532d; }
    .tag.leaves { background: #fbefd8; color: #7a4f00; }
    .tag.blocked { background: #fde3e1; color: #7f1d1d; }
    .chip.ok { background: #e1f2e8; color: #1d5b36; }
    .chip.wait { background: #fbefd8; color: #8a5a00; }
    .nothing { margin: 4px 0; }
  `,
})
export class CalendarPage {
  private api = inject(ReservationsApi);
  private props = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);

  label = label;
  dayLabel = dayLabel;
  statusLabel = statusLabel;
  statusClass = statusClass;
  kindText = { arrives: 'Arrives', stays: 'In house', leaves: 'Leaves', blocked: 'Blocked' } as const;

  mobile = toSignal(inject(BreakpointObserver).observe('(max-width: 760px)').pipe(map(r => r.matches)), { initialValue: false });
  view = signal<CalView>('week');
  anchor = signal('');
  cal = signal<Calendar | null>(null);
  loading = signal(false);
  error = signal('');
  banner = signal('');

  private pid = computed(() => this.props.current()?.id ?? '');
  canMove = computed(() => { this.auth.me(); return this.auth.can('reservation.update', this.pid()); });
  window = computed(() => windowFor(this.view(), this.anchor()));
  days = computed(() => daysIn(this.window().from, this.window().to));
  rows = computed<CalRow[]>(() => buildRows(this.cal()?.rooms ?? []));
  perDay = computed(() => { const c = this.cal(); return c ? byDay(c, this.rows(), this.days()) : []; });
  title = computed(() => {
    const { from, to } = this.window();
    if (!this.anchor()) return '';
    if (this.view() === 'day') return dayLabel(from);
    if (this.view() === 'month')
      return new Intl.DateTimeFormat('en-IN', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(new Date(from + 'T00:00:00Z'));
    const last = daysIn(from, to).at(-1)!;
    return `${shortDate(from)} – ${shortDate(last)} ${last.slice(0, 4)}`;
  });

  private load$ = new Subject<void>();

  constructor() {
    this.load$.pipe(
      tap(() => { this.loading.set(true); this.error.set(''); }),
      switchMap(() => this.api.calendar(this.pid(), this.window().from, this.window().to)),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe({
      next: c => { this.cal.set(c); this.loading.set(false); },
      error: e => { this.error.set(toProblem(e).detail); this.loading.set(false); },
    });
    // a new property: start at its today
    effect(() => {
      const p = this.props.current();
      if (!p) return;
      this.anchor.set(todayIn(p.timezone));
      this.load();
    });
  }

  load() { if (this.pid() && this.anchor()) this.load$.next(); }
  setView(v: CalView) { this.view.set(v); this.banner.set(''); this.load(); }
  go(dir: 1 | -1) { this.anchor.set(shift(this.view(), this.anchor(), dir)); this.banner.set(''); this.load(); }
  goToday() { this.anchor.set(this.cal()?.today ?? todayIn(this.props.current()!.timezone)); this.banner.set(''); this.load(); }

  /** A bar was dropped on another row: say at once why it can't go there, else ask for the reason and let the server decide. */
  dropped(d: CalendarDrop) {
    const cal = this.cal();
    if (!cal) return;
    const problem = moveProblem(d.booking, d.target, cal);
    if (problem) { this.banner.set(`Can't move ${d.booking.reference} to ${d.target.label}: ${problem}`); return; }
    this.banner.set('');
    this.openDialog({ propertyId: this.pid(), cal, booking: d.booking, target: d.target, canMove: this.canMove() });
  }

  open(b: CalendarBooking) {
    const cal = this.cal();
    if (cal) this.openDialog({ propertyId: this.pid(), cal, booking: b, canMove: this.canMove() });
  }

  private openDialog(data: MoveDialogData) {
    this.dialog.open<MoveDialog, MoveDialogData, MoveResult>(MoveDialog, { data, autoFocus: 'first-tabbable' }).afterClosed()
      .subscribe(r => {
        if (r) this.snack.open(`${r.reservation.reference} moved to ${r.to}`, undefined, { duration: 3000 });
        this.load();   // the latest from the server, moved or not (someone else may have changed it)
      });
  }
}
