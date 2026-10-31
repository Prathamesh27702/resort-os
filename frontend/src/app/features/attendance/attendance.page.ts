import { Component, computed, effect, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { dayText } from '../../core/dates';
import { toProblem } from '../../core/http';
import { AttendanceDay, AttendanceRow, AttendanceRowState, ClockAction, MyAttendance } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { hm, hours, plusDays } from '../employees/employees.api';
import { ACTION_LABEL, AttendanceApi, EVENT_LABEL, STATUS_LABEL, currentPosition, mins, where } from './attendance.api';
import { GeofencesDialog, RecordData, RecordDialog } from './attendance-dialogs';

const ROW_STATE: Record<AttendanceRowState, string> = {
  NOT_YET: 'Not started', NOT_IN: 'Not in', ON_SHIFT: 'At work', ON_BREAK: 'On a break', DONE: 'Clocked out', MISSING_OUT: 'No clock-out',
};

/**
 * Attendance (Sprint 18, brief §43). On a phone, the employee clocks in and out with one big button: the app asks the
 * browser for the phone's position and the server decides whether it is inside the resort's geofence (a refusal says how
 * far away it was). Breaks are one tap. Below, for those who may see it, the day's board: who is at work, who hasn't come
 * in, who was late, with a manager entry (with the reason) for someone whose phone can't clock in.
 */
@Component({
  selector: 'app-attendance',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatIconModule, MatProgressSpinnerModule, MatSnackBarModule],
  template: `
    <div class="page attendance">
      <div class="head"><h1>Attendance</h1></div>

      @if (canSelf()) {
        @if (me(); as m) {
          <section class="clock" [class.on]="m.state !== 'OFF'" [class.brk]="m.state === 'ON_BREAK'" data-testid="clock">
            <div class="who"><b>{{ m.employeeName }}</b><span class="mute">{{ m.departmentName }}</span></div>
            <p class="state" data-testid="clock-state">
              @switch (m.state) {
                @case ('ON_SHIFT') { <mat-icon inline>work</mat-icon> At work since {{ time(m.since) }} }
                @case ('ON_BREAK') { <mat-icon inline>coffee</mat-icon> On a break since {{ time(m.since) }} }
                @default { <mat-icon inline>schedule</mat-icon> Not clocked in }
              }
            </p>
            @if (m.todayShift; as s) {
              <p class="shift">Shift: <b>{{ s.templateName ?? 'Shift' }} {{ hm(s.startTime) }}–{{ hm(s.endTime) }}</b> · {{ s.departmentName }}@if (s.workDate !== m.today) { ({{ dayText(s.workDate) }}) }</p>
            } @else {
              <p class="shift mute">No shift on the roster today.@if (m.nextShift; as n) { Next: {{ dayText(n.workDate) }} {{ hm(n.startTime) }}–{{ hm(n.endTime) }}. }</p>
            }
            @if (m.missedClockOut) { <p class="warn" role="status"><mat-icon inline>warning</mat-icon> {{ m.missedClockOut }}</p> }

            <div class="buttons">
              @for (a of actions(); track a) {
                <button mat-flat-button class="big" [class.out]="a === 'CLOCK_OUT'" [class.second]="a === 'BREAK_START' || a === 'BREAK_END'"
                  (click)="clock(a)" [disabled]="!!busy()">{{ label[a] }}</button>
              }
            </div>
            @if (busy()) { <p class="mute locating"><mat-spinner diameter="18" /> {{ busy() }}</p> }
            @if (error()) { <p class="err" role="alert" data-testid="clock-error">{{ error() }}</p> }
            @if (m.geofenceRequired) { <p class="mute small"><mat-icon inline>location_on</mat-icon> Clocking in and out uses your phone's location to check you are at the resort.</p> }

            @if (m.events.length) {
              <ol class="events" data-testid="my-events">
                @for (e of m.events; track e.id) {
                  <li><b>{{ hm(e.time) }}</b> {{ evLabel[e.eventType] }} <small class="mute">{{ where(e) }}</small></li>
                }
              </ol>
              <p class="sofar">Worked {{ hours(m.workedMinutes) }}@if (m.breakMinutes) { · break {{ hours(m.breakMinutes) }} }{{ m.state !== 'OFF' ? ' so far' : '' }}</p>
            }
            <p class="week">
              @if (m.week; as w) { This week: <b>{{ hours(w.workedMinutes) }}</b> · {{ statusLabel[w.status] }} · }
              <a routerLink="/timesheets">My timesheet</a>
            </p>
          </section>
        } @else if (meError()) {
          <p class="mute notlinked" data-testid="not-linked"><mat-icon inline>info</mat-icon> {{ meError() }}</p>
        }
      }

      @if (canRead()) {
        <section class="board" data-testid="board">
          <div class="bhead">
            <h2>Team</h2>
            <div class="tools">
              <button mat-icon-button (click)="go(-1)" aria-label="Previous day"><mat-icon>chevron_left</mat-icon></button>
              <button mat-stroked-button (click)="go(0)">{{ day()?.date === day()?.today ? 'Today' : 'Back to today' }}</button>
              <button mat-icon-button (click)="go(1)" [disabled]="day()?.date === day()?.today" aria-label="Next day"><mat-icon>chevron_right</mat-icon></button>
              @if (canUpdate()) { <button mat-stroked-button (click)="areas()"><mat-icon>my_location</mat-icon>Areas</button> }
            </div>
          </div>
          @if (day(); as d) {
            <p class="date">{{ dayText(d.date) }}</p>
            <div class="counts">
              <span class="chip on" data-testid="count-on">{{ d.onShift }} at work</span>
              <span class="chip notin" data-testid="count-notin">{{ d.notIn }} not in</span>
              <span class="chip late">{{ d.late }} late</span>
            </div>
            @if (!d.geofences.length && canUpdate()) {
              <p class="warn"><mat-icon inline>warning</mat-icon> No area is set up, so clock-ins are accepted from anywhere. Use <b>Areas</b> to add the resort's location.</p>
            }
            <ul class="rows">
              @for (r of d.rows; track r.employeeId) {
                <li [attr.data-row]="r.employeeCode" [class]="'st-' + r.state">
                  <div class="nm"><b>{{ r.employeeName }}</b>
                    <small class="mute">{{ r.departmentName }}@if (r.shift; as s) { · {{ s.templateName ?? 'Shift' }} {{ hm(s.startTime) }}–{{ hm(s.endTime) }} }</small></div>
                  <div class="tm">
                    <span class="st">{{ rowState[r.state] }}</span>
                    @if (r.clockIn) { <small>{{ hm(r.clockIn) }}{{ r.clockOut ? '–' + hm(r.clockOut) : '' }}@if (r.lateMinutes) { · <span class="lt">{{ mins(r.lateMinutes) }} late</span> }</small> }
                    @if (r.overridden) { <small class="mute" [attr.title]="overrides(r)"><mat-icon inline>edit_note</mat-icon> manager entry</small> }
                  </div>
                  @if (canUpdate()) { <button mat-icon-button (click)="record(r)" [attr.aria-label]="'Record for ' + r.employeeName"><mat-icon>more_time</mat-icon></button> }
                </li>
              } @empty { <li class="mute">Nobody is on the roster on this day and nobody clocked in.</li> }
            </ul>
          }
          @if (boardError()) { <p class="err" role="alert">{{ boardError() }}</p> }
        </section>
      }
    </div>
  `,
  styles: `
    .attendance { max-width: 880px; padding-bottom: 60px; }
    .mute { color: #5b6b6e; } .small { font-size: .85rem; } .err { color: #b3261e; } .warn { color: #8a5a00; }
    .clock { background: #fff; border: 1px solid #d9e0de; border-radius: 16px; padding: 16px 18px; margin-bottom: 18px; }
    .clock.on { border-color: #7cb89a; background: #f3faf6; } .clock.brk { border-color: #e0b34a; background: #fffaf0; }
    .who { display: flex; flex-direction: column; } .who b { font-size: 1.1rem; }
    .state { font-size: 1.35rem; font-weight: 600; margin: 10px 0 4px; } .shift { margin: 0 0 8px; }
    .buttons { display: flex; gap: 10px; flex-wrap: wrap; margin: 12px 0 4px; }
    .big { flex: 1 1 140px; height: 64px; font-size: 1.15rem; border-radius: 14px; }
    .big.out { --mdc-filled-button-container-color: #8a2449; --mat-button-filled-container-color: #8a2449; }
    .big.second { --mdc-filled-button-container-color: #e9efed; --mat-button-filled-container-color: #e9efed;
                  --mdc-filled-button-label-text-color: #0e3b43; --mat-button-filled-label-text-color: #0e3b43; }
    .locating { display: flex; align-items: center; gap: 8px; }
    .events { list-style: none; padding: 0; margin: 12px 0 0; border-top: 1px solid #e3e9e7; } .events li { padding: 6px 0; border-bottom: 1px solid #eef2f1; }
    .events b { display: inline-block; width: 52px; } .sofar { font-weight: 500; margin: 8px 0 0; } .week { margin: 10px 0 0; }
    .notlinked { background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 12px 16px; }
    .board { background: #fff; border: 1px solid #d9e0de; border-radius: 16px; padding: 12px 16px; }
    .bhead { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 6px; } .bhead h2 { margin: 0; }
    .tools { display: flex; align-items: center; gap: 4px; flex-wrap: wrap; } .date { margin: 4px 0; font-weight: 500; }
    .counts { display: flex; gap: 6px; flex-wrap: wrap; margin: 4px 0 8px; }
    .chip { border-radius: 14px; padding: 2px 10px; font-size: .85rem; } .chip.on { background: #e3f3ea; color: #1e6b3a; }
    .chip.notin { background: #fdeceb; color: #8c1d18; } .chip.late { background: #fdf6e6; color: #6b4a00; }
    .rows { list-style: none; margin: 0; padding: 0; } .rows li { display: flex; align-items: center; gap: 8px; padding: 8px 0; border-top: 1px solid #eef2f1; }
    .nm { flex: 1; display: flex; flex-direction: column; min-width: 0; } .tm { display: flex; flex-direction: column; align-items: flex-end; text-align: right; }
    .st { font-weight: 500; } .st-ON_SHIFT .st { color: #1e6b3a; } .st-ON_BREAK .st { color: #8a5a00; } .st-NOT_IN .st, .st-MISSING_OUT .st { color: #b3261e; }
    .lt { color: #8a5a00; }
    @media (max-width: 760px) { .big { flex-basis: 100%; } }
  `,
})
export class AttendancePage {
  private api = inject(AttendanceApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  hm = hm; hours = hours; mins = mins; dayText = dayText; where = where;
  label = ACTION_LABEL; evLabel = EVENT_LABEL; statusLabel = STATUS_LABEL; rowState = ROW_STATE;

  me = signal<MyAttendance | null>(null);
  meError = signal('');
  day = signal<AttendanceDay | null>(null);
  boardError = signal('');
  busy = signal('');
  error = signal('');
  private date = signal<string | null>(null);
  private pid = computed(() => this.ctx.current()?.id ?? '');
  private tz = computed(() => this.ctx.current()?.timezone ?? 'Asia/Kolkata');
  canSelf = computed(() => this.auth.can('attendance.self', this.pid()));
  canRead = computed(() => this.auth.can('attendance.read', this.pid()));
  canUpdate = computed(() => this.auth.can('attendance.update', this.pid()));
  actions = computed<ClockAction[]>(() => {
    switch (this.me()?.state) {
      case 'ON_SHIFT': return ['BREAK_START', 'CLOCK_OUT'];
      case 'ON_BREAK': return ['BREAK_END', 'CLOCK_OUT'];
      default: return ['CLOCK_IN'];
    }
  });

  constructor() {
    effect(() => { if (this.pid()) { this.loadMe(); this.loadDay(); } });
  }

  time(iso: string | null) { return iso ? inZone(iso, this.tz(), { hour: '2-digit', minute: '2-digit', hour12: false }) : ''; }

  loadMe() {
    if (!this.canSelf()) return;
    this.api.me(this.pid()).subscribe({
      next: m => { this.me.set(m); this.meError.set(''); },
      error: e => { this.me.set(null); this.meError.set(toProblem(e).detail); },
    });
  }

  loadDay() {
    if (!this.canRead()) return;
    this.api.day(this.pid(), this.date()).subscribe({
      next: d => { this.day.set(d); this.boardError.set(''); },
      error: e => this.boardError.set(toProblem(e).detail),
    });
  }

  /** Asks the phone where it is (clocking in and out need it when the property has a geofence), then sends the tap. */
  async clock(action: ClockAction) {
    const m = this.me();
    if (!m) return;
    this.error.set('');
    let pos = null;
    if (m.geofenceRequired || action === 'CLOCK_IN' || action === 'CLOCK_OUT') {
      this.busy.set('Finding where you are…');
      try { pos = await currentPosition(); } catch (e) {
        if (m.geofenceRequired && action !== 'BREAK_START' && action !== 'BREAK_END') { this.busy.set(''); this.error.set((e as Error).message); return; }
      }
    }
    this.busy.set(action === 'CLOCK_IN' ? 'Clocking in…' : action === 'CLOCK_OUT' ? 'Clocking out…' : 'Saving…');
    this.api.clock(this.pid(), action, pos).subscribe({
      next: x => {
        this.busy.set('');
        this.me.set(x);
        this.snack.open(this.done(action, x), 'OK', { duration: 4000 });
        this.loadDay();
      },
      error: e => {
        this.busy.set('');
        const p = toProblem(e);
        this.error.set(p.code === 'VALIDATION_FAILED' && p.errors ? Object.values(p.errors).flat().join(' ') : p.detail);
        this.loadMe();
      },
    });
  }

  private done(action: ClockAction, m: MyAttendance): string {
    const last = m.events.at(-1);
    const at = last ? hm(last.time) : '';
    return action === 'CLOCK_IN' ? `Clocked in at ${at}.` : action === 'CLOCK_OUT' ? `Clocked out at ${at}. Worked ${hours(m.workedMinutes)}.`
      : action === 'BREAK_START' ? `Break started at ${at}.` : `Break ended at ${at}.`;
  }

  go(days: number) {
    const d = this.day();
    if (!d) return;
    this.date.set(days === 0 ? null : plusDays(d.date, days));
    this.loadDay();
  }

  overrides(r: AttendanceRow) { return r.events.filter(e => e.source === 'MANAGER').map(where).join('\n'); }

  record(r: AttendanceRow) {
    const data: RecordData = { propertyId: this.pid(), row: r, date: this.day()!.date, now: this.time(new Date().toISOString()) };
    this.dialog.open(RecordDialog, { data, maxWidth: '100vw' }).afterClosed().subscribe((d?: AttendanceDay & { message: string }) => {
      if (!d) return;
      this.snack.open(d.message, 'OK', { duration: 4000 });
      this.loadDay();
      this.loadMe();
    });
  }

  areas() {
    this.dialog.open(GeofencesDialog, { data: { propertyId: this.pid() }, maxWidth: '100vw' }).afterClosed().subscribe(changed => {
      if (changed) { this.loadDay(); this.loadMe(); }
    });
  }
}
