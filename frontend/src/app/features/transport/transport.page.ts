import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { Observable } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { dayText } from '../../core/dates';
import { toProblem } from '../../core/http';
import { CancelTripResult, CreateTripsResult, MyTrips, TransportDay, TransportRoute, Trip, TripDetail, TripPassenger, Vehicle } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { rupees, seats } from '../activities/activities.api';
import { ReasonData, ReasonDialog } from '../restaurant/order-dialogs';
import { BookTripDialog, RouteDialog, TripEditDialog, TripsDialog, VehicleDialog } from './transport-dialogs';
import { PASSENGER_STATUS, TRIP_STATUS, TransportApi, fareText } from './transport.api';

type View = 'day' | 'mine' | 'setup';

/** "2026-11-03" plus n days. */
function addDays(iso: string, n: number): string {
  const d = new Date(iso + 'T00:00:00Z');
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
}

/**
 * Beach transport (Sprint 21, brief §47). Trips: the day's shuttle runs with the seats left; open one to book a guest
 * (₹500 a seat goes on their bill), cancel a seat, print the manifest, or cancel the trip. My trips: the driver's phone
 * view, today's own trips with the passengers to tick as they board, then "Left" and "Arrived". Managers put trips on and
 * keep the vehicles and routes.
 */
