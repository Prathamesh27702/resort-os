import { Component, DestroyRef, Injector, afterNextRender, computed, inject, signal } from '@angular/core';
import { FormBuilder, FormsModule, ReactiveFormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { BreakpointObserver } from '@angular/cdk/layout';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { Subject, debounceTime, forkJoin, map, of, switchMap } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatStepperModule } from '@angular/material/stepper';
import { fieldErrors, showFieldErrors, toProblem } from '../../core/http';
import {
  Availability, AvailabilityType, Guest, GuestListItem, PossibleDuplicate, Problem, RatePlan, RateQuote, ReservationSource,
} from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { GuestsApi, formatPhone } from '../guests/guests.api';
import { shortDate } from '../rooms/rooms.api';
import { RatesApi, dayLabel, rupees } from '../settings/rates/rates.api';
import { ReservationsApi, guestsText, newKey } from './reservations.api';
import { bookingForm, dateError, newGuestForm, stayForm } from './reservations.forms';

interface ChosenGuest { id: string; fullName: string; phone: string | null; }

/**
 * New booking in five steps: dates → availability → guest → rate → confirm. A walk-in with today's dates is five
 * clicks: Check availability, Choose, Add guest (or pick a found one), Continue, Book. Every API error is shown under
 * the field it concerns, or next to the step's button with its reason.
 */
