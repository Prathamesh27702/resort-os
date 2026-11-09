import { Component, computed, effect, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '../../core/auth.service';
import { PropertyContext, inZone } from '../../core/property-context';
import { MyAttendance, Room, User } from '../../core/models';
import { InstallPrompt } from '../../core/install';
import { RoomsApi } from '../rooms/rooms.api';
import { AttendanceApi, STATUS_LABEL } from '../attendance/attendance.api';
import { hm, hours } from '../employees/employees.api';
import { LeaveApi } from '../leave/leave.api';
import { OwnerBoard } from './owner-board';

interface Step { done: boolean; title: string; detail: string; link: string; action: string; }

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink, MatIconModule, MatButtonModule, OwnerBoard],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>{{ greeting() }}, {{ firstName() }}</h1>
          @if (props.current(); as p) { <p>{{ longDate() }} at {{ p.name }}</p> }
        </div>
      </div>

      @if (showInstall()) {
        <section class="panel install" data-testid="install">
          <mat-icon>install_mobile</mat-icon>
          <div class="txt">
            <b>Put Resort OS on your home screen</b>
            @if (install.canInstall()) { <span>It opens like an app, full screen, straight to your shifts and the clock-in button.</span> }
            @else { <span>In Safari tap <b>Share</b> <mat-icon inline>ios_share</mat-icon>, then <b>Add to Home Screen</b>.</span> }
          </div>
          @if (install.canInstall()) { <button mat-flat-button (click)="install.install()">Install</button> }
          <button mat-icon-button (click)="install.dismiss()" aria-label="Hide this"><mat-icon>close</mat-icon></button>
        </section>
      }

      @if (me(); as m) {
        <section class="panel myday" data-testid="my-day">
          <h2>Your day</h2>
          <p class="state">
            @switch (m.state) {
              @case ('ON_SHIFT') { <mat-icon inline>work</mat-icon> At work since {{ time(m.since) }} }
              @case ('ON_BREAK') { <mat-icon inline>coffee</mat-icon> On a break since {{ time(m.since) }} }
              @default { <mat-icon inline>schedule</mat-icon> Not clocked in }
            }
          </p>
          @if (m.todayShift; as s) { <p>Today: <b>{{ s.templateName ?? 'Shift' }} {{ hm(s.startTime) }}–{{ hm(s.endTime) }}</b> · {{ s.departmentName }}</p> }
          @else if (m.nextShift; as n) { <p class="sub">No shift today. Next: {{ n.templateName ?? 'Shift' }} on {{ dayName(n.workDate) }} {{ hm(n.startTime) }}–{{ hm(n.endTime) }}.</p> }
          @else { <p class="sub">No shifts on the roster in the next two weeks.</p> }
          @if (m.missedClockOut) { <p class="warn"><mat-icon inline>warning</mat-icon> {{ m.missedClockOut }}</p> }
          <div class="row">
            <a mat-flat-button routerLink="/attendance">{{ m.state === 'OFF' ? 'Clock in' : 'Clock out or break' }}</a>
            <a mat-stroked-button routerLink="/timesheets" [queryParams]="{ view: 'mine' }">My timesheet</a>
            @if (m.week; as w) { <span class="sub">This week {{ hours(w.workedMinutes) }} · {{ statusLabel[w.status] }}</span> }
          </div>
        </section>
      }

      @if (leaveWaiting(); as n) {
        <section class="panel myday" data-testid="leave-waiting">
          <h2>Leave waiting for you</h2>
          <p>{{ n }} {{ n === 1 ? 'request is' : 'requests are' }} waiting for approval.</p>
          <div class="row"><a mat-flat-button routerLink="/leave" [queryParams]="{ view: 'team' }">Look at {{ n === 1 ? 'it' : 'them' }}</a></div>
        </section>
      }

      @if (reportsFor(); as pid) { <app-owner-board [propertyId]="pid" /> }

      @if (steps().length) {
        <section class="panel setup">
          <h2>Set up {{ props.current()!.name }}</h2>
          <p class="sub">{{ doneCount() }} of {{ steps().length }} done. Bookings and billing open up in the next sprints.</p>
          <ul>
            @for (s of steps(); track s.title) {
              <li [class.done]="s.done">
                <mat-icon>{{ s.done ? 'check_circle' : 'radio_button_unchecked' }}</mat-icon>
                <div class="txt"><b>{{ s.title }}</b><span>{{ s.detail }}</span></div>
                @if (!s.done) { <a mat-stroked-button [routerLink]="s.link">{{ s.action }}</a> }
              </li>
            }
          </ul>
        </section>
      } @else if (!me()) {
        <section class="panel setup">
          <h2>You're signed in</h2>
          <p class="sub">Your shifts and the clock-in button show here once HR links your account to your employee record.</p>
        </section>
      }
    </div>
  `,
  styles: `
    .setup { padding: var(--space-5) var(--space-6); max-width: 720px; }
    .sub { color: var(--ro-mute); margin: var(--space-1) 0 var(--space-4); }
    ul { list-style: none; margin: 0; padding: 0; }
    li { display: flex; align-items: center; gap: 14px; padding: 14px 0; border-top: 1px solid var(--ro-line-soft); }
    li mat-icon { color: var(--ro-outline); }
    li.done mat-icon { color: #2e6b3c; }
    li.done b { color: var(--ro-mute); font-weight: 500; }
    .txt { flex: 1; display: flex; flex-direction: column; }
    .txt span { color: var(--ro-mute); font-size: 0.9rem; }
    .install { display: flex; align-items: center; gap: var(--space-3); padding: var(--space-3) var(--space-4); max-width: 720px; margin-bottom: var(--space-3); background: #eef6f4; }
    .install > mat-icon { color: var(--ro-tide); }
    .myday { padding: var(--space-4) var(--space-5); max-width: 720px; margin-bottom: var(--space-3); } .myday h2 { margin: 0; }
    .myday .state { font-size: 1.2rem; font-weight: 600; margin: var(--space-2) 0 var(--space-1); } .myday p { margin: var(--space-1) 0; }
    .myday .row { display: flex; gap: var(--space-2); align-items: center; flex-wrap: wrap; margin-top: 10px; }
    .warn { color: var(--ro-warn); }
  `,
})
export class DashboardPage {
  auth = inject(AuthService);
  props = inject(PropertyContext);
  private http = inject(HttpClient);
  private users = signal<User[] | null>(null);
  private rooms = signal<Room[] | null>(null);
  private roomsApi = inject(RoomsApi);
  private attendance = inject(AttendanceApi);
  install = inject(InstallPrompt);
  me = signal<MyAttendance | null>(null);
  /** Leave requests waiting for the caller to approve (Sprint 19): the manager is told here. */
  leaveWaiting = signal(0);
  private leave = inject(LeaveApi);
  hm = hm; hours = hours; statusLabel = STATUS_LABEL;
  /** The property whose numbers show here (Sprint 23): only for someone who may read the operational reports. */
  reportsFor = computed(() => { const p = this.props.current(); return p && this.auth.can('report.operational.read', p.id) ? p.id : null; });
  showInstall = computed(() => !this.install.installed() && !this.install.dismissed() && (this.install.canInstall() || this.install.iosHint()));

  constructor() {
    if (this.auth.canAnywhere('user.read')) this.http.get<User[]>('/api/v1/users').subscribe(u => this.users.set(u));
    effect(() => {
      const p = this.props.current();
      this.rooms.set(null);
      if (p && this.auth.can('accommodation.read', p.id)) this.roomsApi.rooms(p.id).subscribe(r => this.rooms.set(r));
      // The employee's own day (Sprint 18): only when their account is linked to an employee record here.
      this.me.set(null);
      if (p && this.auth.can('attendance.self', p.id)) this.attendance.me(p.id).subscribe({ next: m => this.me.set(m), error: () => this.me.set(null) });
      this.leaveWaiting.set(0);
      if (p && this.auth.can('leave.approve', p.id))
        this.leave.requests(p.id, { status: 'PENDING' }).subscribe({ next: l => this.leaveWaiting.set(l.requests.filter(r => r.actions.includes('approve')).length) });
    });
  }

  time(iso: string | null) {
    return iso ? inZone(iso, this.props.current()?.timezone ?? 'Asia/Kolkata', { hour: '2-digit', minute: '2-digit', hour12: false }) : '';
  }
  dayName(iso: string) { return inZone(iso + 'T12:00:00Z', 'UTC', { weekday: 'short', day: 'numeric', month: 'short' }); }

  firstName = computed(() => this.auth.me()?.fullName.split(' ')[0] ?? '');
  greeting = computed(() => {
    const tz = this.props.current()?.timezone ?? 'Asia/Kolkata';
    const h = Number(inZone(new Date(), tz, { hour: 'numeric', hour12: false }));
    return h < 12 ? 'Good morning' : h < 17 ? 'Good afternoon' : 'Good evening';
  });
  longDate = computed(() => {
    const p = this.props.current();
    return p ? inZone(new Date(), p.timezone, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }) : '';
  });

  steps = computed<Step[]>(() => {
    const p = this.props.current();
    if (!p || !this.auth.can('property.update', p.id)) return [];
    const users = this.users() ?? [];
    return [
      { done: !!(p.gstStateCode && p.addressLine1 && p.postalCode), title: 'Add address and GST state',
        detail: 'Needed for GST place of supply on invoices.', link: `/settings/properties/${p.id}`, action: 'Edit property' },
      { done: !!(p.phone && p.email), title: 'Add guest contact details',
        detail: 'Shown on confirmations and invoices.', link: `/settings/properties/${p.id}`, action: 'Edit property' },
      { done: users.length > 1, title: 'Create staff accounts',
        detail: 'Give each person their own sign-in and only the access their job needs.', link: '/users/new', action: 'Add staff' },
      { done: (this.rooms() ?? []).some(r => r.isActive), title: 'Set up rooms',
        detail: 'Add your room types, rooms and dormitory beds so they can be sold.', link: '/rooms', action: 'Set up rooms' },
    ];
  });
  doneCount = computed(() => this.steps().filter(s => s.done).length);
}
