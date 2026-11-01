import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AbstractControl, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { debounceTime } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { toProblem } from '../../core/http';
import { Holiday, LeaveAccrual, LeaveLedger, LeavePreview, LeaveRequest, LeaveType } from '../../core/models';
import { dayText } from '../../core/dates';
import { ACCRUAL_LABEL, AskLeave, ENTRY_LABEL, LeaveApi, days, showError } from './leave.api';

const server = (c: AbstractControl | null) => (c?.errors?.['server'] as string | undefined) ?? '';
const text = (v: string | null | undefined) => (v ?? '').trim() || null;

// ------------------------------------------------------------------ asking for leave

export interface AskData {
  propertyId: string; types: LeaveType[]; today: string;
  /** For a manager asking for someone else: the people to choose from. */
  employees?: { id: string; name: string }[];
}

/**
 * Asking for leave: the kind, the first and last day, a half day at either end, and why. As the dates change, the server
 * works out how many days count (holidays and the weekly day off don't), the balance after, and what it does to the
 * roster, so nothing is a surprise when it is sent.
 */
@Component({
  selector: 'app-ask-leave-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ data.employees ? 'Leave for someone' : 'Ask for leave' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="ask-form">
      @if (data.employees) {
        <mat-form-field appearance="outline"><mat-label>Who</mat-label>
          <mat-select formControlName="employeeId" [aria-label]="'Who'">
            @for (e of data.employees; track e.id) { <mat-option [value]="e.id">{{ e.name }}</mat-option> }
          </mat-select>
          <mat-error>{{ server(form.get('employeeId')) || 'Choose who is off.' }}</mat-error></mat-form-field>
      }
      <mat-form-field appearance="outline"><mat-label>Kind of leave</mat-label>
        <mat-select formControlName="leaveTypeId" [aria-label]="'Kind of leave'">
          @for (t of data.types; track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }
        </mat-select>
        <mat-error>{{ server(form.get('leaveTypeId')) || 'Choose the kind of leave.' }}</mat-error></mat-form-field>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>From</mat-label><input matInput type="date" formControlName="fromDate" />
          <mat-error>{{ server(form.get('fromDate')) || 'Choose the first day.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>To</mat-label><input matInput type="date" formControlName="toDate" />
          <mat-error>{{ server(form.get('toDate')) || 'Choose the last day.' }}</mat-error></mat-form-field>
      </div>
      @if (type()?.allowHalfDay) {
        <div class="halves">
          <mat-checkbox formControlName="halfDayStart">{{ oneDay() ? 'Afternoon only' : 'First day: from lunch' }}</mat-checkbox>
          <mat-checkbox formControlName="halfDayEnd">{{ oneDay() ? 'Morning only' : 'Last day: until lunch' }}</mat-checkbox>
        </div>
        @if (server(form.get('halfDayStart')) || server(form.get('halfDayEnd'))) {
          <p class="err" role="alert">{{ server(form.get('halfDayStart')) || server(form.get('halfDayEnd')) }}</p>
        }
      }
      <mat-form-field appearance="outline"><mat-label>Reason (optional)</mat-label>
        <input matInput formControlName="reason" maxlength="300" placeholder="e.g. Sister's wedding" />
        <mat-error>{{ server(form.get('reason')) }}</mat-error></mat-form-field>

      @if (preview(); as p) {
        <section class="preview" data-testid="leave-preview">
          <p class="big"><b>{{ days(p.days) }}</b>
            @if (p.balanceAfter !== null) { <span> · {{ days(p.available!) }} available, <b>{{ days(p.balanceAfter) }}</b> left after</span> }
          </p>
          @if (p.skipped.length) { <p class="mute small">Not counted: {{ p.skipped.join('; ') }}</p> }
          @for (w of p.warnings; track w.message) {
            <p class="w" [class]="'k-' + w.kind"><mat-icon inline>{{ w.kind === 'ROSTERED' ? 'event_busy' : 'warning' }}</mat-icon> {{ w.message }}</p>
          }
        </section>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="ask-form" [disabled]="busy()">Send</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(460px, 84vw); } .row { display: flex; gap: 8px; flex-wrap: wrap; }
           .row > * { flex: 1 1 150px; } .halves { display: flex; gap: 12px; flex-wrap: wrap; margin: -4px 0 8px; }
           .err { color: #b3261e; } .mute { color: #5b6b6e; } .small { font-size: .85rem; }
           .preview { background: #f4f8f7; border-radius: 10px; padding: 8px 12px; margin-top: 4px; } .preview p { margin: 4px 0; }
           .big { font-size: 1.05rem; } .w { color: #5b4a00; } .k-ROSTERED { color: #0e3b43; }`,
})
export class AskLeaveDialog {
  data = inject<AskData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<AskLeaveDialog, LeaveRequest>);
  private api = inject(LeaveApi);
  server = server; days = days;
  busy = signal(false);
  error = signal('');
  preview = signal<LeavePreview | null>(null);
  form = inject(FormBuilder).group({
    employeeId: [null as string | null, this.data.employees ? Validators.required : []],
    leaveTypeId: [this.data.types[0]?.id ?? null, Validators.required],
    fromDate: [this.data.today, Validators.required],
    toDate: [this.data.today, Validators.required],
    halfDayStart: [false],
    halfDayEnd: [false],
    reason: [''],
  });
  type = signal<LeaveType | undefined>(this.data.types[0]);
  oneDay = signal(true);

  constructor() {
    this.form.valueChanges.pipe(debounceTime(250), takeUntilDestroyed(inject(DestroyRef))).subscribe(v => {
      this.type.set(this.data.types.find(t => t.id === v.leaveTypeId));
      this.oneDay.set(v.fromDate === v.toDate);
      if (!this.type()?.allowHalfDay && (v.halfDayStart || v.halfDayEnd)) {
        this.form.patchValue({ halfDayStart: false, halfDayEnd: false }, { emitEvent: false });
      }
      this.check();
    });
    this.check();
  }

  private body(): AskLeave {
    const v = this.form.getRawValue();
    return { employeeId: v.employeeId, leaveTypeId: v.leaveTypeId!, fromDate: v.fromDate!, toDate: v.toDate!, halfDayStart: !!v.halfDayStart,
      halfDayEnd: !!v.halfDayEnd, reason: text(v.reason) };
  }

  /** Asks the server what these dates come to; its reasons show under the fields straight away. */
  private check() {
    const v = this.form.getRawValue();
    if (!v.leaveTypeId || !v.fromDate || !v.toDate || (this.data.employees && !v.employeeId)) { this.preview.set(null); return; }
    for (const c of Object.values(this.form.controls)) {
      if (!c.errors?.['server']) continue;
      const rest = { ...c.errors };
      delete rest['server'];
      c.setErrors(Object.keys(rest).length ? rest : null);
    }
    this.error.set('');
    this.api.preview(this.data.propertyId, this.body()).subscribe({
      next: p => this.preview.set(p),
      error: e => { this.preview.set(null); showError(this.form, e, m => this.error.set(m)); },
    });
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.error.set('');
    this.api.ask(this.data.propertyId, this.body()).subscribe({
      next: r => this.ref.close(r),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ approving

export interface ApproveData { propertyId: string; request: LeaveRequest; }

/** Approving shows what it does first: the days off the balance, the shifts that come off the roster, the cover. */
@Component({
  selector: 'app-approve-leave-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Approve {{ r.code }}?</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="approve-form">
      <p><b>{{ r.employeeName }}</b> · {{ r.leaveTypeName }} · {{ days(r.days) }}</p>
      @if (r.balance !== null) { <p class="mute">{{ days(r.balance) }} in the balance, {{ days(r.balance - r.days) }} after.</p> }
      @for (w of r.warnings; track w.message) { <p class="w"><mat-icon inline>{{ w.kind === 'ROSTERED' ? 'event_busy' : 'warning' }}</mat-icon> {{ w.message }}</p> }
      <mat-form-field appearance="outline"><mat-label>Note (optional)</mat-label><input matInput formControlName="note" maxlength="300" />
        <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Not now</button>
      <button mat-flat-button type="submit" form="approve-form" [disabled]="busy()">Approve</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; min-width: min(440px, 84vw); } .err { color: #b3261e; } .mute { color: #5b6b6e; }
           .w { color: #5b4a00; margin: 4px 0; } p { margin: 4px 0; }`,
})
export class ApproveLeaveDialog {
  data = inject<ApproveData>(MAT_DIALOG_DATA);
  r = this.data.request;
  private ref = inject(MatDialogRef<ApproveLeaveDialog, LeaveRequest>);
  private api = inject(LeaveApi);
  server = server; days = days;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({ note: [''] });

  save() {
    this.busy.set(true);
    this.error.set('');
    this.api.approve(this.data.propertyId, this.r, text(this.form.value.note)).subscribe({
      next: r => this.ref.close(r),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ a leave type

export interface TypeData { propertyId: string; type: LeaveType | null; }

/** A kind of leave and its policy for everyone: days a year, how they are earned, the most someone can hold. */
@Component({
  selector: 'app-leave-type-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatSlideToggleModule],
  template: `
    <h2 mat-dialog-title>{{ t ? t.name : 'New kind of leave' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="type-form">
      <div class="row">
        <mat-form-field appearance="outline" class="code"><mat-label>Code</mat-label><input matInput formControlName="code" maxlength="10" placeholder="CL" />
          <mat-error>{{ server(form.get('code')) || 'Give a short code, e.g. CL.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" maxlength="60" placeholder="Casual leave" />
          <mat-error>{{ server(form.get('name')) || 'Give the name.' }}</mat-error></mat-form-field>
      </div>
      <div class="toggles">
        <mat-slide-toggle formControlName="isPaid" [aria-label]="'Paid'">Paid</mat-slide-toggle>
        <mat-slide-toggle formControlName="needsBalance" [aria-label]="'Needs a balance'">Needs a balance</mat-slide-toggle>
        <mat-slide-toggle formControlName="allowHalfDay" [aria-label]="'Half days'">Half days</mat-slide-toggle>
        @if (t) { <mat-slide-toggle formControlName="isActive" [aria-label]="'In use'">In use</mat-slide-toggle> }
      </div>
      @if (server(form.get('needsBalance'))) { <p class="err">{{ server(form.get('needsBalance')) }}</p> }
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Days a year</mat-label><input matInput type="number" step="0.5" formControlName="daysPerYear" />
          <mat-error>{{ server(form.get('daysPerYear')) || 'Days a year are 0–365.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Earned</mat-label>
          <mat-select formControlName="accrual" [aria-label]="'Earned'">
            @for (a of accruals; track a) { <mat-option [value]="a">{{ accrualLabel[a] }}</mat-option> }
          </mat-select>
          <mat-error>{{ server(form.get('accrual')) }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>At most</mat-label><input matInput type="number" step="0.5" formControlName="maxBalance" />
          <mat-hint>Empty: no limit</mat-hint>
          <mat-error>{{ server(form.get('maxBalance')) || 'At most 0–999 days.' }}</mat-error></mat-form-field>
      </div>
      @if (others().length) { <p class="mute small">Also: {{ others() }}</p> }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="type-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(520px, 84vw); } .row { display: flex; gap: 8px; flex-wrap: wrap; }
           .row > * { flex: 1 1 140px; } .code { flex: 0 1 110px; } .toggles { display: flex; gap: 16px; flex-wrap: wrap; margin: 0 0 12px; }
           .err { color: #b3261e; } .mute { color: #5b6b6e; } .small { font-size: .85rem; }`,
})
export class LeaveTypeDialog {
  data = inject<TypeData>(MAT_DIALOG_DATA);
  t = this.data.type;
  private ref = inject(MatDialogRef<LeaveTypeDialog, LeaveType>);
  private api = inject(LeaveApi);
  server = server; accrualLabel = ACCRUAL_LABEL;
  accruals: LeaveAccrual[] = ['MONTHLY', 'YEARLY', 'NONE'];
  busy = signal(false);
  error = signal('');
  private everyone = this.t?.policies.find(p => p.employmentType === null);
  form = inject(FormBuilder).group({
    code: [this.t?.code ?? '', Validators.required],
    name: [this.t?.name ?? '', Validators.required],
    isPaid: [this.t?.isPaid ?? true],
    needsBalance: [this.t?.needsBalance ?? true],
    allowHalfDay: [this.t?.allowHalfDay ?? true],
    isActive: [this.t?.isActive ?? true],
    daysPerYear: [this.everyone?.daysPerYear ?? 12, Validators.required],
    accrual: [(this.everyone?.accrual ?? 'MONTHLY') as LeaveAccrual],
    maxBalance: [this.everyone?.maxBalance ?? null as number | null],
  });
  others = signal((this.t?.policies ?? []).filter(p => p.employmentType).map(p =>
    `${p.employmentType!.toLowerCase().replace('_', '-')} staff ${p.daysPerYear} a year`).join('; '));

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.error.set('');
    const v = this.form.getRawValue();
    this.api.saveType(this.data.propertyId, this.t?.id ?? null, {
      code: v.code!.trim().toUpperCase(), name: v.name!.trim(), isPaid: !!v.isPaid, needsBalance: !!v.needsBalance, allowHalfDay: !!v.allowHalfDay,
      sortOrder: this.t?.sortOrder ?? 50, isActive: !!v.isActive, daysPerYear: Number(v.daysPerYear), accrual: v.accrual!,
      maxBalance: v.maxBalance === null || (v.maxBalance as unknown) === '' ? null : Number(v.maxBalance), version: this.t?.version ?? null,
    }).subscribe({
      next: t => this.ref.close(t),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ a holiday

export interface HolidayData { propertyId: string; holiday: Holiday | null; }

@Component({
  selector: 'app-holiday-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSlideToggleModule],
  template: `
    <h2 mat-dialog-title>{{ h ? h.name : 'Add a holiday' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="holiday-form">
      <p class="mute">Leave over a holiday doesn't count it. Leave already asked for keeps its days.</p>
      <mat-form-field appearance="outline"><mat-label>Date</mat-label><input matInput type="date" formControlName="date" />
        <mat-error>{{ server(form.get('date')) || 'Choose the date.' }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" maxlength="60" placeholder="e.g. Diwali" />
        <mat-error>{{ server(form.get('name')) || 'Give the holiday a name.' }}</mat-error></mat-form-field>
      @if (h) { <mat-slide-toggle formControlName="isActive" [aria-label]="'A holiday'">A holiday</mat-slide-toggle> }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="holiday-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(380px, 84vw); } .err { color: #b3261e; } .mute { color: #5b6b6e; margin-top: 0; }`,
})
export class HolidayDialog {
  data = inject<HolidayData>(MAT_DIALOG_DATA);
  h = this.data.holiday;
  private ref = inject(MatDialogRef<HolidayDialog, Holiday>);
  private api = inject(LeaveApi);
  server = server;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({
    date: [this.h?.date ?? '', Validators.required],
    name: [this.h?.name ?? '', [Validators.required, Validators.minLength(2)]],
    isActive: [this.h?.isActive ?? true],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.error.set('');
    const v = this.form.getRawValue();
    this.api.saveHoliday(this.data.propertyId, this.h?.id ?? null, { date: v.date!, name: v.name!.trim(), isActive: !!v.isActive, version: this.h?.version ?? null })
      .subscribe({
        next: h => this.ref.close(h),
        error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
      });
  }
}

// ------------------------------------------------------------------ one person's ledger, and adjusting it

export interface LedgerData { propertyId: string; employeeId: string; canAdjust: boolean; types: LeaveType[]; }

/** Every change to one person's balances, newest first, with the running balance; HR adds or takes off days with a reason. */
@Component({
  selector: 'app-ledger-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ ledger()?.employee?.employeeName ?? 'Leave' }}</h2>
    <div mat-dialog-content>
      @if (ledger(); as l) {
        <div class="bal">
          @for (b of l.employee.balances; track b.leaveTypeId) {
            @if (b.needsBalance) { <span><b>{{ days(b.balance) }}</b> {{ b.code }}</span> }
          }
        </div>
        @if (data.canAdjust) {
          <form [formGroup]="form" (ngSubmit)="adjust()" id="adjust-form" class="adjust">
            <mat-form-field appearance="outline"><mat-label>Leave</mat-label>
              <mat-select formControlName="leaveTypeId" [aria-label]="'Leave'">
                @for (t of balanceTypes; track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }
              </mat-select>
              <mat-error>{{ server(form.get('leaveTypeId')) || 'Choose the leave.' }}</mat-error></mat-form-field>
            <mat-form-field appearance="outline" class="days"><mat-label>Days</mat-label><input matInput type="number" step="0.5" formControlName="days" />
              <mat-hint>e.g. 2, or -1 to take off</mat-hint>
              <mat-error>{{ server(form.get('days')) || 'Give the days.' }}</mat-error></mat-form-field>
            <mat-form-field appearance="outline" class="why"><mat-label>Why</mat-label><input matInput formControlName="reason" maxlength="300" />
              <mat-error>{{ server(form.get('reason')) || 'Say why, at least 3 characters.' }}</mat-error></mat-form-field>
            <mat-checkbox formControlName="opening">Opening balance</mat-checkbox>
            <button mat-flat-button type="submit" [disabled]="busy()">Adjust</button>
          </form>
          @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
        }
        <table class="ledger">
          <thead><tr><th>Date</th><th>What</th><th>Days</th><th>Balance</th></tr></thead>
          <tbody>
            @for (e of l.entries; track e.id) {
              <tr><td>{{ dayText(e.entryDate) }}</td>
                <td>{{ entryLabel[e.entryType] }} {{ e.leaveTypeCode }}@if (e.requestCode) { · {{ e.requestCode }} }@if (e.period) { · {{ e.period }} }
                  @if (e.reason) { <small class="mute">{{ e.reason }}</small> }@if (e.by) { <small class="mute">by {{ e.by }}</small> }</td>
                <td class="n" [class.neg]="e.days < 0">{{ e.days > 0 ? '+' : '' }}{{ e.days }}</td><td class="n">{{ e.balanceAfter }}</td></tr>
            } @empty { <tr><td colspan="4" class="mute">Nothing yet: no leave earned, taken or brought in.</td></tr> }
          </tbody>
        </table>
      } @else if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>
    <div mat-dialog-actions align="end"><button mat-button type="button" (click)="ref.close(changed)">Close</button></div>
  `,
  styles: `.bal { display: flex; gap: 14px; flex-wrap: wrap; margin-bottom: 8px; } .adjust { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
           .adjust > mat-form-field { flex: 1 1 140px; } .adjust .days { flex: 0 1 120px; } .adjust .why { flex: 2 1 200px; }
           .ledger { width: 100%; border-collapse: collapse; margin-top: 8px; font-size: .9rem; } .ledger th, .ledger td { text-align: left; padding: 6px; border-bottom: 1px solid #eef2f1; vertical-align: top; }
           .ledger small { display: block; } .n { text-align: right; white-space: nowrap; } .neg { color: #8c1d18; } .err { color: #b3261e; } .mute { color: #5b6b6e; }
           :host { display: block; min-width: min(640px, 88vw); }`,
})
export class LedgerDialog {
  data = inject<LedgerData>(MAT_DIALOG_DATA);
  ref = inject(MatDialogRef<LedgerDialog, boolean>);
  private api = inject(LeaveApi);
  server = server; days = days; dayText = dayText; entryLabel = ENTRY_LABEL;
  ledger = signal<LeaveLedger | null>(null);
  busy = signal(false);
  error = signal('');
  changed = false;
  balanceTypes = this.data.types.filter(t => t.needsBalance && t.isActive);
  form = inject(FormBuilder).group({
    leaveTypeId: [this.balanceTypes[0]?.id ?? null, Validators.required],
    days: [null as number | null, Validators.required],
    reason: ['', [Validators.required, Validators.minLength(3)]],
    opening: [false],
  });

  constructor() {
    this.api.ledger(this.data.propertyId, this.data.employeeId).subscribe({ next: l => this.ledger.set(l), error: e => this.error.set(toProblem(e).detail) });
  }

  adjust() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.error.set('');
    const v = this.form.getRawValue();
    this.api.adjust(this.data.propertyId, { employeeId: this.data.employeeId, leaveTypeId: v.leaveTypeId!, days: Number(v.days), reason: v.reason!.trim(),
      opening: !!v.opening }).subscribe({
      next: l => { this.busy.set(false); this.ledger.set(l); this.changed = true; this.form.reset({ leaveTypeId: v.leaveTypeId, days: null, reason: '', opening: false }); },
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}
