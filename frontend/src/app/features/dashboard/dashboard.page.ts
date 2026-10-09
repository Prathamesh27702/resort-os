import { Component, computed, effect, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '../../core/auth.service';
import { PropertyContext, inZone } from '../../core/property-context';
import { Room, User } from '../../core/models';
import { RoomsApi } from '../rooms/rooms.api';

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
      } @else {
        <section class="panel setup">
          <h2>You're signed in</h2>
          <p class="sub">Your shifts, tasks and leave will show here once those modules are switched on.</p>
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
  `,
})
export class DashboardPage {
  auth = inject(AuthService);
  props = inject(PropertyContext);
  private http = inject(HttpClient);
  private users = signal<User[] | null>(null);
  private rooms = signal<Room[] | null>(null);
  private roomsApi = inject(RoomsApi);

  constructor() {
    if (this.auth.canAnywhere('user.read')) this.http.get<User[]>('/api/v1/users').subscribe(u => this.users.set(u));
    effect(() => {
      const p = this.props.current();
      this.rooms.set(null);
      if (p && this.auth.can('accommodation.read', p.id)) this.roomsApi.rooms(p.id).subscribe(r => this.rooms.set(r));
    });
  }

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