@Component({
  selector: 'app-transport',
  imports: [MatButtonModule, MatButtonToggleModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page trans">
      <div class="head">
        <h1>Beach transport</h1>
        @if (views().length > 1) {
          <mat-button-toggle-group [value]="view()" (change)="view.set($event.value)" aria-label="Show">
            @for (v of views(); track v) { <mat-button-toggle [value]="v">{{ viewName[v] }}</mat-button-toggle> }
          </mat-button-toggle-group>
        }
      </div>

      @if (view() !== 'setup') {
        <div class="bar">
          <button mat-icon-button (click)="go(-1)" aria-label="Previous day"><mat-icon>chevron_left</mat-icon></button>
          <b class="date" data-testid="day">{{ date() ? dayText(date()!) : '' }}@if (date() === today()) { <small> · today</small> }</b>
          <button mat-icon-button (click)="go(1)" aria-label="Next day"><mat-icon>chevron_right</mat-icon></button>
          @if (date() !== today()) { <button mat-button (click)="date.set(today())">Today</button> }
          <span class="grow"></span>
          @if (view() === 'day' && canManage()) { <button mat-stroked-button (click)="putOn()"><mat-icon>event</mat-icon>Put trips on</button> }
        </div>
      }

      @if (view() === 'day') {
        <ul class="trips" data-testid="trips">
          @for (t of day()?.trips ?? []; track t.id) {
            <li [attr.data-trip]="t.routeCode + '-' + t.departTime" [class.off]="t.status === 'CANCELLED'" [class.open]="openId() === t.id">
              <button class="row" type="button" (click)="toggle(t)" [attr.aria-expanded]="openId() === t.id">
                <span class="time">{{ t.departTime }}<small>to {{ t.arriveTime }}</small></span>
                <span class="what"><b>{{ t.routeName }}</b>
                  <small class="mute">{{ t.origin }} → {{ t.destination }}</small>
                  <small class="mute">{{ t.vehicleName }} · {{ t.driverName ? 'driver ' + t.driverName : 'no driver yet' }} · {{ t.price ? rupees(t.price) + ' a seat' : 'free' }}@if (t.note) { · {{ t.note }} }</small>
                  @if (t.status === 'CANCELLED') { <small class="cx">Cancelled: {{ t.cancelReason }}</small> }
                  @else if (t.status !== 'SCHEDULED') { <small class="st">{{ statusText(t) }}</small> }
                </span>
                <span class="seats" [class.full]="t.status === 'SCHEDULED' && t.seatsLeft === 0">
                  <b>{{ t.seatsBooked }}/{{ t.capacity }}</b>
                  <small>{{ t.status === 'CANCELLED' ? 'cancelled' : t.status !== 'SCHEDULED' ? t.onBoard + ' on board' : t.seatsLeft === 0 ? 'full' : t.seatsLeft + ' left' }}</small>
                  <span class="meter"><span [style.width.%]="100 * t.seatsBooked / t.capacity"></span></span>
                </span>
                <mat-icon class="chev" aria-hidden="true">{{ openId() === t.id ? 'expand_less' : 'expand_more' }}</mat-icon>
              </button>
              @if (openId() === t.id) {
                <div class="detail">
                  <div class="actions">
                    @if (t.actions.includes('book')) { <button mat-flat-button (click)="book(t)"><mat-icon>person_add</mat-icon>Book</button> }
                    @if (t.actions.includes('edit')) { <button mat-stroked-button (click)="edit(t)">Vehicle, driver, seats</button> }
                    <button mat-stroked-button (click)="manifest(t)"><mat-icon>picture_as_pdf</mat-icon>Manifest</button>
                    @if (t.actions.includes('depart')) { <button mat-stroked-button (click)="depart(t)"><mat-icon>directions_bus</mat-icon>Left</button> }
                    @if (t.actions.includes('complete')) { <button mat-stroked-button (click)="complete(t)"><mat-icon>where_to_vote</mat-icon>Arrived</button> }
                    @if (t.actions.includes('cancel')) { <button mat-stroked-button color="warn" (click)="cancelTrip(t)">Cancel trip</button> }
                  </div>
                  <ul class="pax" data-testid="passengers">
                    @for (p of detail()?.passengers ?? []; track p.id) {
                      <li [attr.data-passenger]="p.code" [class.off]="p.status === 'CANCELLED'">
                        <div class="main">
                          <span><b>{{ p.guestName }}</b> · {{ seats(p.seats) }}@if (p.room) { · {{ p.room }} }</span>
                          <small class="mute">{{ p.code }} · {{ fareText(p) }}@if (p.note) { · {{ p.note }} }</small>
                          @if (p.status === 'CANCELLED') { <small class="cx">Cancelled: {{ p.cancelReason }}</small> }
                          @else if (p.status !== 'BOOKED') { <small class="st" [class.ok]="p.status === 'BOARDED'">{{ passengerStatus[p.status] }}</small> }
                        </div>
                        <div class="btns">
                          @if (p.actions.includes('board')) { <button mat-stroked-button (click)="board(p, true)">On board</button> }
                          @if (p.actions.includes('unboard')) { <button mat-button (click)="board(p, false)">Not on board</button> }
                          @if (p.actions.includes('cancel')) { <button mat-button (click)="cancel(p)">Cancel</button> }
                        </div>
                      </li>
                    } @empty { <li class="mute">Nobody is booked yet.</li> }
                  </ul>
                </div>
              }
            </li>
          } @empty { <li class="mute empty">No trips on this day.@if (canManage()) { Put some on with the button above. }</li> }
        </ul>
      } @else if (view() === 'mine') {
        <div class="mine" data-testid="my-trips">
          @for (d of mine()?.trips ?? []; track d.trip.id) {
            <section class="card" [attr.data-my-trip]="d.trip.routeCode + '-' + d.trip.departTime" [class.off]="d.trip.status === 'CANCELLED'">
              <header>
                <span class="big">{{ d.trip.departTime }}</span>
                <span class="what"><b>{{ d.trip.routeName }}</b><small>{{ d.trip.origin }} → {{ d.trip.destination }} · {{ d.trip.vehicleName }} {{ d.trip.registration }}</small></span>
                <span class="count" data-testid="on-board"><b>{{ d.trip.onBoard }}/{{ d.trip.seatsBooked }}</b><small>on board</small></span>
              </header>
              @if (d.trip.status === 'CANCELLED') { <p class="cx">Cancelled: {{ d.trip.cancelReason }}</p> }
              @else if (d.trip.status !== 'SCHEDULED') { <p class="st">{{ statusText(d.trip) }}</p> }
              <ul class="tick">
                @for (p of live(d); track p.id) {
                  <li [attr.data-passenger]="p.code" [class.on]="p.status === 'BOARDED'" [class.gone]="p.status === 'NO_SHOW'">
                    <button type="button" class="box" [disabled]="!p.actions.includes('board') && !p.actions.includes('unboard')"
                            (click)="board(p, p.status !== 'BOARDED')" [attr.aria-pressed]="p.status === 'BOARDED'"
                            [attr.aria-label]="(p.status === 'BOARDED' ? 'Untick ' : 'Tick ') + p.guestName">
                      <mat-icon>{{ p.status === 'BOARDED' ? 'check_box' : p.status === 'NO_SHOW' ? 'block' : 'check_box_outline_blank' }}</mat-icon>
                    </button>
                    <span class="who">
                      <span class="line"><b>{{ p.guestName }}</b><span class="n">{{ seats(p.seats) }}</span></span>
                      <small>{{ p.room ?? (p.settlement === 'COMPLIMENTARY' ? 'Complimentary' : 'Visitor') }}@if (p.note) { · {{ p.note }} }@if (p.status === 'NO_SHOW') { · no-show }</small>
                    </span>
                    @if (p.status === 'BOARDED') { <span class="tag ok">On board</span> }
                  </li>
                } @empty { <li class="mute">Nobody is booked on this trip.</li> }
              </ul>
              <div class="actions big-btns">
                @if (d.trip.actions.includes('depart')) { <button mat-flat-button class="go" (click)="depart(d.trip)"><mat-icon>directions_bus</mat-icon>Left</button> }
                @if (d.trip.actions.includes('complete')) { <button mat-flat-button class="go" (click)="complete(d.trip)"><mat-icon>where_to_vote</mat-icon>Arrived</button> }
                <button mat-stroked-button (click)="manifest(d.trip)"><mat-icon>picture_as_pdf</mat-icon>Manifest</button>
              </div>
            </section>
          } @empty { <p class="mute empty">You have no trips on this day.</p> }
        </div>
      } @else {
        <h2>Vehicles</h2>
        <div class="bar"><span class="grow"></span><button mat-flat-button (click)="editVehicle(null)"><mat-icon>add</mat-icon>New vehicle</button></div>
        <ul class="list" data-testid="vehicles">
          @for (v of day()?.vehicles ?? []; track v.id) {
            <li [class.off]="!v.isActive">
              <div class="main"><b>{{ v.name }}</b> <small class="mute">{{ v.code }} · {{ v.registration }}</small>
                <small>{{ seats(v.seats) }}@if (v.note) { · {{ v.note }} }{{ v.isActive ? '' : ' · not in use' }}</small></div>
              <button mat-button (click)="editVehicle(v)">Change</button>
            </li>
          } @empty { <li class="mute">No vehicles yet.</li> }
        </ul>
        <h2>Routes</h2>
        <div class="bar"><span class="grow"></span><button mat-flat-button (click)="editRoute(null)"><mat-icon>add</mat-icon>New route</button></div>
        <ul class="list" data-testid="routes">
          @for (r of day()?.routes ?? []; track r.id) {
            <li [class.off]="!r.isActive">
              <div class="main"><b>{{ r.name }}</b> <small class="mute">{{ r.code }}</small>
                <small>{{ r.origin }} → {{ r.destination }} · {{ r.distanceKm }} km · {{ r.durationMinutes }} min · {{ r.price ? rupees(r.price) + ' a seat' : 'free' }}{{ r.isActive ? '' : ' · not in use' }}</small></div>
              <button mat-button (click)="editRoute(r)">Change</button>
            </li>
          } @empty { <li class="mute">No routes yet.</li> }
        </ul>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>
  `,
  styles: `
    .trans { max-width: 900px; padding-bottom: 60px; }
    .head, .bar { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; } .head { justify-content: space-between; }
    .grow { flex: 1; } .date { min-width: 130px; text-align: center; } .mute { color: #5b6b6e; } .err { color: #b3261e; } .cx { color: #8a1c1c; }
    .st { color: #0e3b43; } .st.ok { color: #1b6e3a; } h2 { margin: 18px 0 0; font-size: 1.1rem; }
    .trips, .list, .pax, .tick { list-style: none; margin: 8px 0 0; padding: 0; }
    .trips > li { background: #fff; border: 1px solid #d9e0de; border-radius: 14px; margin-bottom: 8px; overflow: hidden; }
    .trips > li.empty { padding: 14px; } .trips > li.off { opacity: .7; }
    .row { display: flex; gap: 12px; align-items: center; width: 100%; padding: 10px 14px; background: none; border: 0; text-align: left; cursor: pointer; font: inherit; color: inherit; }
    .row:hover { background: #f4f8f7; }
    .time { display: flex; flex-direction: column; font-weight: 600; font-size: 1.05rem; min-width: 52px; } .time small { font-weight: 400; color: #5b6b6e; font-size: .8rem; }
    .what { flex: 1; display: flex; flex-direction: column; min-width: 0; }
    .seats { display: flex; flex-direction: column; align-items: flex-end; min-width: 72px; } .seats small { color: #5b6b6e; }
    .seats.full b, .seats.full small { color: #8a1c1c; }
    .meter { width: 72px; height: 5px; background: #eef2f1; border-radius: 3px; margin-top: 3px; overflow: hidden; }
    .meter span { display: block; height: 100%; background: #0e7c86; }
    .detail { border-top: 1px solid #eef2f1; padding: 8px 14px 12px; }
    .actions { display: flex; gap: 8px; flex-wrap: wrap; margin: 6px 0; }
    .pax li { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; padding: 8px 0; border-top: 1px solid #f1f4f3; }
    .pax li:first-child { border-top: 0; } .pax li.off { opacity: .65; }
    .main { flex: 1 1 240px; display: flex; flex-direction: column; min-width: 0; }
    .btns { display: flex; gap: 6px; flex-wrap: wrap; }
    .list { background: #fff; border: 1px solid #d9e0de; border-radius: 14px; }
    .list li { display: flex; gap: 8px; align-items: center; padding: 10px 14px; border-top: 1px solid #eef2f1; } .list li:first-child { border-top: 0; }
    .list li.off { opacity: .6; }
    .mine { display: flex; flex-direction: column; gap: 12px; margin-top: 8px; } .empty { padding: 14px 0; }
    .card { background: #fff; border: 1px solid #d9e0de; border-radius: 16px; padding: 12px 14px; } .card.off { opacity: .7; }
    .card header { display: flex; gap: 12px; align-items: center; }
    .big { font-size: 1.6rem; font-weight: 700; color: #0e3b43; }
    .card .what small { color: #5b6b6e; }
    .count { display: flex; flex-direction: column; align-items: flex-end; } .count b { font-size: 1.3rem; } .count small { color: #5b6b6e; }
    .tick li { display: flex; gap: 10px; align-items: center; padding: 4px 0; border-top: 1px solid #f1f4f3; } .tick li:first-child { border-top: 0; }
    .tick li.on .who b { color: #1b6e3a; } .tick li.gone { opacity: .6; }
    .box { width: 48px; height: 48px; display: inline-flex; align-items: center; justify-content: center; border: 0; background: none; cursor: pointer;
           color: #0e3b43; border-radius: 10px; flex: none; }
    .box:disabled { cursor: default; color: #9aa7a9; } .box:not(:disabled):hover { background: #eef5f4; }
    .box mat-icon { font-size: 32px; width: 32px; height: 32px; }
    .tick li.on .box { color: #1b6e3a; }
    .who { flex: 1; display: flex; flex-direction: column; min-width: 0; } .who small { color: #5b6b6e; }
    .who .line { display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap; }
    .who .n { font-size: .85rem; color: #0e3b43; background: #eef5f4; border-radius: 10px; padding: 1px 8px; }
    .tag { font-size: .8rem; border-radius: 10px; padding: 2px 8px; flex: none; } .tag.ok { color: #1b6e3a; background: #e3f0e6; }
    .big-btns { margin-top: 10px; } .big-btns button { min-height: 48px; border-radius: 24px; }
    .chev { color: #5b6b6e; flex: none; }
  `,
})
export class TransportPage {
  private api = inject(TransportApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  dayText = dayText; rupees = rupees; seats = seats; fareText = fareText; passengerStatus = PASSENGER_STATUS;
  viewName: Record<View, string> = { day: 'Trips', mine: 'My trips', setup: 'Vehicles and routes' };

  private pid = computed(() => this.ctx.current()?.id ?? '');
  canManage = computed(() => this.auth.can('transport.manage', this.pid()));
  private canOperate = computed(() => this.auth.can('transport.trip_operate', this.pid()));
  private canBook = computed(() => this.auth.can('transport_booking.create', this.pid()));
  views = computed<View[]>(() => [
    'day' as View,
    ...(this.canOperate() ? ['mine' as View] : []),
    ...(this.canManage() ? ['setup' as View] : []),
  ]);
  /** A driver (runs trips but doesn't book or manage) starts on their own trips. */
  view = signal<View>('day');
  date = signal<string | null>(null);
  day = signal<TransportDay | null>(null);
  mine = signal<MyTrips | null>(null);
  today = computed(() => this.day()?.today ?? this.mine()?.today ?? null);
  openId = signal<string | null>(null);
  detail = signal<TripDetail | null>(null);
  error = signal('');

  constructor() {
    effect(() => {
      const pid = this.pid();
      if (pid) untracked(() => { if (this.canOperate() && !this.canBook() && !this.canManage()) this.view.set('mine'); });
    });
    effect(() => {
      const d = this.date();
      const v = this.view();
      if (this.pid()) untracked(() => this.load(d, v));
    });
  }

  load(d: string | null = this.date(), v: View = this.view()) {
    this.error.set('');
    if (v === 'mine') {
      this.api.mine(this.pid(), d).subscribe({
        next: x => { this.mine.set(x); if (!this.date()) this.date.set(x.date); },
        error: e => this.error.set(toProblem(e).detail),
      });
      return;
    }
    this.api.day(this.pid(), d).subscribe({
      next: x => {
        this.day.set(x);
        if (!this.date()) this.date.set(x.date);
        if (this.openId() && !x.trips.some(t => t.id === this.openId())) { this.openId.set(null); this.detail.set(null); }
        if (this.openId()) this.loadDetail(this.openId()!);
      },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  go(n: number) {
    const d = this.date();
    if (!d) return;
    this.openId.set(null);
    this.detail.set(null);
    this.date.set(addDays(d, n));
  }

  toggle(t: Trip) {
    if (this.openId() === t.id) { this.openId.set(null); this.detail.set(null); return; }
    this.openId.set(t.id);
    this.detail.set(null);
    this.loadDetail(t.id);
  }

  private loadDetail(id: string) {
    this.api.trip(this.pid(), id).subscribe({ next: x => { if (this.openId() === id) this.detail.set(x); }, error: e => this.error.set(toProblem(e).detail) });
  }

  live(d: TripDetail) { return d.passengers.filter(p => p.status !== 'CANCELLED'); }

  statusText(t: Trip) {
    const at = (iso: string | null) => iso ? new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', timeZone: 'Asia/Kolkata' }) : '';
    return t.status === 'DEPARTED' ? `${TRIP_STATUS[t.status]} since ${at(t.departedAt)}` : t.status === 'COMPLETED' ? `Arrived at ${at(t.completedAt)}` : TRIP_STATUS[t.status];
  }

  book(t: Trip) {
    this.dialog.open(BookTripDialog, { data: { propertyId: this.pid(), trip: t }, maxWidth: '100vw' }).afterClosed().subscribe((p?: TripPassenger) => {
      if (!p) return;
      const fare = fareText(p);
      this.snack.open(`${p.code}: ${p.guestName} booked on the ${p.departTime} ${p.routeName} (${seats(p.seats)}). ${fare[0].toUpperCase()}${fare.slice(1)}.`,
        'OK', { duration: 6000 });
      this.load();
    });
  }

  cancel(p: TripPassenger) {
    const data: ReasonData<TripPassenger> = {
      title: `Cancel ${p.code}?`, button: 'Cancel the seats', placeholder: 'Guest changed plans',
      intro: p.settlement === 'ROOM_CHARGE' ? `${rupees(p.amount)} comes off ${p.reservationReference}'s bill.` : `${p.guestName}'s seats are freed.`,
      run: reason => this.api.cancel(this.pid(), p, reason),
    };
    this.dialog.open(ReasonDialog, { data, maxWidth: '100vw' }).afterClosed().subscribe(x => {
      if (!x) return;
      this.snack.open(`${p.code} cancelled.`, 'OK', { duration: 4000 });
      this.load();
    });
  }

  cancelTrip(t: Trip) {
    const data: ReasonData<CancelTripResult> = {
      title: `Cancel the ${t.departTime} ${t.routeName}?`, button: 'Cancel the trip', placeholder: 'Van broke down',
      intro: t.seatsBooked ? `${seats(t.seatsBooked)} booked: every charge comes off the guests' bills. Tell them, and book them on another trip.` : 'Nobody is booked on it.',
      run: reason => this.api.cancelTrip(this.pid(), t, reason),
    };
    this.dialog.open(ReasonDialog, { data, maxWidth: '100vw' }).afterClosed().subscribe((r?: CancelTripResult) => {
      if (!r) return;
      this.snack.open(`The ${r.trip.departTime} ${r.trip.routeName} is cancelled.${r.refunded ? ` ${r.refunded} refunded (${rupees(r.amountRefunded)} off the bills).` : ''}`,
        'OK', { duration: 7000 });
      this.load();
    });
  }

  board(p: TripPassenger, on: boolean) {
    this.run(this.api.board(this.pid(), p, on), x => `${x.guestName} ${on ? 'is on board' : 'is not on board'}.`);
  }

  depart(t: Trip) {
    this.run(this.api.depart(this.pid(), t), d => {
      const gone = d.passengers.filter(p => p.status === 'NO_SHOW');
      return `The ${t.departTime} ${t.routeName} left with ${d.trip.onBoard} on board.${gone.length ? ' No-show: ' + gone.map(p => p.guestName).join(', ') + '.' : ''}`;
    });
  }

  complete(t: Trip) { this.run(this.api.complete(this.pid(), t), () => `The ${t.departTime} ${t.routeName} has arrived.`); }

  manifest(t: Trip) {
    this.error.set('');
    this.api.manifest(this.pid(), t).subscribe({ error: e => this.error.set(toProblem(e).detail) });
  }

  private run<T>(call: Observable<T>, done: (x: T) => string) {
    this.error.set('');
    call.subscribe({
      next: x => { this.snack.open(done(x), 'OK', { duration: 4000 }); this.load(); },
      error: e => { this.error.set(toProblem(e).detail); this.load(); },
    });
  }

  edit(t: Trip) {
    const d = this.day();
    if (!d) return;
    this.dialog.open(TripEditDialog, { data: { propertyId: this.pid(), trip: t, vehicles: d.vehicles.filter(v => v.isActive || v.id === t.vehicleId), drivers: d.drivers },
      maxWidth: '100vw' }).afterClosed().subscribe((x?: Trip) => {
      if (!x) return;
      this.snack.open(`The ${x.departTime} ${x.routeName}: ${x.vehicleName}, ${x.driverName ?? 'no driver yet'}, ${seats(x.capacity)}.`, 'OK', { duration: 5000 });
      this.load();
    });
  }

  putOn() {
    const d = this.day();
    if (!d) return;
    this.dialog.open(TripsDialog, {
      data: { propertyId: this.pid(), routes: d.routes.filter(r => r.isActive), vehicles: d.vehicles.filter(v => v.isActive), drivers: d.drivers, from: this.date() ?? d.today },
      maxWidth: '100vw',
    }).afterClosed().subscribe((r?: CreateTripsResult) => {
      if (!r) return;
      this.snack.open(`${r.created} ${r.created === 1 ? 'trip' : 'trips'} put on.${r.skipped.length ? ' Skipped: ' + r.skipped.join('; ') + '.' : ''}`, 'OK', { duration: 8000 });
      this.load();
    });
  }

  editVehicle(v: Vehicle | null) {
    this.dialog.open(VehicleDialog, { data: { propertyId: this.pid(), vehicle: v }, maxWidth: '100vw' }).afterClosed().subscribe((x?: Vehicle) => {
      if (!x) return;
      this.snack.open(`${x.name} saved.`, 'OK', { duration: 4000 });
      this.load();
    });
  }

  editRoute(r: TransportRoute | null) {
    this.dialog.open(RouteDialog, { data: { propertyId: this.pid(), route: r }, maxWidth: '100vw' }).afterClosed().subscribe((x?: TransportRoute) => {
      if (!x) return;
      this.snack.open(`${x.name} saved.`, 'OK', { duration: 4000 });
      this.load();
    });
  }
}