@Component({
  selector: 'app-reservation-new',
  imports: [ReactiveFormsModule, FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule,
    MatSelectModule, MatSnackBarModule, MatStepperModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div><a routerLink="/reservations" class="back">Reservations</a><h1>New booking</h1></div>
      </div>

      <mat-stepper #stepper [linear]="true" [orientation]="mobile() ? 'vertical' : 'horizontal'" [selectedIndex]="step()"
                   (selectionChange)="step.set($event.selectedIndex)" class="panel">

        <!-- 1. Dates and guests -->
        <mat-step [completed]="!!availability()" label="Dates">
          <form [formGroup]="stay" (ngSubmit)="checkAvailability()" class="dates">
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>Check-in</mat-label>
              <input matInput type="date" formControlName="checkIn" [min]="today" />
              <mat-error>{{ dateErr(stay.controls.checkIn) }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>Check-out</mat-label>
              <input matInput type="date" formControlName="checkOut" [min]="today" />
              @if (nights() > 0) { <mat-hint>{{ nights() }} night{{ nights() === 1 ? '' : 's' }}</mat-hint> }
              <mat-error>{{ dateErr(stay.controls.checkOut) }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline" class="small">
              <mat-label>Adults</mat-label>
              <input matInput type="number" min="1" max="50" formControlName="adults" />
              <mat-error>{{ stay.controls.adults.errors?.['server'] ?? 'At least one adult is needed.' }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline" class="small">
              <mat-label>Children</mat-label>
              <input matInput type="number" min="0" max="50" formControlName="children" />
              <mat-error>{{ stay.controls.children.errors?.['server'] ?? 'Children must be between 0 and 50.' }}</mat-error>
            </mat-form-field>
            <div class="actions full">
              <button mat-flat-button type="submit" [disabled]="busy()">Check availability</button>
              @if (stepError(0)) { <span class="err" role="alert">{{ stepError(0) }}</span> }
            </div>
          </form>
        </mat-step>

        <!-- 2. Availability -->
        <mat-step [completed]="!!chosen()" label="Room">
          @if (availability(); as a) {
            <p class="lead">{{ stayText(a.from, a.to) }} · {{ guests() }}</p>
            <div class="types">
              @for (t of a.types; track t.accommodationTypeId) {
                <div class="type" [class.full]="!t.fits">
                  <div class="what">
                    <b>{{ t.name }}</b>
                    <small class="mute">{{ t.inventoryMode === 'BED' ? 'Per bed' : 'Up to ' + t.maxOccupancy + ' guests' }} ·
                      <span [class.none]="t.free === 0">{{ t.free }} of {{ t.total }} {{ t.inventoryMode === 'BED' ? 'beds' : 'rooms' }} free</span></small>
                    @if (t.fitProblem) { <small class="warn">{{ t.fitProblem }}</small> }
                  </div>
                  <div class="price num">
                    @if (t.price !== null) { <b>{{ rupees(t.price) }}</b><small class="mute">{{ a.nights }} night{{ a.nights === 1 ? '' : 's' }}, {{ t.ratePlanName }}</small> }
                    @else { <small class="warn">{{ t.priceProblem }}</small> }
                  </div>
                  @if (t.inventoryMode === 'UNIT' && t.fits) {
                    <mat-form-field appearance="outline" class="pick" subscriptSizing="dynamic">
                      <mat-label>Room</mat-label>
                      <mat-select [ngModel]="roomPick()[t.accommodationTypeId] ?? null" (ngModelChange)="pickRoom(t.accommodationTypeId, $event)"
                                  [attr.aria-label]="'Room for ' + t.name">
                        <mat-option [value]="null">First free room</mat-option>
                        @for (r of t.freeRooms; track r.id) { <mat-option [value]="r.id">{{ r.name }}@if (r.floor) { · {{ r.floor }} }</mat-option> }
                      </mat-select>
                    </mat-form-field>
                  }
                  <button mat-flat-button (click)="choose(t)" [disabled]="!t.fits || t.price === null"
                          [attr.aria-label]="'Choose ' + t.name">Choose</button>
                </div>
              } @empty { <p class="mute">This property has no active room types. Add them in Room types.</p> }
            </div>
            @if (stepError(1)) { <p class="err" role="alert">{{ stepError(1) }}</p> }
            <button mat-button matStepperPrevious>Change dates</button>
          }
        </mat-step>

        <!-- 3. Guest -->
        <mat-step [completed]="!!guest()" label="Guest">
          <div class="guest-step">
            <section>
              <h3>Find a returning guest</h3>
              <mat-form-field appearance="outline" class="wide">
                <mat-icon matPrefix>search</mat-icon>
                <mat-label>Name, phone or email</mat-label>
                <input matInput [ngModel]="guestQ()" (ngModelChange)="searchGuests($event)" autocomplete="off" />
              </mat-form-field>
              @for (g of found(); track g.id) {
                <button mat-stroked-button class="found" (click)="useGuest(g)">
                  <b>{{ g.fullName }}</b>@if (g.phone) { · <span class="mute num">{{ phone(g.phone) }}</span> }
                </button>
              } @empty { @if (guestQ().trim().length >= 2) { <p class="mute">No guest matches “{{ guestQ() }}”. Add them below.</p> } }
            </section>
            <section>
              <h3>Or add a new guest</h3>
              <form [formGroup]="newGuest" (ngSubmit)="addGuest(false)" class="form-grid">
                <mat-form-field appearance="outline">
                  <mat-label>First name</mat-label>
                  <input matInput formControlName="firstName" autocomplete="off" />
                  <mat-error>{{ newGuest.controls.firstName.errors?.['server'] ?? 'Enter the guest’s first name.' }}</mat-error>
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>Last name</mat-label>
                  <input matInput formControlName="lastName" autocomplete="off" />
                  <mat-error>{{ newGuest.controls.lastName.errors?.['server'] }}</mat-error>
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>Phone</mat-label>
                  <input matInput formControlName="phone" type="tel" placeholder="98200 12345" autocomplete="off" />
                  <mat-error>{{ newGuest.controls.phone.errors?.['server'] }}</mat-error>
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>Email</mat-label>
                  <input matInput formControlName="email" type="email" autocomplete="off" />
                  <mat-error>{{ newGuest.controls.email.errors?.['server'] ?? 'Enter a valid email, e.g. name@example.com.' }}</mat-error>
                </mat-form-field>
                @if (duplicates().length) {
                  <div class="full dups" role="alert">
                    <p>This may be someone who has stayed before:</p>
                    @for (d of duplicates(); track d.id) {
                      <button mat-stroked-button type="button" (click)="useGuest(d)"><b>{{ d.fullName }}</b> · {{ phone(d.phone) || d.email }} · {{ d.reasons.join(', ') }}</button>
                    }
                    <button mat-button type="button" (click)="addGuest(true)">No, add a new profile</button>
                  </div>
                }
                <div class="actions full">
                  <button mat-flat-button type="submit" [disabled]="busy()">Add guest and continue</button>
                  @if (stepError(2)) { <span class="err" role="alert">{{ stepError(2) }}</span> }
                </div>
              </form>
            </section>
          </div>
          <button mat-button matStepperPrevious>Back to rooms</button>
        </mat-step>

        <!-- 4. Rate and details -->
        <mat-step [completed]="rateDone()" label="Rate">
          <form [formGroup]="details" (ngSubmit)="toConfirm()" class="form-grid">
            <mat-form-field appearance="outline">
              <mat-label>Rate plan</mat-label>
              <mat-select formControlName="ratePlanId" (selectionChange)="requote()">
                @for (p of plans(); track p.id) { <mat-option [value]="p.id">{{ p.name }} ({{ p.mealPlan }})</mat-option> }
              </mat-select>
              <mat-error>{{ details.controls.ratePlanId.errors?.['server'] ?? 'Choose a rate plan.' }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Booked via</mat-label>
              <mat-select formControlName="sourceId">
                @for (s of sources(); track s.id) { <mat-option [value]="s.id">{{ s.name }}</mat-option> }
              </mat-select>
              <mat-error>{{ details.controls.sourceId.errors?.['server'] ?? 'Choose where the booking came from.' }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Status</mat-label>
              <mat-select formControlName="status">
                <mat-option value="CONFIRMED">Confirmed</mat-option>
                <mat-option value="PENDING">Pending (holds the room until confirmed)</mat-option>
                <mat-option value="INQUIRY">Enquiry (holds nothing)</mat-option>
              </mat-select>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Special requests</mat-label>
              <input matInput formControlName="specialRequests" maxlength="2000" placeholder="e.g. ground floor, extra pillows" />
              <mat-error>{{ details.controls.specialRequests.errors?.['server'] }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline" class="full">
              <mat-label>Note for the team (optional)</mat-label>
              <input matInput formControlName="note" maxlength="2000" />
            </mat-form-field>

            <div class="full quote">
              @if (quoting()) { <p class="mute">Pricing…</p> }
              @else if (quote(); as q) {
                <table class="data compact">
                  <thead><tr><th>Night</th><th class="right">Price</th></tr></thead>
                  <tbody>
                    @for (n of q.breakdown; track n.date) {
                      <tr><td>{{ day(n.date) }}@if (n.units > 1) { <small class="mute"> · {{ n.units }} beds × {{ rupees(n.unitPrice) }}</small> }
                        @if (n.extraAdults) { <small class="mute"> · {{ n.extraAdults }} extra adult</small> }
                        @if (n.extraChildren) { <small class="mute"> · {{ n.extraChildren }} extra child</small> }</td>
                        <td class="right num">{{ rupees(n.total) }}</td></tr>
                    }
                    <tr class="sum"><td>Total before tax</td><td class="right num">{{ rupees(q.total) }}</td></tr>
                  </tbody>
                </table>
              } @else if (stepError(3)) { <p class="err" role="alert">{{ stepError(3) }}</p> }
            </div>
            <div class="actions full">
              <button mat-flat-button type="submit" [disabled]="!quote() || quoting()">Continue</button>
            </div>
          </form>
          <button mat-button matStepperPrevious>Back to guest</button>
        </mat-step>

        <!-- 5. Confirm -->
        <mat-step label="Confirm">
          @if (chosen() && guest() && quote(); as q) {
            <dl class="summary">
              <dt>Guest</dt><dd><b>{{ guest()!.fullName }}</b>@if (guest()!.phone) { · <span class="mute num">{{ phone(guest()!.phone) }}</span> }</dd>
              <dt>Stay</dt><dd>{{ stayText(availability()!.from, availability()!.to) }} · {{ guests() }}</dd>
              <dt>Room</dt><dd>{{ chosen()!.name }} · {{ chosenRoomName() }}</dd>
              <dt>Rate</dt><dd>{{ planName() }} · <b class="num">{{ rupees(q.total) }}</b> before tax</dd>
              <dt>Booked via</dt><dd>{{ sourceName() }} · {{ statusText() }}</dd>
              @if (details.controls.specialRequests.value) { <dt>Requests</dt><dd>{{ details.controls.specialRequests.value }}</dd> }
            </dl>
            <div class="actions">
              <button mat-flat-button (click)="book()" [disabled]="busy()">{{ details.controls.status.value === 'INQUIRY' ? 'Save enquiry' : 'Book' }}</button>
              @if (stepError(4)) { <span class="err" role="alert">{{ stepError(4) }}</span> }
            </div>
            @if (roomTaken()) { <button mat-button (click)="backToRooms()">See what's free now</button> }
          }
          <button mat-button matStepperPrevious>Back</button>
        </mat-step>
      </mat-stepper>
    </div>
  `,
  styles: `
    .back { color: #5b6b6e; text-decoration: none; font-size: 0.9rem; } .back::before { content: '‹ '; }
    mat-stepper { padding: 8px 4px; }
    .dates { display: grid; grid-template-columns: 1fr 1fr 120px 120px; gap: 4px 12px; align-items: start; padding-top: 8px; }
    .dates .full { grid-column: 1 / -1; }
    .actions { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin: 4px 0 12px; }
    .err { color: #b3261e; }
    .warn { color: #8a5a00; display: block; }
    .mute { color: #5b6b6e; }
    .none { color: #b3261e; }
    .lead { margin: 4px 0 12px; font-weight: 500; }
    .types { display: flex; flex-direction: column; gap: 8px; margin-bottom: 8px; }
    .type { display: grid; grid-template-columns: 1fr 150px 200px auto; gap: 12px; align-items: center; padding: 12px; border: 1px solid #d9e0de; border-radius: 10px; }
    .type.full { background: #f8faf9; }
    .type .what small, .type .price small { display: block; }
    .type .price { text-align: right; }
    .pick { width: 100%; }
    .guest-step { display: grid; grid-template-columns: 1fr 1.4fr; gap: 24px; }
    .guest-step h3 { font-size: 1rem; margin: 4px 0 8px; }
    .wide { width: 100%; }
    .found { display: block; width: 100%; text-align: left; margin-bottom: 6px; }
    .dups { background: #fbefd8; border-radius: 8px; padding: 8px 12px; margin-bottom: 8px; }
    .dups p { margin: 0 0 6px; }
    .dups button { display: block; margin-bottom: 4px; text-align: left; }
    .quote { margin: 4px 0 8px; }
    table.compact td, table.compact th { padding: 6px 10px; }
    .right { text-align: right; }
    tr.sum td { font-weight: 600; border-top: 1px solid #d9e0de; }
    .summary { display: grid; grid-template-columns: 120px 1fr; gap: 6px 16px; margin: 8px 0 16px; }
    .summary dt { color: #5b6b6e; }
    .summary dd { margin: 0; }
    @media (max-width: 760px) {
      .dates { grid-template-columns: 1fr 1fr; }
      .type { grid-template-columns: 1fr auto; }
      .type .pick { grid-column: 1 / -1; }
      .type .price { text-align: left; }
      .guest-step { grid-template-columns: 1fr; }
      .summary { grid-template-columns: 1fr; gap: 0 0; }
      .summary dd { margin-bottom: 8px; }
    }
  `,
})
export class ReservationNewPage {
  private fb = inject(FormBuilder);
  private api = inject(ReservationsApi);
  private guestsApi = inject(GuestsApi);
  private rates = inject(RatesApi);
  private props = inject(PropertyContext);
  private router = inject(Router);
  private snack = inject(MatSnackBar);

  mobile = toSignal(inject(BreakpointObserver).observe('(max-width: 760px)').pipe(map(r => r.matches)), { initialValue: false });
  private pid = this.props.current()!.id;
  today = this.props.current()!.localDate;

  stay = stayForm(this.fb, this.today);
  newGuest = newGuestForm(this.fb);
  details = bookingForm(this.fb);

  step = signal(0);
  private injector = inject(Injector);
  busy = signal(false);
  errors = signal<Record<number, string>>({});
  availability = signal<Availability | null>(null);
  chosen = signal<AvailabilityType | null>(null);
  roomPick = signal<Record<string, string | null>>({});
  guest = signal<ChosenGuest | null>(null);
  guestQ = signal('');
  found = signal<GuestListItem[]>([]);
  duplicates = signal<PossibleDuplicate[]>([]);
  plans = signal<RatePlan[]>([]);
  sources = signal<ReservationSource[]>([]);
  quote = signal<RateQuote | null>(null);
  quoting = signal(false);
  rateDone = signal(false);
  roomTaken = signal(false);

  private stayValue = toSignal(this.stay.valueChanges, { initialValue: this.stay.getRawValue() });
  nights = computed(() => {
    const v = this.stayValue();
    if (!v.checkIn || !v.checkOut) return 0;
    return Math.max(0, Math.round((Date.parse(v.checkOut) - Date.parse(v.checkIn)) / 86_400_000));
  });
  guests = computed(() => { const a = this.availability(); return a ? guestsText(a.adults, a.children) : ''; });
  chosenRoomName = computed(() => {
    const t = this.chosen();
    if (!t) return '';
    if (t.inventoryMode === 'BED') { const n = this.availability()!.adults + this.availability()!.children; return `${n} bed${n === 1 ? '' : 's'}`; }
    const id = this.roomPick()[t.accommodationTypeId];
    return id ? t.freeRooms.find(r => r.id === id)?.name ?? '' : `first free room (${t.freeRooms[0]?.name ?? '—'})`;
  });

  rupees = rupees;
  phone = formatPhone;
  day = dayLabel;

  /** The key for the next Book press. A new one only when the booking itself changes, so a retry can't book twice. */
  private key = newKey();
  private keyFor = '';
  private guestSearch$ = new Subject<string>();

  constructor() {
    forkJoin([this.rates.plans(this.pid), this.api.sources(this.pid)]).subscribe({
      next: ([plans, sources]) => {
        this.plans.set(plans.filter(p => p.isActive));
        this.sources.set(sources);
        this.details.patchValue({
          ratePlanId: plans.find(p => p.isDefault)?.id ?? plans.find(p => p.isActive)?.id ?? '',
          sourceId: sources.find(s => s.code === 'WALK_IN')?.id ?? sources[0]?.id ?? '',
        });
      },
      error: e => this.setError(0, toProblem(e).detail),
    });
    this.guestSearch$.pipe(
      debounceTime(250),
      switchMap(q => q.trim().length < 2 ? of(null) : this.guestsApi.list(q, { pageSize: 5 })),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe({ next: r => this.found.set(r?.items ?? []), error: () => this.found.set([]) });
  }

  dateErr = (c: Parameters<typeof dateError>[0]) => dateError(c, this.today);
  stepError = (i: number) => this.errors()[i] ?? '';
  stayText(from: string, to: string) {
    const n = Math.round((Date.parse(to) - Date.parse(from)) / 86_400_000);
    return `${dayLabel(from)} – ${dayLabel(to)} · ${n} night${n === 1 ? '' : 's'}`;
  }
  planName = () => this.plans().find(p => p.id === this.details.controls.ratePlanId.value)?.name ?? '';
  sourceName = () => this.sources().find(s => s.id === this.details.controls.sourceId.value)?.name ?? '';
  statusText = () => ({ CONFIRMED: 'Confirmed', PENDING: 'Pending', INQUIRY: 'Enquiry, holds nothing' })[this.details.controls.status.value];

  // ------------------------------------------------------------------ step 1 → 2
  checkAvailability() {
    if (this.stay.invalid) { this.stay.markAllAsTouched(); return; }
    const v = this.stay.getRawValue();
    this.busy.set(true); this.setError(0, '');
    this.api.availability(this.pid, { from: v.checkIn, to: v.checkOut, adults: v.adults, children: v.children ?? 0 }).subscribe({
      next: a => {
        this.busy.set(false);
        this.availability.set(a);
        this.chosen.set(null);
        this.roomTaken.set(false);
        this.go(1);
      },
      error: e => this.fail(0, e, { From: 'checkIn', To: 'checkOut', Adults: 'adults', Children: 'children' }, this.stay),
    });
  }

  // ------------------------------------------------------------------ step 2 → 3
  pickRoom(typeId: string, roomId: string | null) { this.roomPick.update(p => ({ ...p, [typeId]: roomId })); }

  choose(t: AvailabilityType) {
    this.chosen.set(t);
    this.setError(1, '');
    this.quote.set(null);
    this.go(this.guest() ? 3 : 2);
    if (this.guest()) this.requote();
  }

  // ------------------------------------------------------------------ step 3 → 4
  searchGuests(q: string) { this.guestQ.set(q); this.guestSearch$.next(q); }

  useGuest(g: { id: string; fullName: string; phone: string | null }) {
    this.guest.set({ id: g.id, fullName: g.fullName, phone: g.phone });
    this.duplicates.set([]);
    this.go(3);
    this.requote();
  }

  addGuest(ignoreDuplicates: boolean) {
    if (this.newGuest.invalid) { this.newGuest.markAllAsTouched(); return; }
    const v = this.newGuest.getRawValue();
    this.busy.set(true); this.setError(2, '');
    this.guestsApi.create({
      firstName: v.firstName.trim(), lastName: v.lastName.trim() || null, phone: v.phone.trim() || null, email: v.email.trim() || null,
      dateOfBirth: null, nationality: null, companyName: null, gstin: null, notes: null, isVip: false, address: null, preferences: null,
      ignoreDuplicates,
    }).subscribe({
      next: (g: Guest) => { this.busy.set(false); this.useGuest(g); },
      error: e => {
        const p = toProblem(e);
        if (p.code === 'POSSIBLE_DUPLICATE') {
          this.busy.set(false);
          this.duplicates.set((e.error?.details ?? []) as PossibleDuplicate[]);
          return;
        }
        this.fail(2, e, {}, this.newGuest);
      },
    });
  }

  // ------------------------------------------------------------------ step 4
  requote() {
    const a = this.availability(), t = this.chosen(), plan = this.details.controls.ratePlanId.value;
    if (!a || !t || !plan) return;
    this.quoting.set(true); this.setError(3, ''); this.quote.set(null); this.rateDone.set(false);
    this.rates.quote(this.pid, { type: t.accommodationTypeId, ratePlan: plan, from: a.from, to: a.to, adults: a.adults, children: a.children })
      .subscribe({
        next: q => { this.quote.set(q); this.quoting.set(false); },
        error: e => { this.quoting.set(false); this.setError(3, toProblem(e).detail); },
      });
  }

  toConfirm() {
    if (this.details.invalid) { this.details.markAllAsTouched(); return; }
    this.rateDone.set(true);
    this.go(4);
  }

  // ------------------------------------------------------------------ step 5
  book() {
    const a = this.availability()!, t = this.chosen()!, g = this.guest()!, d = this.details.getRawValue();
    const body = {
      checkIn: a.from, checkOut: a.to,
      rooms: [{ accommodationTypeId: t.accommodationTypeId, unitId: this.roomPick()[t.accommodationTypeId] ?? null, adults: a.adults, children: a.children }],
      guestIds: [g.id], sourceId: d.sourceId, ratePlanId: d.ratePlanId, status: d.status,
      specialRequests: d.specialRequests.trim() || null, note: d.note.trim() || null,
    };
    const json = JSON.stringify(body);
    if (json !== this.keyFor) { this.key = newKey(); this.keyFor = json; }

    this.busy.set(true); this.setError(4, ''); this.roomTaken.set(false);
    this.api.create(this.pid, body, this.key).subscribe({
      next: r => {
        this.snack.open(`${r.status === 'INQUIRY' ? 'Enquiry saved' : 'Booked'}: ${r.reference}`, 'OK', { duration: 4000 });
        this.router.navigateByUrl(`/reservations/${r.id}`);
      },
      error: e => {
        const p = toProblem(e);
        this.roomTaken.set(p.code === 'ROOM_UNAVAILABLE');
        // Field errors go back to the step that has the field.
        const errs = fieldErrors(p);
        if (errs['checkIn'] || errs['checkOut']) { showFieldErrors(this.stay, p); this.busy.set(false); this.setError(0, p.detail); this.go(0); return; }
        if (errs['ratePlanId'] || errs['sourceId'] || errs['specialRequests']) { showFieldErrors(this.details, p); this.busy.set(false); this.go(3); return; }
        this.fail(4, e, {}, null);
      },
    });
  }

  backToRooms() { this.go(0); this.checkAvailability(); }

  // ------------------------------------------------------------------ helpers
  /** Moves to step i once the steps' `completed` bindings have rendered: a linear stepper refuses to move past a step
   *  it still sees as incomplete, and the move would otherwise be read in the same pass as the binding change. */
  private go(i: number) { afterNextRender(() => this.step.set(i), { injector: this.injector }); }

  private setError(step: number, msg: string) { this.errors.update(e => ({ ...e, [step]: msg })); }

  /** Shows an API error: field messages under their fields (API names mapped to control names), the reason by the button. */
  private fail(step: number, e: unknown, map: Record<string, string>, form: import('@angular/forms').FormGroup | null) {
    this.busy.set(false);
    const p: Problem = toProblem(e);
    if (form && p.errors) {
      const renamed: Problem = { ...p, errors: Object.fromEntries(Object.entries(p.errors).map(([k, v]) => [map[k] ?? k, v])) };
      const shown = showFieldErrors(form, renamed);
      if (Object.keys(shown).length && Object.keys(shown).every(k => form.get(k))) { this.setError(step, ''); return; }
    }
    this.setError(step, p.detail);
  }

  protected readonly shortDate = shortDate;
}
