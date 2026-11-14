import { MatDatepickerModule } from '@angular/material/datepicker';
import { Component, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { ActivityStay, CreateTripsResult, Driver, TransportRoute, Trip, TripPassenger, Vehicle } from '../../core/models';
import { rupees, seats } from '../activities/activities.api';
import { short, showError } from '../leave/leave.api';
import { TransportApi } from './transport.api';

const server = (c: AbstractControl | null) => (c?.errors?.['server'] as string | undefined) ?? '';
const text = (v: string | null | undefined) => (v ?? '').trim() || null;
const TIMES = /^\s*([01]?\d|2[0-3]):[0-5]\d(\s*,\s*([01]?\d|2[0-3]):[0-5]\d)*\s*$/;

// ------------------------------------------------------------------ booking seats

export interface BookTripData { propertyId: string; trip: Trip; }

/**
 * Booking seats on a trip: a staying guest (₹500 a seat goes on their bill), or anyone by name when the trip is free or
 * the seat is complimentary (with the reason). The seats left are shown; the server checks them again under the trip's lock.
 */
@Component({
  selector: 'app-book-trip-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Book {{ t.routeName }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="trip-book-form">
      <p class="mute">{{ short(t.date) }}, {{ t.departTime }} {{ t.origin }} → {{ t.destination }} {{ t.arriveTime }}, {{ seats(t.seatsLeft) }} left,
        {{ t.price ? rupees(t.price) + ' a seat' : 'free' }}</p>
      <mat-form-field appearance="outline"><mat-label>Guest staying</mat-label>
        <mat-select formControlName="reservationId" [aria-label]="'Guest staying'">
          <mat-option [value]="null">Not staying with us (type the name)</mat-option>
          @for (x of stays(); track x.reservationId) {
            <mat-option [value]="x.reservationId">{{ x.rooms }}, {{ x.guestName }} ({{ x.reference }}{{ x.status === 'CONFIRMED' ? ', arriving' : '' }})</mat-option>
          }
        </mat-select>
        <mat-error>{{ server(form.get('reservationId')) }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>{{ form.value.reservationId ? 'Name on the booking (optional)' : 'Name' }}</mat-label>
        <input matInput formControlName="guestName" maxlength="100" placeholder="e.g. Asha Rao" />
        <mat-error>{{ server(form.get('guestName')) }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Seats</mat-label>
        <input matInput type="number" formControlName="seats" min="1" [max]="t.seatsLeft" />
        <mat-error>{{ server(form.get('seats')) || 'Book 1–' + t.seatsLeft + ' seats.' }}</mat-error></mat-form-field>
      @if (t.price) {
        <mat-checkbox formControlName="complimentary">Complimentary (no charge)</mat-checkbox>
        @if (form.value.complimentary) {
          <mat-form-field appearance="outline"><mat-label>Why it is complimentary</mat-label>
            <input matInput formControlName="complimentaryReason" maxlength="200" placeholder="e.g. Honeymoon welcome" />
            <mat-error>{{ server(form.get('complimentaryReason')) || 'Say why it is complimentary.' }}</mat-error></mat-form-field>
        }
      }
      <mat-form-field appearance="outline"><mat-label>Note for the driver (optional)</mat-label>
        <input matInput formControlName="note" maxlength="200" placeholder="e.g. Pick up at the cottages, child seat" />
        <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>
      @if (t.price && !form.value.complimentary) {
        <p class="total" data-testid="trip-total">{{ rupees(t.price * (form.value.seats || 0)) }}
          {{ form.value.reservationId ? 'goes on the guest\\'s bill' : '' }}</p>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="trip-book-form" [disabled]="busy()">Book</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 2px; min-width: min(460px, 84vw); } .err { color: var(--ro-error); } .mute { color: var(--ro-mute); }
           .total { font-weight: 600; margin: var(--space-1) 0; } mat-checkbox { margin: -4px 0 var(--space-2); }`,
})
export class BookTripDialog {
  data = inject<BookTripData>(MAT_DIALOG_DATA);
  t = this.data.trip;
  private ref = inject(MatDialogRef<BookTripDialog, TripPassenger>);
  private api = inject(TransportApi);
  server = server; short = short; rupees = rupees; seats = seats;
  busy = signal(false);
  error = signal('');
  stays = signal<ActivityStay[]>([]);
  /** One key per booking attempt: a retry after a lost answer replays the first booking. */
  private key = crypto.randomUUID();
  form = inject(FormBuilder).group({
    reservationId: [null as string | null],
    guestName: [''],
    seats: [1, [Validators.required, Validators.min(1), Validators.max(Math.max(1, this.t.seatsLeft))]],
    complimentary: [false],
    complimentaryReason: [''],
    note: [''],
  });

  constructor() {
    this.api.stays(this.data.propertyId, this.t.date).subscribe({ next: x => this.stays.set(x), error: e => showError(null, e, m => this.error.set(m)) });
    this.form.valueChanges.subscribe(() => { this.key = crypto.randomUUID(); });
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.error.set('');
    this.api.book(this.data.propertyId, {
      tripId: this.t.id, seats: Number(v.seats), reservationId: v.reservationId, guestName: text(v.guestName), complimentary: !!v.complimentary,
      complimentaryReason: v.complimentary ? text(v.complimentaryReason) : null, note: text(v.note),
    }, this.key).subscribe({
      next: p => this.ref.close(p),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ putting trips on, changing one

export interface TripsData { propertyId: string; routes: TransportRoute[]; vehicles: Vehicle[]; drivers: Driver[]; from: string; }

/** Puts trips on for a range of days at one or more departure times, with the vehicle and the driver. */
@Component({
  selector: 'app-trips-dialog',
  imports: [MatDatepickerModule, ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Put trips on</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="trips-form">
      <mat-form-field appearance="outline"><mat-label>Route</mat-label>
        <mat-select formControlName="routeId" [aria-label]="'Route'">
          @for (r of data.routes; track r.id) { <mat-option [value]="r.id">{{ r.name }} ({{ r.origin }} → {{ r.destination }})</mat-option> }
        </mat-select>
        <mat-error>{{ server(form.get('routeId')) || 'Choose the route.' }}</mat-error></mat-form-field>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Vehicle</mat-label>
          <mat-select formControlName="vehicleId" [aria-label]="'Vehicle'">
            @for (v of data.vehicles; track v.id) { <mat-option [value]="v.id">{{ v.name }}, {{ seats(v.seats) }}</mat-option> }
          </mat-select>
          <mat-error>{{ server(form.get('vehicleId')) || 'Choose the vehicle.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Driver</mat-label>
          <mat-select formControlName="driverUserId" [aria-label]="'Driver'">
            <mat-option [value]="null">Choose later</mat-option>
            @for (d of data.drivers; track d.userId) { <mat-option [value]="d.userId">{{ d.name }}{{ d.isDriver ? '' : ' (manager)' }}</mat-option> }
          </mat-select>
          <mat-error>{{ server(form.get('driverUserId')) }}</mat-error></mat-form-field>
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>From</mat-label><input matInput [matDatepicker]="dp26" formControlName="fromDate" /><mat-datepicker-toggle matIconSuffix [for]="dp26" /><mat-datepicker #dp26 />
          <mat-error>{{ server(form.get('fromDate')) || 'Choose the first day.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>To</mat-label><input matInput [matDatepicker]="dp27" formControlName="toDate" /><mat-datepicker-toggle matIconSuffix [for]="dp27" /><mat-datepicker #dp27 />
          <mat-error>{{ server(form.get('toDate')) || 'Choose the last day.' }}</mat-error></mat-form-field>
      </div>
      <mat-form-field appearance="outline"><mat-label>Departure times</mat-label>
        <input matInput formControlName="times" placeholder="08:00, 10:00, 16:00" />
        <mat-hint>24-hour times, separated by commas</mat-hint>
        <mat-error>{{ server(form.get('times')) || 'Give times like 08:00, 16:00.' }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Price a seat (optional)</mat-label><input matInput type="number" formControlName="price" />
        <mat-hint>Empty: the route's</mat-hint><mat-error>{{ server(form.get('price')) }}</mat-error></mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="trips-form" [disabled]="busy()">Put on</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: var(--space-1); min-width: min(480px, 84vw); } .row { display: flex; gap: var(--space-2); flex-wrap: wrap; }
           .row > * { flex: 1 1 150px; } .err { color: var(--ro-error); }`,
})
export class TripsDialog {
  data = inject<TripsData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<TripsDialog, CreateTripsResult>);
  private api = inject(TransportApi);
  server = server; seats = seats;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({
    routeId: [this.data.routes[0]?.id ?? null, Validators.required],
    vehicleId: [this.data.vehicles[0]?.id ?? null, Validators.required],
    driverUserId: [this.data.drivers.find(d => d.isDriver)?.userId ?? null as string | null],
    fromDate: [this.data.from, Validators.required],
    toDate: [this.data.from, Validators.required],
    times: ['08:00', [Validators.required, Validators.pattern(TIMES)]],
    price: [null as number | null],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    const times = v.times!.split(',').map(t => t.trim()).filter(Boolean).map(t => t.padStart(5, '0'));
    this.busy.set(true);
    this.error.set('');
    this.api.createTrips(this.data.propertyId, {
      routeId: v.routeId!, vehicleId: v.vehicleId!, driverUserId: v.driverUserId, fromDate: v.fromDate!, toDate: v.toDate!, times,
      price: v.price === null || (v.price as unknown) === '' ? null : Number(v.price),
    }).subscribe({
      next: r => this.ref.close(r),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}

export interface TripEditData { propertyId: string; trip: Trip; vehicles: Vehicle[]; drivers: Driver[]; }

/** Changes a trip's vehicle, driver, seats, price for new bookings and note. */
@Component({
  selector: 'app-trip-edit-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ t.routeName }}, {{ short(t.date) }} {{ t.departTime }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="trip-edit-form">
      <p class="mute">{{ seats(t.seatsBooked) }} booked. A new price is for new bookings only.</p>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Vehicle</mat-label>
          <mat-select formControlName="vehicleId" [aria-label]="'Vehicle'">
            @for (v of data.vehicles; track v.id) { <mat-option [value]="v.id">{{ v.name }}, {{ seats(v.seats) }}</mat-option> }
          </mat-select>
          <mat-error>{{ server(form.get('vehicleId')) || 'Choose the vehicle.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Driver</mat-label>
          <mat-select formControlName="driverUserId" [aria-label]="'Driver'">
            <mat-option [value]="null">Not chosen yet</mat-option>
            @for (d of data.drivers; track d.userId) { <mat-option [value]="d.userId">{{ d.name }}{{ d.isDriver ? '' : ' (manager)' }}</mat-option> }
          </mat-select>
          <mat-error>{{ server(form.get('driverUserId')) }}</mat-error></mat-form-field>
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Seats</mat-label><input matInput type="number" formControlName="capacity" />
          <mat-error>{{ server(form.get('capacity')) || 'Seats are 1–60.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Price a seat</mat-label><input matInput type="number" formControlName="price" />
          <mat-error>{{ server(form.get('price')) || 'The price is ₹0 or more.' }}</mat-error></mat-form-field>
      </div>
      <mat-form-field appearance="outline"><mat-label>Note (optional)</mat-label><input matInput formControlName="note" maxlength="200" />
        <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="trip-edit-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; min-width: min(460px, 84vw); } .row { display: flex; gap: var(--space-2); flex-wrap: wrap; }
           .row > * { flex: 1 1 150px; } .err { color: var(--ro-error); } .mute { color: var(--ro-mute); }`,
})
export class TripEditDialog {
  data = inject<TripEditData>(MAT_DIALOG_DATA);
  t = this.data.trip;
  private ref = inject(MatDialogRef<TripEditDialog, Trip>);
  private api = inject(TransportApi);
  server = server; short = short; seats = seats;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({
    vehicleId: [this.t.vehicleId, Validators.required],
    driverUserId: [this.t.driverUserId],
    capacity: [this.t.capacity, [Validators.required, Validators.min(1), Validators.max(60)]],
    price: [this.t.price, [Validators.required, Validators.min(0)]],
    note: [this.t.note ?? ''],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.error.set('');
    this.api.updateTrip(this.data.propertyId, this.t, {
      vehicleId: v.vehicleId!, driverUserId: v.driverUserId, capacity: Number(v.capacity), price: Number(v.price), note: text(v.note),
    }).subscribe({
      next: r => this.ref.close(r),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ vehicles and routes

export interface VehicleData { propertyId: string; vehicle: Vehicle | null; }

@Component({
  selector: 'app-vehicle-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSlideToggleModule],
  template: `
    <h2 mat-dialog-title>{{ v ? v.name : 'New vehicle' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="vehicle-form">
      <div class="row">
        <mat-form-field appearance="outline" class="code"><mat-label>Code</mat-label><input matInput formControlName="code" maxlength="10" placeholder="VAN1" />
          <mat-error>{{ server(form.get('code')) || 'Give a short code, e.g. VAN1.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" maxlength="60" placeholder="Force Traveller" />
          <mat-error>{{ server(form.get('name')) || 'Give the vehicle a name.' }}</mat-error></mat-form-field>
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Registration number</mat-label>
          <input matInput formControlName="registration" maxlength="16" placeholder="MH 07 AB 1234" />
          <mat-error>{{ server(form.get('registration')) || 'Give the registration number, e.g. MH 07 AB 1234.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Passenger seats</mat-label><input matInput type="number" formControlName="seats" />
          <mat-hint>Not counting the driver</mat-hint>
          <mat-error>{{ server(form.get('seats')) || '1–60 seats.' }}</mat-error></mat-form-field>
      </div>
      <mat-form-field appearance="outline"><mat-label>Note (optional)</mat-label><input matInput formControlName="note" maxlength="200" />
        <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>
      <mat-slide-toggle formControlName="isActive">In use</mat-slide-toggle>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="vehicle-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: var(--space-1); min-width: min(480px, 86vw); } .row { display: flex; gap: var(--space-2); flex-wrap: wrap; }
           .row > * { flex: 1 1 150px; } .code { flex: 0 1 130px; } .err { color: var(--ro-error); } mat-slide-toggle { margin: var(--space-1) 0; }`,
})
export class VehicleDialog {
  data = inject<VehicleData>(MAT_DIALOG_DATA);
  v = this.data.vehicle;
  private ref = inject(MatDialogRef<VehicleDialog, Vehicle>);
  private api = inject(TransportApi);
  server = server;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({
    code: [this.v?.code ?? '', Validators.required],
    name: [this.v?.name ?? '', Validators.required],
    registration: [this.v?.registration ?? '', Validators.required],
    seats: [this.v?.seats ?? 12, [Validators.required, Validators.min(1), Validators.max(60)]],
    note: [this.v?.note ?? ''],
    isActive: [this.v?.isActive ?? true],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const x = this.form.getRawValue();
    this.busy.set(true);
    this.error.set('');
    this.api.saveVehicle(this.data.propertyId, this.v?.id ?? null, {
      code: x.code!.trim().toUpperCase(), name: x.name!.trim(), registration: x.registration!.trim(), seats: Number(x.seats), note: text(x.note),
      sortOrder: this.v?.sortOrder ?? 0, isActive: !!x.isActive, version: this.v?.version ?? null,
    }).subscribe({
      next: r => this.ref.close(r),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}

export interface RouteData { propertyId: string; route: TransportRoute | null; }

@Component({
  selector: 'app-route-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSlideToggleModule],
  template: `
    <h2 mat-dialog-title>{{ r ? r.name : 'New route' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="route-form">
      <div class="row">
        <mat-form-field appearance="outline" class="code"><mat-label>Code</mat-label><input matInput formControlName="code" maxlength="10" placeholder="BEACH" />
          <mat-error>{{ server(form.get('code')) || 'Give a short code, e.g. BEACH.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" maxlength="60" placeholder="Beach shuttle" />
          <mat-error>{{ server(form.get('name')) || 'Give the route a name.' }}</mat-error></mat-form-field>
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>From</mat-label><input matInput formControlName="origin" maxlength="60" placeholder="Resort gate" />
          <mat-error>{{ server(form.get('origin')) || 'Say where it starts.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>To</mat-label><input matInput formControlName="destination" maxlength="60" placeholder="Tarkarli beach" />
          <mat-error>{{ server(form.get('destination')) || 'Say where it goes.' }}</mat-error></mat-form-field>
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Km</mat-label><input matInput type="number" formControlName="distanceKm" />
          <mat-error>{{ server(form.get('distanceKm')) || 'More than 0, up to 500 km.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Minutes</mat-label><input matInput type="number" formControlName="durationMinutes" />
          <mat-error>{{ server(form.get('durationMinutes')) || '5–360 minutes.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Price a seat (₹)</mat-label><input matInput type="number" formControlName="price" />
          <mat-error>{{ server(form.get('price')) || '₹0 or more.' }}</mat-error></mat-form-field>
      </div>
      <mat-slide-toggle formControlName="isActive">In use</mat-slide-toggle>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="route-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: var(--space-1); min-width: min(520px, 86vw); } .row { display: flex; gap: var(--space-2); flex-wrap: wrap; }
           .row > * { flex: 1 1 120px; } .code { flex: 0 1 130px; } .err { color: var(--ro-error); } mat-slide-toggle { margin: var(--space-1) 0; }`,
})
export class RouteDialog {
  data = inject<RouteData>(MAT_DIALOG_DATA);
  r = this.data.route;
  private ref = inject(MatDialogRef<RouteDialog, TransportRoute>);
  private api = inject(TransportApi);
  server = server;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({
    code: [this.r?.code ?? '', Validators.required],
    name: [this.r?.name ?? '', Validators.required],
    origin: [this.r?.origin ?? '', Validators.required],
    destination: [this.r?.destination ?? '', Validators.required],
    distanceKm: [this.r?.distanceKm ?? 8, [Validators.required, Validators.min(0.1), Validators.max(500)]],
    durationMinutes: [this.r?.durationMinutes ?? 20, [Validators.required, Validators.min(5), Validators.max(360)]],
    price: [this.r?.price ?? 500, [Validators.required, Validators.min(0)]],
    isActive: [this.r?.isActive ?? true],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const x = this.form.getRawValue();
    this.busy.set(true);
    this.error.set('');
    this.api.saveRoute(this.data.propertyId, this.r?.id ?? null, {
      code: x.code!.trim().toUpperCase(), name: x.name!.trim(), origin: x.origin!.trim(), destination: x.destination!.trim(),
      distanceKm: Number(x.distanceKm), durationMinutes: Number(x.durationMinutes), price: Number(x.price), sortOrder: this.r?.sortOrder ?? 0,
      isActive: !!x.isActive, version: this.r?.version ?? null,
    }).subscribe({
      next: r => this.ref.close(r),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}
