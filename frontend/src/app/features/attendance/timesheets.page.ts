import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { dayText } from '../../core/dates';
import { toProblem } from '../../core/http';
import { Timesheet, TimesheetLine, TimesheetStatus, TimesheetWeek } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { ReasonData, ReasonDialog } from '../restaurant/order-dialogs';
import { hm, hours, mondayOf, plusDays, shortDay } from '../employees/employees.api';
import { AttendanceApi, EVENT_LABEL, mins, LINE_LABEL, STATUS_LABEL, where } from './attendance.api';
import { CorrectData, CorrectDialog } from './attendance-dialogs';

// ------------------------------------------------------------------ one timesheet (shared by "mine" and a manager's view)

/** A week's timesheet: totals, one line per day (with its clock events), corrections, and the buttons the caller may use. */
@Component({
  selector: 'app-timesheet-card',
  imports: [MatButtonModule, MatIconModule],
  template: `
    @if (t(); as t) {
      <section class="sheet" data-testid="timesheet">
        <div class="top">
          <div>
            <h2>{{ t.sheet.employeeName }} <small class="mute">{{ t.sheet.employeeCode }} · {{ t.sheet.departmentName }}</small></h2>
            <p class="mute">Week of {{ dayText(t.sheet.weekStart) }} – {{ dayText(t.sheet.weekEnd) }}</p>
          </div>
          <span class="status" [class]="'s-' + t.sheet.status" data-testid="sheet-status">{{ statusLabel[t.sheet.status] }}</span>
        </div>
        @if (t.sheet.rejectionReason) { <p class="rejected" role="status"><mat-icon inline>undo</mat-icon> Sent back by {{ t.sheet.decidedBy }}: {{ t.sheet.rejectionReason }}</p> }
        @if (t.note) { <p class="note"><mat-icon inline>chat</mat-icon> {{ t.submittedBy ?? t.sheet.employeeName }}: {{ t.note }}</p> }
        @if (t.sheet.status === 'APPROVED' || t.sheet.status === 'LOCKED') {
          <p class="mute small">Approved by {{ t.sheet.decidedBy }} on {{ when(t.sheet.decidedAt) }}@if (t.sheet.lockedAt) { · locked for payroll on {{ when(t.sheet.lockedAt) }} }</p>
        }

        <div class="totals" data-testid="totals">
          <div><b>{{ hours(t.sheet.workedMinutes) }}</b><span>worked</span></div>
          <div><b>{{ t.sheet.daysWorked }}</b><span>{{ t.sheet.daysWorked === 1 ? 'day' : 'days' }}</span></div>
          <div><b>{{ mins(t.sheet.overtimeMinutes) }}</b><span>overtime</span></div>
          <div><b>{{ mins(t.sheet.lateMinutes) }}</b><span>late</span></div>
          <div><b>{{ mins(t.sheet.earlyMinutes) }}</b><span>left early</span></div>
        </div>

        <ul class="lines">
          @for (l of t.lines; track l.workDate) {
            <li [attr.data-day]="l.workDate" [class]="'l-' + l.status">
              <div class="d"><b>{{ shortDay(l.workDate) }}</b>
                <small class="mute">@if (l.shiftName) { {{ l.shiftName }} {{ hm(l.shiftStart!) }}–{{ hm(l.shiftEnd!) }} } @else { No shift }</small></div>
              <div class="t">
                @if (l.clockIn) { <span class="io">{{ hm(l.clockIn) }} – {{ l.clockOut ? hm(l.clockOut) : '…' }}@if (l.clockOutNextDay) { <small>(next day)</small> }</span> }
                <span class="ls">{{ lineLabel[l.status] }}@if (l.status === 'WORKED') { · <b data-testid="worked">{{ hours(l.workedMinutes) }}</b> }</span>
                <small class="mute">
                  @if (l.breakMinutes) { break {{ l.breakMinutes }} min }
                  @if (l.lateMinutes) { · <span class="late">{{ mins(l.lateMinutes) }} late</span> }
                  @if (l.earlyMinutes) { · <span class="late">left {{ mins(l.earlyMinutes) }} early</span> }
                  @if (l.overtimeMinutes) { · {{ mins(l.overtimeMinutes) }} overtime }
                  @if (l.corrected) { · <span class="fix">corrected</span> }
                  @if (l.overridden) { · manager entry }
                </small>
              </div>
              @if (can('correct')) { <button mat-icon-button (click)="correct.emit(l)" [attr.aria-label]="'Correct ' + shortDay(l.workDate)"><mat-icon>edit</mat-icon></button> }
              @if (l.events.length) {
                <details><summary class="mute">{{ l.events.length }} {{ l.events.length === 1 ? 'entry' : 'entries' }}</summary>
                  <ol>@for (e of l.events; track e.id) { <li>{{ hm(e.time) }} {{ evLabel[e.eventType] }} <small class="mute">{{ where(e) }}</small></li> }</ol>
                </details>
              }
            </li>
          } @empty { <li class="mute">Nothing yet this week: no clock-ins and no shifts that have started.</li> }
        </ul>

        @if (t.corrections.length) {
          <h3>Corrections</h3>
          <ul class="corr" data-testid="corrections">
            @for (c of t.corrections; track c.id) {
              <li><b>{{ shortDay(c.workDate) }}</b>: {{ c.oldClockIn ? hm(c.oldClockIn) : 'no clock-in' }} to {{ c.oldClockOut ? hm(c.oldClockOut) : 'no clock-out' }}
                ({{ c.oldWorkedMinutes === null ? 'no line' : hours(c.oldWorkedMinutes) }}) → {{ c.newClockIn ? hm(c.newClockIn) : 'no clock-in' }} to {{ c.newClockOut ? hm(c.newClockOut) : 'no clock-out' }}
                ({{ hours(c.newWorkedMinutes) }}) · {{ c.reason }} <small class="mute">— {{ c.by }}, {{ when(c.createdAt) }}</small></li>
            }
          </ul>
        }

        <div class="actions">
          @if (can('correct')) { <button mat-stroked-button (click)="correct.emit(null)"><mat-icon>add</mat-icon>Correct a day</button> }
          <span class="grow"></span>
          @if (can('reject')) { <button mat-stroked-button color="warn" (click)="act.emit('reject')">Send back</button> }
          @if (can('approve')) { <button mat-flat-button (click)="act.emit('approve')">Approve</button> }
          @if (can('lock')) { <button mat-flat-button (click)="act.emit('lock')"><mat-icon>lock</mat-icon>Lock for payroll</button> }
        </div>
      </section>
    }
  `,
  styles: `
    .sheet { background: #fff; border: 1px solid #d9e0de; border-radius: 16px; padding: 14px 16px; }
    .top { display: flex; justify-content: space-between; gap: 8px; align-items: flex-start; } .top h2 { margin: 0; font-size: 1.15rem; } .top p { margin: 2px 0 0; }
    .mute { color: #5b6b6e; } .small { font-size: .85rem; }
    .status { border-radius: 14px; padding: 3px 10px; font-size: .85rem; white-space: nowrap; background: #eef2f1; }
    .s-SUBMITTED { background: #e6f0ff; color: #0b3d91; } .s-APPROVED { background: #e3f3ea; color: #1e6b3a; } .s-REJECTED { background: #fdeceb; color: #8c1d18; }
    .s-LOCKED { background: #1d3b40; color: #fff; }
    .rejected { color: #8c1d18; } .note { background: #f6f8f7; border-radius: 10px; padding: 6px 10px; }
    .totals { display: flex; flex-wrap: wrap; gap: 8px; margin: 10px 0; } .totals div { flex: 1 1 90px; background: #f6f8f7; border-radius: 10px; padding: 6px 10px; display: flex; flex-direction: column; }
    .totals b { font-size: 1.05rem; } .totals span { color: #5b6b6e; font-size: .8rem; }
    .lines { list-style: none; padding: 0; margin: 0; } .lines > li { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 10px; padding: 8px 0; border-top: 1px solid #eef2f1; }
    .d { width: 150px; display: flex; flex-direction: column; } .t { flex: 1; display: flex; flex-direction: column; min-width: 180px; } .io { font-weight: 500; }
    .l-ABSENT .ls, .l-MISSING_OUT .ls { color: #b3261e; } .late { color: #8a5a00; } .fix { color: #0b3d91; }
    details { flex-basis: 100%; padding-left: 150px; font-size: .88rem; } details ol { margin: 4px 0; padding-left: 18px; }
    h3 { font-size: .95rem; margin: 12px 0 4px; } .corr { margin: 0; padding-left: 18px; font-size: .9rem; } .corr li { margin: 3px 0; }
    .actions { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 12px; } .grow { flex: 1; }
    @media (max-width: 760px) { .d { width: auto; flex-basis: 100%; } details { padding-left: 0; } }
  `,
})
export class TimesheetCard {
  t = input<Timesheet | null>(null);
  tz = input('Asia/Kolkata');
  act = output<'approve' | 'reject' | 'lock'>();
  correct = output<TimesheetLine | null>();
  hm = hm; hours = hours; mins = mins; dayText = dayText; shortDay = shortDay; where = where;
  statusLabel = STATUS_LABEL; lineLabel = LINE_LABEL; evLabel = EVENT_LABEL;
  can(a: string) { return this.t()?.sheet.actions.includes(a as never) ?? false; }
  when(iso: string | null) { return iso ? inZone(iso, this.tz(), { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false }) : ''; }
}

