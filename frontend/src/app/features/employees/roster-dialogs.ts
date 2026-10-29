import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { AbstractControl, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { map } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { dayText, pickerError } from '../../core/dates';
import { toProblem } from '../../core/http';
import { CopyRosterWeekResult, RosterShift, RosterWeek, ShiftTemplate } from '../../core/models';
import { ReasonData, ReasonDialog } from '../restaurant/order-dialogs';
import { report } from './employee-dialogs';
import { EmployeesApi, hm, hours, plusDays } from './employees.api';

const server = (c: AbstractControl | null) => (c?.errors?.['server'] as string | undefined) ?? '';
const text = (v: string | null | undefined) => (v ?? '').trim() || null;
/** "07:00" → "07:00:00" (the API's time format). */
const time = (v: string | null | undefined) => (v ? (v.length === 5 ? v + ':00' : v) : null);
const OTHER = 'OTHER';

/** What the roster page sends back: 'saved' or 'removed', with the message for the snack bar. */
export interface ShiftResult { kind: 'saved' | 'removed'; message: string; }

export interface ShiftData {
  propertyId: string; week: RosterWeek; shift: RosterShift | null; employeeId: string | null; date: string | null; templateId: string | null;
}

// ------------------------------------------------------------------ one shift

/**
 * Puts someone on a shift (or changes or removes one). A shift that overlaps another of theirs is refused with the reason
 * (naming the other shift); the manager can then allow the overlap with a reason, e.g. a handover.
 */
@Component({
  selector: 'app-shift-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatDatepickerModule, MatFormFieldModule, MatIconModule, MatInputModule,
    MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ s ? 'Change shift' : 'Add to the roster' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="shift-form">
      <mat-form-field appearance="outline"><mat-label>Who</mat-label>
        <mat-select formControlName="employeeId" aria-label="Who">
          @for (d of data.week.departments; track d.id) {
            <mat-optgroup [label]="d.name">
              @for (e of people(d.id); track e.id) { <mat-option [value]="e.id">{{ e.name }} · {{ hours(e.paidMinutes) }} this week</mat-option> }
            </mat-optgroup>
          }
        </mat-select>
        <mat-error>{{ server(form.get('employeeId')) || 'Choose who works this shift.' }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Day</mat-label>
        <input matInput [matDatepicker]="dp" formControlName="workDate" />
        <mat-datepicker-toggle matIconSuffix [for]="dp" /><mat-datepicker #dp />
        <mat-error>{{ server(form.get('workDate')) || pickerError(form.get('workDate')?.errors) || 'Choose the day.' }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Shift</mat-label>
        <mat-select formControlName="templateId" aria-label="Shift">
          @for (t of templates(); track t.id) {
            <mat-option [value]="t.id">{{ t.departmentName }} · {{ t.name }} {{ hm(t.startTime) }}–{{ hm(t.endTime) }}@if (t.crossesMidnight) { (next day) }</mat-option>
          }
          <mat-option [value]="other">Other times</mat-option>
        </mat-select>
        <mat-error>{{ server(form.get('templateId')) || 'Choose a shift or other times.' }}</mat-error></mat-form-field>
      @if (custom()) {
        <div class="row">
          <mat-form-field appearance="outline"><mat-label>Department</mat-label>
            <mat-select formControlName="departmentId" aria-label="Department">
              @for (d of data.week.departments; track d.id) { <mat-option [value]="d.id">{{ d.name }}</mat-option> }
            </mat-select>
            <mat-error>{{ server(form.get('departmentId')) }}</mat-error></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Break (min)</mat-label><input matInput type="number" inputmode="numeric" formControlName="breakMinutes" />
            <mat-error>{{ server(form.get('breakMinutes')) || 'The break is 0–180 minutes.' }}</mat-error></mat-form-field>
        </div>
        <div class="row">
          <mat-form-field appearance="outline"><mat-label>Starts</mat-label><input matInput type="time" formControlName="startTime" />
            <mat-error>{{ server(form.get('startTime')) || 'Give the start time.' }}</mat-error></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Ends</mat-label><input matInput type="time" formControlName="endTime" />
            <mat-hint>Earlier than the start: the next day</mat-hint>
            <mat-error>{{ server(form.get('endTime')) || 'Give the end time.' }}</mat-error></mat-form-field>
        </div>
      } @else if (server(form.get('endTime')) || server(form.get('startTime')) || server(form.get('breakMinutes'))) {
        <p class="err">{{ server(form.get('startTime')) || server(form.get('endTime')) || server(form.get('breakMinutes')) }}</p>
      }
      @if (dayOff()) { <p class="warn">{{ dayOff() }}</p> }
      @if (clash()) {
        <div class="clash" role="alert" data-testid="clash">
          <p><mat-icon inline>warning</mat-icon> {{ clash() }}</p>
          <mat-checkbox formControlName="allowOverlap">Allow the overlap</mat-checkbox>
          @if (form.controls.allowOverlap.value) {
            <mat-form-field appearance="outline"><mat-label>Why</mat-label><input matInput formControlName="overlapReason" maxlength="200" placeholder="e.g. Handover to the dinner chef" />
              <mat-error>{{ server(form.get('overlapReason')) || 'Say why, at least 3 characters.' }}</mat-error></mat-form-field>
          }
        </div>
      }
      <mat-form-field appearance="outline"><mat-label>Note</mat-label><input matInput formControlName="note" maxlength="200" />
        <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      @if (s) { <button mat-button color="warn" type="button" (click)="remove()" class="rm">Remove from roster</button> }
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="shift-form" [disabled]="busy()">{{ s ? 'Save' : 'Add' }}</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(480px, 84vw); } .row { display: flex; gap: 8px; flex-wrap: wrap; }
           .row > * { flex: 1 1 160px; } .err { color: #b3261e; } .warn { color: #8a5a00; margin: 0 0 8px; }
           .clash { border: 1px solid #e3a6a1; background: #fdeceb; border-radius: 10px; padding: 8px 12px; margin-bottom: 8px; display: flex; flex-direction: column; }
           .clash p { margin: 0 0 4px; color: #8c1d18; } .rm { margin-right: auto; }`,
})
export class ShiftDialog {
  data = inject<ShiftData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<ShiftDialog, ShiftResult>);
  private api = inject(EmployeesApi);
  private dialog = inject(MatDialog);
  server = server; pickerError = pickerError; hm = hm; hours = hours;
  other = OTHER;
  busy = signal(false);
  error = signal('');
  clash = signal('');
  s = this.data.shift;
  templates = computed(() => this.data.week.templates.filter(t => t.isActive || t.id === this.s?.templateId));
  form = inject(FormBuilder).group({
    employeeId: [this.s?.employeeId ?? this.data.employeeId, Validators.required],
    workDate: [this.s?.workDate ?? this.data.date ?? this.data.week.weekStart, Validators.required],
    templateId: [this.s ? (this.s.templateId ?? OTHER) : (this.data.templateId ?? this.defaultTemplate()), Validators.required],
    departmentId: [this.s?.departmentId ?? null as string | null],
    startTime: [this.s ? hm(this.s.startTime) : ''],
    endTime: [this.s ? hm(this.s.endTime) : ''],
    breakMinutes: [this.s?.breakMinutes ?? 30],
    allowOverlap: [this.s?.overlapAllowed ?? false],
    overlapReason: [this.s?.overlapReason ?? ''],
    note: [this.s?.note ?? ''],
  });
  private tpl = toSignal(this.form.controls.templateId.valueChanges, { initialValue: this.form.controls.templateId.value });
  private emp = toSignal(this.form.controls.employeeId.valueChanges, { initialValue: this.form.controls.employeeId.value });
  private day = toSignal(this.form.controls.workDate.valueChanges, { initialValue: this.form.controls.workDate.value });
  custom = computed(() => this.tpl() === OTHER);
  dayOff = computed(() => {
    const e = this.data.week.employees.find(x => x.id === this.emp()), d = this.day();
    if (!e?.weeklyOff || !d) return '';
    const [y, m, dd] = d.split('-').map(Number), dow = new Date(Date.UTC(y, m - 1, dd)).getUTCDay();
    return (dow === 0 ? 7 : dow) === e.weeklyOff ? `${dayText(d)} is ${e.name}'s weekly day off. You can still put them on.` : '';
  });

  constructor() {
    if (this.s?.overlapAllowed) this.clash.set(`This shift overlaps another of ${this.s.employeeName}'s shifts: ${this.s.overlapReason}`);
    this.form.controls.employeeId.valueChanges.subscribe(() => this.clearClash());
    this.form.controls.workDate.valueChanges.subscribe(() => this.clearClash());
    this.form.controls.templateId.valueChanges.subscribe(() => this.clearClash());
  }

  private defaultTemplate(): string {
    const e = this.data.week.employees.find(x => x.id === this.data.employeeId);
    return this.data.week.templates.find(t => t.isActive && (!e || t.departmentId === e.departmentId))?.id ?? OTHER;
  }

  private clearClash() {
    if (this.form.controls.allowOverlap.value && this.clash()) return;
    this.clash.set('');
  }

  people(deptId: string) {
    const d = this.form.controls.workDate.value ?? this.data.week.weekStart;
    return this.data.week.employees.filter(e => e.departmentId === deptId && e.joinedOn <= d && (!e.leftOn || e.leftOn >= d));
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.error.set('');
    const v = this.form.getRawValue();
    const custom = v.templateId === OTHER;
    this.api.saveShift(this.data.propertyId, this.s?.id ?? null, {
      employeeId: v.employeeId!, workDate: v.workDate!, templateId: custom ? null : v.templateId, departmentId: custom ? v.departmentId : null,
      startTime: custom ? time(v.startTime) : null, endTime: custom ? time(v.endTime) : null, breakMinutes: custom ? Number(v.breakMinutes ?? 0) : null,
      allowOverlap: !!(this.clash() && v.allowOverlap), overlapReason: this.clash() && v.allowOverlap ? text(v.overlapReason) : null, note: text(v.note),
      version: this.s?.version ?? null,
    }).subscribe({
      next: x => this.ref.close({ kind: 'saved', message: `${x.employeeName} on ${x.templateName ?? 'a shift'} ${hm(x.startTime)}–${hm(x.endTime)}, ${dayText(x.workDate)}.` }),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        if (p.code === 'SHIFT_CLASH') { this.clash.set(p.detail); this.form.controls.allowOverlap.setValue(false); return; }
        report(this.form, e, m => this.error.set(m));
      },
    });
  }

  remove() {
    const s = this.s!;
    const data: ReasonData<boolean> = {
      title: `Take ${s.employeeName} off ${dayText(s.workDate)}`, intro: `${hm(s.startTime)}–${hm(s.endTime)}${s.templateName ? ' · ' + s.templateName : ''}. The change is kept in the audit trail.`,
      button: 'Remove', placeholder: 'Swapped with Ravi', run: r => this.api.cancelShift(this.data.propertyId, s.id, r, s.version).pipe(map(() => true)),
    };
    this.dialog.open(ReasonDialog<boolean>, { data, maxWidth: '100vw' }).afterClosed().subscribe(ok => {
      if (ok === true) this.ref.close({ kind: 'removed', message: `${s.employeeName} is off the roster on ${dayText(s.workDate)}.` });
    });
  }
}

// ------------------------------------------------------------------ shift templates

/** The shifts each department works, and how many people each needs every day (what the shortage view checks). */
@Component({
  selector: 'app-templates-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Shifts and people needed</h2>
    <div mat-dialog-content class="body">
      <ul>
        @for (t of list(); track t.id) {
          <li [class.off]="!t.isActive" [attr.data-template]="t.code">
            <div><b>{{ t.departmentName }} · {{ t.name }}</b> {{ hm(t.startTime) }}–{{ hm(t.endTime) }}@if (t.crossesMidnight) { (next day) }
              <small class="mute">· {{ t.code }} · {{ hours(t.paidMinutes) }} paid · needs {{ t.minStaff || 'nobody fixed' }}@if (!t.isActive) { · switched off }</small></div>
            <button mat-button type="button" (click)="edit(t)">Edit</button>
          </li>
        } @empty { <li class="mute">No shifts yet. Add Morning 07:00–15:00 for Front office, for example.</li> }
      </ul>
      <form [formGroup]="form" (ngSubmit)="save()" class="box" id="template-form">
        <b>{{ editing() ? 'Edit ' + editing()!.name : 'Add a shift' }}</b>
        <div class="row">
          <mat-form-field appearance="outline"><mat-label>Department</mat-label>
            <mat-select formControlName="departmentId" aria-label="Shift department">
              @for (d of data.departments; track d.id) { <mat-option [value]="d.id">{{ d.name }}</mat-option> }
            </mat-select>
            <mat-error>{{ server(form.get('departmentId')) || 'Choose the department.' }}</mat-error></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" maxlength="40" placeholder="e.g. Morning" />
            <mat-error>{{ server(form.get('name')) || 'Give the name, 2–40 characters.' }}</mat-error></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Code</mat-label><input matInput formControlName="code" maxlength="20" placeholder="e.g. FO-AM" />
            <mat-error>{{ server(form.get('code')) || 'The code is 1–20 letters, digits, - or _.' }}</mat-error></mat-form-field>
        </div>
        <div class="row">
          <mat-form-field appearance="outline"><mat-label>Starts</mat-label><input matInput type="time" formControlName="startTime" />
            <mat-error>{{ server(form.get('startTime')) || 'Give the start time.' }}</mat-error></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Ends</mat-label><input matInput type="time" formControlName="endTime" />
            <mat-hint>Earlier than the start: the next day</mat-hint>
            <mat-error>{{ server(form.get('endTime')) || 'Give the end time.' }}</mat-error></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Break (min)</mat-label><input matInput type="number" inputmode="numeric" formControlName="breakMinutes" />
            <mat-error>{{ server(form.get('breakMinutes')) || 'The break is 0–180 minutes.' }}</mat-error></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>People needed</mat-label><input matInput type="number" inputmode="numeric" formControlName="minStaff" />
            <mat-hint>Every day; 0: no minimum</mat-hint>
            <mat-error>{{ server(form.get('minStaff')) || 'Enter 0–50.' }}</mat-error></mat-form-field>
        </div>
        @if (editing()) { <mat-checkbox formControlName="isActive">In use</mat-checkbox> }
        <div class="acts">
          @if (editing()) { <button mat-button type="button" (click)="edit(null)">New instead</button> }
          <button mat-flat-button type="submit" [disabled]="busy()">{{ editing() ? 'Save shift' : 'Add shift' }}</button>
        </div>
      </form>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>
    <div mat-dialog-actions align="end"><button mat-button [mat-dialog-close]="changed()" type="button">Done</button></div>
  `,
  styles: `.body { min-width: min(620px, 84vw); display: flex; flex-direction: column; gap: 12px; } ul { list-style: none; margin: 0; padding: 0; }
           li { display: flex; justify-content: space-between; align-items: center; gap: 8px; padding: 6px 0; border-bottom: 1px solid #e3e8e6; } li.off { opacity: .6; }
           .box { display: flex; flex-direction: column; gap: 4px; border: 1px solid #d9e0de; border-radius: 12px; padding: 10px 12px; }
           .row { display: flex; gap: 8px; flex-wrap: wrap; } .row > * { flex: 1 1 130px; } .acts { display: flex; justify-content: flex-end; gap: 8px; }
           .mute { color: #5b6b6e; } .err { color: #b3261e; }`,
})
export class TemplatesDialog {
  data = inject<{ propertyId: string; departments: { id: string; name: string }[] }>(MAT_DIALOG_DATA);
  private api = inject(EmployeesApi);
  server = server; hm = hm; hours = hours;
  list = signal<ShiftTemplate[]>([]);
  editing = signal<ShiftTemplate | null>(null);
  changed = signal(false);
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).nonNullable.group({
    departmentId: [this.data.departments[0]?.id ?? '', Validators.required], code: ['', Validators.required], name: ['', [Validators.required, Validators.minLength(2)]],
    startTime: ['07:00', Validators.required], endTime: ['15:00', Validators.required], breakMinutes: [30, [Validators.min(0), Validators.max(180)]],
    minStaff: [1, [Validators.min(0), Validators.max(50)]], isActive: [true],
  });

  constructor() { this.load(); }

  load() { this.api.templates(this.data.propertyId).subscribe({ next: t => this.list.set(t), error: e => this.error.set(toProblem(e).detail) }); }

  edit(t: ShiftTemplate | null) {
    this.editing.set(t);
    this.form.reset({
      departmentId: t?.departmentId ?? this.data.departments[0]?.id ?? '', code: t?.code ?? '', name: t?.name ?? '', startTime: t ? hm(t.startTime) : '07:00',
      endTime: t ? hm(t.endTime) : '15:00', breakMinutes: t?.breakMinutes ?? 30, minStaff: t?.minStaff ?? 1, isActive: t?.isActive ?? true,
    });
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue(), t = this.editing();
    this.busy.set(true);
    this.error.set('');
    this.api.saveTemplate(this.data.propertyId, t?.id ?? null, {
      departmentId: v.departmentId, code: v.code.trim(), name: v.name.trim(), startTime: time(v.startTime)!, endTime: time(v.endTime)!,
      breakMinutes: Number(v.breakMinutes), minStaff: Number(v.minStaff), isActive: v.isActive, version: t?.version ?? null,
    }).subscribe({
      next: () => { this.busy.set(false); this.changed.set(true); this.edit(null); this.load(); },
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ copy a week

/** Copies the week before into the week on screen, then lists what it skipped and why. */
@Component({
  selector: 'app-copy-week-dialog',
  imports: [MatDialogModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>Copy last week</h2>
    <div mat-dialog-content class="body">
      @if (!result()) {
        <p>Copy every shift of the week of {{ dayText(from) }} into the week of {{ dayText(data.toWeek) }}, day for day?</p>
        <p class="mute">Shifts already on this week stay. A copy that would clash, falls after someone leaves or uses a switched-off shift is skipped and listed.</p>
      } @else {
        <p data-testid="copied"><b>{{ result()!.copied }} {{ result()!.copied === 1 ? 'shift' : 'shifts' }} copied.</b></p>
        @if (result()!.skipped.length) {
          <p>Skipped:</p>
          <ul>@for (s of result()!.skipped; track $index) { <li>{{ s }}</li> }</ul>
        }
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>
    <div mat-dialog-actions align="end">
      @if (!result()) {
        <button mat-button mat-dialog-close type="button">Cancel</button>
        <button mat-flat-button type="button" (click)="run()" [disabled]="busy()">Copy</button>
      } @else { <button mat-flat-button [mat-dialog-close]="result()" type="button">Done</button> }
    </div>
  `,
  styles: `.body { min-width: min(440px, 84vw); } .mute { color: #5b6b6e; } .err { color: #b3261e; } ul { padding-left: 18px; }`,
})
export class CopyWeekDialog {
  data = inject<{ propertyId: string; toWeek: string }>(MAT_DIALOG_DATA);
  private api = inject(EmployeesApi);
  dayText = dayText;
  from = plusDays(this.data.toWeek, -7);
  busy = signal(false);
  error = signal('');
  result = signal<CopyRosterWeekResult | null>(null);

  run() {
    this.busy.set(true);
    this.error.set('');
    this.api.copyWeek(this.data.propertyId, this.from, this.data.toWeek).subscribe({
      next: r => { this.busy.set(false); this.result.set(r); },
      error: e => { this.busy.set(false); const p = toProblem(e); this.error.set(Object.values(p.errors ?? {})[0]?.[0] ?? p.detail); },
    });
  }
}
