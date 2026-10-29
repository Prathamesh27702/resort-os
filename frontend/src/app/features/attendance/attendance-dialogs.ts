import { Component, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { dayText } from '../../core/dates';
import { toProblem } from '../../core/http';
import { AttendanceDay, AttendanceRow, ClockAction, Geofence, Timesheet, TimesheetLine } from '../../core/models';
import { report } from '../employees/employee-dialogs';
import { hm, hours } from '../employees/employees.api';
import { ACTION_LABEL, AttendanceApi, currentPosition } from './attendance.api';

const server = (c: AbstractControl | null) => (c?.errors?.['server'] as string | undefined) ?? '';
const text = (v: string | null | undefined) => (v ?? '').trim() || null;
/** "07:00" → "07:00:00" (the API's time format). */
const time = (v: string | null | undefined) => (v ? (v.length === 5 ? v + ':00' : v) : null);

// ------------------------------------------------------------------ a manager entry

export interface RecordData { propertyId: string; row: AttendanceRow; date: string; now: string; }

/**
 * A manager records a clock-in, break or clock-out for someone (attendance.update), e.g. their phone had no signal or
 * they forgot to clock out. Always with the reason; it is kept with the entry and shown on the timesheet.
 */
@Component({
  selector: 'app-record-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Record for {{ data.row.employeeName }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="record-form">
      <p class="mute">For when their phone can't do it. The entry is marked as yours, with the reason, on their timesheet.</p>
      <mat-form-field appearance="outline"><mat-label>What</mat-label>
        <mat-select formControlName="action" aria-label="What">
          @for (a of actions; track a) { <mat-option [value]="a">{{ label[a] }}</mat-option> }
        </mat-select>
        <mat-error>{{ server(form.get('action')) || 'Choose what to record.' }}</mat-error></mat-form-field>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Day</mat-label><input matInput type="date" formControlName="date" />
          <mat-error>{{ server(form.get('date')) || 'Choose the day.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Time</mat-label><input matInput type="time" formControlName="time" />
          <mat-hint>At the property</mat-hint>
          <mat-error>{{ server(form.get('time')) || 'Give the time.' }}</mat-error></mat-form-field>
      </div>
      <mat-form-field appearance="outline"><mat-label>Why</mat-label>
        <input matInput formControlName="reason" maxlength="300" placeholder="e.g. Phone had no signal at the beach" />
        <mat-error>{{ server(form.get('reason')) || 'Say why, at least 3 characters.' }}</mat-error></mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="record-form" [disabled]="busy()">Record</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(440px, 84vw); } .row { display: flex; gap: 8px; flex-wrap: wrap; }
           .row > * { flex: 1 1 150px; } .err { color: #b3261e; } .mute { color: #5b6b6e; margin-top: 0; }`,
})
export class RecordDialog {
  data = inject<RecordData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<RecordDialog, AttendanceDay & { message: string }>);
  private api = inject(AttendanceApi);
  server = server; label = ACTION_LABEL;
  actions: ClockAction[] = ['CLOCK_IN', 'BREAK_START', 'BREAK_END', 'CLOCK_OUT'];
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({
    action: [this.next(), Validators.required],
    date: [this.data.date, Validators.required],
    time: [this.data.now, Validators.required],
    reason: ['', [Validators.required, Validators.minLength(3)]],
  });

  private next(): ClockAction {
    switch (this.data.row.state) {
      case 'ON_SHIFT': case 'MISSING_OUT': return 'CLOCK_OUT';
      case 'ON_BREAK': return 'BREAK_END';
      default: return 'CLOCK_IN';
    }
  }

  save() {
    this.form.controls.reason.setValue((this.form.controls.reason.value ?? '').trim());
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.error.set('');
    const v = this.form.getRawValue();
    this.api.record(this.data.propertyId, { employeeId: this.data.row.employeeId, action: v.action!, date: v.date, time: time(v.time), reason: v.reason! })
      .subscribe({
        next: d => this.ref.close({ ...d, message: `${ACTION_LABEL[v.action!]} at ${v.time} recorded for ${this.data.row.employeeName}.` }),
        error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m)); },
      });
  }
}

// ------------------------------------------------------------------ geofences

/**
 * The areas where clocking in counts (attendance.update): a point and a radius. "Use my location" fills the point from
 * this device, so a manager can stand at the gate and save it.
 */
