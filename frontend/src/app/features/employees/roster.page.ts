import { BreakpointObserver } from '@angular/cdk/layout';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { dayText } from '../../core/dates';
import { toProblem } from '../../core/http';
import { CopyRosterWeekResult, RosterEmployee, RosterShift, RosterShortage, RosterWeek } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { EmployeesApi, cellShifts, hm, hours, isoDay, mondayOf, plusDays, shortDay, shortagesByDay } from './employees.api';
import { CopyWeekDialog, ShiftData, ShiftDialog, ShiftResult, TemplatesDialog } from './roster-dialogs';

/**
 * The weekly roster (Sprint 17): everyone by department, Monday to Sunday, with their shifts. The shortage panel lists
 * every shift with fewer people than it needs on a day (tap one to fill it); warnings flag a weekly day off, a week over
 * 48 hours and allowed overlaps. A shift that clashes with another of the person's shifts is refused with the reason.
 * On a phone (360 px) the week becomes one day at a time.
 */
@Component({
  selector: 'app-roster',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page roster">
      <div class="head">
        <h1>Roster</h1>
        @if (week(); as w) { <p class="range" data-testid="week">{{ dayText(w.weekStart) }} – {{ dayText(w.weekEnd) }}</p> }
      </div>
      <div class="tools">
        <button mat-icon-button (click)="go(-7)" aria-label="Previous week"><mat-icon>chevron_left</mat-icon></button>
        <button mat-stroked-button (click)="thisWeek()">This week</button>
        <button mat-icon-button (click)="go(7)" aria-label="Next week"><mat-icon>chevron_right</mat-icon></button>
        <span class="grow"></span>
        @if (canManage()) {
          <button mat-stroked-button (click)="copy()"><mat-icon>content_copy</mat-icon>Copy last week</button>
          <button mat-stroked-button (click)="templates()"><mat-icon>schedule</mat-icon>Shifts</button>
        }
        @if (canEmployees()) { <a mat-button routerLink="/employees"><mat-icon>badge</mat-icon>Employees</a> }
      </div>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

      @if (week(); as w) {
        @if (w.shortages.length) {
          <section class="short" data-testid="shortages">
            <h2><mat-icon inline>error</mat-icon> {{ missing() }} {{ missing() === 1 ? 'person' : 'people' }} short this week</h2>
            <ul>
              @for (s of w.shortages; track s.date + s.templateId) {
                <li [class.today]="s.date === w.today">
                  @if (canManage()) { <button mat-button (click)="fill(s)" [attr.aria-label]="'Fill ' + s.departmentName + ' ' + s.templateName + ' on ' + shortDay(s.date)">
                    <b>{{ shortDay(s.date) }}</b> {{ s.departmentName }} · {{ s.templateName }} {{ hm(s.startTime) }}–{{ hm(s.endTime) }}: {{ s.rostered }} of {{ s.needed }}</button> }
                  @else { <b>{{ shortDay(s.date) }}</b> {{ s.departmentName }} · {{ s.templateName }} {{ hm(s.startTime) }}–{{ hm(s.endTime) }}: {{ s.rostered }} of {{ s.needed }} }
                </li>
              }
            </ul>
          </section>
        } @else if (w.templates.length) { <p class="ok" data-testid="no-shortages"><mat-icon inline>check_circle</mat-icon> Every shift has the people it needs this week.</p> }
        @if (w.warnings.length) {
          <section class="warn" data-testid="warnings">
            <ul>@for (x of w.warnings; track $index) { <li><mat-icon inline>info</mat-icon> {{ x.message }}</li> }</ul>
          </section>
        }
        @if (!w.templates.length && canManage()) { <p class="mute">No shifts set up yet. Use <b>Shifts</b> to add Morning, Evening … and how many people each needs.</p> }

        @if (!mobile()) {
          <div class="gridwrap">
            <table class="week-grid">
              <thead>
                <tr><th class="who">Who</th>
                  @for (d of days(); track d) {
                    <th [class.today]="d === w.today">{{ shortDay(d) }}@if (shortDays()[d]; as s) { <span class="badge" [attr.title]="s.length + ' shifts short'">{{ s.length }}</span> }</th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (dept of w.departments; track dept.id) {
                  @if (people(dept.id).length) {
                    <tr class="dept"><th [attr.colspan]="8">{{ dept.name }}</th></tr>
                    @for (e of people(dept.id); track e.id) {
                      <tr [attr.data-row]="e.code">
                        <th class="who"><span>{{ e.name }}</span><small class="mute">{{ e.designationName ?? e.code }} · {{ hours(e.paidMinutes) }}</small></th>
                        @for (d of days(); track d) {
                          <td [class.off]="e.weeklyOff === isoDay(d)" [class.out]="!works(e, d)" [class.today]="d === w.today">
                            @for (s of cell(e.id, d); track s.id) {
                              <button class="chip" [class.overlap]="s.overlapAllowed" [class.other]="s.departmentId !== e.departmentId" (click)="open(s)"
                                [disabled]="!canManage()" [attr.data-shift]="e.code + '-' + d">
                                <b>{{ s.templateName ?? 'Shift' }}</b> {{ hm(s.startTime) }}–{{ hm(s.endTime) }}</button>
                            }
                            @if (canManage() && works(e, d)) {
                              <button class="add" (click)="add(e.id, d)" [attr.aria-label]="'Add a shift for ' + e.name + ' on ' + shortDay(d)">+</button>
                            }
                            @if (e.weeklyOff === isoDay(d) && !cell(e.id, d).length) { <small class="mute">off</small> }
                          </td>
                        }
                      </tr>
                    }
                  }
                }
              </tbody>
            </table>
          </div>
        } @else {
          <div class="days" role="tablist">
            @for (d of days(); track d) {
              <button role="tab" [class.on]="d === day()" [attr.aria-selected]="d === day()" (click)="day.set(d)">
                {{ shortDay(d).slice(0, 3) }}<br /><small>{{ +d.slice(8) }}</small>@if (shortDays()[d]) { <i class="dot"></i> }</button>
            }
          </div>
          @for (dept of w.departments; track dept.id) {
            @if (people(dept.id).length) {
              <h3>{{ dept.name }}</h3>
              <ul class="list">
                @for (e of people(dept.id); track e.id) {
                  <li [attr.data-row]="e.code">
                    <div class="nm"><b>{{ e.name }}</b><small class="mute">{{ hours(e.paidMinutes) }} this week</small></div>
                    <div class="sh">
                      @for (s of cell(e.id, day()); track s.id) {
                        <button class="chip" [class.overlap]="s.overlapAllowed" (click)="open(s)" [disabled]="!canManage()" [attr.data-shift]="e.code + '-' + day()">
                          <b>{{ s.templateName ?? 'Shift' }}</b> {{ hm(s.startTime) }}–{{ hm(s.endTime) }}</button>
                      } @empty { <small class="mute">{{ !works(e, day()) ? 'not working here' : e.weeklyOff === isoDay(day()) ? 'day off' : 'not on' }}</small> }
                      @if (canManage() && works(e, day())) { <button class="add" (click)="add(e.id, day())" [attr.aria-label]="'Add a shift for ' + e.name">+</button> }
                    </div>
                  </li>
                }
              </ul>
            }
          }
        }
      }
    </div>
  `,
  styles: `
    .roster { max-width: 1240px; padding-bottom: 60px; }
    .head { display: flex; justify-content: space-between; align-items: baseline; gap: 8px; flex-wrap: wrap; } .range { font-weight: 500; }
    .tools { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; margin-bottom: 8px; } .grow { flex: 1; }
    .mute { color: #5b6b6e; } .err { color: #b3261e; } .ok { color: #1e6b3a; }
    .short { border: 1px solid #e3a6a1; background: #fdeceb; border-radius: 12px; padding: 6px 12px 8px; margin-bottom: 8px; }
    .short h2 { font-size: 1rem; color: #8c1d18; margin: 4px 0; } .short ul, .warn ul { list-style: none; margin: 0; padding: 0; display: flex; flex-wrap: wrap; gap: 0 12px; }
    .short li button { color: #8c1d18; padding: 0 6px; height: auto; min-height: 32px; text-align: left; white-space: normal; } .short li.today b { text-decoration: underline; }
    .warn { border: 1px solid #ecd29a; background: #fdf6e6; border-radius: 12px; padding: 6px 12px; margin-bottom: 8px; color: #6b4a00; } .warn li { padding: 2px 0; }
    .gridwrap { overflow-x: auto; background: #fff; border: 1px solid #d9e0de; border-radius: 12px; }
    table.week-grid { border-collapse: collapse; width: 100%; min-width: 900px; font-size: .88rem; }
    .week-grid th, .week-grid td { border-bottom: 1px solid #eef1f0; padding: 4px 6px; vertical-align: top; text-align: left; }
    .week-grid thead th { position: sticky; top: 0; background: #f6f8f7; font-weight: 600; white-space: nowrap; } .week-grid thead th.today { background: #e6f0ff; }
    .week-grid td.today { background: #f5f9ff; } .week-grid td.off { background: repeating-linear-gradient(45deg, #fafafa, #fafafa 6px, #f1f3f2 6px, #f1f3f2 12px); }
    .week-grid td.out { background: #f3f3f3; }
    .week-grid .who { width: 170px; } .week-grid th.who { display: table-cell; } .week-grid th.who span { display: block; } .week-grid th.who small { display: block; font-weight: normal; }
    .week-grid tr.dept th { background: #eef3f1; font-size: .8rem; text-transform: uppercase; letter-spacing: .03em; color: #34484b; }
    .badge { display: inline-block; background: #b3261e; color: #fff; border-radius: 9px; font-size: .72rem; padding: 0 6px; margin-left: 4px; }
    .chip { display: block; width: 100%; text-align: left; border: 1px solid #b9d3c6; background: #eaf5ef; border-radius: 8px; padding: 2px 6px; margin: 2px 0; cursor: pointer; font: inherit; color: inherit; }
    .chip:disabled { cursor: default; } .chip.overlap { border-color: #e0a100; background: #fff4d6; } .chip.other { border-style: dashed; }
    .add { border: 1px dashed #b5c2c0; background: none; border-radius: 8px; width: 100%; color: #5b6b6e; cursor: pointer; font-size: 1rem; line-height: 1.3; }
    .days { display: grid; grid-template-columns: repeat(7, 1fr); gap: 4px; margin: 4px 0 8px; }
    .days button { border: 1px solid #d9e0de; background: #fff; border-radius: 10px; padding: 4px 0; font: inherit; position: relative; }
    .days button.on { background: #1d5c4e; color: #fff; border-color: #1d5c4e; } .dot { position: absolute; top: 3px; right: 5px; width: 7px; height: 7px; border-radius: 50%; background: #b3261e; }
    h3 { font-size: .85rem; text-transform: uppercase; color: #34484b; margin: 12px 0 4px; }
    .list { list-style: none; margin: 0; padding: 0; } .list li { display: flex; gap: 8px; align-items: flex-start; background: #fff; border: 1px solid #d9e0de; border-radius: 10px; padding: 6px 10px; margin-top: 6px; }
    .list .nm { flex: 1; display: flex; flex-direction: column; min-width: 0; } .list .sh { flex: 1.2; display: flex; flex-direction: column; gap: 2px; }
    @media (max-width: 760px) { h1 { font-size: 1.35rem; } }
  `,
})
export class RosterPage {
  private api = inject(EmployeesApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  mobile = toSignal(inject(BreakpointObserver).observe('(max-width: 760px)').pipe(map(r => r.matches)), { initialValue: false });

  week = signal<RosterWeek | null>(null);
  error = signal('');
  private monday = signal<string | null>(null);
  day = signal('');
  dayText = dayText; hm = hm; hours = hours; shortDay = shortDay; isoDay = isoDay;
  private pid = computed(() => this.ctx.current()?.id ?? '');
  canManage = computed(() => this.auth.can('roster.manage', this.pid()));
  canEmployees = computed(() => this.auth.can('employee.read', this.pid()));
  days = computed(() => { const w = this.week(); return w ? Array.from({ length: 7 }, (_, i) => plusDays(w.weekStart, i)) : []; });
  shortDays = computed(() => shortagesByDay(this.week()?.shortages ?? []));
  missing = computed(() => (this.week()?.shortages ?? []).reduce((t, s) => t + s.missing, 0));

  constructor() {
    effect(() => { if (this.pid()) this.load(); });
  }

  load() {
    this.api.week(this.pid(), this.monday()).subscribe({
      next: w => {
        this.week.set(w);
        this.monday.set(w.weekStart);
        this.error.set('');
        if (!this.day() || this.day() < w.weekStart || this.day() > w.weekEnd) this.day.set(w.today >= w.weekStart && w.today <= w.weekEnd ? w.today : w.weekStart);
      },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  go(days: number) { const m = this.monday(); if (m) { this.monday.set(plusDays(m, days)); this.load(); } }
  thisWeek() { this.monday.set(null); this.day.set(''); this.load(); }

  people(deptId: string): RosterEmployee[] { return (this.week()?.employees ?? []).filter(e => e.departmentId === deptId); }
  cell(employeeId: string, date: string): RosterShift[] { const w = this.week(); return w ? cellShifts(w, employeeId, date) : []; }
  works(e: RosterEmployee, d: string) { return e.joinedOn <= d && (!e.leftOn || e.leftOn >= d); }

  add(employeeId: string, date: string) { this.shiftDialog({ employeeId, date, templateId: null, shift: null }); }
  fill(s: RosterShortage) { this.shiftDialog({ employeeId: null, date: s.date, templateId: s.templateId, shift: null }); }
  open(s: RosterShift) { this.shiftDialog({ employeeId: s.employeeId, date: s.workDate, templateId: s.templateId, shift: s }); }

  private shiftDialog(d: Omit<ShiftData, 'propertyId' | 'week'>) {
    const data: ShiftData = { propertyId: this.pid(), week: this.week()!, ...d };
    this.dialog.open(ShiftDialog, { data, maxWidth: '100vw' }).afterClosed().subscribe((r?: ShiftResult) => {
      if (!r) return;
      this.snack.open(r.message, 'OK', { duration: 4000 });
      this.load();
    });
  }

  templates() {
    this.dialog.open(TemplatesDialog, { data: { propertyId: this.pid(), departments: this.week()?.departments ?? [] }, maxWidth: '100vw' })
      .afterClosed().subscribe(changed => { if (changed) this.load(); });
  }

  copy() {
    const to = this.monday() ?? mondayOf(this.week()!.today);
    this.dialog.open(CopyWeekDialog, { data: { propertyId: this.pid(), toWeek: to }, maxWidth: '100vw' }).afterClosed().subscribe((r?: CopyRosterWeekResult) => {
      if (r) this.week.set(r.week);
    });
  }
}
