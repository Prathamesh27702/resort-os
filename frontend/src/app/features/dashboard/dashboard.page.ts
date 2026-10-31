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

interface Step { done: boolean; title: string; detail: string; link: string; action: string; }

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink, MatIconModule, MatButtonModule],
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
    .setup { padding: 20px 24px; max-width: 720px; }
    .sub { color: #5b6b6e; margin: 4px 0 16px; }
    ul { list-style: none; margin: 0; padding: 0; }
    li { display: flex; align-items: center; gap: 14px; padding: 14px 0; border-top: 1px solid #edf1f0; }
    li mat-icon { color: #a9b6b3; }
    li.done mat-icon { color: #2e6b3c; }
    li.done b { color: #5b6b6e; font-weight: 500; }
    .txt { flex: 1; display: flex; flex-direction: column; }
    .txt span { color: #5b6b6e; font-size: 0.9rem; }
    .install { display: flex; align-items: center; gap: 12px; padding: 12px 16px; max-width: 720px; margin-bottom: 12px; background: #eef6f4; }
    .install > mat-icon { color: #0e3b43; }
    .myday { padding: 16px 20px; max-width: 720px; margin-bottom: 12px; } .myday h2 { margin: 0; }
    .myday .state { font-size: 1.2rem; font-weight: 600; margin: 8px 0 4px; } .myday p { margin: 4px 0; }
    .myday .row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-top: 10px; }
    .warn { color: #8a5a00; }
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
  hm = hm; hours = hours; statusLabel = STATUS_LABEL;
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
