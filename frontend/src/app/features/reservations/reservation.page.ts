import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormArray, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { controlPath, showFieldErrors, toProblem } from '../../core/http';
import { Reservation, ReservationStatus } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { CanDirective } from '../../shared/can.directive';
import { formatPhone } from '../guests/guests.api';
import { ReasonDialog, ReasonDialogData } from '../rooms/block-dialog';
import { shortDate } from '../rooms/rooms.api';
import { dayLabel, rupees, rupeesExact } from '../settings/rates/rates.api';
import { ReservationsApi, guestsText, statusClass, statusLabel } from './reservations.api';
import { dateError, stayDates } from './reservations.forms';

/** One booking: stay, rooms, the price night by night, guests, notes and every status change. */
@Component({
  selector: 'app-reservation',
  imports: [RouterLink, ReactiveFormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule,
            MatSnackBarModule, CanDirective],
  template: `
    <div class="page">
      @if (r(); as r) {
        <div class="page-head">
          <div>
            <a routerLink="/reservations" class="back">Reservations</a>
            <h1><span class="num">{{ r.reference }}</span> · {{ primary()?.fullName }}
              <span [class]="'chip ' + statusClass(r.status)">{{ statusLabel(r.status) }}</span></h1>
            <p>{{ dayLabel(r.checkIn) }} – {{ dayLabel(r.checkOut) }} · {{ r.nights }} night{{ r.nights === 1 ? '' : 's' }} ·
               {{ guestsText(r.adults, r.children) }}</p>
          </div>
          <div class="head-actions">
            @if (can('CONFIRMED') && auth.can('reservation.update', pid)) {
              <button mat-flat-button (click)="confirm()" [disabled]="busy()">{{ r.status === 'INQUIRY' ? 'Turn into a booking' : 'Confirm' }}</button>
            }
            @if (changeable() && auth.can('reservation.update', pid)) {
              <button mat-stroked-button (click)="change()" [disabled]="busy()"><mat-icon>edit_calendar</mat-icon>Change dates or guests</button>
            }
            @if (can('NO_SHOW') && auth.can('reservation.cancel', pid)) {
              <button mat-stroked-button (click)="noShow()" [disabled]="busy()">No-show</button>
            }
            @if (can('CANCELLED') && auth.can('reservation.cancel', pid)) {
              <button mat-stroked-button class="danger" (click)="cancel()" [disabled]="busy()">Cancel booking</button>
            }
          </div>
        </div>
        @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

        <div class="grid">
          <section class="panel card">
            <h2>Booking</h2>
            <dl>
              <dt>Source</dt><dd>{{ r.sourceName }}</dd>
              <dt>Rate plan</dt><dd>{{ r.ratePlanName }} <span class="mute">({{ r.mealPlan }})</span></dd>
              <dt>Total</dt><dd class="num"><b>{{ rupeesExact(r.totalAmount) }}</b></dd>
              <dt>Booked</dt><dd>{{ when(r.createdAt) }}@if (r.createdByName) { by {{ r.createdByName }} }</dd>
              @if (r.specialRequests) { <dt>Requests</dt><dd class="pre">{{ r.specialRequests }}</dd> }
            </dl>
          </section>

          <section class="panel card">
            <h2>Guests</h2>
            @for (g of r.guests; track g.guestId) {
              <p><a [routerLink]="'/guests/' + g.guestId">{{ g.fullName }}</a> @if (g.isPrimary) { <span class="chip">Booker</span> }
                 <br /><small class="mute num">{{ phone(g.phone) || g.email || 'No contact details' }}</small></p>
            }
          </section>
        </div>

        <section class="panel card wide">
          <h2>Rooms</h2>
          <table class="data">
            <thead><tr><th>Room</th><th>Type</th><th>Guests</th><th class="right">Amount</th></tr></thead>
            <tbody>
              @for (u of currentUnits(); track u.id) {
                <tr>
                  <td><b>{{ u.unitName }}</b>@if (u.bedCode) { · bed {{ u.bedCode }} }</td>
                  <td>{{ u.accommodationTypeName }}</td>
                  <td>{{ guestsText(u.adults, u.children) }}</td>
                  <td class="right num">{{ rupeesExact(u.total) }}</td>
                </tr>
              }
            </tbody>
          </table>
          @if (!holds()) { <p class="hint">This booking doesn't hold these rooms: they can be sold to someone else.</p> }
        </section>

        <section class="panel card wide">
          <h2>Price night by night</h2>
          <table class="data">
            <thead><tr><th>Night</th><th>Room</th><th class="right">Room price</th><th class="right hide-sm">Extra guests</th><th class="right">Total</th></tr></thead>
            <tbody>
              @for (n of r.breakdown; track n.reservationUnitId + n.date) {
                <tr>
                  <td class="num">{{ dayLabel(n.date) }}</td>
                  <td>{{ unitName(n.reservationUnitId) }}</td>
                  <td class="right num">{{ rupeesExact(n.baseAmount) }}</td>
                  <td class="right num hide-sm">{{ n.extraAdultAmount + n.extraChildAmount ? rupeesExact(n.extraAdultAmount + n.extraChildAmount) : '—' }}</td>
                  <td class="right num">{{ rupeesExact(n.total) }}</td>
                </tr>
              }
            </tbody>
            <tfoot><tr><td colspan="4" class="right"><b>Total</b></td><td class="right num"><b>{{ rupeesExact(r.totalAmount) }}</b></td></tr></tfoot>
          </table>
        </section>

        <div class="grid">
          <section class="panel card">
            <h2>Notes</h2>
            @for (n of r.notes; track n.id) {
              <p class="note"><span class="pre">{{ n.note }}</span><br /><small class="mute">{{ when(n.createdAt) }}@if (n.createdByName) { · {{ n.createdByName }} }</small></p>
            } @empty { <p class="mute">No notes yet.</p> }
            <form [formGroup]="noteForm" (ngSubmit)="addNote()" *appCan="'reservation.update'">
              <mat-form-field appearance="outline" class="full">
                <mat-label>Add a note</mat-label>
                <textarea matInput formControlName="note" rows="2" maxlength="2000" placeholder="e.g. Advance of ₹2,000 received by UPI"></textarea>
                @if (noteForm.controls.note.errors?.['server']; as m) { <mat-error>{{ m }}</mat-error> }
                @else { <mat-error>Write the note first.</mat-error> }
              </mat-form-field>
              <button mat-stroked-button type="submit" [disabled]="busy()">Add note</button>
            </form>
          </section>

          <section class="panel card">
            <h2>History</h2>
            @for (h of history(); track $index) {
              <p>
                @if (h.fromStatus) { {{ statusLabel(h.fromStatus) }} → } <b>{{ statusLabel(h.toStatus) }}</b>
                @if (h.reason) { <br /><span class="pre">“{{ h.reason }}”</span> }
                <br /><small class="mute">{{ when(h.changedAt) }}@if (h.changedByName) { · {{ h.changedByName }} }</small>
              </p>
            }
          </section>
        </div>
      } @else if (error()) {
        <p class="err" role="alert">{{ error() }}</p>
        <a routerLink="/reservations">Back to reservations</a>
      } @else {
        <p class="mute">Loading…</p>
      }
    </div>
  `,
  styles: `
    .back { color: #5b6b6e; text-decoration: none; font-size: 0.9rem; } .back::before { content: '‹ '; }
    h1 .chip { font-size: .8rem; vertical-align: middle; margin-left: 8px; }
    .grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; margin-bottom: 16px; }
    .card { padding: 16px 20px; }
    .wide { margin-bottom: 16px; }
    h2 { font-size: 1rem; color: #0e3b43; margin: 0 0 12px; }
    .head-actions { display: flex; gap: 8px; flex-wrap: wrap; }
    dl { display: grid; grid-template-columns: 120px 1fr; gap: 6px 12px; margin: 0; }
    dt { color: #5b6b6e; }
    dd { margin: 0; }
    .mute { color: #5b6b6e; }
    .right { text-align: right; white-space: nowrap; }
    .pre { white-space: pre-wrap; }
    .err { color: #b3261e; }
    .danger { color: #b3261e; }
    .full { width: 100%; }
    .note { border-bottom: 1px solid #edf1f0; padding-bottom: 8px; margin: 0 0 8px; }
    .hint { color: #5b6b6e; font-size: .85rem; margin: 8px 0 0; }
    .chip.ok { background: #e1f2e8; color: #1d5b36; }
    .chip.wait { background: #fbefd8; color: #8a5a00; }
    @media (max-width: 900px) { .grid { grid-template-columns: 1fr; } }
  `,
})
export class ReservationPage implements OnInit {
  id = input.required<string>();
  private api = inject(ReservationsApi);
  private props = inject(PropertyContext);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  auth = inject(AuthService);

