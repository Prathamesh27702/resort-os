import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { dayText } from '../../core/dates';
import { toProblem } from '../../core/http';
import {
  Activity, ActivityBooking, ActivityDay, ActivitySession, ActivitySessionDetail, CreateSessionsResult, EquipmentAssignment, EquipmentType,
  WeatherCancelResult,
} from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { ReasonData, ReasonDialog } from '../restaurant/order-dialogs';
import { ActivitiesApi, rupees, seats, settlementText } from './activities.api';
import { ActivityDialog, BookActivityDialog, SessionEditDialog, SessionsDialog, WeatherCancelDialog } from './activity-dialogs';
import { IssueEquipmentDialog } from './equipment-dialogs';

type View = 'day' | 'activities';

/** "2026-11-03" plus n days. */
function addDays(iso: string, n: number): string {
  const d = new Date(iso + 'T00:00:00Z');
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
}

/**
 * Activities (Sprint 20, brief §48). The day's sessions with the seats left; open one to see who is booked, book a guest
 * (the price goes on their bill), cancel a booking, issue the equipment they need, or cancel the whole session for the
 * weather (refunds on the bill, or moves to another session). Managers put sessions on and keep the list of activities.
 */
@Component({
  selector: 'app-activities',
  imports: [MatButtonModule, MatButtonToggleModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page acts">
      <div class="head">
        <h1>Activities</h1>
        @if (canManage()) {
          <mat-button-toggle-group [value]="view()" (change)="view.set($event.value)" aria-label="Show">
            <mat-button-toggle value="day">Day</mat-button-toggle>
            <mat-button-toggle value="activities">Activities</mat-button-toggle>
          </mat-button-toggle-group>
        }
      </div>

      @if (view() === 'day') {
        <div class="bar">
          <button mat-icon-button (click)="go(-1)" aria-label="Previous day"><mat-icon>chevron_left</mat-icon></button>
          <b class="date" data-testid="day">{{ date() ? dayText(date()!) : '' }}@if (date() === day()?.today) { <small>, today</small> }</b>
          <button mat-icon-button (click)="go(1)" aria-label="Next day"><mat-icon>chevron_right</mat-icon></button>
          @if (date() !== day()?.today) { <button mat-button (click)="date.set(day()?.today ?? null)">Today</button> }
          <span class="grow"></span>
          @if (canManage()) { <button mat-stroked-button (click)="putOn()"><mat-icon>event</mat-icon>Put sessions on</button> }
        </div>

        <ul class="sessions" data-testid="sessions">
          @for (s of day()?.sessions ?? []; track s.id) {
            <li [attr.data-session]="s.activityCode + '-' + s.startTime" [class.off]="s.status === 'CANCELLED'" [class.open]="openId() === s.id">
              <button class="row" type="button" (click)="toggle(s)" [attr.aria-expanded]="openId() === s.id">
                <span class="time">{{ s.startTime }}<small>{{ s.endTime }}</small></span>
                <span class="what"><b>{{ s.activityName }}</b>
                  <small class="mute">{{ s.price ? rupees(s.price) + ' a person' : 'Free' }}@if (s.meetingPoint) {, {{ s.meetingPoint }} }@if (s.note) {, {{ s.note }} }</small>
                  @if (s.status === 'CANCELLED') { <small class="cx">Cancelled: {{ s.cancelReason }}</small> }
                </span>
                <span class="seats" [class.full]="s.status === 'SCHEDULED' && s.seatsLeft === 0">
                  <b>{{ s.seatsBooked }}/{{ s.capacity }}</b>
                  <small>{{ s.status === 'CANCELLED' ? 'cancelled' : s.seatsLeft === 0 ? 'full' : s.seatsLeft + ' left' }}</small>
                  <span class="meter"><span [style.width.%]="100 * s.seatsBooked / s.capacity"></span></span>
                </span>
              </button>
              @if (openId() === s.id) {
                <div class="detail">
                  <div class="actions">
                    @if (s.actions.includes('book')) { <button mat-flat-button (click)="book(s)"><mat-icon>person_add</mat-icon>Book</button> }
                    @if (s.actions.includes('edit')) { <button mat-stroked-button (click)="edit(s)">Seats and price</button> }
                    @if (s.actions.includes('weather-cancel')) { <button mat-stroked-button color="warn" (click)="weather()"><mat-icon>thunderstorm</mat-icon>Cancel for weather</button> }
                  </div>
                  <ul class="bookings" data-testid="bookings">
                    @for (b of detail()?.bookings ?? []; track b.id) {
                      <li [attr.data-booking]="b.code" [class.off]="b.status === 'CANCELLED'">
                        <div class="main">
                          <span><b>{{ b.guestName }}</b>, {{ seats(b.seats) }}@if (b.room) {, {{ b.room }} }</span>
                          <small class="mute">{{ b.code }}, {{ money(b) }}@if (b.movedFromCode) {, moved from {{ b.movedFromCode }} }@if (b.note) {, {{ b.note }} }</small>
                          @if (b.status === 'CANCELLED') { <small class="cx">{{ b.cancelKind === 'WEATHER' ? 'Weather' : 'Cancelled' }}: {{ b.cancelReason }}</small> }
                          @if (b.equipment.length) { <small class="kit">{{ kit(b.equipment) }}</small> }
                        </div>
                        <div class="btns">
                          @if (b.actions.includes('equipment')) { <button mat-stroked-button (click)="issue(b, s)"><mat-icon>sailing</mat-icon>Equipment</button> }
                          @if (b.actions.includes('cancel')) { <button mat-button (click)="cancel(b)">Cancel</button> }
                        </div>
                      </li>
                    } @empty { <li class="mute">Nobody is booked yet.</li> }
                  </ul>
                </div>
              }
            </li>
          } @empty { <li class="mute empty">No sessions on this day.@if (canManage()) { Put some on with the button above. }</li> }
        </ul>
      } @else {
        <div class="bar"><span class="grow"></span><button mat-flat-button (click)="editActivity(null)"><mat-icon>add</mat-icon>New activity</button></div>
        <ul class="list" data-testid="activities">
          @for (a of activities(); track a.id) {
            <li [class.off]="!a.isActive">
              <div class="main"><b>{{ a.name }}</b> <small class="mute">{{ a.code }}</small>
                <small>{{ a.durationMinutes }} min, {{ seats(a.capacity) }}, {{ a.price ? rupees(a.price) + ' a person' : 'free' }}@if (a.equipmentTypeName) {, a {{ a.equipmentTypeName.toLowerCase() }} each }</small>
                <small class="mute">{{ a.meetingPoint }}{{ a.weatherDependent ? '' : ', runs in any weather' }}{{ a.isActive ? '' : ', not offered' }}</small></div>
              <button mat-button (click)="editActivity(a)">Change</button>
            </li>
          } @empty { <li class="mute">No activities yet.</li> }
        </ul>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>
  `,
  styles: `
    .acts { max-width: 900px; padding-bottom: 60px; }
    .head, .bar { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; } .head { justify-content: space-between; }
    .grow { flex: 1; } .date { min-width: 130px; text-align: center; } .mute { color: var(--ro-mute); } .err { color: var(--ro-error); } .cx { color: var(--ro-error-strong); }
    .sessions, .list, .bookings { list-style: none; margin: var(--space-2) 0 0; padding: 0; }
    .sessions > li { background: var(--ro-paper); border: 1px solid var(--ro-line); border-radius: 14px; margin-bottom: var(--space-2); overflow: hidden; }
    .sessions > li.empty { padding: 14px; }
    .sessions > li.off { opacity: .7; }
    .row { display: flex; gap: var(--space-3); align-items: center; width: 100%; padding: 10px 14px; background: none; border: 0; text-align: left; cursor: pointer; font: inherit; color: inherit; }
    .row:hover { background: var(--ro-wash); }
    .time { display: flex; flex-direction: column; font-weight: 600; font-size: 1.05rem; min-width: 52px; } .time small { font-weight: 400; color: var(--ro-mute); font-size: .8rem; }
    .what { flex: 1; display: flex; flex-direction: column; min-width: 0; }
    .seats { display: flex; flex-direction: column; align-items: flex-end; min-width: 72px; } .seats small { color: var(--ro-mute); }
    .seats.full b, .seats.full small { color: var(--ro-error-strong); }
    .meter { width: 72px; height: 5px; background: var(--ro-sunk); border-radius: 3px; margin-top: 3px; overflow: hidden; }
    .meter span { display: block; height: 100%; background: var(--ro-sea); }
    .detail { border-top: 1px solid var(--ro-sunk); padding: var(--space-2) 14px var(--space-3); }
    .actions { display: flex; gap: var(--space-2); flex-wrap: wrap; margin-bottom: 6px; }
    .bookings li { display: flex; gap: var(--space-2); align-items: center; flex-wrap: wrap; padding: var(--space-2) 0; border-top: 1px solid var(--ro-tint); }
    .bookings li:first-child { border-top: 0; } .bookings li.off { opacity: .65; }
    .main { flex: 1 1 240px; display: flex; flex-direction: column; min-width: 0; } .kit { color: var(--ro-tide); }
    .btns { display: flex; gap: 6px; flex-wrap: wrap; }
    .list { background: var(--ro-paper); border: 1px solid var(--ro-line); border-radius: 14px; }
    .list li { display: flex; gap: var(--space-2); align-items: center; padding: 10px 14px; border-top: 1px solid var(--ro-sunk); } .list li:first-child { border-top: 0; }
    .list li.off { opacity: .6; }
  `,
})
export class ActivitiesPage {
  private api = inject(ActivitiesApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  dayText = dayText; rupees = rupees; seats = seats;

  private pid = computed(() => this.ctx.current()?.id ?? '');
  canManage = computed(() => this.auth.can('activity.manage', this.pid()));
  view = signal<View>('day');
  date = signal<string | null>(null);
  day = signal<ActivityDay | null>(null);
  activities = computed<Activity[]>(() => this.day()?.activities ?? []);
  openId = signal<string | null>(null);
  detail = signal<ActivitySessionDetail | null>(null);
  error = signal('');
  private types: EquipmentType[] = [];

  constructor() {
    effect(() => {
      const d = this.date();
      if (this.pid()) untracked(() => this.load(d));
    });
  }

  load(d: string | null = this.date()) {
    this.error.set('');
    this.api.day(this.pid(), d).subscribe({
      next: x => {
        this.day.set(x);
        if (!this.date()) this.date.set(x.date);
        if (this.openId() && !x.sessions.some(s => s.id === this.openId())) { this.openId.set(null); this.detail.set(null); }
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

  toggle(s: ActivitySession) {
    if (this.openId() === s.id) { this.openId.set(null); this.detail.set(null); return; }
    this.openId.set(s.id);
    this.detail.set(null);
    this.loadDetail(s.id);
  }

  private loadDetail(id: string) {
    this.api.session(this.pid(), id).subscribe({ next: x => { if (this.openId() === id) this.detail.set(x); }, error: e => this.error.set(toProblem(e).detail) });
  }

  money(b: ActivityBooking) { return settlementText(b); }
  kit(list: EquipmentAssignment[]) {
    return list.map(x => `${x.tag} ${x.status === 'RESERVED' ? 'kept aside' : x.status === 'ISSUED' ? 'out' : x.returnCondition === 'GOOD' ? 'back'
      : x.returnCondition === 'DAMAGED' ? 'back damaged' : 'lost'}`).join(', ');
  }

  book(s: ActivitySession) {
    this.dialog.open(BookActivityDialog, { data: { propertyId: this.pid(), session: s }, maxWidth: '100vw' }).afterClosed().subscribe((b?: ActivityBooking) => {
      if (!b) return;
      this.snack.open(`${b.code}: ${b.guestName} booked on ${b.activityName} at ${b.startTime} (${seats(b.seats)}). ${settlementText(b)[0].toUpperCase()}${settlementText(b).slice(1)}.`,
        'OK', { duration: 6000 });
      this.load();
    });
  }

  cancel(b: ActivityBooking) {
    const data: ReasonData<ActivityBooking> = {
      title: `Cancel ${b.code}?`, button: 'Cancel the booking', placeholder: 'Guest changed plans',
      intro: b.settlement === 'ROOM_CHARGE' ? `${rupees(b.amount)} comes off ${b.reservationReference}'s bill.` : `${b.guestName}'s seats are freed.`,
      run: reason => this.api.cancel(this.pid(), b, reason),
    };
    this.dialog.open(ReasonDialog, { data, maxWidth: '100vw' }).afterClosed().subscribe(x => {
      if (!x) return;
      this.snack.open(`${b.code} cancelled.`, 'OK', { duration: 4000 });
      this.load();
    });
  }

  weather() {
    const d = this.detail();
    if (!d) return;
    this.dialog.open(WeatherCancelDialog, { data: { propertyId: this.pid(), detail: d }, maxWidth: '100vw' }).afterClosed()
      .subscribe((r?: WeatherCancelResult) => {
        if (!r) return;
        const parts = [r.refunded ? `${r.refunded} refunded (${rupees(r.amountRefunded)} off the bills)` : '', r.moved ? `${r.moved} moved` : '',
          r.nothingToRefund ? `${r.nothingToRefund} with nothing to refund` : ''].filter(Boolean);
        this.snack.open(`${r.session.activityName} at ${r.session.startTime} cancelled.${parts.length ? ' ' + parts.join(', ') + '.' : ''}`, 'OK', { duration: 7000 });
        this.load();
      });
  }

  edit(s: ActivitySession) {
    this.dialog.open(SessionEditDialog, { data: { propertyId: this.pid(), session: s }, maxWidth: '100vw' }).afterClosed().subscribe((x?: ActivitySession) => {
      if (!x) return;
      this.snack.open(`${x.activityName} at ${x.startTime}: ${seats(x.capacity)}, ${x.price ? rupees(x.price) + ' a person' : 'free'}.`, 'OK', { duration: 4000 });
      this.load();
    });
  }

  issue(b: ActivityBooking, s: ActivitySession) {
    const open = () => this.dialog.open(IssueEquipmentDialog, {
      data: { propertyId: this.pid(), types: this.types.filter(t => t.isActive), booking: b, typeId: s.equipmentTypeId, quantity: b.seats }, maxWidth: '100vw',
    }).afterClosed().subscribe((x?: EquipmentAssignment[]) => {
      if (!x?.length) return;
      this.snack.open(`${x.map(i => i.tag).join(', ')} ${x[0].status === 'RESERVED' ? 'kept aside' : 'issued'} for ${b.guestName}.`, 'OK', { duration: 5000 });
      this.load();
    });
    if (this.types.length) { open(); return; }
    this.api.board(this.pid()).subscribe({ next: x => { this.types = x.types; open(); }, error: e => this.error.set(toProblem(e).detail) });
  }

  putOn() {
    const d = this.day();
    if (!d) return;
    this.dialog.open(SessionsDialog, { data: { propertyId: this.pid(), activities: d.activities.filter(a => a.isActive), from: this.date() ?? d.today }, maxWidth: '100vw' })
      .afterClosed().subscribe((r?: CreateSessionsResult) => {
        if (!r) return;
        this.snack.open(`${r.created} ${r.created === 1 ? 'session' : 'sessions'} put on.${r.skipped.length ? ' Skipped: ' + r.skipped.join('; ') + '.' : ''}`, 'OK',
          { duration: 7000 });
        this.load();
      });
  }

  editActivity(a: Activity | null) {
    const open = () => this.dialog.open(ActivityDialog, { data: { propertyId: this.pid(), activity: a, equipmentTypes: this.types }, maxWidth: '100vw' })
      .afterClosed().subscribe((x?: Activity) => {
        if (!x) return;
        this.snack.open(`${x.name} saved.`, 'OK', { duration: 4000 });
        this.load();
      });
    if (this.types.length || !this.auth.can('equipment.read', this.pid())) { open(); return; }
    this.api.board(this.pid()).subscribe({ next: x => { this.types = x.types; open(); }, error: () => open() });
  }
}
