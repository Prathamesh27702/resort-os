import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { fieldErrors, toProblem } from '../../core/http';
import { Calendar, CalendarBooking, Reservation } from '../../core/models';
import { shortDate } from '../rooms/rooms.api';
import { minTrimmed } from '../rooms/rooms.forms';
import { rupees } from '../settings/rates/rates.api';
import { paymentText } from './calendar-grid';
import { CalRow, MOVABLE, buildRows, moveProblem, nightsText, rowKey } from './calendar.model';
import { ReservationsApi, guestsText, statusClass, statusLabel } from './reservations.api';

/** What a successful move returns: the updated booking and where the guest went. */
export interface MoveResult { reservation: Reservation; to: string; }

export interface MoveDialogData { propertyId: string; cal: Calendar; booking: CalendarBooking; target?: CalRow; canMove: boolean; }

/**
 * A booking on the calendar: who, when, status, source and payment; and, for people who may change bookings, moving it
 * to another room (or dorm bed) with a reason. Opened by clicking a bar, or by dropping one on another row (the target is
 * then chosen already, so a move is drag + reason + Move). Closes with the updated reservation after a move.
 */
@Component({
  selector: 'app-move-dialog',
  imports: [ReactiveFormsModule, RouterLink, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ b.guestName }} <span class="num mute">{{ b.reference }}</span></h2>
    <div mat-dialog-content>
      <dl class="summary">
        <dt>Status</dt><dd><span [class]="'chip ' + statusClass(b.status)">{{ statusLabel(b.status) }}</span></dd>
        <dt>Stay</dt><dd>{{ shortDate(b.startDate) }} – {{ shortDate(b.endDate) }} · {{ guestsText(b.adults, b.children) }}</dd>
        <dt>Room</dt><dd>{{ current()?.label }}</dd>
        <dt>Source</dt><dd>{{ b.sourceName }}</dd>
        <dt>Payment</dt><dd>{{ paymentText(b.paymentStatus) }} · booking total {{ rupees(b.totalAmount) }}</dd>
      </dl>

      @if (data.canMove && movable) {
        <form [formGroup]="form" (ngSubmit)="move()" id="move-form">
          <h3>Move to another {{ isBed ? 'bed' : 'room' }}</h3>
          <p class="hint">Same nights ({{ nightsText(b.startDate, b.endDate) }}); the price stays as booked.</p>
          <mat-form-field appearance="outline" class="full">
            <mat-label>Move to</mat-label>
            <mat-select formControlName="target">
              @for (r of options(); track r.key) {
                <mat-option [value]="r.key">{{ r.label }} · {{ r.typeName }}@if (problems().get(r.key)) { <small class="mute"> · not free</small> }</mat-option>
              }
            </mat-select>
            @if (targetError()) { <mat-error>{{ targetError() }}</mat-error> }
            @else { <mat-error>Choose the {{ isBed ? 'bed' : 'room' }} to move the guest to.</mat-error> }
          </mat-form-field>
          @if (localProblem() && !form.controls.target.errors) { <p class="err" role="alert">{{ localProblem() }}</p> }
          <mat-form-field appearance="outline" class="full">
            <mat-label>Reason</mat-label>
            <input matInput formControlName="reason" maxlength="500" placeholder="e.g. AC not working in 102" />
            @if (err()['reason']) { <mat-error>{{ err()['reason'] }}</mat-error> }
            @else { <mat-error>Say why the guest is moving (at least 3 characters).</mat-error> }
          </mat-form-field>
          @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
        </form>
      } @else if (data.canMove) {
        <p class="hint">{{ statusLabel(b.status) }} bookings can't be moved from the calendar.</p>
      }
    </div>
    <div mat-dialog-actions align="end">
      <a mat-button [routerLink]="['/reservations', b.reservationId]" mat-dialog-close>Open booking</a>
      <button mat-button mat-dialog-close type="button">Close</button>
      @if (data.canMove && movable) {
        <button mat-flat-button type="submit" form="move-form" [disabled]="busy()">Move</button>
      }
    </div>
  `,
  styles: `
    [mat-dialog-content] { min-width: min(440px, 82vw); }
    .summary { display: grid; grid-template-columns: max-content 1fr; gap: 6px 16px; margin: 0 0 12px; }
    .summary dt { color: #5b6b6e; }
    .summary dd { margin: 0; }
    h3 { margin: 8px 0 2px; font-size: 15px; }
    .hint { color: #5b6b6e; margin: 0 0 12px; }
    .full { width: 100%; }
    .mute { color: #5b6b6e; }
    .err { color: #b3261e; margin: -4px 0 12px; }
    .chip.ok { background: #e1f2e8; color: #1d5b36; }
    .chip.wait { background: #fbefd8; color: #8a5a00; }
  `,
})
export class MoveDialog {
  data = inject<MoveDialogData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<MoveDialog, MoveResult>);
  private api = inject(ReservationsApi);

  b = this.data.booking;
  isBed = this.b.bedId !== null;
  movable = MOVABLE.includes(this.b.status);
  shortDate = shortDate;
  rupees = rupees;
  guestsText = guestsText;
  statusLabel = statusLabel;
  statusClass = statusClass;
  paymentText = paymentText;
  nightsText = nightsText;

  busy = signal(false);
  error = signal('');
  err = signal<Record<string, string>>({});
  serverTargetError = signal('');

  private rows = buildRows(this.data.cal.rooms);
  current = signal(this.rows.find(r => r.key === rowKey(this.b.unitId, this.b.bedId)) ?? null);
  /** Rows of the same kind (rooms for a room booking, beds for a bed), other than where the guest is now. */
  options = signal(this.rows.filter(r => (r.mode === 'BED') === this.isBed && r.key !== this.current()?.key && r.isActive));
  problems = computed(() => new Map(this.options().map(r => [r.key, moveProblem(this.b, r, this.data.cal)])));

  form = inject(FormBuilder).nonNullable.group({
    target: [this.data.target?.key ?? '', [(c: { value: string }) => (c.value ? null : { required: true })]],
    reason: ['', [minTrimmed(3)]],
  });
  private targetKey = toSignal(this.form.controls.target.valueChanges, { initialValue: this.form.controls.target.value });
  localProblem = computed(() => {
    const row = this.rows.find(r => r.key === this.targetKey());
    return row ? moveProblem(this.b, row, this.data.cal) : null;
  });
  targetError = computed(() => this.serverTargetError());

  constructor() {
    this.form.controls.target.valueChanges.subscribe(() => this.serverTargetError.set(''));
  }

  move() {
    this.error.set('');
    this.err.set({});
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const target = this.rows.find(r => r.key === this.form.controls.target.value)!;
    this.busy.set(true);
    this.api.reassign(this.data.propertyId, this.b.reservationId, {
      reservationUnitId: this.b.reservationUnitId, unitId: target.unitId, bedId: target.bedId,
      reason: this.form.controls.reason.value.trim(), version: this.b.version,
    }).subscribe({
      next: r => this.ref.close({ reservation: r, to: target.label }),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const f = fieldErrors(p);
        this.err.set(f);
        if (f['reason']) { this.form.controls.reason.setErrors({ server: f['reason'] }); this.form.controls.reason.markAsTouched(); }
        const t = f['unitId'] ?? f['bedId'];
        if (t) {
          this.serverTargetError.set(t);
          this.form.controls.target.setErrors({ server: t });
          this.form.controls.target.markAsTouched();
        }
        if (!p.errors) this.error.set(p.code === 'CONCURRENCY_CONFLICT'
          ? 'Someone changed this booking since the calendar was loaded. Close this, and the calendar will reload with the latest.'
          : p.detail);
        else if (f['reservationUnitId']) this.error.set(f['reservationUnitId']);
      },
    });
  }
}
