import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormBuilder, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, debounceTime, of, switchMap } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { fieldErrors, showFieldErrors, toProblem } from '../../core/http';
import { Availability, AvailabilityType, GuestListItem, PossibleDuplicate } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { GuestsApi, formatPhone } from '../guests/guests.api';
import { addDays } from '../rooms/rooms.api';
import { ReservationsApi, guestsText, newKey } from '../reservations/reservations.api';
import { dateError, newGuestForm } from '../reservations/reservations.forms';
import { dayLabel, rupees } from '../settings/rates/rates.api';
import { FrontDeskApi } from './front-desk.api';

interface ChosenGuest { id: string; fullName: string; phone: string | null; }

/**
 * A guest at the desk with no booking: stay (from today) → guest → check in. The last press creates the guest (if new),
 * the booking from today and the check-in in one call; if any part fails nothing is saved. Every error is shown under
 * its field, or by the button with its reason.
 */
@Component({
  selector: 'app-walk-in',
  imports: [ReactiveFormsModule, FormsModule, RouterLink, MatButtonModule, MatDatepickerModule, MatFormFieldModule, MatIconModule,
    MatInputModule, MatSelectModule, MatSnackBarModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div><a routerLink="/front-desk/arrivals" class="back">Front desk</a><h1>Walk-in</h1></div>
      </div>

      <!-- 1. Stay -->
      <section class="panel step">
        <h2><span class="n">1</span> Stay from today, {{ day(today) }}</h2>
        <form [formGroup]="stay" (ngSubmit)="checkAvailability()" class="dates">
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Leaving on</mat-label>
            <input matInput [matDatepicker]="dpOut" formControlName="checkOut" [min]="tomorrow" />
            <mat-datepicker-toggle matIconSuffix [for]="dpOut" />
            <mat-datepicker #dpOut />
            @if (nights() > 0) { <mat-hint>{{ nights() }} night{{ nights() === 1 ? '' : 's' }}</mat-hint> }
            <mat-error>{{ outErr() }}</mat-error>
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
          <div class="actions">
            <button mat-flat-button type="submit" [disabled]="busy()">Find a room</button>
            @if (errors()['stay']) { <span class="err" role="alert">{{ errors()['stay'] }}</span> }
          </div>
        </form>

        @if (availability(); as a) {
          <div class="types">
            @for (t of a.types; track t.accommodationTypeId) {
              <div class="type" [class.on]="chosen()?.accommodationTypeId === t.accommodationTypeId" [class.full]="!t.fits">
                <div class="what">
                  <b>{{ t.name }}</b>
                  <small class="mute">{{ t.free }} of {{ t.total }} {{ t.inventoryMode === 'BED' ? 'beds' : 'rooms' }} free</small>
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
                      <mat-option [value]="null">Any ready room</mat-option>
                      @for (r of t.freeRooms; track r.id) { <mat-option [value]="r.id">{{ r.name }}</mat-option> }
                    </mat-select>
                  </mat-form-field>
                } @else { <span></span> }
                <button mat-flat-button (click)="choose(t)" [disabled]="!t.fits || t.price === null" [attr.aria-label]="'Choose ' + t.name">Choose</button>
              </div>
            } @empty { <p class="mute">This property has no active room types. Add them in Room types.</p> }
          </div>
        }
      </section>

      <!-- 2. Guest -->
      <section class="panel step" [class.off]="!chosen()">
        <h2><span class="n">2</span> Guest</h2>
        @if (chosen()) {
          @if (guest(); as g) {
            <p class="picked"><mat-icon>person</mat-icon><b>{{ g.fullName }}</b>@if (g.phone) { · <span class="num mute">{{ phone(g.phone) }}</span> }
              <button mat-button (click)="clearGuest()">Change</button></p>
          } @else if (newGuestReady()) {
            <p class="picked"><mat-icon>person_add</mat-icon><b>New: {{ newGuestName() }}</b>
              <button mat-button (click)="newGuestReady.set(false)">Change</button></p>
          } @else {
            <div class="guest-step">
              <div>
                <h3>Returning guest</h3>
                <mat-form-field appearance="outline" class="wide">
                  <mat-icon matPrefix>search</mat-icon>
                  <mat-label>Name, phone or email</mat-label>
                  <input matInput [ngModel]="guestQ()" (ngModelChange)="searchGuests($event)" autocomplete="off" />
                </mat-form-field>
                @for (g of found(); track g.id) {
                  <button mat-stroked-button class="found" (click)="useGuest(g)">
                    <b>{{ g.fullName }}</b>@if (g.phone) { · <span class="mute num">{{ phone(g.phone) }}</span> }
                  </button>
                } @empty { @if (guestQ().trim().length >= 2) { <p class="mute">No guest matches “{{ guestQ() }}”. Add them as new.</p> } }
              </div>
              <form [formGroup]="newGuest" (ngSubmit)="useNewGuest()" class="form-grid">
                <h3 class="full">New guest</h3>
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
                <div class="actions full"><button mat-stroked-button type="submit">Use this new guest</button></div>
              </form>
            </div>
          }
        }
      </section>

      <!-- 3. Check in -->
      <section class="panel step" [class.off]="!ready()">
        <h2><span class="n">3</span> Check in</h2>
        @if (ready()) {
          <dl class="summary">
            <dt>Guest</dt><dd><b>{{ guest()?.fullName ?? newGuestName() }}</b>@if (!guest()) { <span class="mute"> (new profile)</span> }</dd>
            <dt>Stay</dt><dd>{{ day(today) }} – {{ day(availability()!.to) }} · {{ availability()!.nights }} night{{ availability()!.nights === 1 ? '' : 's' }} · {{ guestsLine() }}</dd>
            <dt>Room</dt><dd>{{ chosen()!.name }} · {{ roomLine() }}</dd>
            <dt>Price</dt><dd><b class="num">{{ rupees(chosen()!.price!) }}</b> before tax, {{ chosen()!.ratePlanName }}</dd>
          </dl>
          <form [formGroup]="extra" class="form-grid">
            <mat-form-field appearance="outline">
              <mat-label>Special requests</mat-label>
              <input matInput formControlName="specialRequests" maxlength="2000" />
              <mat-error>{{ extra.controls.specialRequests.errors?.['server'] }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Note for the team (optional)</mat-label>
              <input matInput formControlName="note" maxlength="2000" />
              <mat-error>{{ extra.controls.note.errors?.['server'] }}</mat-error>
            </mat-form-field>
            @if (notReady()) {
              <mat-form-field appearance="outline" class="full">
                <mat-label>Why check in anyway?</mat-label>
                <input matInput formControlName="overrideReason" maxlength="500" placeholder="e.g. guest agreed to wait in the room while it's finished" />
                <mat-error>{{ extra.controls.overrideReason.errors?.['server'] ?? 'Say why the guest is going into a room that isn’t ready.' }}</mat-error>
              </mat-form-field>
            }
          </form>
          @if (duplicates().length) {
            <div class="dups" role="alert">
              <p>This may be someone who has stayed before:</p>
              @for (d of duplicates(); track d.id) {
                <button mat-stroked-button type="button" (click)="useGuest(d)"><b>{{ d.fullName }}</b> · {{ phone(d.phone) || d.email }} · {{ d.reasons.join(', ') }}</button>
              }
              <button mat-button type="button" (click)="submit(true)">No, add a new profile and check in</button>
            </div>
          }
          <div class="actions">
            <button mat-flat-button (click)="submit(false)" [disabled]="busy()">{{ notReady() ? 'Check in anyway' : 'Check in' }}</button>
            @if (errors()['submit']) { <span class="err" role="alert">{{ errors()['submit'] }}</span> }
          </div>
        }
      </section>
    </div>
  `,
  styles: `
    .back { color: #5b6b6e; text-decoration: none; font-size: 0.9rem; } .back::before { content: '‹ '; }
    .step { margin-bottom: 16px; }
    .step.off { opacity: 0.55; }
    h2 { font-size: 1.05rem; margin: 0 0 12px; display: flex; align-items: center; gap: 10px; }
    h3 { font-size: 0.95rem; margin: 4px 0 8px; }
    .n { display: inline-grid; place-items: center; width: 26px; height: 26px; border-radius: 50%; background: #0e3b43; color: #fff; font-size: 0.85rem; }
    .dates { display: grid; grid-template-columns: 1fr 120px 120px auto; gap: 4px 12px; align-items: start; }
    .actions { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin: 4px 0; }
    .err { color: #b3261e; }
    .warn { color: #8a5a00; display: block; }
    .mute { color: #5b6b6e; }
    .types { display: flex; flex-direction: column; gap: 8px; margin-top: 12px; }
    .type { display: grid; grid-template-columns: 1fr 150px 200px auto; gap: 12px; align-items: center; padding: 12px; border: 1px solid #d9e0de; border-radius: 10px; }
    .type.on { border-color: #0e3b43; background: #eef5f3; }
    .type.full { background: #f8faf9; }
    .type .what small, .type .price small { display: block; }
    .type .price { text-align: right; }
    .pick { width: 100%; }
    .guest-step { display: grid; grid-template-columns: 1fr 1.4fr; gap: 24px; }
    .wide { width: 100%; }
    .found { display: block; width: 100%; text-align: left; margin-bottom: 6px; }
    .picked { display: flex; align-items: center; gap: 8px; margin: 0; }
    .dups { background: #fbefd8; border-radius: 8px; padding: 8px 12px; margin-bottom: 8px; }
    .dups p { margin: 0 0 6px; }
    .dups button { display: block; margin-bottom: 4px; text-align: left; }
    .summary { display: grid; grid-template-columns: 120px 1fr; gap: 6px 16px; margin: 0 0 12px; }
    .summary dt { color: #5b6b6e; }
    .summary dd { margin: 0; }
    @media (max-width: 760px) {
      .dates { grid-template-columns: 1fr 1fr; }
      .type { grid-template-columns: 1fr auto; }
      .type .pick { grid-column: 1 / -1; }
      .type .price { text-align: left; }
      .guest-step { grid-template-columns: 1fr; }
      .summary { grid-template-columns: 1fr; gap: 0; }
      .summary dd { margin-bottom: 8px; }
    }
  `,
})
export class WalkInPage {
  private fb = inject(FormBuilder);
  private reservations = inject(ReservationsApi);
  private guestsApi = inject(GuestsApi);
  private frontDesk = inject(FrontDeskApi);
  private props = inject(PropertyContext);
  private router = inject(Router);
  private snack = inject(MatSnackBar);

  private pid = this.props.current()!.id;
  today = this.props.current()!.localDate;
  tomorrow = addDays(this.today, 1);

  stay = this.fb.nonNullable.group({
    checkOut: [this.tomorrow, Validators.required],
    adults: [1, [Validators.required, Validators.min(1), Validators.max(50)]],
    children: [0, [Validators.min(0), Validators.max(50)]],
  });
  newGuest = newGuestForm(this.fb);
  extra = this.fb.nonNullable.group({
    specialRequests: ['', Validators.maxLength(2000)],
    note: ['', Validators.maxLength(2000)],
    overrideReason: ['', Validators.maxLength(500)],
  });

  busy = signal(false);
  errors = signal<Record<string, string>>({});
  availability = signal<Availability | null>(null);
  chosen = signal<AvailabilityType | null>(null);
  roomPick = signal<Record<string, string | null>>({});
  guest = signal<ChosenGuest | null>(null);
  newGuestReady = signal(false);
  guestQ = signal('');
  found = signal<GuestListItem[]>([]);
  duplicates = signal<PossibleDuplicate[]>([]);
  /** The room chosen isn't ready (housekeeping): the next press checks in anyway, with a reason. */
  notReady = signal(false);

  private checkOut = signal(this.tomorrow);
  nights = computed(() => Math.max(0, Math.round((Date.parse(this.checkOut()) - Date.parse(this.today)) / 86_400_000)));
  ready = computed(() => !!this.chosen() && (!!this.guest() || this.newGuestReady()));
  guestsLine = computed(() => { const a = this.availability(); return a ? guestsText(a.adults, a.children) : ''; });
  roomLine = computed(() => {
    const t = this.chosen(), a = this.availability();
    if (!t || !a) return '';
    if (t.inventoryMode === 'BED') { const n = a.adults + a.children; return `${n} bed${n === 1 ? '' : 's'}`; }
    const id = this.roomPick()[t.accommodationTypeId];
    return id ? t.freeRooms.find(r => r.id === id)?.name ?? '' : 'any ready room';
  });

  rupees = rupees;
  phone = formatPhone;
  day = dayLabel;

  /** One key per walk-in as entered, so pressing Check in twice (or retrying after a network drop) checks in once. */
  private key = newKey();
  private keyFor = '';
  private guestSearch$ = new Subject<string>();

  constructor() {
    this.stay.controls.checkOut.valueChanges.pipe(takeUntilDestroyed()).subscribe(v => this.checkOut.set(v ?? ''));
    this.guestSearch$.pipe(
      debounceTime(250),
      switchMap(q => q.trim().length < 2 ? of(null) : this.guestsApi.list(q, { pageSize: 5 })),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe({ next: r => this.found.set(r?.items ?? []), error: () => this.found.set([]) });
  }

  outErr() {
    const c = this.stay.controls.checkOut;
    if (c.errors?.['matDatepickerMin'] || (c.value && c.value <= this.today)) return `Leaving must be after today (${dayLabel(this.today)}).`;
    return dateError(c, this.today);
  }

  newGuestName() { const v = this.newGuest.getRawValue(); return `${v.firstName.trim()} ${v.lastName.trim()}`.trim(); }

  // ------------------------------------------------------------------ 1. stay
  checkAvailability() {
    if (this.stay.invalid) { this.stay.markAllAsTouched(); return; }
    const v = this.stay.getRawValue();
    if (v.checkOut <= this.today) { this.stay.controls.checkOut.setErrors({ server: this.outErr() }); this.stay.markAllAsTouched(); return; }
    this.busy.set(true); this.setError('stay', '');
    this.reservations.availability(this.pid, { from: this.today, to: v.checkOut, adults: v.adults, children: v.children ?? 0 }).subscribe({
      next: a => { this.busy.set(false); this.availability.set(a); this.chosen.set(null); this.notReady.set(false); },
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const shown = showFieldErrors(this.stay, { ...p, errors: rename(p.errors, { To: 'checkOut', Adults: 'adults', Children: 'children' }) });
        this.setError('stay', Object.keys(shown).length ? '' : p.detail);
      },
    });
  }

  pickRoom(typeId: string, roomId: string | null) { this.roomPick.update(p => ({ ...p, [typeId]: roomId })); this.notReady.set(false); }
  choose(t: AvailabilityType) { this.chosen.set(t); this.notReady.set(false); this.setError('submit', ''); }

  // ------------------------------------------------------------------ 2. guest
  searchGuests(q: string) { this.guestQ.set(q); this.guestSearch$.next(q); }
  useGuest(g: { id: string; fullName: string; phone: string | null }) {
    this.guest.set({ id: g.id, fullName: g.fullName, phone: g.phone });
    this.newGuestReady.set(false);
    this.duplicates.set([]);
  }
  useNewGuest() {
    if (this.newGuest.invalid) { this.newGuest.markAllAsTouched(); return; }
    this.newGuestReady.set(true);
  }
  clearGuest() { this.guest.set(null); this.duplicates.set([]); }

  // ------------------------------------------------------------------ 3. check in
  submit(ignoreDuplicates: boolean) {
    const a = this.availability()!, t = this.chosen()!, g = this.guest(), x = this.extra.getRawValue();
    if (this.notReady() && !x.overrideReason.trim()) {
      this.extra.controls.overrideReason.setErrors({ required: true }); this.extra.controls.overrideReason.markAsTouched(); return;
    }
    const n = this.newGuest.getRawValue();
    const body = {
      guestId: g?.id ?? null,
      newGuest: g ? null : {
        firstName: n.firstName.trim(), lastName: n.lastName.trim() || null, phone: n.phone.trim() || null, email: n.email.trim() || null,
        dateOfBirth: null, nationality: null, companyName: null, gstin: null, notes: null, isVip: false, address: null, preferences: null,
        ignoreDuplicates,
      },
      checkOut: a.to,
      rooms: [{ accommodationTypeId: t.accommodationTypeId, unitId: this.roomPick()[t.accommodationTypeId] ?? null, adults: a.adults, children: a.children }],
      ratePlanId: t.ratePlanId,
      specialRequests: x.specialRequests.trim() || null, note: x.note.trim() || null,
      overrideHousekeeping: this.notReady(), overrideReason: this.notReady() ? x.overrideReason.trim() : null,
    };
    const json = JSON.stringify(body);
    if (json !== this.keyFor) { this.key = newKey(); this.keyFor = json; }

    this.busy.set(true); this.setError('submit', ''); this.duplicates.set([]);
    this.frontDesk.walkIn(this.pid, body, this.key).subscribe({
      next: r => {
        const room = r.units.filter(u => u.current).map(u => u.bedCode ? `${u.unitName} bed ${u.bedCode}` : u.unitName).join(', ');
        this.snack.open(`Checked in: ${r.reference} · ${room}`, 'OK', { duration: 5000 });
        this.router.navigateByUrl(`/reservations/${r.id}`);
      },
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        if (p.code === 'POSSIBLE_DUPLICATE') { this.duplicates.set(((e as { error?: { details?: unknown } }).error?.details ?? []) as PossibleDuplicate[]); return; }
        if (p.code === 'HOUSEKEEPING_NOT_READY') { this.notReady.set(true); this.setError('submit', p.detail); return; }
        const errs = fieldErrors(p);
        const guestErrs = Object.fromEntries(Object.entries(errs).filter(([k]) => k.startsWith('newGuest.')).map(([k, v]) => [k.slice('newGuest.'.length), v]));
        if (Object.keys(guestErrs).length) {
          this.newGuestReady.set(false);
          showFieldErrors(this.newGuest, { ...p, errors: Object.fromEntries(Object.entries(guestErrs).map(([k, v]) => [k, [v]])) });
          this.setError('submit', 'Fix the guest details above.');
          return;
        }
        const shown = showFieldErrors(this.extra, p);
        this.setError('submit', Object.keys(shown).length && Object.keys(shown).every(k => this.extra.get(k)) ? '' : p.detail);
      },
    });
  }

  private setError(k: string, msg: string) { this.errors.update(e => ({ ...e, [k]: msg })); }
}

function rename(errors: Record<string, string[]> | undefined, map: Record<string, string>) {
  return errors ? Object.fromEntries(Object.entries(errors).map(([k, v]) => [map[k] ?? k, v])) : errors;
}