  r = signal<Reservation | null>(null);
  error = signal('');
  busy = signal(false);
  noteForm = inject(FormBuilder).nonNullable.group({ note: ['', [Validators.maxLength(2000)]] });

  pid = this.props.current()!.id;
  primary = computed(() => this.r()?.guests.find(g => g.isPrimary) ?? null);
  currentUnits = computed(() => this.r()?.units.filter(u => u.current) ?? []);
  holds = computed(() => this.currentUnits().some(u => u.holdsInventory));
  changeable = computed(() => ['INQUIRY', 'PENDING', 'CONFIRMED'].includes(this.r()?.status ?? ''));
  history = computed(() => [...(this.r()?.history ?? [])].reverse());

  statusLabel = statusLabel;
  statusClass = statusClass;
  guestsText = guestsText;
  dayLabel = dayLabel;
  rupees = rupees;
  rupeesExact = rupeesExact;
  shortDate = shortDate;
  phone = formatPhone;

  ngOnInit() { this.load(); }

  private load() {
    this.api.get(this.pid, this.id()).subscribe({
      next: r => this.r.set(r),
      error: e => this.error.set(toProblem(e).status === 404
        ? 'This booking doesn’t exist at this property. Check the property selected at the top.' : toProblem(e).detail),
    });
  }

