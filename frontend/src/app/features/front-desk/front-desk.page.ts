import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable, Subject, switchMap, tap } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { FrontDeskBoardRoom, FrontDeskDashboard, FrontDeskList, FrontDeskStay, Housekeeping, Reservation } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { formatPhone } from '../guests/guests.api';
import { guestsText, statusClass, statusLabel } from '../reservations/reservations.api';
import { addDays, shortDate } from '../rooms/rooms.api';
import { dayLabel, rupeesExact } from '../settings/rates/rates.api';
import { DeskActions } from './desk-actions';
import { FrontDeskApi, deskStay, housekeepingLabel, occupancyLabel, placeName, stayAction } from './front-desk.api';

export type DeskTab = 'arrivals' | 'departures' | 'in-house' | 'rooms';

/**
 * Reception (brief §54): today's numbers at the top, then arrivals, departures, guests in house and every room's state.
 * "Today" is the property's business date from the API, never this computer's date. Check-in is one click when the
 * room is ready; check-out asks only what it must (a reason when leaving early). Every error says why, next to the
 * row or in the dialog that caused it.
 */
@Component({
  selector: 'app-front-desk',
  imports: [RouterLink, RouterLinkActive, MatButtonModule, MatIconModule, MatMenuModule, MatSnackBarModule, MatTooltipModule],
  template: `
    <div class="page wide">
      <div class="page-head">
        <div>
          <h1>Front desk</h1>
          <p>{{ todayText() }} at {{ props.current().name }}</p>
        </div>
        <div class="head-actions">
          @if (canWalkIn()) { <a mat-flat-button routerLink="/front-desk/walk-in"><mat-icon>person_add</mat-icon>Walk-in</a> }
        </div>
      </div>

      @if (dash(); as d) {
        <div class="tiles">
          <a class="tile" routerLink="/front-desk/arrivals"><b class="num">{{ d.arrivalsCheckedIn }} / {{ d.arrivalsExpected }}</b><span>Arrivals checked in</span></a>
          <a class="tile" routerLink="/front-desk/departures"><b class="num">{{ d.departuresCheckedOut }} / {{ d.departuresExpected }}</b>
            <span>Departures checked out@if (d.overdue) { · <em>{{ d.overdue }} overdue</em> }</span></a>
          <a class="tile" routerLink="/front-desk/in-house"><b class="num">{{ d.inHouseStays }}</b><span>In house · {{ d.inHouseGuests }} guest{{ d.inHouseGuests === 1 ? '' : 's' }}</span></a>
          <a class="tile" routerLink="/front-desk/rooms"><b class="num">{{ d.roomsReady }}</b>
            <span>Ready to sell · {{ d.roomsNotReady }} not ready · {{ d.roomsOccupied }} occupied</span></a>
        </div>
      }

      <nav class="tabs" aria-label="Front desk lists">
        <a routerLink="/front-desk/arrivals" routerLinkActive="on">Arrivals</a>
        <a routerLink="/front-desk/departures" routerLinkActive="on">Departures</a>
        <a routerLink="/front-desk/in-house" routerLinkActive="on">In house</a>
        <a routerLink="/front-desk/rooms" routerLinkActive="on">Rooms</a>
      </nav>

      @if (tab() === 'arrivals' || tab() === 'departures') {
        <div class="day">
          <button mat-icon-button (click)="shift(-1)" aria-label="Previous day"><mat-icon>chevron_left</mat-icon></button>
          <span class="when">{{ dayText() }}</span>
          <button mat-icon-button (click)="shift(1)" aria-label="Next day"><mat-icon>chevron_right</mat-icon></button>
          @if (date() && date() !== today()) { <button mat-button (click)="date.set('')">Today</button> }
        </div>
      }

      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

      @if (tab() === 'rooms') {
        <div class="board">
          @for (r of dash()?.rooms ?? []; track r.unitId + (r.bedId ?? '')) {
            <div class="room" [class]="'room ' + r.occupancy + ' hk-' + r.housekeeping" [attr.data-room]="place(r)">
              <div class="top"><b class="code">{{ r.bedCode ? r.unitCode + ' · ' + r.bedCode : r.unitCode }}</b><span class="type">{{ r.typeName }}</span></div>
              <div class="state">{{ occupancy(r.occupancy) }}</div>
              <span [class]="'chip hk ' + r.housekeeping">{{ hk(r.housekeeping) }}</span>
              @if (r.reference) {
                <a class="who" [routerLink]="['/reservations', r.reservationId]">{{ r.guestName }} · {{ r.reference }}</a>
                @if (r.until) { <small class="mute">until {{ shortDate(r.until) }}</small> }
              }
              @if (canHousekeeping() && r.occupancy !== 'INACTIVE') {
                <div class="hk-actions">
                  @if (r.housekeeping !== 'READY') { <button mat-stroked-button (click)="setHk(r, 'READY')" [disabled]="busy()">Mark ready</button> }
                  @else { <button mat-button (click)="setHk(r, 'DIRTY')" [disabled]="busy()">Mark dirty</button> }
                </div>
              }
            </div>
          } @empty { @if (dash()) { <p class="mute">No rooms set up yet.</p> } }
        </div>
      } @else {
        <div class="panel">
          <table class="data">
            <thead><tr><th>Guest</th><th>Stay</th><th>Room</th><th class="hide-sm">Status</th><th class="right">&nbsp;</th></tr></thead>
            <tbody>
              @for (s of list()?.items ?? []; track s.reservationId) {
                <tr [attr.data-ref]="s.reference">
                  <td>
                    <a [routerLink]="['/reservations', s.reservationId]"><b>{{ s.guestName }}</b></a>
                    @if (s.isVip) { <span class="chip vip">VIP</span> }
                    <br /><small class="mute num">{{ s.reference }}@if (s.guestPhone) { · {{ phone(s.guestPhone) }} } · {{ guestsText(s.adults, s.children) }}</small>
                    @if (s.specialRequests) { <br /><small class="req">{{ s.specialRequests }}</small> }
                  </td>
                  <td class="num">{{ shortDate(s.checkIn) }} – {{ shortDate(s.checkOut) }}<br />
                    <small class="mute">{{ s.nights }} night{{ s.nights === 1 ? '' : 's' }}</small>
                    @if (s.late) { <br /><small class="warn">Due {{ shortDate(s.checkIn) }}, not arrived</small> }
                    @if (s.overdue) { <br /><small class="bad">Overdue: was due out {{ shortDate(s.checkOut) }}</small> }
                  </td>
                  <td>
                    @for (room of s.rooms; track room.reservationUnitId) {
                      <div class="rm">{{ place(room) }} <span [class]="'chip hk ' + room.housekeeping">{{ hk(room.housekeeping) }}</span></div>
                    }
                    @if (s.checkInProblem && action(s) === 'check-in') { <small class="warn" role="note">{{ s.checkInProblem }}</small> }
                    @if (s.balance > 0) { <small class="bad">{{ rupees(s.balance) }} owed</small> }
                  </td>
                  <td class="hide-sm"><span [class]="'chip ' + statusClass(s.status)">{{ statusLabel(s.status) }}</span>
                    @if (s.checkedInAt && s.status === 'CHECKED_IN') { <br /><small class="mute">in {{ time(s.checkedInAt) }}</small> }
                    @if (s.checkedOutAt) { <br /><small class="mute">out {{ time(s.checkedOutAt) }}</small> }
                  </td>
                  <td class="right actions">
                    @switch (action(s)) {
                      @case ('check-in') {
                        @if (canCheckIn()) {
                          <button mat-flat-button (click)="checkIn(s)" [disabled]="busy()" [attr.aria-label]="'Check in ' + s.guestName">Check in</button>
                          <button mat-button (click)="assign(s)" [disabled]="busy()">Room</button>
                        }
                      }
                      @case ('check-out') {
                        @if (canCheckOut() && (tab() !== 'in-house' || s.overdue || s.checkOut <= today())) {
                          <button mat-flat-button (click)="checkOut(s)" [disabled]="busy()" [attr.aria-label]="'Check out ' + s.guestName">Check out</button>
                        }
                        @if (canCheckIn() && tab() === 'in-house') { <button mat-button (click)="assign(s)" [disabled]="busy()">Move</button> }
                        @if (canCheckOut() && tab() === 'in-house' && !s.overdue && s.checkOut > today()) {
                          <button mat-button (click)="checkOut(s)" [disabled]="busy()">Check out</button>
                        }
                      }
                      @default { <span class="mute done">{{ s.status === 'CHECKED_OUT' ? 'Left' : s.status === 'CHECKED_IN' ? 'In' : '' }}</span> }
                    }
                    @if (rowError()[s.reservationId]; as m) { <p class="err row-err" role="alert">{{ m }}</p> }
                  </td>
                </tr>
              } @empty {
                <tr><td colspan="5" class="empty">
                  @if (loading()) { Loading… }
                  @else if (tab() === 'arrivals') { No arrivals {{ date() && date() !== today() ? 'on ' + dayText() : 'today' }}. }
                  @else if (tab() === 'departures') { No departures {{ date() && date() !== today() ? 'on ' + dayText() : 'today' }}. }
                  @else { Nobody is checked in. }
                </td></tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
  styles: `
    .wide { max-width: 1200px; }
    .head-actions { display: flex; gap: 8px; flex-wrap: wrap; }
    .tiles { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; margin-bottom: 16px; }
    .tile { display: flex; flex-direction: column; gap: 2px; padding: 12px 16px; border-radius: 10px; background: #fff; border: 1px solid #d9e0de;
            color: #1b2a2e; text-decoration: none; }
    .tile:hover { box-shadow: 0 1px 6px rgba(14,59,67,.12); }
    .tile b { font-size: 1.5rem; letter-spacing: -0.01em; color: #0e3b43; }
    .tile span { color: #5b6b6e; font-size: .85rem; }
    .tile em { color: #b3261e; font-style: normal; font-weight: 600; }
    .tabs { display: flex; gap: 4px; border-bottom: 1px solid #d9e0de; margin-bottom: 12px; overflow-x: auto; }
    .tabs a { padding: 10px 14px; color: #5b6b6e; text-decoration: none; border-bottom: 3px solid transparent; white-space: nowrap; }
    .tabs a.on { color: #0e3b43; border-bottom-color: #0e3b43; font-weight: 500; }
    .day { display: flex; align-items: center; gap: 4px; margin-bottom: 8px; }
    .day .when { font-weight: 500; min-width: 150px; text-align: center; }
    .mute { color: #5b6b6e; }
    .warn { color: #8a5a00; display: block; }
    .bad { color: #b3261e; display: block; }
    .req { color: #0e3b43; }
    .err { color: #b3261e; }
    .row-err { margin: 6px 0 0; text-align: left; white-space: normal; max-width: 280px; margin-left: auto; }
    .right { text-align: right; }
    .actions { white-space: nowrap; }
    .actions button { margin-left: 4px; }
    .rm { margin-bottom: 2px; }
    .empty { color: #5b6b6e; padding: 20px 16px; }
    .chip.ok { background: #e1f2e8; color: #1d5b36; }
    .chip.wait { background: #fbefd8; color: #8a5a00; }
    .chip.vip { background: #f6e3ec; color: #8a2449; }
    .chip.hk { font-size: .72rem; }
    .chip.hk.READY { background: #e1f2e8; color: #1d5b36; }
    .chip.hk.DIRTY { background: #fbefd8; color: #8a5a00; }
    .chip.hk.OUT_OF_ORDER { background: #f3e3e3; color: #7a1f1f; }
    .board { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 12px; }
    .room { display: flex; flex-direction: column; align-items: flex-start; gap: 4px; min-height: 120px; padding: 12px 14px; border-radius: 10px;
            background: #fff; border: 1px solid #d9e0de; border-left-width: 5px; }
    .room.VACANT { border-left-color: #2e6b3c; }
    .room.OCCUPIED { border-left-color: #0e3b43; background: #f2f7f8; }
    .room.ARRIVING { border-left-color: #d29a2c; }
    .room.BLOCKED { border-left-color: #b45309; background: #fffaf0; }
    .room.INACTIVE { border-left-color: #a9b6b3; color: #5b6b6e; background: #f5f7f6; }
    .room .top { display: flex; justify-content: space-between; align-items: baseline; gap: 8px; width: 100%; }
    .room .code { font-size: 1.25rem; }
    .room .type { font-size: .75rem; color: #5b6b6e; text-align: right; }
    .room .state { font-weight: 500; }
    .room .who { font-size: .85rem; color: #0e3b43; }
    .hk-actions { margin-top: auto; }
    @media (max-width: 760px) {
      .tiles { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .board { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .actions { white-space: normal; }
      .actions button { margin: 0 0 4px 0; display: block; width: 100%; }
      table.data td, table.data th { padding: 10px 8px; }
    }
  `,
})
export class FrontDeskPage {
  tab = input<DeskTab>('arrivals');
  private api = inject(FrontDeskApi);
  private desk = inject(DeskActions);
  props = inject(PropertyContext);
  private auth = inject(AuthService);
  private snack = inject(MatSnackBar);
  private router = inject(Router);

  dash = signal<FrontDeskDashboard | null>(null);
  list = signal<FrontDeskList | null>(null);
  loading = signal(true);
  busy = signal(false);
  error = signal('');
  rowError = signal<Record<string, string>>({});
  /** The day shown on arrivals/departures; '' = today at the property. */
  date = signal('');
  today = computed(() => this.list()?.today ?? this.dash()?.today ?? this.props.current()?.localDate ?? '');

  statusLabel = statusLabel;
  statusClass = statusClass;
  guestsText = guestsText;
  shortDate = shortDate;
  phone = formatPhone;
  rupees = rupeesExact;
  hk = housekeepingLabel;
  occupancy = occupancyLabel;
  place = placeName;
  private pid = () => this.props.current()!.id;

  canCheckIn = computed(() => { this.auth.me(); return this.auth.can('frontdesk.checkin', this.props.current()?.id); });
  canCheckOut = computed(() => { this.auth.me(); return this.auth.can('frontdesk.checkout', this.props.current()?.id); });
  canWalkIn = computed(() => this.canCheckIn() && this.auth.can('reservation.create', this.props.current()?.id));
  canHousekeeping = computed(() => { this.auth.me(); return this.auth.can('housekeeping.update', this.props.current()?.id); });

  todayText = computed(() => this.today() ? dayLabel(this.today()) : '');
  dayText = computed(() => { const d = this.date() || this.today(); return d ? (d === this.today() ? 'Today, ' : '') + dayLabel(d) : ''; });

  private reload$ = new Subject<void>();

  constructor() {
    this.reload$.pipe(
      tap(() => { this.loading.set(true); this.error.set(''); }),
      switchMap(() => this.load()),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe({
      next: l => { this.list.set(l); this.loading.set(false); },
      error: e => { this.error.set(toProblem(e).detail); this.loading.set(false); },
    });
    effect(() => { this.tab(); this.date(); if (this.props.current()) this.refresh(); });
  }

  private load(): Observable<FrontDeskList | null> {
    const pid = this.pid(), d = this.date() || undefined;
    switch (this.tab()) {
      case 'arrivals': return this.api.arrivals(pid, d);
      case 'departures': return this.api.departures(pid, d);
      case 'in-house': return this.api.inHouse(pid);
      default: return new Observable<null>(o => { o.next(null); o.complete(); });
    }
  }

  private refresh() {
    this.rowError.set({});
    this.reload$.next();
    this.api.dashboard(this.pid()).subscribe({ next: d => this.dash.set(d), error: e => this.error.set(toProblem(e).detail) });
  }

  shift(days: number) { this.date.set(addDays(this.date() || this.today(), days)); }
  action(s: FrontDeskStay) { return stayAction(s, this.today()); }
  time(iso: string) { return inZone(iso, this.props.current()!.timezone, { hour: '2-digit', minute: '2-digit', hour12: false }); }

  checkIn(s: FrontDeskStay) {
    this.run(s, this.desk.checkIn(this.pid(), deskStay(s), this.today()),
      r => `Checked in: ${r.reference}, ${r.units.filter(u => u.current && u.endDate === r.checkOut).map(placeName).join(', ')}`);
  }
  assign(s: FrontDeskStay) {
    this.run(s, this.desk.assignRoom(this.pid(), deskStay(s), this.today()),
      r => `${r.reference}: ${r.units.filter(u => u.current && u.endDate === r.checkOut).map(placeName).join(', ')}`);
  }
  checkOut(s: FrontDeskStay) {
    this.run(s, this.desk.checkOut(this.pid(), deskStay(s), this.today()), r => `Checked out: ${r.reference}`);
  }

  private run(s: FrontDeskStay, action: Observable<Reservation | null>, message: (r: Reservation) => string) {
    this.busy.set(true);
    this.rowError.update(e => ({ ...e, [s.reservationId]: '' }));
    action.subscribe({
      next: r => {
        this.busy.set(false);
        if (!r) return;
        this.snack.open(message(r), 'Open', { duration: 4000 }).onAction().subscribe(() => this.router.navigateByUrl(`/reservations/${r.id}`));
        this.refresh();
      },
      error: e => { this.busy.set(false); this.rowError.update(x => ({ ...x, [s.reservationId]: toProblem(e).detail })); },
    });
  }

  setHk(r: FrontDeskBoardRoom, status: Housekeeping) {
    this.busy.set(true);
    this.api.setHousekeeping(this.pid(), r.unitId, { bedId: r.bedId, status, reason: null }).subscribe({
      next: () => { this.busy.set(false); this.snack.open(`${placeName(r)}: ${housekeepingLabel(status)}`, undefined, { duration: 2500 }); this.refresh(); },
      error: e => { this.busy.set(false); this.error.set(`${placeName(r)}: ${toProblem(e).detail}`); },
    });
  }
}
