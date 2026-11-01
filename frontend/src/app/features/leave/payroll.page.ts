import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { toProblem } from '../../core/http';
import { PayrollExport, PayrollPreview } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { hours } from '../employees/employees.api';
import { LeaveApi, monthName, short } from './leave.api';

/**
 * Payroll export (Sprint 19, brief §45). Choose a month: the table shows what the file will hold for each person (days
 * employed, holidays, days and hours worked, overtime, paid and unpaid leave, days not worked, payable days) and what
 * isn't ready yet (weeks not approved, leave not decided). Making the file locks the approved weeks it used; every file
 * made is kept and can be downloaded again.
 */
@Component({
  selector: 'app-payroll',
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSnackBarModule],
  template: `
    <div class="page payroll">
      <div class="head"><h1>Payroll export</h1></div>
      <p class="mute intro">A CSV file for the payroll provider: days and hours for each person in the month (no pay amounts; the provider holds the salaries).
        Hours come from approved timesheets; making the file locks them.</p>
      <div class="pick">
        <mat-form-field appearance="outline"><mat-label>Month</mat-label>
          <input matInput type="month" [ngModel]="month()" (ngModelChange)="month.set($event)" aria-label="Month" />
          @if (monthError()) { <mat-hint class="err">{{ monthError() }}</mat-hint> }
        </mat-form-field>
      </div>

      @if (preview(); as p) {
        <h2>{{ monthName(p.month) }}</h2>
        @if (p.warnings.length) {
          <section class="warnings" data-testid="payroll-warnings">
            <b><mat-icon inline>warning</mat-icon> Not ready yet</b>
            <ul>@for (w of p.warnings; track w) { <li>{{ w }}</li> }</ul>
            <mat-checkbox [ngModel]="confirm()" (ngModelChange)="confirm.set($event)">Make the file anyway; what is listed above is left out</mat-checkbox>
          </section>
        }
        <p class="mute small">{{ p.rows.length }} {{ p.rows.length === 1 ? 'person' : 'people' }}@if (p.holidays.length) { · holidays: @for (h of p.holidays; track h.id) { {{ short(h.date) }} {{ h.name }}{{ $last ? '' : ',' }} } }
          @if (p.approvedToLock) { · making it locks {{ p.approvedToLock }} approved {{ p.approvedToLock === 1 ? 'timesheet' : 'timesheets' }} }</p>
        <div class="scroll">
          <table data-testid="payroll-rows">
            <thead><tr><th>Employee</th><th class="n">Employed</th><th class="n">Holidays</th><th class="n">Worked</th><th class="n">Hours</th>
              <th class="n">Overtime</th><th class="n">Paid leave</th><th class="n">Unpaid</th><th class="n">Not in</th><th class="n">Payable</th></tr></thead>
            <tbody>
              @for (r of p.rows; track r.employeeCode) {
                <tr [attr.data-emp]="r.employeeCode">
                  <td><b>{{ r.employeeName }}</b> <small class="mute">{{ r.employeeCode }} · {{ r.department }}@if (r.leaveDetail) { · {{ r.leaveDetail }} }</small></td>
                  <td class="n">{{ r.employedDays }}</td><td class="n">{{ r.holidays }}</td><td class="n">{{ r.daysWorked }}</td>
                  <td class="n">{{ hours(r.workedMinutes) }}</td><td class="n">{{ r.overtimeMinutes ? hours(r.overtimeMinutes) : '–' }}</td>
                  <td class="n">{{ r.paidLeaveDays || '–' }}</td><td class="n">{{ r.unpaidLeaveDays || '–' }}</td><td class="n">{{ r.absentDays || '–' }}</td>
                  <td class="n"><b>{{ r.payableDays }}</b></td>
                </tr>
              } @empty { <tr><td colspan="10" class="mute">Nobody worked here in this month.</td></tr> }
            </tbody>
          </table>
        </div>
        <div class="go">
          <button mat-flat-button (click)="make()" [disabled]="busy() || (p.warnings.length > 0 && !confirm())"><mat-icon>file_download</mat-icon>Make the file</button>
        </div>
      }
      @if (error()) { <p class="err" role="alert" data-testid="payroll-error">{{ error() }}</p> }

      <h2>Files made</h2>
      <ul class="files" data-testid="payroll-files">
        @for (x of exports(); track x.id) {
          <li><div><b>{{ monthName(x.month) }}</b> <small class="mute">{{ x.employees }} people · made {{ when(x.createdAt) }} by {{ x.by }}
            @if (x.timesheetsLocked) { · locked {{ x.timesheetsLocked }} } @if (x.warnings.length) { · {{ x.warnings.length }} left out }</small>
            <small class="mute sha">SHA-256 {{ x.sha256.slice(0, 16) }}…</small></div>
            <button mat-stroked-button (click)="download(x)"><mat-icon>download</mat-icon>{{ x.fileName }}</button></li>
        } @empty { <li class="mute">No payroll files yet.</li> }
      </ul>
    </div>
  `,
  styles: `
    .payroll { max-width: 1100px; padding-bottom: 60px; } .mute { color: #5b6b6e; } .small { font-size: .85rem; } .err { color: #b3261e; }
    .intro { max-width: 760px; } .pick { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
    h2 { margin: 16px 0 6px; font-size: 1.1rem; }
    .warnings { background: #fffaf0; border: 1px solid #e0b34a; border-radius: 12px; padding: 10px 14px; margin-bottom: 8px; } .warnings ul { margin: 6px 0; padding-left: 20px; }
    .scroll { overflow-x: auto; background: #fff; border: 1px solid #d9e0de; border-radius: 14px; }
    table { width: 100%; border-collapse: collapse; font-size: .92rem; } th, td { padding: 8px 10px; border-bottom: 1px solid #eef2f1; text-align: left; white-space: nowrap; }
    td small { display: block; white-space: normal; } .n { text-align: right; }
    .go { margin: 12px 0; }
    .files { list-style: none; padding: 0; margin: 0; background: #fff; border: 1px solid #d9e0de; border-radius: 14px; }
    .files li { display: flex; gap: 10px; align-items: center; justify-content: space-between; padding: 10px 14px; border-top: 1px solid #eef2f1; flex-wrap: wrap; }
    .files li:first-child { border-top: 0; } .files li > div { display: flex; flex-direction: column; min-width: 0; } .sha { font-family: monospace; }
  `,
})
export class PayrollPage {
  private api = inject(LeaveApi);
  private ctx = inject(PropertyContext);
  private snack = inject(MatSnackBar);
  hours = hours; short = short; monthName = monthName;
  private pid = computed(() => this.ctx.current()?.id ?? '');
  /** Last month by default: the usual one to send. */
  month = signal(lastMonth());
  preview = signal<PayrollPreview | null>(null);
  exports = signal<PayrollExport[]>([]);
  confirm = signal(false);
  busy = signal(false);
  error = signal('');
  monthError = signal('');