  can(s: ReservationStatus) { return this.r()?.nextStatuses.includes(s) ?? false; }
  when(iso: string) { return inZone(iso, this.props.current()!.timezone, { dateStyle: 'medium', timeStyle: 'short' }); }
  unitName(ruId: string) {
    const u = this.r()?.units.find(x => x.id === ruId);
    return u ? u.unitName + (u.bedCode ? ` · bed ${u.bedCode}` : '') : '';
  }

  private done(r: Reservation, message: string) {
    this.r.set(r); this.busy.set(false); this.error.set('');
    this.snack.open(message, undefined, { duration: 2500 });
  }
  private failed(e: unknown) { this.busy.set(false); this.error.set(toProblem(e).detail); }

  confirm() {
    this.busy.set(true);
    this.api.confirm(this.pid, this.id()).subscribe({ next: r => this.done(r, `${r.reference} confirmed`), error: e => this.failed(e) });
  }

  cancel() {
    const r = this.r()!;
    this.askReason({
      title: `Cancel ${r.reference}?`,
      message: 'The rooms become free for other guests straight away. The booking and its history are kept.',
      action: 'Cancel booking',
    }, reason => this.api.cancel(this.pid, r.id, reason).subscribe({ next: x => this.done(x, `${x.reference} cancelled`), error: e => this.failed(e) }));
  }

  noShow() {
    const r = this.r()!;
    this.askReason({
      title: `Mark ${r.reference} as a no-show?`,
      message: 'Use this when the guest didn’t arrive. The rooms become free for the remaining nights.',
      action: 'Mark no-show',
    }, reason => this.api.noShow(this.pid, r.id, reason).subscribe({ next: x => this.done(x, `${x.reference} marked as no-show`), error: e => this.failed(e) }));
  }

  private askReason(data: ReasonDialogData, then: (reason: string) => void) {
    this.dialog.open<ReasonDialog, ReasonDialogData, string>(ReasonDialog, { data }).afterClosed().subscribe(reason => {
      if (!reason) return;
      this.busy.set(true);
      then(reason);
    });
  }

  addNote() {
    const c = this.noteForm.controls.note;
    if (!c.value.trim()) { c.setErrors({ empty: true }); c.markAsTouched(); return; }
    this.busy.set(true);
    this.api.addNote(this.pid, this.id(), c.value.trim()).subscribe({
      next: r => { this.noteForm.reset(); c.markAsUntouched(); this.done(r, 'Note added'); },
      error: e => { this.busy.set(false); const p = toProblem(e); if (!showFieldErrors(this.noteForm, p)['note']) this.error.set(p.detail); },
    });
  }

  change() {
    this.dialog.open<ChangeStayDialog, ChangeStayData, Reservation>(ChangeStayDialog, {
      data: { reservation: this.r()!, propertyId: this.pid, today: this.props.current()!.localDate },
    }).afterClosed().subscribe(r => { if (r) this.done(r, `${r.reference} updated · new total ${rupeesExact(r.totalAmount)}`); });
  }
}

export interface ChangeStayData { reservation: Reservation; propertyId: string; today: string; }

/**
 * Change the dates, the guests per room, or the requests. The same rooms are kept when they are free for the new
 * dates; otherwise the API picks other free rooms of the same type, and re-prices every night.
 */
