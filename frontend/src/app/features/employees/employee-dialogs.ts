import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { AbstractControl, FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { pickerError } from '../../core/dates';
import { controlPath, fieldErrors, toProblem } from '../../core/http';
import { Department, Employee, EmployeeCompensation, EmployeeDocument, EmployeeDocumentType, EmploymentType, LinkableAccount, PayBasis } from '../../core/models';
import { rupees } from '../settings/rates/rates.api';
import { DOCUMENT_LABEL, EMPLOYMENT_LABEL, EmployeesApi, WEEKDAYS } from './employees.api';

const server = (c: AbstractControl | null) => (c?.errors?.['server'] as string | undefined) ?? '';
const text = (v: string | null | undefined) => (v ?? '').trim() || null;

/** The API's field messages go under their fields; anything without a field of its own goes to the dialog's error line. */
export function report(form: FormGroup, e: unknown, error: (m: string) => void) {
  const p = toProblem(e);
  const errs = fieldErrors(p);
  const left: string[] = [];
  for (const [k, msg] of Object.entries(errs)) {
    const c = form.get(k) ?? form.get(controlPath(k));
    if (c) { c.setErrors({ ...c.errors, server: msg }); c.markAsTouched(); } else left.push(msg);
  }
  if (!Object.keys(errs).length) error(p.detail);
  else if (left.length) error(left.join(' '));
}

// ------------------------------------------------------------------ an employee

/** An employee's profile: name, department and job title, type, joining date, weekly day off, staff account, contacts. */
@Component({
  selector: 'app-employee-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatDatepickerModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ e ? e.fullName + ' · ' + e.code : 'New employee' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="employee-form">
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>First name</mat-label><input matInput formControlName="firstName" maxlength="60" cdkFocusInitial />
          <mat-error>{{ server(form.get('firstName')) || 'Give the first name.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Last name</mat-label><input matInput formControlName="lastName" maxlength="60" />
          <mat-error>{{ server(form.get('lastName')) }}</mat-error></mat-form-field>
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Department</mat-label>
          <mat-select formControlName="departmentId" aria-label="Department">
            @for (d of departments(); track d.id) { <mat-option [value]="d.id" [disabled]="!d.isActive">{{ d.name }}</mat-option> }
          </mat-select>
          @if (!departments().length) { <mat-hint>Add a department first (Departments button).</mat-hint> }
          <mat-error>{{ server(form.get('departmentId')) || 'Choose the department they work in.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Job title</mat-label>
          <mat-select formControlName="designationId" aria-label="Job title">
            <mat-option [value]="null">None</mat-option>
            @for (x of titles(); track x.id) { <mat-option [value]="x.id" [disabled]="!x.isActive">{{ x.name }}</mat-option> }
          </mat-select>
          <mat-error>{{ server(form.get('designationId')) }}</mat-error></mat-form-field>
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Type</mat-label>
          <mat-select formControlName="employmentType" aria-label="Type">
            @for (t of types; track t) { <mat-option [value]="t">{{ typeLabel[t] }}</mat-option> }
          </mat-select>
          <mat-error>{{ server(form.get('employmentType')) }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Joined on</mat-label>
          <input matInput [matDatepicker]="dpJoin" formControlName="joinedOn" />
          <mat-datepicker-toggle matIconSuffix [for]="dpJoin" /><mat-datepicker #dpJoin />
          <mat-error>{{ server(form.get('joinedOn')) || pickerError(form.get('joinedOn')?.errors) || 'Enter the joining date.' }}</mat-error></mat-form-field>
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Weekly day off</mat-label>
          <mat-select formControlName="weeklyOff" aria-label="Weekly day off">
            <mat-option [value]="null">None fixed</mat-option>
            @for (d of weekdays; track d; let i = $index) { <mat-option [value]="i + 1">{{ d }}</mat-option> }
          </mat-select>
          <mat-hint>The roster flags it</mat-hint>
          <mat-error>{{ server(form.get('weeklyOff')) }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Date of birth</mat-label>
          <input matInput [matDatepicker]="dpDob" formControlName="dateOfBirth" />
          <mat-datepicker-toggle matIconSuffix [for]="dpDob" /><mat-datepicker #dpDob startView="multi-year" />
          <mat-error>{{ server(form.get('dateOfBirth')) || pickerError(form.get('dateOfBirth')?.errors) }}</mat-error></mat-form-field>
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Phone</mat-label><input matInput type="tel" formControlName="phone" maxlength="20" />
          <mat-error>{{ server(form.get('phone')) }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>E-mail</mat-label><input matInput type="email" formControlName="email" maxlength="120" />
          <mat-error>{{ server(form.get('email')) }}</mat-error></mat-form-field>
      </div>
      @if (accounts().length) {
        <mat-form-field appearance="outline"><mat-label>Staff account (optional)</mat-label>
          <mat-select formControlName="userId" aria-label="Staff account">
            <mat-option [value]="null">No account</mat-option>
            @for (a of accounts(); track a.id) {
              <mat-option [value]="a.id" [disabled]="!!a.linkedEmployeeId && a.linkedEmployeeId !== e?.id">{{ a.fullName }} · {{ a.email }}@if (a.linkedTo && a.linkedEmployeeId !== e?.id) { (linked to {{ a.linkedTo }}) }</mat-option>
            }
          </mat-select>
          <mat-hint>Linked: they see their own shifts</mat-hint>
          <mat-error>{{ server(form.get('userId')) }}</mat-error></mat-form-field>
      }
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Emergency contact</mat-label><input matInput formControlName="emergencyContactName" maxlength="80" />
          <mat-error>{{ server(form.get('emergencyContactName')) }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Their phone</mat-label><input matInput type="tel" formControlName="emergencyContactPhone" maxlength="20" />
          <mat-error>{{ server(form.get('emergencyContactPhone')) }}</mat-error></mat-form-field>
      </div>
      <mat-form-field appearance="outline"><mat-label>Address</mat-label><input matInput formControlName="address" maxlength="300" />
        <mat-error>{{ server(form.get('address')) }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Note</mat-label><input matInput formControlName="note" maxlength="500" />
        <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="employee-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(560px, 84vw); } .row { display: flex; gap: 8px; flex-wrap: wrap; }
           .row > * { flex: 1 1 200px; } .err { color: #b3261e; }`,
})
export class EmployeeDialog {
  data = inject<{ propertyId: string; employee: Employee | null; departments: Department[] }>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<EmployeeDialog, Employee>);
  private api = inject(EmployeesApi);
  server = server;
  pickerError = pickerError;
  types: EmploymentType[] = ['FULL_TIME', 'PART_TIME', 'CONTRACT', 'SEASONAL'];
  typeLabel = EMPLOYMENT_LABEL;
  weekdays = WEEKDAYS;
  busy = signal(false);
  error = signal('');
  departments = signal(this.data.departments);
  accounts = signal<LinkableAccount[]>([]);
  e = this.data.employee;
  form = inject(FormBuilder).group({
    firstName: [this.e?.firstName ?? '', Validators.required],
    lastName: [this.e?.lastName ?? ''],
    departmentId: [this.e?.departmentId ?? (this.data.departments.find(d => d.isActive)?.id ?? null), Validators.required],
    designationId: [this.e?.designationId ?? null as string | null],
    employmentType: [this.e?.employmentType ?? 'FULL_TIME' as EmploymentType, Validators.required],
    joinedOn: [this.e?.joinedOn ?? null as string | null, Validators.required],
    weeklyOff: [this.e?.weeklyOff ?? null as number | null],
    dateOfBirth: [this.e?.dateOfBirth ?? null as string | null],
    phone: [this.e?.phone ?? ''],
    email: [this.e?.email ?? ''],
    userId: [this.e?.userId ?? null as string | null],
    emergencyContactName: [this.e?.emergencyContactName ?? ''],
    emergencyContactPhone: [this.e?.emergencyContactPhone ?? ''],
    address: [this.e?.address ?? ''],
    note: [this.e?.note ?? ''],
  });
  private deptId = toSignal(this.form.controls.departmentId.valueChanges, { initialValue: this.form.controls.departmentId.value });
  titles = computed(() => this.departments().find(d => d.id === this.deptId())?.designations ?? []);

  constructor() {
    this.form.controls.departmentId.valueChanges.subscribe(() => {
      if (!this.titles().some(t => t.id === this.form.controls.designationId.value)) this.form.controls.designationId.setValue(null);
    });
    if (!this.departments().length) this.api.departments(this.data.propertyId).subscribe(d => this.departments.set(d));
    // Accounts need employee.update; without it the picker just isn't offered.
    this.api.accounts(this.data.propertyId).subscribe({ next: a => this.accounts.set(a.filter(x => x.isActive || x.id === this.e?.userId)), error: () => this.accounts.set([]) });
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.error.set('');
    const v = this.form.getRawValue();
    this.api.saveEmployee(this.data.propertyId, this.e?.id ?? null, {
      firstName: v.firstName!.trim(), lastName: text(v.lastName), phone: text(v.phone), email: text(v.email), dateOfBirth: v.dateOfBirth,
      departmentId: v.departmentId!, designationId: v.designationId, employmentType: v.employmentType!, joinedOn: v.joinedOn!, weeklyOff: v.weeklyOff,
      userId: v.userId, emergencyContactName: text(v.emergencyContactName), emergencyContactPhone: text(v.emergencyContactPhone), address: text(v.address),
      note: text(v.note), version: this.e?.version ?? null,
    }).subscribe({
      next: x => this.ref.close(x),
      error: err => { this.busy.set(false); report(this.form, err, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ departments and job titles

/** Departments and their job titles: add, rename, switch off. */
@Component({
  selector: 'app-departments-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Departments and job titles</h2>
    <div mat-dialog-content class="body">
      <ul class="depts">
        @for (d of list(); track d.id) {
          <li [class.off]="!d.isActive" [attr.data-dept]="d.code">
            <div><b>{{ d.name }}</b> <small class="mute">{{ d.code }} · {{ d.employeeCount }} working</small>
              @if (!d.isActive) { <small class="mute"> · switched off</small> }</div>
            <small class="mute">@for (t of d.designations; track t.id) { <span class="chip" [class.off]="!t.isActive">{{ t.name }}</span> } @empty { No job titles yet. }</small>
            <button mat-button type="button" (click)="editDept(d)">Edit</button>
          </li>
        } @empty { <li class="mute">No departments yet. Add Front office, Housekeeping, Kitchen …</li> }
      </ul>

      <form [formGroup]="dept" (ngSubmit)="saveDept()" class="box" id="dept-form">
        <b>{{ editing() ? 'Edit ' + editing()!.name : 'Add a department' }}</b>
        <div class="row">
          <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" maxlength="60" placeholder="e.g. Housekeeping" />
            <mat-error>{{ server(dept.get('name')) || 'Give the name, 2–60 characters.' }}</mat-error></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Code</mat-label><input matInput formControlName="code" maxlength="20" placeholder="e.g. HK" />
            <mat-error>{{ server(dept.get('code')) || 'The code is 1–20 letters, digits, - or _.' }}</mat-error></mat-form-field>
        </div>
        @if (editing()) { <mat-checkbox formControlName="isActive">In use</mat-checkbox>
          @if (server(dept.get('isActive'))) { <p class="err">{{ server(dept.get('isActive')) }}</p> } }
        <div class="acts">
          @if (editing()) { <button mat-button type="button" (click)="editDept(null)">New instead</button> }
          <button mat-flat-button type="submit" [disabled]="busy()">{{ editing() ? 'Save department' : 'Add department' }}</button>
        </div>
      </form>

      @if (list().length) {
        <form [formGroup]="title" (ngSubmit)="saveTitle()" class="box" id="title-form">
          <b>Add a job title</b>
          <div class="row">
            <mat-form-field appearance="outline"><mat-label>Department</mat-label>
              <mat-select formControlName="departmentId" aria-label="Job title department">
                @for (d of list(); track d.id) { <mat-option [value]="d.id">{{ d.name }}</mat-option> }
              </mat-select>
              <mat-error>{{ server(title.get('departmentId')) || 'Choose the department.' }}</mat-error></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Job title</mat-label><input matInput formControlName="name" maxlength="60" placeholder="e.g. Commis chef" />
              <mat-error>{{ server(title.get('name')) || 'Give the job title, 2–60 characters.' }}</mat-error></mat-form-field>
          </div>
          <div class="acts"><button mat-stroked-button type="submit" [disabled]="busy()">Add job title</button></div>
        </form>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>
    <div mat-dialog-actions align="end"><button mat-button mat-dialog-close type="button">Done</button></div>
  `,
  styles: `.body { min-width: min(560px, 84vw); display: flex; flex-direction: column; gap: 12px; }
           ul { list-style: none; margin: 0; padding: 0; } .depts li { display: grid; grid-template-columns: 1fr auto; gap: 2px 8px; padding: 8px 0; border-bottom: 1px solid #e3e8e6; }
           .depts li > small { grid-column: 1; } .depts li > button { grid-row: 1 / span 2; grid-column: 2; align-self: center; }
           .off { opacity: .6; } .chip { display: inline-block; background: #eef3f1; border-radius: 10px; padding: 0 8px; margin: 2px 4px 0 0; }
           .box { display: flex; flex-direction: column; gap: 4px; border: 1px solid #d9e0de; border-radius: 12px; padding: 10px 12px; }
           .row { display: flex; gap: 8px; flex-wrap: wrap; } .row > * { flex: 1 1 200px; } .acts { display: flex; justify-content: flex-end; gap: 8px; }
           .mute { color: #5b6b6e; } .err { color: #b3261e; }`,
})
export class DepartmentsDialog {
  data = inject<{ propertyId: string }>(MAT_DIALOG_DATA);
  private api = inject(EmployeesApi);
  private fb = inject(FormBuilder).nonNullable;
  server = server;
  list = signal<Department[]>([]);
  editing = signal<Department | null>(null);
  busy = signal(false);
  error = signal('');
  dept = this.fb.group({ name: ['', [Validators.required, Validators.minLength(2)]], code: ['', Validators.required], isActive: [true] });
  title = this.fb.group({ departmentId: ['', Validators.required], name: ['', [Validators.required, Validators.minLength(2)]] });

  constructor() { this.load(); }

  load() {
    this.api.departments(this.data.propertyId).subscribe({
      next: d => { this.list.set(d); if (!this.title.controls.departmentId.value && d.length) this.title.controls.departmentId.setValue(d[0].id); },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  editDept(d: Department | null) {
    this.editing.set(d);
    this.dept.reset({ name: d?.name ?? '', code: d?.code ?? '', isActive: d?.isActive ?? true });
  }

  saveDept() {
    if (this.dept.invalid) { this.dept.markAllAsTouched(); return; }
    const v = this.dept.getRawValue(), d = this.editing();
    this.busy.set(true);
    this.error.set('');
    this.api.saveDepartment(this.data.propertyId, d?.id ?? null, {
      code: v.code.trim(), name: v.name.trim(), sortOrder: d?.sortOrder ?? (this.list().length + 1), isActive: v.isActive, version: d?.version ?? null,
    }).subscribe({
      next: () => { this.busy.set(false); this.editDept(null); this.load(); },
      error: e => { this.busy.set(false); report(this.dept, e, m => this.error.set(m)); },
    });
  }

  saveTitle() {
    if (this.title.invalid) { this.title.markAllAsTouched(); return; }
    const v = this.title.getRawValue();
    this.busy.set(true);
    this.error.set('');
    this.api.saveDesignation(this.data.propertyId, null, { departmentId: v.departmentId, name: v.name.trim(), isActive: true, version: null }).subscribe({
      next: () => { this.busy.set(false); this.title.controls.name.reset(''); this.load(); },
      error: e => { this.busy.set(false); report(this.title, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ leaving

/** The employee leaves: their last working day and why. Refused while they are on the roster after it. */
@Component({
  selector: 'app-leave-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatDatepickerModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ data.employee.fullName }} leaves</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="leave-form">
      <p class="mute">Their record stays (with their pay and documents) and they drop off the roster after their last day.</p>
      <mat-form-field appearance="outline"><mat-label>Last working day</mat-label>
        <input matInput [matDatepicker]="dp" formControlName="leftOn" />
        <mat-datepicker-toggle matIconSuffix [for]="dp" /><mat-datepicker #dp />
        <mat-error>{{ server(form.get('leftOn')) || pickerError(form.get('leftOn')?.errors) || 'Enter the last working day.' }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Reason</mat-label><input matInput formControlName="reason" maxlength="300" placeholder="e.g. Resigned, end of season" />
        <mat-error>{{ server(form.get('reason')) || 'Say why, at least 3 characters.' }}</mat-error></mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button color="warn" type="submit" form="leave-form" [disabled]="busy()">Record leaving</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(420px, 84vw); } .mute { color: #5b6b6e; } .err { color: #b3261e; }`,
})
export class LeaveDialog {
  data = inject<{ propertyId: string; employee: Employee; today: string }>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<LeaveDialog, Employee>);
  private api = inject(EmployeesApi);
  server = server;
  pickerError = pickerError;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).nonNullable.group({ leftOn: [this.data.today, Validators.required], reason: ['', [Validators.required, Validators.minLength(3)]] });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    const v = this.form.getRawValue();
    this.api.leave(this.data.propertyId, this.data.employee.id, { leftOn: v.leftOn, reason: v.reason.trim(), version: this.data.employee.version }).subscribe({
      next: x => this.ref.close(x),
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ pay

/** Records pay from a date. Entries are never changed: a raise or a correction is a new entry (the newest for a date wins). */
@Component({
  selector: 'app-pay-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatDatepickerModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Pay for {{ data.employee.fullName }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="pay-form">
      <p class="mute">Only people with salary access see this. A raise or a correction is a new entry; earlier entries stay in the history.</p>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>From</mat-label>
          <input matInput [matDatepicker]="dp" formControlName="effectiveFrom" />
          <mat-datepicker-toggle matIconSuffix [for]="dp" /><mat-datepicker #dp />
          <mat-error>{{ server(form.get('effectiveFrom')) || pickerError(form.get('effectiveFrom')?.errors) || 'Enter the date this pay starts.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Paid</mat-label>
          <mat-select formControlName="payBasis" aria-label="Paid">
            <mat-option value="MONTHLY">Per month</mat-option><mat-option value="DAILY">Per day</mat-option>
          </mat-select>
          <mat-error>{{ server(form.get('payBasis')) }}</mat-error></mat-form-field>
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Basic (₹)</mat-label><input matInput type="number" inputmode="decimal" formControlName="basic" />
          <mat-error>{{ server(form.get('basic')) || 'Enter the basic pay, more than ₹0.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Allowances (₹)</mat-label><input matInput type="number" inputmode="decimal" formControlName="allowances" />
          <mat-hint>HRA, food, travel …</mat-hint>
          <mat-error>{{ server(form.get('allowances')) || 'Enter 0 or more.' }}</mat-error></mat-form-field>
      </div>
      <p>Gross {{ rupees(gross()) }} {{ basis() === 'DAILY' ? 'a day' : 'a month' }}</p>
      <div class="row"><mat-checkbox formControlName="pfApplicable">PF</mat-checkbox><mat-checkbox formControlName="esiApplicable">ESI</mat-checkbox></div>
      <mat-form-field appearance="outline"><mat-label>Note</mat-label><input matInput formControlName="note" maxlength="300" placeholder="e.g. Annual raise" />
        <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="pay-form" [disabled]="busy()">Record pay</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(460px, 84vw); } .row { display: flex; gap: 8px; flex-wrap: wrap; }
           .row > * { flex: 1 1 180px; } .mute { color: #5b6b6e; } .err { color: #b3261e; }`,
})
export class PayDialog {
  data = inject<{ propertyId: string; employee: Employee; current: EmployeeCompensation['current']; today: string }>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<PayDialog, EmployeeCompensation>);
  private api = inject(EmployeesApi);
  server = server;
  pickerError = pickerError;
  rupees = rupees;
  busy = signal(false);
  error = signal('');
  private c = this.data.current;
  form = inject(FormBuilder).nonNullable.group({
    effectiveFrom: [this.c ? this.data.today : this.data.employee.joinedOn, Validators.required],
    payBasis: [(this.c?.payBasis ?? 'MONTHLY') as PayBasis],
    basic: [this.c?.basic ?? (null as unknown as number), [Validators.required, Validators.min(0.01)]],
    allowances: [this.c?.allowances ?? 0, [Validators.required, Validators.min(0)]],
    pfApplicable: [this.c?.pfApplicable ?? false],
    esiApplicable: [this.c?.esiApplicable ?? false],
    note: [''],
  });
  private v = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });
  gross = computed(() => (Number(this.v().basic) || 0) + (Number(this.v().allowances) || 0));
  basis = computed(() => this.v().payBasis);

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    const v = this.form.getRawValue();
    this.api.recordPay(this.data.propertyId, this.data.employee.id, {
      effectiveFrom: v.effectiveFrom, payBasis: v.payBasis, basic: Number(v.basic), allowances: Number(v.allowances), pfApplicable: v.pfApplicable,
      esiApplicable: v.esiApplicable, note: text(v.note),
    }).subscribe({
      next: x => this.ref.close(x),
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ a document

/** Adds a document: its number (encrypted; Aadhaar keeps the last 4 digits only) and/or a scan (JPEG, PNG or PDF, 5 MB). */
@Component({
  selector: 'app-document-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatDatepickerModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Add a document for {{ data.employee.fullName }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="doc-form">
      <mat-form-field appearance="outline"><mat-label>Document</mat-label>
        <mat-select formControlName="documentType" aria-label="Document">
          @for (t of types; track t) { <mat-option [value]="t">{{ label[t] }}</mat-option> }
        </mat-select>
        <mat-error>{{ server(form.get('documentType')) }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Number</mat-label><input matInput formControlName="number" maxlength="40" autocomplete="off" />
        <mat-hint>{{ type() === 'AADHAAR' ? 'Only the last 4 digits are kept.' : 'Stored encrypted; lists show the last 4 characters.' }}</mat-hint>
        <mat-error>{{ server(form.get('number')) }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Expires on</mat-label>
        <input matInput [matDatepicker]="dp" formControlName="expiresOn" />
        <mat-datepicker-toggle matIconSuffix [for]="dp" /><mat-datepicker #dp />
        <mat-error>{{ server(form.get('expiresOn')) || pickerError(form.get('expiresOn')?.errors) }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Note</mat-label><input matInput formControlName="note" maxlength="200" />
        <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>
      <div class="file">
        <button mat-stroked-button type="button" (click)="picker.click()"><mat-icon>attach_file</mat-icon>{{ file() ? 'Change scan' : 'Attach a scan' }}</button>
        <span class="mute">{{ file()?.name ?? 'JPEG, PNG or PDF, up to 5 MB' }}</span>
        <input #picker type="file" hidden accept="image/jpeg,image/png,application/pdf" (change)="pick($event)" data-testid="doc-file" />
      </div>
      @if (server(form.get('file'))) { <p class="err">{{ server(form.get('file')) }}</p> }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="doc-form" [disabled]="busy()">Save document</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(440px, 84vw); } .file { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
           .mute { color: #5b6b6e; } .err { color: #b3261e; }`,
})
export class DocumentDialog {
  data = inject<{ propertyId: string; employee: Employee }>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<DocumentDialog, EmployeeDocument>);
  private api = inject(EmployeesApi);
  server = server;
  pickerError = pickerError;
  types: EmployeeDocumentType[] = ['AADHAAR', 'PAN', 'BANK_ACCOUNT', 'PASSPORT', 'DRIVING_LICENCE', 'OFFER_LETTER', 'OTHER'];
  label = DOCUMENT_LABEL;
  busy = signal(false);
  error = signal('');
  file = signal<File | null>(null);
  form = inject(FormBuilder).group({
    documentType: ['PAN' as EmployeeDocumentType, Validators.required], number: [''], expiresOn: [null as string | null], note: [''], file: [null as null],
  });
  type = toSignal(this.form.controls.documentType.valueChanges, { initialValue: this.form.controls.documentType.value });

  pick(ev: Event) {
    const f = (ev.target as HTMLInputElement).files?.[0] ?? null;
    this.file.set(f);
    this.form.controls.file.setErrors(null);
  }

  save() {
    this.busy.set(true);
    this.error.set('');
    const v = this.form.getRawValue();
    const fd = new FormData();
    fd.set('documentType', v.documentType ?? 'OTHER');
    if (text(v.number)) fd.set('number', text(v.number)!);
    if (v.expiresOn) fd.set('expiresOn', v.expiresOn);
    if (text(v.note)) fd.set('note', text(v.note)!);
    const f = this.file();
    if (f) fd.set('file', f, f.name);
    this.api.uploadDocument(this.data.propertyId, this.data.employee.id, fd).subscribe({
      next: x => this.ref.close(x),
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m)); },
    });
  }
}