/** Approve, send back (with the reason) or lock one timesheet; correct a day. Shared by both pages. */
abstract class TimesheetActions {
  protected api = inject(AttendanceApi);
  protected ctx = inject(PropertyContext);
  protected dialog = inject(MatDialog);
  protected snack = inject(MatSnackBar);
  error = signal('');
  pid = computed(() => this.ctx.current()?.id ?? '');
  tz = computed(() => this.ctx.current()?.timezone ?? 'Asia/Kolkata');
  protected abstract sheet(): Timesheet | null;
  protected abstract set(t: Timesheet): void;

  act(kind: 'approve' | 'reject' | 'lock') {
    const t = this.sheet()!;
    const s = t.sheet;
    this.error.set('');
    if (kind === 'reject') {
      const data: ReasonData<Timesheet> = {
        title: `Send back ${s.employeeName}'s timesheet`, intro: 'They see your reason, fix what is needed with you, and submit again.', button: 'Send back',
        placeholder: 'Thursday\'s clock-out is missing', run: r => this.api.reject(this.pid(), s.id, r, s.version),
      };
      this.dialog.open(ReasonDialog<Timesheet>, { data, maxWidth: '100vw' }).afterClosed().subscribe(x => {
        if (x) { this.set(x); this.snack.open(`Sent back to ${s.employeeName}.`, 'OK', { duration: 4000 }); }
      });
      return;
    }
    const call = kind === 'approve' ? this.api.approve(this.pid(), s.id, s.version) : this.api.lock(this.pid(), s.id, s.version);
    call.subscribe({
      next: x => { this.set(x); this.snack.open(kind === 'approve' ? `${s.employeeName}'s week is approved.` : `${s.employeeName}'s week is locked for payroll.`, 'OK', { duration: 4000 }); },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  correct(line: TimesheetLine | null) {
    const data: CorrectData = { propertyId: this.pid(), timesheet: this.sheet()!, line };
    this.dialog.open(CorrectDialog, { data, maxWidth: '100vw' }).afterClosed().subscribe((x?: Timesheet) => {
      if (x) { this.set(x); this.snack.open('Correction saved. The old times are kept on the timesheet.', 'OK', { duration: 4000 }); }
    });
  }
}

// ------------------------------------------------------------------ /timesheets

/**
 * Timesheets (Sprint 18, brief §44). "Mine": the employee's week with each day worked out (in, out, breaks, late, overtime)
 * and a Submit button. "Team" (attendance.read): everyone's week with its status; "Approve all submitted" approves every
 * timesheet that can be and lists the others with the reason (e.g. a missing clock-out to correct first).
 */
@Component({
  selector: 'app-timesheets',
  imports: [FormsModule, RouterLink, MatButtonModule, MatButtonToggleModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule,
    MatSnackBarModule, TimesheetCard],
  template: `
    <div class="page timesheets">
      <div class="head">
        <h1>Timesheets</h1>
        @if (canSelf() && canTeam()) {
          <mat-button-toggle-group [value]="view()" (change)="setView($event.value)" aria-label="Whose timesheets">
            <mat-button-toggle value="mine">Mine</mat-button-toggle>
            <mat-button-toggle value="team">Team</mat-button-toggle>
          </mat-button-toggle-group>
        }
      </div>
      <div class="tools">
        <button mat-icon-button (click)="go(-7)" [disabled]="!monday()" aria-label="Previous week"><mat-icon>chevron_left</mat-icon></button>
        <button mat-stroked-button (click)="go(0)">This week</button>
        <button mat-icon-button (click)="go(7)" [disabled]="!monday() || isCurrent()" aria-label="Next week"><mat-icon>chevron_right</mat-icon></button>
        <span class="range" data-testid="week">{{ monday() ? 'Week of ' + dayText(monday()!) : '' }}</span>
      </div>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

      @if (view() === 'mine') {
        @if (mine(); as t) {
          <app-timesheet-card [t]="t" [tz]="tz()" />
          @if (t.sheet.actions.includes('submit')) {
            <div class="submit">
              <mat-form-field appearance="outline" class="grow"><mat-label>Note for your manager (optional)</mat-label>
                <input matInput [(ngModel)]="note" maxlength="300" placeholder="e.g. Forgot to clock out on Tuesday, left at 16:00" /></mat-form-field>
              <button mat-flat-button (click)="submit()" [disabled]="busy()">{{ t.sheet.status === 'REJECTED' ? 'Submit again' : 'Submit week' }}</button>
            </div>
          }
        } @else if (mineError()) { <p class="mute" data-testid="not-linked">{{ mineError() }}</p> }
      } @else if (week(); as w) {
        <div class="counts">
          @for (s of statuses; track s) { <span class="chip" [class]="'s-' + s">{{ w.counts[s] }} {{ statusLabel[s].toLowerCase() }}</span> }
          <span class="grow"></span>
          @if (canApprove() && w.counts.SUBMITTED) { <button mat-flat-button (click)="approveAll()">Approve all submitted ({{ w.counts.SUBMITTED }})</button> }
        </div>
        @if (skipped().length) {
          <section class="skipped" role="status" data-testid="skipped"><b>Not approved yet:</b>
            <ul>@for (s of skipped(); track s) { <li>{{ s }}</li> }</ul></section>
        }
        <ul class="team" data-testid="team">
          @for (s of w.timesheets; track s.id) {
            <li [attr.data-row]="s.employeeCode">
              <a [routerLink]="['/timesheets', s.id]">
                <div class="nm"><b>{{ s.employeeName }}</b><small class="mute">{{ s.departmentName }}</small></div>
                <div class="hrs"><b>{{ hours(s.workedMinutes) }}</b><small class="mute">{{ s.daysWorked }} {{ s.daysWorked === 1 ? 'day' : 'days' }}@if (s.overtimeMinutes) { · {{ mins(s.overtimeMinutes) }} OT }@if (s.lateMinutes) { · {{ mins(s.lateMinutes) }} late }</small></div>
                <div class="st"><span class="chip" [class]="'s-' + s.status">{{ statusLabel[s.status] }}</span>
                  @if (s.openDays) { <small class="warn">{{ s.openDays }} {{ s.openDays === 1 ? 'day' : 'days' }} without a clock-out</small> }
                  @if (s.corrections) { <small class="mute">{{ s.corrections }} corrected</small> }</div>
              </a>
            </li>
          } @empty { <li class="mute">Nobody clocked in or was on the roster this week.</li> }
        </ul>
      }
    </div>
  `,
  styles: `
    .timesheets { max-width: 960px; padding-bottom: 60px; }
    .head { display: flex; justify-content: space-between; align-items: center; gap: 8px; flex-wrap: wrap; }
    .tools { display: flex; align-items: center; gap: 4px; flex-wrap: wrap; margin-bottom: 8px; } .range { margin-left: 8px; font-weight: 500; }
    .mute { color: #5b6b6e; } .err { color: #b3261e; } .warn { color: #8a5a00; } .grow { flex: 1; }
    .submit { display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap; margin-top: 12px; }
    .counts { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; margin-bottom: 8px; }
    .chip { border-radius: 14px; padding: 2px 10px; font-size: .85rem; background: #eef2f1; white-space: nowrap; }
    .s-SUBMITTED { background: #e6f0ff; color: #0b3d91; } .s-APPROVED { background: #e3f3ea; color: #1e6b3a; } .s-REJECTED { background: #fdeceb; color: #8c1d18; }
    .s-LOCKED { background: #1d3b40; color: #fff; }
    .skipped { border: 1px solid #ecd29a; background: #fdf6e6; border-radius: 12px; padding: 6px 12px; margin-bottom: 8px; color: #6b4a00; } .skipped ul { margin: 4px 0; }
    .team { list-style: none; padding: 0; margin: 0; background: #fff; border: 1px solid #d9e0de; border-radius: 12px; }
    .team li { border-top: 1px solid #eef2f1; } .team li:first-child { border-top: 0; }
    .team a { display: flex; gap: 10px; align-items: center; padding: 10px 14px; color: inherit; text-decoration: none; flex-wrap: wrap; } .team a:hover { background: #f6f8f7; }
    .nm, .hrs, .st { display: flex; flex-direction: column; } .nm { flex: 1 1 180px; } .hrs { flex: 1 1 160px; } .st { flex: 0 1 200px; align-items: flex-start; }
  `,
})
export class TimesheetsPage extends TimesheetActions {
  private auth = inject(AuthService);
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  hours = hours; mins = mins; dayText = dayText; statusLabel = STATUS_LABEL;
  statuses: TimesheetStatus[] = ['DRAFT', 'SUBMITTED', 'APPROVED', 'REJECTED', 'LOCKED'];
  canSelf = computed(() => this.auth.can('timesheet.self', this.pid()));
  canTeam = computed(() => this.auth.can('attendance.read', this.pid()));
  canApprove = computed(() => this.auth.can('timesheet.approve', this.pid()));
  private queryView = toSignal(this.route.queryParamMap.pipe(map(q => q.get('view'))), { initialValue: null });
  view = computed<'mine' | 'team'>(() => {
    const q = this.queryView();
    if (q === 'mine' && this.canSelf()) return 'mine';
    if (q === 'team' && this.canTeam()) return 'team';
    return this.canApprove() || !this.canSelf() ? 'team' : 'mine';
  });
  mine = signal<Timesheet | null>(null);
  mineError = signal('');
  week = signal<TimesheetWeek | null>(null);
  skipped = signal<string[]>([]);
  monday = signal<string | null>(null);
  private today = signal<string | null>(null);
  busy = signal(false);
  note = '';
  isCurrent = computed(() => !!this.today() && this.monday() === mondayOf(this.today()!));

  constructor() {
    super();
    effect(() => { if (this.pid()) { this.view(); untracked(() => this.load()); } });
  }

  protected sheet() { return this.mine(); }
  protected set(t: Timesheet) { this.mine.set(t); }

  setView(v: 'mine' | 'team') { this.router.navigate([], { queryParams: { view: v }, replaceUrl: true }); }

  load() {
    this.error.set('');
    if (this.view() === 'mine') {
      this.api.mine(this.pid(), this.monday()).subscribe({
        next: t => { this.mine.set(t); this.mineError.set(''); this.monday.set(t.sheet.weekStart); this.note = t.note ?? ''; if (!this.today()) this.today.set(t.sheet.weekStart); },
        error: e => { this.mine.set(null); this.mineError.set(toProblem(e).detail); },
      });
    } else {
      this.api.week(this.pid(), this.monday()).subscribe({
        next: w => { this.week.set(w); this.monday.set(w.weekStart); this.today.set(w.today); },
        error: e => this.error.set(toProblem(e).detail),
      });
    }
  }

  go(days: number) {
    this.skipped.set([]);
    this.monday.set(days === 0 || !this.monday() ? null : plusDays(this.monday()!, days));
    this.load();
  }

  submit() {
    const t = this.mine()!;
    this.busy.set(true);
    this.error.set('');
    this.api.submit(this.pid(), t.sheet.id, this.note.trim() || null, t.sheet.version).subscribe({
      next: x => { this.busy.set(false); this.mine.set(x); this.snack.open('Submitted. Your manager will approve it or send it back.', 'OK', { duration: 4000 }); },
      error: e => { this.busy.set(false); const p = toProblem(e); this.error.set(p.code === 'VALIDATION_FAILED' && p.errors ? Object.values(p.errors).flat().join(' ') : p.detail); },
    });
  }

  approveAll() {
    const w = this.week()!;
    this.api.approveWeek(this.pid(), w.weekStart).subscribe({
      next: r => {
        this.week.set(r.week);
        this.skipped.set(r.skipped);
        this.snack.open(`${r.approved} ${r.approved === 1 ? 'timesheet' : 'timesheets'} approved.${r.skipped.length ? ` ${r.skipped.length} need a look first.` : ''}`, 'OK', { duration: 5000 });
      },
      error: e => { const p = toProblem(e); this.error.set(p.code === 'VALIDATION_FAILED' && p.errors ? Object.values(p.errors).flat().join(' ') : p.detail); },
    });
  }
}

// ------------------------------------------------------------------ /timesheets/:id

/** One employee's week for a manager: approve, send back, lock, or correct a day. */
@Component({
  selector: 'app-timesheet',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatIconModule, MatSnackBarModule, TimesheetCard],
  template: `
    <div class="page one">
      <a mat-button [routerLink]="['/timesheets']" [queryParams]="{ view: 'team' }"><mat-icon>arrow_back</mat-icon>Timesheets</a>
      @if (error()) { <p class="err" role="alert" data-testid="sheet-error">{{ error() }}</p> }
      <app-timesheet-card [t]="t()" [tz]="tz()" (act)="act($event)" (correct)="correct($event)" />
    </div>
  `,
  styles: `.one { max-width: 960px; padding-bottom: 60px; } .err { color: #b3261e; background: #fdeceb; border-radius: 10px; padding: 8px 12px; }`,
})
export class TimesheetPage extends TimesheetActions {
  id = input.required<string>();
  t = signal<Timesheet | null>(null);

  constructor() {
    super();
    effect(() => {
      if (!this.pid()) return;
      this.api.timesheet(this.pid(), this.id()).subscribe({ next: t => this.t.set(t), error: e => this.error.set(toProblem(e).detail) });
    });
  }

  protected sheet() { return this.t(); }
  protected set(t: Timesheet) { this.t.set(t); this.error.set(''); }
}