  constructor() {
    effect(() => {
      const m = this.month();
      if (this.pid() && m) untracked(() => this.load(m));
    });
  }

  when(iso: string) {
    return inZone(iso, this.ctx.current()?.timezone ?? 'Asia/Kolkata', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false });
  }

  load(m: string) {
    this.error.set('');
    this.monthError.set('');
    this.confirm.set(false);
    this.api.payrollPreview(this.pid(), m).subscribe({
      next: p => this.preview.set(p),
      error: e => {
        this.preview.set(null);
        const p = toProblem(e);
        if (p.code === 'VALIDATION_FAILED' && p.errors?.['Month']) this.monthError.set(p.errors['Month'][0]); else this.error.set(p.detail);
      },
    });
    this.api.payrollExports(this.pid()).subscribe({ next: x => this.exports.set(x) });
  }

  make() {
    const p = this.preview();
    if (!p) return;
    this.busy.set(true);
    this.error.set('');
    this.api.exportPayroll(this.pid(), p.month, this.confirm()).subscribe({
      next: x => {
        this.busy.set(false);
        this.snack.open(`Payroll file for ${monthName(x.month)} made: ${x.employees} people${x.timesheetsLocked ? `, ${x.timesheetsLocked} timesheets locked` : ''}.`, 'OK',
          { duration: 6000 });
        this.download(x);
        this.load(p.month);
      },
      error: e => { this.busy.set(false); this.error.set(toProblem(e).detail); },
    });
  }

  download(x: PayrollExport) {
    this.api.payrollFile(this.pid(), x.id).subscribe({
      next: blob => {
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = x.fileName;
        a.click();
        setTimeout(() => URL.revokeObjectURL(url), 60_000);
      },
      error: e => this.error.set(toProblem(e).detail),
    });
  }
}

function lastMonth(): string {
  const d = new Date();
  d.setDate(1);
  d.setMonth(d.getMonth() - 1);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
}
