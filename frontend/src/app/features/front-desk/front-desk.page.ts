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
import { FoliosApi } from '../folios/folios.api';
import { CashDrawerPanel } from '../payments/cash-drawer-panel';
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
  imports: [RouterLink, RouterLinkActive, MatButtonModule, MatIconModule, MatMenuModule, MatSnackBarModule, MatTooltipModule, CashDrawerPanel],
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
            <span>Departures checked out</span>@if (d.overdue) { <em>{{ d.overdue }} overdue</em> }</a>
          <a class="tile" routerLink="/front-desk/in-house"><b class="num">{{ d.inHouseStays }}</b><span>In house</span><small>{{ d.inHouseGuests }} guest{{ d.inHouseGuests === 1 ? '' : 's' }}</small></a>
          <a class="tile" routerLink="/front-desk/rooms"><b class="num">{{ d.roomsReady }}</b>
            <span>Ready to sell</span><small>{{ d.roomsNotReady }} not ready, {{ d.roomsOccupied }} occupied</small></a>
        </div>
      }

      @if (dash(); as d) {
        @if (d.formCPending && canFormC()) {
          <a [class]="'formc' + (d.formCOverdue ? ' late' : '')" routerLink="/form-c" data-testid="form-c-card">
            <mat-icon>{{ d.formCOverdue ? 'warning' : 'badge' }}</mat-icon>
            <span><b>{{ d.formCPending }} Form C to file</b> for foreign guests@if (d.formCOverdue) {, {{ d.formCOverdue }} past the 24 hours}. The FRRO must get each within 24 hours of arrival.</span>
            <span class="go">Open</span>
          </a>
        }
      }

      @if (canDrawer()) { <app-cash-drawer-panel /> }

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
              <div class="top"><b class="code">{{ r.unitCode }}@if (r.bedCode) { <span class="bed">{{ r.bedCode }}</span> }</b><span class="type">{{ r.typeName }}</span></div>
              <div class="state">{{ occupancy(r.occupancy) }}</div>
              <span [class]="'chip hk ' + r.housekeeping">{{ hk(r.housekeeping) }}</span>
              @if (r.reference) {
                <a class="who" [routerLink]="['/reservations', r.reservationId]">{{ r.guestName }} <small class="ref num">{{ r.reference }}</small></a>
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
                    <br /><small class="mute num">{{ s.reference }}@if (s.guestPhone) {, {{ phone(s.guestPhone) }}}, {{ guestsText(s.adults, s.children) }}</small>
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
                        @if (canFolio()) { <button mat-button (click)="openBill(s)" [attr.aria-label]="'Bill of ' + s.guestName">Bill</button> }
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
    .head-actions { display: flex; gap: var(--space-2); flex-wrap: wrap; }
    .tiles { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: var(--space-3); margin-bottom: var(--space-4); }
    .tile { display: flex; flex-direction: column; gap: 2px; padding: var(--space-3) var(--space-4); border-radius: var(--ro-radius); background: var(--ro-paper); border: 1px solid var(--ro-line);
            color: var(--ro-ink); text-decoration: none; }
    .tile:hover { box-shadow: 0 1px 6px var(--ro-shadow); }
    .tile b { font-size: 1.5rem; letter-spacing: -0.01em; color: var(--ro-tide); }
    .tile span { color: var(--ro-mute); font-size: .85rem; }
    .tile em { color: var(--ro-error); font-style: normal; font-weight: 600; font-size: .8rem; }
    .tile small { color: var(--ro-mute); font-size: .8rem; }
    .formc { display: flex; align-items: center; gap: var(--space-3); padding: var(--space-3) var(--space-4); margin-bottom: var(--space-4); border-radius: var(--ro-radius);
             background: var(--ro-warn-bg); color: var(--ro-warn-strong); border: 1px solid var(--ro-warn-line); text-decoration: none; }
    .formc.late { background: var(--ro-error-bg); color: var(--ro-error-strong); border-color: var(--ro-error-line); }
    .formc .go { margin-left: auto; font-weight: 600; text-decoration: underline; }
    .tabs { display: flex; gap: var(--space-1); margin-bottom: var(--space-3); overflow-x: auto; padding: 4px; border-radius: 999px; width: fit-content; max-width: 100%;
            box-sizing: border-box; background: var(--ro-glass); border: 1px solid var(--ro-glass-edge); scrollbar-width: none; }
    .tabs a { padding: 8px 16px; color: var(--ro-ink-soft); text-decoration: none; border-radius: 999px; white-space: nowrap; font-weight: 500; min-height: 40px; box-sizing: border-box; display: inline-flex; align-items: center; }
    .tabs a:hover { background: var(--ro-hover); }
    .tabs a.on { background: var(--ro-accent-grad); color: var(--ro-on-accent); box-shadow: var(--ro-accent-glow); font-weight: 600; }
    .day { display: flex; align-items: center; gap: var(--space-1); margin-bottom: var(--space-2); }
    .day .when { font-weight: 500; min-width: 150px; text-align: center; }
    .mute { color: var(--ro-mute); }
    .warn { color: var(--ro-warn); display: block; }
    .bad { color: var(--ro-error); display: block; }
    .req { color: var(--ro-tide); }
    .err { color: var(--ro-error); }
    .row-err { margin: 6px 0 0; text-align: left; white-space: normal; max-width: 280px; margin-left: auto; }
    .right { text-align: right; }
    .actions { white-space: nowrap; }
    .actions button { margin-left: var(--space-1); }
    .rm { margin-bottom: 2px; }
    .empty { color: var(--ro-mute); padding: var(--space-5) var(--space-4); }
    .chip.ok { background: var(--ro-ok-bg); color: var(--ro-ok); }
    .chip.wait { background: var(--ro-warn-bg); color: var(--ro-warn); }
    .chip.vip { background: var(--ro-kokum-wash); color: var(--ro-kokum); }
    .chip.hk { font-size: .72rem; }
    .chip.hk.READY { background: var(--ro-ok-bg); color: var(--ro-ok); }
    .chip.hk.DIRTY { background: var(--ro-warn-bg); color: var(--ro-warn); }
    .chip.hk.OUT_OF_ORDER { background: var(--ro-off-bg); color: var(--ro-off); }
    .board { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: var(--space-3); }
    .room { display: flex; flex-direction: column; align-items: flex-start; gap: var(--space-1); min-height: 120px; padding: var(--space-3) 14px; border-radius: var(--ro-radius);
            background: var(--ro-paper); border: 1px solid var(--ro-line); border-left-width: 5px; }
    .room.VACANT { border-left-color: var(--ro-ok-mid); }
    .room.OCCUPIED { border-left-color: var(--ro-tide); background: var(--ro-wash); }
    .room.ARRIVING { border-left-color: var(--ro-warn-bright); }
    .room.BLOCKED { border-left-color: var(--ro-rust); background: var(--ro-warn-wash); }
    .room.INACTIVE { border-left-color: var(--ro-outline); color: var(--ro-mute); background: var(--ro-wash); }
    .room .top { display: flex; justify-content: space-between; align-items: baseline; gap: var(--space-2); width: 100%; }
    .room .code { font-size: 1.25rem; white-space: nowrap; }
    .room .code .bed, .room .who .ref { margin-left: 6px; font-weight: 400; color: var(--ro-mute); }
    .room .code .bed { font-size: .9rem; }
    .room .type { font-size: .75rem; color: var(--ro-mute); text-align: right; }
    .room .state { font-weight: 500; }
    .room .who { font-size: .85rem; color: var(--ro-tide); }
    .hk-actions { margin-top: auto; }
    @media (max-width: 760px) {
      .tiles { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .board { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .actions { white-space: normal; }
      .actions button { margin: 0 0 var(--space-1) 0; display: block; width: 100%; }
      table.data td, table.data th { padding: 10px var(--space-2); }
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
  private folios = inject(FoliosApi);

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
  canFormC = computed(() => { this.auth.me(); return this.auth.can('guest_document.read', this.props.current()?.id); });
  canDrawer = computed(() => { this.auth.me(); return this.auth.can('payment.read', this.props.current()?.id); });
  canFolio = computed(() => { this.auth.me(); return this.auth.can('folio.read', this.props.current()?.id); });
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

  /** Sprint 8: opens the stay's main bill. */
  openBill(s: FrontDeskStay) {
    this.folios.forReservation(this.pid(), s.reservationId).subscribe({
      next: list => { const main = list.find(f => f.isPrimary); if (main) this.router.navigate(['/folios', main.id]); },
      error: e => this.rowError.update(x => ({ ...x, [s.reservationId]: toProblem(e).detail })),
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
