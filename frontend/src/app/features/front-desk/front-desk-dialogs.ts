import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { fieldErrors, toProblem } from '../../core/http';
import { Reservation, RoomOption } from '../../core/models';
import { minTrimmed } from '../rooms/rooms.forms';
import { dayLabel, rupeesExact } from '../settings/rates/rates.api';
import { DeskStay, FrontDeskApi, housekeepingLabel, leavingEarly, placeName } from './front-desk.api';

// ------------------------------------------------------------------ check in when something isn't right

export interface CheckInProblemData { propertyId: string; stay: DeskStay; code: string; message: string; }
/** Closes with the checked-in booking, or 'assign' when the desk wants to put the guest in another room. */
export type CheckInProblemResult = Reservation | 'assign';

/**
 * Shown when a check-in didn't go straight through: the reason in words, and what can be done about it — choose another
 * room, or (for a room that isn't ready) check in anyway with a reason, which is kept in the history and the audit trail.
 */
@Component({
  selector: 'app-check-in-problem-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Check in {{ s.guestName }} <span class="num mute">{{ s.reference }}</span></h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="anyway()" id="checkin-form">
      <p class="problem" role="alert">{{ data.message }}</p>
      @if (canOverride) {
        <p class="hint">Or check the guest in now, for example if they agree to wait while the room is cleaned. The reason is kept in
          the booking's history.</p>
        <mat-form-field appearance="outline" class="full">
          <mat-label>Reason to check in anyway</mat-label>
          <input matInput formControlName="reason" maxlength="500" placeholder="e.g. Guest agreed to wait in the lobby" />
          @if (serverError()) { <mat-error>{{ serverError() }}</mat-error> }
          @else { <mat-error>Say why the guest goes in anyway (at least 3 characters).</mat-error> }
        </mat-form-field>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Close</button>
      <button mat-stroked-button type="button" (click)="assign()">Choose another room</button>
      @if (canOverride) { <button mat-flat-button type="submit" form="checkin-form" [disabled]="busy()">Check in anyway</button> }
    </div>
  `,
  styles: `
    form { min-width: min(440px, 82vw); display: flex; flex-direction: column; }
    .problem { background: #fbefd8; color: #6b4600; border-radius: 8px; padding: 10px 12px; margin: 0 0 12px; }
    .hint { color: #5b6b6e; margin: 0 0 12px; }
    .full { width: 100%; }
    .mute { color: #5b6b6e; }
    .err { color: #b3261e; }
  `,
})
export class CheckInProblemDialog {
  data = inject<CheckInProblemData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<CheckInProblemDialog, CheckInProblemResult>);
  private api = inject(FrontDeskApi);
  s = this.data.stay;
  canOverride = this.data.code === 'HOUSEKEEPING_NOT_READY';
  busy = signal(false);
  error = signal('');
  serverError = signal('');
  form = inject(FormBuilder).nonNullable.group({ reason: ['', [minTrimmed(3)]] });

  assign() { this.ref.close('assign'); }

  anyway() {
    this.error.set(''); this.serverError.set('');
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.api.checkIn(this.data.propertyId, this.s.reservationId,
      { version: null, overrideHousekeeping: true, overrideReason: this.form.controls.reason.value.trim() }).subscribe({
      next: r => this.ref.close(r),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const f = fieldErrors(p)['overrideReason'];
        if (f) { this.serverError.set(f); this.form.controls.reason.setErrors({ server: f }); this.form.controls.reason.markAsTouched(); }
        else this.error.set(p.detail);
      },
    });
  }
}

// ------------------------------------------------------------------ assign a room (or move an in-house guest)

export interface AssignRoomData { propertyId: string; stay: DeskStay; today: string; }

/**
 * Puts a booking's room (or dorm bed) in a chosen room. Before check-in it moves all its nights (the price stays as
 * booked). For a guest already in, it moves them from today and needs a reason; the old room is marked for cleaning.
 * Rooms are listed ready-and-free first; one that isn't ready can still be chosen with a reason.
 */
@Component({
  selector: 'app-assign-room-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ inHouse ? 'Move' : 'Assign a room for' }} {{ s.guestName }} <span class="num mute">{{ s.reference }}</span></h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="assign-form">
      <p class="hint">{{ inHouse ? 'From today; the nights already slept stay with the current room, which is marked for cleaning.'
                                 : dayLabel(s.checkIn) + ' – ' + dayLabel(s.checkOut) + '. The price stays as booked.' }}</p>
      @if (s.rooms.length > 1) {
        <mat-form-field appearance="outline" class="full">
          <mat-label>Which room of the booking</mat-label>
          <mat-select formControlName="row">
            @for (r of s.rooms; track r.reservationUnitId) { <mat-option [value]="r.reservationUnitId">{{ place(r) }}</mat-option> }
          </mat-select>
        </mat-form-field>
      } @else { <p>Now: <b>{{ place(s.rooms[0]) }}</b></p> }

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ inHouse ? 'Move to' : 'Room' }}</mat-label>
        <mat-select formControlName="target">
          @for (o of options(); track key(o)) {
            <mat-option [value]="key(o)" [disabled]="!o.free || !o.fits">
              {{ place(o) }} · {{ o.typeName }} · {{ hk(o.housekeeping) }}
              @if (o.problem) { <small class="mute"> — {{ o.problem }}</small> }
            </mat-option>
          }
        </mat-select>
        @if (loading()) { <mat-hint>Loading rooms…</mat-hint> }
        @if (targetError()) { <mat-error>{{ targetError() }}</mat-error> }
        @else { <mat-error>Choose the room.</mat-error> }
      </mat-form-field>
      @if (chosen(); as o) {
        @if (o.problem && o.free && o.fits) {
          <p class="warn">{{ o.problem }}</p>
          @if (needsReady()) {
            <mat-checkbox formControlName="override">Put the guest there anyway (say why below)</mat-checkbox>
          }
        }
      }
      <mat-form-field appearance="outline" class="full">
        <mat-label>Reason{{ reasonRequired() ? '' : ' (optional)' }}</mat-label>
        <input matInput formControlName="reason" maxlength="500" placeholder="e.g. Guest asked for a quieter room" />
        @if (reasonError()) { <mat-error>{{ reasonError() }}</mat-error> }
        @else { <mat-error>Say why (at least 3 characters).</mat-error> }
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Close</button>
      <button mat-flat-button type="submit" form="assign-form" [disabled]="busy()">{{ inHouse ? 'Move guest' : 'Assign room' }}</button>
    </div>
  `,
  styles: `
    form { min-width: min(480px, 84vw); display: flex; flex-direction: column; }
    .hint { color: #5b6b6e; margin: 0 0 12px; }
    .full { width: 100%; }
    .mute { color: #5b6b6e; }
    .warn { color: #8a5a00; margin: -8px 0 8px; }
    .err { color: #b3261e; }
    mat-checkbox { margin: 0 0 12px; }
  `,
})
export class AssignRoomDialog {
  data = inject<AssignRoomData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<AssignRoomDialog, Reservation>);
  private api = inject(FrontDeskApi);
  s = this.data.stay;
  inHouse = this.s.status === 'CHECKED_IN';
  dayLabel = dayLabel;
  hk = housekeepingLabel;
  place = placeName;

  busy = signal(false);
  loading = signal(false);
  error = signal('');
  targetError = signal('');
  reasonError = signal('');
  options = signal<RoomOption[]>([]);

  form = inject(FormBuilder).nonNullable.group({
    row: [this.s.rooms.find(r => r.housekeeping !== null && r.housekeeping !== 'READY')?.reservationUnitId ?? this.s.rooms[0]?.reservationUnitId ?? ''],
    target: ['', [(c: { value: string }) => (c.value ? null : { required: true })]],
    override: [false],
    reason: [''],
  });
  private target = toSignal(this.form.controls.target.valueChanges, { initialValue: '' });
  private override = toSignal(this.form.controls.override.valueChanges, { initialValue: false });
  chosen = computed(() => this.options().find(o => this.key(o) === this.target()) ?? null);
  /** The guest walks in now (in house, or arriving today), so the room must be ready unless overridden. */
  needsReady = computed(() => this.inHouse || this.s.checkIn <= this.data.today);
  reasonRequired = computed(() => this.inHouse || (this.override() && this.needsReady()));

  constructor() {
    this.form.controls.row.valueChanges.subscribe(() => this.load());
    this.form.controls.target.valueChanges.subscribe(() => { this.targetError.set(''); this.form.controls.override.setValue(false); });
    this.load();
  }

  key(o: { unitId: string; bedId: string | null }) { return o.unitId + '/' + (o.bedId ?? ''); }

  private load() {
    this.loading.set(true);
    this.form.controls.target.setValue('');
    this.api.roomOptions(this.data.propertyId, this.s.reservationId, this.form.controls.row.value).subscribe({
      next: o => { this.options.set(o); this.loading.set(false); },
      error: e => { this.loading.set(false); this.error.set(toProblem(e).detail); },
    });
  }

  save() {
    this.error.set(''); this.reasonError.set('');
    const reason = this.form.controls.reason.value.trim();
    this.form.controls.reason.setErrors(null);
    if (this.reasonRequired() && reason.length < 3) this.form.controls.reason.setErrors({ minTrimmed: true });
    else if (reason && reason.length < 3) this.form.controls.reason.setErrors({ minTrimmed: true });
    const o = this.chosen();
    if (this.form.invalid || !o) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.api.assignRoom(this.data.propertyId, this.s.reservationId, {
      reservationUnitId: this.form.controls.row.value, unitId: o.unitId, bedId: o.bedId, reason: reason || null,
      version: this.s.version, overrideHousekeeping: this.form.controls.override.value,
    }).subscribe({
      next: r => this.ref.close(r),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const f = fieldErrors(p);
        if (f['reason']) { this.reasonError.set(f['reason']); this.form.controls.reason.setErrors({ server: f['reason'] }); this.form.controls.reason.markAsTouched(); }
        const t = f['unitId'] ?? f['bedId'];
        if (t) { this.targetError.set(t); this.form.controls.target.setErrors({ server: t }); this.form.controls.target.markAsTouched(); }
        if (!p.errors) this.error.set(p.code === 'CONCURRENCY_CONFLICT'
          ? 'Someone changed this booking a moment ago. Close this window to load the latest, then try again.' : p.detail);
        else if (f['reservationUnitId']) this.error.set(f['reservationUnitId']);
      },
    });
  }
}

// ------------------------------------------------------------------ check out

export interface CheckOutData { propertyId: string; stay: DeskStay; today: string; }

/** Check-out: what is still owed, and, when the guest leaves before the booked day, the reason (the nights left are
 *  released so the room can be sold again). The rooms are marked for cleaning. */
@Component({
  selector: 'app-check-out-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Check out {{ s.guestName }} <span class="num mute">{{ s.reference }}</span></h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="checkout-form">
      <dl class="summary">
        <dt>Stay</dt><dd>{{ dayLabel(s.checkIn) }} – {{ dayLabel(s.checkOut) }}</dd>
        <dt>Room</dt><dd>{{ rooms }}</dd>
        @if (s.balance !== null) {
          <dt>Balance</dt><dd [class.owed]="s.balance > 0" class="num">{{ s.balance > 0 ? rupees(s.balance) + ' still owed' : 'Nothing owed' }}</dd>
        }
      </dl>
      <p class="hint">The {{ s.rooms.length === 1 ? 'room is' : 'rooms are' }} marked dirty for housekeeping, and the bill's GST invoice is issued with a receipt for each payment.</p>
      @if (early) {
        <p class="warn">The guest is booked until {{ dayLabel(s.checkOut) }}. The nights left are released so the room can be sold again.</p>
        <mat-form-field appearance="outline" class="full">
          <mat-label>Why are they leaving early?</mat-label>
          <input matInput formControlName="reason" maxlength="500" placeholder="e.g. Family emergency" />
          @if (reasonError()) { <mat-error>{{ reasonError() }}</mat-error> }
          @else { <mat-error>Say why the guest is leaving early (at least 3 characters).</mat-error> }
        </mat-form-field>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Close</button>
      <button mat-flat-button type="submit" form="checkout-form" [disabled]="busy()">Check out</button>
    </div>
  `,
  styles: `
    form { min-width: min(420px, 82vw); display: flex; flex-direction: column; }
    .summary { display: grid; grid-template-columns: max-content 1fr; gap: 6px 16px; margin: 0 0 8px; }
    .summary dt { color: #5b6b6e; } .summary dd { margin: 0; }
    .owed { color: #b3261e; font-weight: 600; }
    .hint { color: #5b6b6e; margin: 0 0 12px; }
    .warn { color: #8a5a00; margin: 0 0 12px; }
    .full { width: 100%; }
    .mute { color: #5b6b6e; }
    .err { color: #b3261e; }
  `,
})
export class CheckOutDialog {
  data = inject<CheckOutData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<CheckOutDialog, Reservation>);
  private api = inject(FrontDeskApi);
  s = this.data.stay;
  early = leavingEarly(this.s, this.data.today);
  rooms = this.s.rooms.map(placeName).join(', ');
  dayLabel = dayLabel;
  rupees = rupeesExact;
  busy = signal(false);
  error = signal('');
  reasonError = signal('');
  form = inject(FormBuilder).nonNullable.group({ reason: ['', this.early ? [minTrimmed(3)] : []] });

  save() {
    this.error.set(''); this.reasonError.set('');
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.api.checkOut(this.data.propertyId, this.s.reservationId, { version: null, reason: this.form.controls.reason.value.trim() || null }).subscribe({
      next: r => this.ref.close(r),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const f = fieldErrors(p)['reason'];
        if (f && this.early) { this.reasonError.set(f); this.form.controls.reason.setErrors({ server: f }); this.form.controls.reason.markAsTouched(); }
        else this.error.set(f ?? p.detail);
      },
    });
  }
}