@Component({
  selector: 'app-geofences-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSlideToggleModule],
  template: `
    <h2 mat-dialog-title>Areas for clocking in</h2>
    <div mat-dialog-content>
      <p class="mute">Staff can clock in and out only inside one of these (their phone's accuracy counts, up to 50 m).</p>
      <ul class="list">
        @for (g of list(); track g.id) {
          <li [class.off]="!g.isActive"><b>{{ g.name }}</b> <span class="mute">{{ g.radiusM }} m around {{ g.latitude }}, {{ g.longitude }}{{ g.isActive ? '' : ' · switched off' }}</span>
            <button mat-button (click)="edit(g)">Change</button></li>
        } @empty { <li class="mute">No areas yet.</li> }
      </ul>
      <form [formGroup]="form" (ngSubmit)="save()" id="fence-form">
        <h3>{{ editing() ? 'Change ' + editing()!.name : 'Add an area' }}</h3>
        <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" placeholder="e.g. Main gate" />
          <mat-error>{{ server(form.get('name')) || 'Give the area a name.' }}</mat-error></mat-form-field>
        <div class="row">
          <mat-form-field appearance="outline"><mat-label>Latitude</mat-label><input matInput type="number" step="0.000001" formControlName="latitude" />
            <mat-error>{{ server(form.get('latitude')) || 'Give the latitude.' }}</mat-error></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Longitude</mat-label><input matInput type="number" step="0.000001" formControlName="longitude" />
            <mat-error>{{ server(form.get('longitude')) || 'Give the longitude.' }}</mat-error></mat-form-field>
        </div>
        <button mat-stroked-button type="button" (click)="here()"><mat-icon>my_location</mat-icon>Use my location</button>
        <mat-form-field appearance="outline" class="radius"><mat-label>Radius (metres)</mat-label><input matInput type="number" formControlName="radiusM" />
          <mat-error>{{ server(form.get('radiusM')) || 'The radius is 20–2000 metres.' }}</mat-error></mat-form-field>
        @if (editing()) { <mat-slide-toggle formControlName="isActive" [aria-label]="'Area in use'">In use</mat-slide-toggle> }
        @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      </form>
    </div>
    <div mat-dialog-actions align="end">
      @if (editing()) { <button mat-button type="button" (click)="reset()">New area</button> }
      <button mat-button type="button" (click)="ref.close(changed)">Close</button>
      <button mat-flat-button type="submit" form="fence-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `.list { list-style: none; padding: 0; margin: 0 0 8px; } .list li { padding: 4px 0; border-bottom: 1px solid #eef2f1; } .list li.off b { color: #5b6b6e; }
           form { display: flex; flex-direction: column; gap: 4px; min-width: min(460px, 84vw); } .row { display: flex; gap: 8px; flex-wrap: wrap; }
           .row > * { flex: 1 1 150px; } .radius { margin-top: 8px; } .err { color: #b3261e; } .mute { color: #5b6b6e; } h3 { margin: 8px 0 4px; font-size: 1rem; }`,
})
export class GeofencesDialog {
  private data = inject<{ propertyId: string }>(MAT_DIALOG_DATA);
  ref = inject(MatDialogRef<GeofencesDialog, boolean>);
  private api = inject(AttendanceApi);
  private fb = inject(FormBuilder);
  server = server;
  list = signal<Geofence[]>([]);
  editing = signal<Geofence | null>(null);
  busy = signal(false);
  error = signal('');
  changed = false;
  form = this.fb.group({
    name: ['', Validators.required],
    latitude: [null as number | null, Validators.required],
    longitude: [null as number | null, Validators.required],
    radiusM: [200, Validators.required],
    isActive: [true],
  });

  constructor() { this.load(); }

  load() { this.api.geofences(this.data.propertyId).subscribe(l => this.list.set(l)); }

  edit(g: Geofence) {
    this.editing.set(g);
    this.form.reset({ name: g.name, latitude: g.latitude, longitude: g.longitude, radiusM: g.radiusM, isActive: g.isActive });
  }

  reset() { this.editing.set(null); this.form.reset({ name: '', latitude: null, longitude: null, radiusM: 200, isActive: true }); }

  async here() {
    this.error.set('');
    try {
      const p = await currentPosition();
      this.form.patchValue({ latitude: p.latitude, longitude: p.longitude });
    } catch (e) { this.error.set((e as Error).message); }
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.error.set('');
    const v = this.form.getRawValue();
    const g = this.editing();
    this.api.saveGeofence(this.data.propertyId, g?.id ?? null, {
      name: v.name!.trim(), latitude: Number(v.latitude), longitude: Number(v.longitude), radiusM: Number(v.radiusM), isActive: !!v.isActive, version: g?.version ?? null,
    }).subscribe({
      next: () => { this.busy.set(false); this.changed = true; this.reset(); this.load(); },
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ correcting a day

export interface CorrectData { propertyId: string; timesheet: Timesheet; line: TimesheetLine | null; }

/**
 * A manager sets one day's times with the reason (attendance.update). The old and new values are kept on the timesheet.
 * Leave both times empty for a day that wasn't worked.
 */
@Component({
  selector: 'app-correct-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Correct a day · {{ data.timesheet.sheet.employeeName }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="correct-form">
      @if (data.line; as l) {
        <p class="mute">Now: {{ l.clockIn ? hm(l.clockIn) : '—' }} to {{ l.clockOut ? hm(l.clockOut) : '—' }}, break {{ l.breakMinutes }} min, worked {{ hours(l.workedMinutes) }}.
          @if (l.shiftStart) { Shift {{ hm(l.shiftStart) }}–{{ hm(l.shiftEnd!) }}. }</p>
      }
      <mat-form-field appearance="outline"><mat-label>Day</mat-label>
        <mat-select formControlName="workDate" aria-label="Day">
          @for (d of days; track d) { <mat-option [value]="d">{{ dayText(d) }}</mat-option> }
        </mat-select>
        <mat-error>{{ server(form.get('workDate')) }}</mat-error></mat-form-field>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Clock-in</mat-label><input matInput type="time" formControlName="clockIn" />
          <mat-error>{{ server(form.get('clockIn')) }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Clock-out</mat-label><input matInput type="time" formControlName="clockOut" />
          <mat-hint>Earlier than the clock-in: the next day</mat-hint>
          <mat-error>{{ server(form.get('clockOut')) }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Break (min)</mat-label><input matInput type="number" inputmode="numeric" formControlName="breakMinutes" />
          <mat-error>{{ server(form.get('breakMinutes')) || 'The break is 0–180 minutes.' }}</mat-error></mat-form-field>
      </div>
      <mat-form-field appearance="outline"><mat-label>Why</mat-label>
        <input matInput formControlName="reason" maxlength="300" placeholder="e.g. Forgot to clock out, left at 16:00" />
        <mat-error>{{ server(form.get('reason')) || 'Say why, at least 3 characters.' }}</mat-error></mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="correct-form" [disabled]="busy()">Save correction</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(480px, 84vw); } .row { display: flex; gap: 8px; flex-wrap: wrap; }
           .row > * { flex: 1 1 120px; } .err { color: #b3261e; } .mute { color: #5b6b6e; margin-top: 0; }`,
})
export class CorrectDialog {
  data = inject<CorrectData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<CorrectDialog, Timesheet>);
  private api = inject(AttendanceApi);
  server = server; hm = hm; hours = hours; dayText = dayText;
  days = Array.from({ length: 7 }, (_, i) => {
    const [y, m, d] = this.data.timesheet.sheet.weekStart.split('-').map(Number);
    return new Date(Date.UTC(y, m - 1, d + i)).toISOString().slice(0, 10);
  });
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({
    workDate: [this.data.line?.workDate ?? this.days[0], Validators.required],
    clockIn: [this.data.line?.clockIn ? hm(this.data.line.clockIn) : (this.data.line?.shiftStart ? hm(this.data.line.shiftStart) : '')],
    clockOut: [this.data.line?.clockOut ? hm(this.data.line.clockOut) : (this.data.line?.shiftEnd ? hm(this.data.line.shiftEnd) : '')],
    breakMinutes: [this.data.line?.breakMinutes ?? 0, [Validators.required, Validators.min(0), Validators.max(180)]],
    reason: ['', [Validators.required, Validators.minLength(3)]],
  });

  save() {
    this.form.controls.reason.setValue((this.form.controls.reason.value ?? '').trim());
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.error.set('');
    const v = this.form.getRawValue();
    const t = this.data.timesheet.sheet;
    this.api.correct(this.data.propertyId, t.id, {
      workDate: v.workDate!, clockIn: time(text(v.clockIn)), clockOut: time(text(v.clockOut)), breakMinutes: Number(v.breakMinutes ?? 0), reason: v.reason!,
      version: t.version,
    }).subscribe({
      next: x => this.ref.close(x),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        if (p.code === 'CONCURRENCY_CONFLICT') { this.error.set(p.detail); return; }
        report(this.form, e, m => this.error.set(m));
      },
    });
  }
}
