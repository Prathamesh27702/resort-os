import { Component, inject, signal } from '@angular/core';
import { AbstractControl, FormArray, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import {
  Activity, ActivityBooking, ActivitySession, ActivitySessionDetail, ActivityStay, CreateSessionsResult, EquipmentType, WeatherCancelResult,
} from '../../core/models';
import { short, showError } from '../leave/leave.api';
import { ActivitiesApi, rupees, seats } from './activities.api';

const server = (c: AbstractControl | null) => (c?.errors?.['server'] as string | undefined) ?? '';
const text = (v: string | null | undefined) => (v ?? '').trim() || null;

// ------------------------------------------------------------------ booking a guest on a session

export interface BookData { propertyId: string; session: ActivitySession; }

/**
 * Booking seats: a staying guest (the price goes on their bill), or anyone by name when the session is free or the booking
 * is complimentary (with the reason). The seats left are shown; the server checks them again under the session's lock.
 */
@Component({
  selector: 'app-book-activity-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Book {{ s.activityName }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="book-form">
      <p class="mute">{{ short(s.date) }} · {{ s.startTime }}–{{ s.endTime }} · {{ seats(s.seatsLeft) }} left ·
        {{ s.price ? rupees(s.price) + ' a person' : 'free' }}@if (s.meetingPoint) { · meet at {{ s.meetingPoint }} }</p>
      <mat-form-field appearance="outline"><mat-label>Guest staying</mat-label>
        <mat-select formControlName="reservationId" [aria-label]="'Guest staying'">
          <mat-option [value]="null">Not staying with us (type the name)</mat-option>
          @for (x of stays(); track x.reservationId) {
            <mat-option [value]="x.reservationId">{{ x.rooms }} · {{ x.guestName }} ({{ x.reference }}{{ x.status === 'CONFIRMED' ? ', arriving' : '' }})</mat-option>
          }
        </mat-select>
        <mat-error>{{ server(form.get('reservationId')) }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>{{ form.value.reservationId ? 'Name on the booking (optional)' : 'Name' }}</mat-label>
        <input matInput formControlName="guestName" maxlength="100" placeholder="e.g. Asha Rao" />
        <mat-error>{{ server(form.get('guestName')) }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Seats</mat-label>
        <input matInput type="number" formControlName="seats" min="1" [max]="s.seatsLeft" />
        <mat-error>{{ server(form.get('seats')) || 'Book 1–' + s.seatsLeft + ' seats.' }}</mat-error></mat-form-field>
      @if (s.price) {
        <mat-checkbox formControlName="complimentary">Complimentary (no charge)</mat-checkbox>
        @if (form.value.complimentary) {
          <mat-form-field appearance="outline"><mat-label>Why it is complimentary</mat-label>
            <input matInput formControlName="complimentaryReason" maxlength="200" placeholder="e.g. Honeymoon welcome" />
            <mat-error>{{ server(form.get('complimentaryReason')) || 'Say why it is complimentary.' }}</mat-error></mat-form-field>
        }
      }
      <mat-form-field appearance="outline"><mat-label>Note (optional)</mat-label>
        <input matInput formControlName="note" maxlength="200" placeholder="e.g. Two beginners" />
        <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>
      @if (s.price && !form.value.complimentary) {
        <p class="total" data-testid="book-total">{{ rupees(s.price * (form.value.seats || 0)) }}
          {{ form.value.reservationId ? 'goes on the guest\\'s bill' : '' }}</p>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="book-form" [disabled]="busy()">Book</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 2px; min-width: min(460px, 84vw); } .err { color: var(--ro-error); } .mute { color: var(--ro-mute); }
           .total { font-weight: 600; margin: var(--space-1) 0; } mat-checkbox { margin: -4px 0 var(--space-2); }`,
})
export class BookActivityDialog {
  data = inject<BookData>(MAT_DIALOG_DATA);
  s = this.data.session;
  private ref = inject(MatDialogRef<BookActivityDialog, ActivityBooking>);
  private api = inject(ActivitiesApi);
  server = server; short = short; rupees = rupees; seats = seats;
  busy = signal(false);
  error = signal('');
  stays = signal<ActivityStay[]>([]);
  /** One key per booking attempt: a retry after a lost answer replays the first booking. */
  private key = crypto.randomUUID();
  form = inject(FormBuilder).group({
    reservationId: [null as string | null],
    guestName: [''],
    seats: [1, [Validators.required, Validators.min(1), Validators.max(Math.max(1, this.s.seatsLeft))]],
    complimentary: [false],
    complimentaryReason: [''],
    note: [''],
  });

  constructor() {
    this.api.stays(this.data.propertyId, this.s.date).subscribe({ next: x => this.stays.set(x), error: e => showError(null, e, m => this.error.set(m)) });
    this.form.valueChanges.subscribe(() => { this.key = crypto.randomUUID(); });
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.error.set('');
    this.api.book(this.data.propertyId, {
      sessionId: this.s.id, seats: Number(v.seats), reservationId: v.reservationId, guestName: text(v.guestName), complimentary: !!v.complimentary,
      complimentaryReason: v.complimentary ? text(v.complimentaryReason) : null, note: text(v.note),
    }, this.key).subscribe({
      next: b => this.ref.close(b),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ the weather

export interface WeatherData { propertyId: string; detail: ActivitySessionDetail; }

/** Cancelling a session for the weather: each booking is refunded on the bill (the default) or moved to a later session. */
@Component({
  selector: 'app-weather-cancel-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Cancel for the weather?</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="weather-form">
      <p><b>{{ s.activityName }}</b> · {{ short(s.date) }} {{ s.startTime }} · {{ seats(s.seatsBooked) }} booked</p>
      <mat-form-field appearance="outline"><mat-label>What is the weather doing</mat-label>
        <input matInput formControlName="reason" maxlength="300" placeholder="e.g. Rough sea, red flag" cdkFocusInitial />
        <mat-error>{{ server(form.get('reason')) || 'Say what the weather is doing (at least 3 characters).' }}</mat-error></mat-form-field>
      @if (booked.length) {
        <p class="mute">Each guest is refunded on their bill, unless you move them to another session (their charge goes with them).</p>
        <div formArrayName="moves" class="moves">
          @for (b of booked; track b.id; let i = $index) {
            <div class="mv" [attr.data-booking]="b.code">
              <span><b>{{ b.guestName }}</b> · {{ seats(b.seats) }} · {{ b.code }}</span>
              <mat-form-field appearance="outline" subscriptSizing="dynamic"><mat-label>{{ b.code }}</mat-label>
                <mat-select [formControlName]="i" [aria-label]="'What happens to ' + b.code">
                  <mat-option [value]="''">{{ b.settlement === 'ROOM_CHARGE' ? 'Refund ' + rupees(b.amount) + ' on the bill' : 'Cancel (nothing was charged)' }}</mat-option>
                  @for (t of targets; track t.id) {
                    <mat-option [value]="t.id">Move to {{ short(t.date) }} {{ t.startTime }} ({{ seats(t.seatsLeft) }} left)</mat-option>
                  }
                </mat-select>
              </mat-form-field>
            </div>
          }
        </div>
      } @else { <p class="mute">Nobody is booked on it.</p> }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Not now</button>
      <button mat-flat-button color="warn" type="submit" form="weather-form" [disabled]="busy()">Cancel the session</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; min-width: min(520px, 86vw); } .err { color: var(--ro-error); } .mute { color: var(--ro-mute); } p { margin: var(--space-1) 0; }
           .moves { display: flex; flex-direction: column; gap: 6px; } .mv { display: flex; gap: var(--space-2); align-items: center; flex-wrap: wrap; }
           .mv > span { flex: 1 1 180px; } .mv mat-form-field { flex: 1 1 220px; }`,
})
export class WeatherCancelDialog {
  data = inject<WeatherData>(MAT_DIALOG_DATA);
  s = this.data.detail.session;
  booked = this.data.detail.bookings.filter(b => b.status === 'BOOKED');
  targets = this.data.detail.moveTargets;
  private ref = inject(MatDialogRef<WeatherCancelDialog, WeatherCancelResult>);
  private api = inject(ActivitiesApi);
  private fb = inject(FormBuilder);
  server = server; short = short; rupees = rupees; seats = seats;
  busy = signal(false);
  error = signal('');
  form = this.fb.group({
    reason: ['', [Validators.required, Validators.minLength(3)]],
    moves: this.fb.array(this.booked.map(() => this.fb.control(''))),
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const moves = (this.form.get('moves') as FormArray).getRawValue() as string[];
    this.busy.set(true);
    this.error.set('');
    this.api.weatherCancel(this.data.propertyId, this.s, this.form.value.reason!.trim(),
      this.booked.map((b, i) => ({ bookingId: b.id, toSessionId: moves[i] })).filter(m => m.toSessionId)).subscribe({
      next: r => this.ref.close(r),
      // The reasons (a move that doesn't fit, a guest who leaves before) are about the moves list as a whole: shown in words.
      error: e => { this.busy.set(false); showError(null, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ putting sessions on, changing one

export interface SessionsData { propertyId: string; activities: Activity[]; from: string; }

/** Puts sessions on for a range of days at one or more start times. */
@Component({
  selector: 'app-sessions-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Put sessions on</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="sessions-form">
      <mat-form-field appearance="outline"><mat-label>Activity</mat-label>
        <mat-select formControlName="activityId" [aria-label]="'Activity'">
          @for (a of data.activities; track a.id) { <mat-option [value]="a.id">{{ a.name }}</mat-option> }
        </mat-select>
        <mat-error>{{ server(form.get('activityId')) || 'Choose the activity.' }}</mat-error></mat-form-field>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>From</mat-label><input matInput type="date" formControlName="fromDate" />
          <mat-error>{{ server(form.get('fromDate')) || 'Choose the first day.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>To</mat-label><input matInput type="date" formControlName="toDate" />
          <mat-error>{{ server(form.get('toDate')) || 'Choose the last day.' }}</mat-error></mat-form-field>
      </div>
      <mat-form-field appearance="outline"><mat-label>Start times</mat-label>
        <input matInput formControlName="times" placeholder="07:00, 16:00" />
        <mat-hint>24-hour times, separated by commas</mat-hint>
        <mat-error>{{ server(form.get('times')) || 'Give times like 07:00, 16:00.' }}</mat-error></mat-form-field>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Seats (optional)</mat-label><input matInput type="number" formControlName="capacity" />
          <mat-hint>Empty: the activity's</mat-hint><mat-error>{{ server(form.get('capacity')) }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Price a person (optional)</mat-label><input matInput type="number" formControlName="price" />
          <mat-hint>Empty: the activity's</mat-hint><mat-error>{{ server(form.get('price')) }}</mat-error></mat-form-field>
      </div>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="sessions-form" [disabled]="busy()">Put on</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: var(--space-1); min-width: min(460px, 84vw); } .row { display: flex; gap: var(--space-2); flex-wrap: wrap; }
           .row > * { flex: 1 1 150px; } .err { color: var(--ro-error); }`,
})
export class SessionsDialog {
  data = inject<SessionsData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<SessionsDialog, CreateSessionsResult>);
  private api = inject(ActivitiesApi);
  server = server;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({
    activityId: [this.data.activities[0]?.id ?? null, Validators.required],
    fromDate: [this.data.from, Validators.required],
    toDate: [this.data.from, Validators.required],
    times: ['07:00', [Validators.required, Validators.pattern(/^\s*([01]?\d|2[0-3]):[0-5]\d(\s*,\s*([01]?\d|2[0-3]):[0-5]\d)*\s*$/)]],
    capacity: [null as number | null],
    price: [null as number | null],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    const times = v.times!.split(',').map(t => t.trim()).filter(Boolean).map(t => t.padStart(5, '0'));
    this.busy.set(true);
    this.error.set('');
    this.api.createSessions(this.data.propertyId, {
      activityId: v.activityId!, fromDate: v.fromDate!, toDate: v.toDate!, times,
      capacity: v.capacity === null || (v.capacity as unknown) === '' ? null : Number(v.capacity),
      price: v.price === null || (v.price as unknown) === '' ? null : Number(v.price),
    }).subscribe({
      next: r => this.ref.close(r),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}

export interface SessionEditData { propertyId: string; session: ActivitySession; }

@Component({
  selector: 'app-session-edit-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ s.activityName }} · {{ short(s.date) }} {{ s.startTime }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="session-form">
      <p class="mute">{{ seats(s.seatsBooked) }} booked. A new price is for new bookings only.</p>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Seats</mat-label><input matInput type="number" formControlName="capacity" />
          <mat-error>{{ server(form.get('capacity')) || 'Seats are 1–200.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Price a person</mat-label><input matInput type="number" formControlName="price" />
          <mat-error>{{ server(form.get('price')) || 'The price is ₹0 or more.' }}</mat-error></mat-form-field>
      </div>
      <mat-form-field appearance="outline"><mat-label>Note (optional)</mat-label><input matInput formControlName="note" maxlength="200" />
        <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="session-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; min-width: min(420px, 84vw); } .row { display: flex; gap: var(--space-2); flex-wrap: wrap; }
           .row > * { flex: 1 1 140px; } .err { color: var(--ro-error); } .mute { color: var(--ro-mute); }`,
})
export class SessionEditDialog {
  data = inject<SessionEditData>(MAT_DIALOG_DATA);
  s = this.data.session;
  private ref = inject(MatDialogRef<SessionEditDialog, ActivitySession>);
  private api = inject(ActivitiesApi);
  server = server; short = short; seats = seats;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({
    capacity: [this.s.capacity, [Validators.required, Validators.min(1), Validators.max(200)]],
    price: [this.s.price, [Validators.required, Validators.min(0)]],
    note: [this.s.note ?? ''],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.error.set('');
    this.api.updateSession(this.data.propertyId, this.s, Number(v.capacity), Number(v.price), text(v.note)).subscribe({
      next: r => this.ref.close(r),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ an activity

export interface ActivityEditData { propertyId: string; activity: Activity | null; equipmentTypes: EquipmentType[]; }

@Component({
  selector: 'app-activity-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatSlideToggleModule],
  template: `
    <h2 mat-dialog-title>{{ a ? a.name : 'New activity' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="activity-form">
      <div class="row">
        <mat-form-field appearance="outline" class="code"><mat-label>Code</mat-label><input matInput formControlName="code" maxlength="10" placeholder="KAYAK" />
          <mat-error>{{ server(form.get('code')) || 'Give a short code, e.g. KAYAK.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" maxlength="60" placeholder="Kayaking" />
          <mat-error>{{ server(form.get('name')) || 'Give the activity a name.' }}</mat-error></mat-form-field>
      </div>
      <mat-form-field appearance="outline"><mat-label>Description (optional)</mat-label><input matInput formControlName="description" maxlength="500" />
        <mat-error>{{ server(form.get('description')) }}</mat-error></mat-form-field>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Minutes</mat-label><input matInput type="number" formControlName="durationMinutes" />
          <mat-error>{{ server(form.get('durationMinutes')) || '15–720 minutes.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Seats a session</mat-label><input matInput type="number" formControlName="capacity" />
          <mat-error>{{ server(form.get('capacity')) || '1–200 seats.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Price a person (₹)</mat-label><input matInput type="number" formControlName="price" />
          <mat-error>{{ server(form.get('price')) || '₹0 or more.' }}</mat-error></mat-form-field>
      </div>
      <mat-form-field appearance="outline"><mat-label>Where to meet (optional)</mat-label><input matInput formControlName="meetingPoint" maxlength="100" />
        <mat-error>{{ server(form.get('meetingPoint')) }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Equipment each person needs</mat-label>
        <mat-select formControlName="equipmentTypeId" [aria-label]="'Equipment each person needs'">
          <mat-option [value]="null">None</mat-option>
          @for (t of data.equipmentTypes; track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }
        </mat-select>
        <mat-error>{{ server(form.get('equipmentTypeId')) }}</mat-error></mat-form-field>
      <mat-slide-toggle formControlName="weatherDependent">The weather can stop it</mat-slide-toggle>
      <mat-slide-toggle formControlName="isActive">Offered</mat-slide-toggle>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="activity-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: var(--space-1); min-width: min(520px, 86vw); } .row { display: flex; gap: var(--space-2); flex-wrap: wrap; }
           .row > * { flex: 1 1 130px; } .code { flex: 0 1 130px; } .err { color: var(--ro-error); } mat-slide-toggle { margin: var(--space-1) 0; }`,
})
export class ActivityDialog {
  data = inject<ActivityEditData>(MAT_DIALOG_DATA);
  a = this.data.activity;
  private ref = inject(MatDialogRef<ActivityDialog, Activity>);
  private api = inject(ActivitiesApi);
  server = server;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({
    code: [this.a?.code ?? '', Validators.required],
    name: [this.a?.name ?? '', Validators.required],
    description: [this.a?.description ?? ''],
    durationMinutes: [this.a?.durationMinutes ?? 60, [Validators.required, Validators.min(15), Validators.max(720)]],
    capacity: [this.a?.capacity ?? 6, [Validators.required, Validators.min(1), Validators.max(200)]],
    price: [this.a?.price ?? 0, [Validators.required, Validators.min(0)]],
    meetingPoint: [this.a?.meetingPoint ?? ''],
    equipmentTypeId: [this.a?.equipmentTypeId ?? null as string | null],
    weatherDependent: [this.a?.weatherDependent ?? true],
    isActive: [this.a?.isActive ?? true],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.error.set('');
    this.api.saveActivity(this.data.propertyId, this.a?.id ?? null, {
      code: v.code!.trim().toUpperCase(), name: v.name!.trim(), description: text(v.description), durationMinutes: Number(v.durationMinutes),
      capacity: Number(v.capacity), price: Number(v.price), meetingPoint: text(v.meetingPoint), weatherDependent: !!v.weatherDependent,
      equipmentTypeId: v.equipmentTypeId, sortOrder: this.a?.sortOrder ?? 0, isActive: !!v.isActive, version: this.a?.version ?? null,
    }).subscribe({
      next: r => this.ref.close(r),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}