@Component({
  selector: 'app-change-stay-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatDatepickerModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Change {{ data.reservation.reference }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="change-form">
      <div class="grid">
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Check-in</mat-label>
          <input matInput [matDatepicker]="dpCheckIn" formControlName="checkIn" />
          <mat-datepicker-toggle matIconSuffix [for]="dpCheckIn" />
          <mat-datepicker #dpCheckIn />
          <mat-error>{{ dateErr('checkIn') }}</mat-error>
        </mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Check-out</mat-label>
          <input matInput [matDatepicker]="dpCheckOut" formControlName="checkOut" />
          <mat-datepicker-toggle matIconSuffix [for]="dpCheckOut" />
          <mat-datepicker #dpCheckOut />
          <mat-error>{{ dateErr('checkOut') }}</mat-error>
        </mat-form-field>
      </div>
      <div formArrayName="rooms">
        @for (line of rooms.controls; track $index; let i = $index) {
          <div class="grid" [formGroupName]="i">
            <span class="room">{{ names[i] }}</span>
            <mat-form-field appearance="outline">
              <mat-label>{{ bedBased[i] ? 'Beds (adults)' : 'Adults' }}</mat-label>
              <input matInput type="number" min="1" formControlName="adults" />
              <mat-error>{{ line.get('adults')?.errors?.['server'] ?? 'At least one adult.' }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Children</mat-label>
              <input matInput type="number" min="0" formControlName="children" />
              <mat-error>{{ line.get('children')?.errors?.['server'] ?? 'Zero or more.' }}</mat-error>
            </mat-form-field>
          </div>
        }
      </div>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Special requests</mat-label>
        <textarea matInput formControlName="specialRequests" rows="2" maxlength="2000"></textarea>
        <mat-error>{{ form.controls.specialRequests.errors?.['server'] ?? 'At most 2000 characters.' }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Close</button>
      <button mat-flat-button type="submit" form="change-form" [disabled]="busy()">Save and re-price</button>
    </div>
  `,
  styles: `
    form { min-width: min(460px, 80vw); display: flex; flex-direction: column; }
    .grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 4px 12px; align-items: start; }
    .room { grid-column: 1 / -1; font-weight: 500; margin: 4px 0 8px; }
    .full { width: 100%; }
    .err { color: #b3261e; }
  `,
})
export class ChangeStayDialog {
  data = inject<ChangeStayData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<ChangeStayDialog, Reservation>);
  private api = inject(ReservationsApi);
  private fb = inject(FormBuilder).nonNullable;

  error = signal('');
  busy = signal(false);
  names: string[] = [];
  bedBased: boolean[] = [];
  private lines: { accommodationTypeId: string; unitId: string }[] = [];

  // Today's check-in can stay as it is even if it is now in the past; only a moved check-in must be today or later.
  private minCheckIn = this.data.reservation.checkIn < this.data.today ? this.data.reservation.checkIn : this.data.today;

  rooms: FormArray;
  form;

  constructor() {
    const r = this.data.reservation;
    // One line per room; dorm beds are grouped per dorm (the API books that many beds).
    const byUnit = new Map<string, { typeId: string; name: string; bed: boolean; adults: number; children: number }>();
    for (const u of r.units.filter(x => x.current)) {
      const g = byUnit.get(u.unitId) ?? { typeId: u.accommodationTypeId, name: u.unitName, bed: u.bedId !== null, adults: 0, children: 0 };
      g.adults += u.adults; g.children += u.children;
      byUnit.set(u.unitId, g);
    }
    this.rooms = this.fb.array([...byUnit.entries()].map(([unitId, g]) => {
      this.lines.push({ accommodationTypeId: g.typeId, unitId });
      this.names.push(g.name);
      this.bedBased.push(g.bed);
      return this.fb.group({ adults: [g.adults, [Validators.required, Validators.min(1)]], children: [g.children, [Validators.min(0)]] });
    }));
    this.form = this.fb.group({
      checkIn: [r.checkIn, Validators.required],
      checkOut: [r.checkOut, Validators.required],
      rooms: this.rooms,
      specialRequests: [r.specialRequests ?? '', Validators.maxLength(2000)],
    }, { validators: stayDates(this.minCheckIn) });
  }

  dateErr(name: 'checkIn' | 'checkOut') { return dateError(this.form.controls[name], this.minCheckIn); }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue() as { checkIn: string; checkOut: string; rooms: { adults: number; children: number }[]; specialRequests: string };
    const body = {
      checkIn: v.checkIn, checkOut: v.checkOut,
      rooms: v.rooms.map((x, i) => ({ ...this.lines[i], adults: Number(x.adults), children: Number(x.children ?? 0) })),
      specialRequests: v.specialRequests.trim() || null,
      version: this.data.reservation.version,
    };
    this.busy.set(true);
    this.error.set('');
    this.api.update(this.data.propertyId, this.data.reservation.id, body).subscribe({
      next: r => this.ref.close(r),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const shown = showFieldErrors(this.form, p);
        // Room-level problems ("Room 103 isn't free …") and conflicts have no single field: show them above the buttons.
        if (Object.keys(shown).length === 0 || Object.keys(shown).some(k => !this.form.get(k) && !this.form.get(controlPath(k)))) {
          this.error.set(p.status === 409 && p.code === 'CONCURRENCY_CONFLICT'
            ? 'Someone else changed this booking a moment ago. Close this window to see their change, then try again.' : p.detail);
        }
      },
    });
  }
}
