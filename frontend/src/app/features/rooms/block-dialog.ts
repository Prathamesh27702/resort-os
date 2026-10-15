import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { BLOCK_TYPES, Room, UnitBlock } from '../../core/models';
import { pickerError } from '../../core/dates';
import { fieldErrors, toProblem } from '../../core/http';
import { RoomsApi, addDays, label, nightsBetween, shortDate } from './rooms.api';
import { blockForm, minTrimmed } from './rooms.forms';

export interface BlockDialogData { propertyId: string; today: string; rooms: Room[]; roomId?: string; bedId?: string | null; }

/** Take a room, a whole dorm or one dorm bed out of sale. Returns the created block. */
@Component({
  selector: 'app-block-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatDatepickerModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Block a room</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="block-form">
      <p class="hint">Blocked rooms or beds can't be sold for those nights. The reason goes in the audit trail.</p>
      <mat-form-field appearance="outline">
        <mat-label>Room</mat-label>
        <mat-select formControlName="unitId">
          @for (r of activeRooms(); track r.id) { <mat-option [value]="r.id">{{ r.name }} · {{ r.accommodationTypeName }}</mat-option> }
        </mat-select>
      </mat-form-field>
      @if (room()?.inventoryMode === 'BED') {
        <mat-form-field appearance="outline">
          <mat-label>Bed</mat-label>
          <mat-select formControlName="bedId">
            <mat-option [value]="null">Whole dormitory</mat-option>
            @for (b of activeBeds(); track b.id) { <mat-option [value]="b.id">{{ b.name }} ({{ b.code }})</mat-option> }
          </mat-select>
        </mat-form-field>
      }
      <mat-form-field appearance="outline">
        <mat-label>Why</mat-label>
        <mat-select formControlName="blockType">
          @for (t of types; track t) { <mat-option [value]="t">{{ label(t) }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <div class="dates">
        <mat-form-field appearance="outline">
          <mat-label>First night</mat-label>
          <input matInput [matDatepicker]="dpStartDate" formControlName="startDate" />
          <mat-datepicker-toggle matIconSuffix [for]="dpStartDate" />
          <mat-datepicker #dpStartDate />
          <mat-error>{{ pickerError(form.controls.startDate.errors) ?? 'Choose the first night.' }}</mat-error>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Available again on</mat-label>
          <input matInput [matDatepicker]="dpEndDate" formControlName="endDate" />
          <mat-datepicker-toggle matIconSuffix [for]="dpEndDate" />
          <mat-datepicker #dpEndDate />
          <mat-hint>Like a check-out date</mat-hint>
          <mat-error>{{ pickerError(form.controls.endDate.errors) ?? 'Choose the day the room is available again.' }}</mat-error>
        </mat-form-field>
      </div>
      <p class="nights" aria-live="polite">
        @if (form.hasError('endBeforeStart')) { <span class="err">"Available again on" must be after the first night.</span> }
        @else if (form.hasError('inPast')) { <span class="err">The block must end after today.</span> }
        @else if (form.hasError('tooLong')) { <span class="err">A block can't be longer than 366 nights.</span> }
        @else if (nights() > 0) { {{ nights() }} night{{ nights() === 1 ? '' : 's' }}: {{ shortDate(dates().startDate!) }} – {{ shortDate(lastNight()) }} }
      </p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Reason</mat-label>
        <input matInput formControlName="reason" maxlength="500" placeholder="e.g. AC compressor replacement" />
        @if (err()['reason']) { <mat-error>{{ err()['reason'] }}</mat-error> }
        @else { <mat-error>At least 3 characters.</mat-error> }
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="block-form" [disabled]="busy()">Block</button>
    </div>
  `,
  styles: `
    form { display: flex; flex-direction: column; min-width: min(420px, 80vw); }
    .hint { color: #5b6b6e; margin: 0 0 12px; }
    .dates { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .nights { margin: 0 0 12px; color: #1b2a2e; min-height: 1.4em; }
    .err { color: #b3261e; }
    @media (max-width: 480px) { .dates { grid-template-columns: 1fr; } }
  `,
})
export class BlockDialog {
  private data = inject<BlockDialogData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<BlockDialog, UnitBlock>);
  private api = inject(RoomsApi);

  types = BLOCK_TYPES;
  label = label;
  shortDate = shortDate;
  pickerError = pickerError;
  busy = signal(false);
  error = signal('');
  err = signal<Record<string, string>>({});

  form = blockForm(inject(FormBuilder), this.data.today);
  activeRooms = signal(this.data.rooms.filter(r => r.isActive));
  private unitId = toSignal(this.form.controls.unitId.valueChanges, { initialValue: '' });
  room = computed(() => this.data.rooms.find(r => r.id === this.unitId()) ?? null);
  activeBeds = computed(() => (this.room()?.beds ?? []).filter(b => b.isActive));
  dates = toSignal(this.form.valueChanges, { initialValue: this.form.value });
  nights = computed(() => {
    const d = this.dates();
    return d.startDate && d.endDate ? nightsBetween(d.startDate, d.endDate) : 0;
  });
  lastNight = computed(() => addDays(this.dates().endDate!, -1));

  constructor() {
    this.form.patchValue({
      unitId: this.data.roomId ?? '',
      bedId: this.data.bedId ?? null,
      endDate: addDays(this.data.today, 1),
    });
    this.form.controls.unitId.valueChanges.subscribe(() => this.form.controls.bedId.setValue(null));
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true); this.error.set(''); this.err.set({});
    const v = this.form.getRawValue();
    this.api.createBlock(this.data.propertyId, { ...v, reason: v.reason!.trim() }).subscribe({
      next: b => this.ref.close(b),
      error: e => {
        const p = toProblem(e);
        this.err.set(fieldErrors(p));
        this.error.set(p.detail);
        this.busy.set(false);
      },
    });
  }
}

export interface ReasonDialogData { title: string; message: string; action: string; }

/** Asks for a reason (≥ 3 characters) before a change that is recorded in the audit trail. */
@Component({
  selector: 'app-reason-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="ok()" id="reason-form">
      <p>{{ data.message }}</p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Reason</mat-label>
        <input matInput formControlName="reason" maxlength="500" />
        <mat-error>At least 3 characters.</mat-error>
      </mat-form-field>
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="reason-form">{{ data.action }}</button>
    </div>
  `,
  styles: `form { min-width: min(380px, 80vw); display: flex; flex-direction: column; } .full { width: 100%; }`,
})
export class ReasonDialog {
  data = inject<ReasonDialogData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<ReasonDialog, string>);
  form = inject(FormBuilder).nonNullable.group({ reason: ['', [minTrimmed(3)]] });
  ok() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.ref.close(this.form.getRawValue().reason.trim());
  }
}
