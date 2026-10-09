import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { BreakpointObserver } from '@angular/cdk/layout';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatButtonModule } from '@angular/material/button';
import { MatSidenavModule } from '@angular/material/sidenav';
import { AuthService } from '../core/auth.service';
import { PropertyContext, inZone } from '../core/property-context';

interface NavItem { label: string; icon: string; link: string; perm?: string; exact?: boolean; }

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatIconModule, MatMenuModule, MatButtonModule, MatSidenavModule],
  template: `
    <mat-sidenav-container class="frame">
      <mat-sidenav [mode]="mobile() ? 'over' : 'side'" [opened]="!mobile() || navOpen()" (closed)="navOpen.set(false)" class="rail">
        <div class="logo">Resort OS</div>
        <div class="org">{{ auth.me()?.organization?.name }}</div>
        <nav>
          @for (item of nav(); track item.link) {
            <a [routerLink]="item.link" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: !!item.exact }"
               (click)="navOpen.set(false)">
              <mat-icon>{{ item.icon }}</mat-icon><span>{{ item.label }}</span>
            </a>
          }
        </nav>
        <p class="later">Reservations, billing, kitchen and staff modules arrive in later sprints.</p>
      </mat-sidenav>

      <mat-sidenav-content>
        <header class="bar">
          @if (mobile()) {
            <button mat-icon-button (click)="navOpen.set(true)" aria-label="Open menu"><mat-icon>menu</mat-icon></button>
          }
          @if (props.properties().length > 1) {
            <button mat-button [matMenuTriggerFor]="pmenu" class="prop">
              {{ props.current()!.name }} <mat-icon iconPositionEnd>expand_more</mat-icon>
            </button>
            <mat-menu #pmenu="matMenu">
              @for (p of props.properties(); track p.id) {
                <button mat-menu-item (click)="props.select(p.id)">{{ p.name }}</button>
              }
            </mat-menu>
          } @else if (props.current(); as cur) {
            <span class="prop static">{{ cur.name }}</span>
          }

          @if (props.current(); as p) {
            <div class="clock" [title]="'Property time (' + p.timezone + ')'">
              <span class="day">{{ localDay() }}</span>
              <span class="time num">{{ localTime() }}</span>
              <span class="tz">{{ tzLabel() }}</span>
            </div>
          }

          <span class="grow"></span>
          <button mat-button [matMenuTriggerFor]="umenu" class="user">
            <span class="initials">{{ initials() }}</span>
            <span class="who"><b>{{ auth.me()?.fullName }}</b><small>{{ auth.roleLabel() }}</small></span>
          </button>
          <mat-menu #umenu="matMenu">
            <a mat-menu-item routerLink="/account/password"><mat-icon>key</mat-icon>Change password</a>
            <button mat-menu-item (click)="auth.logout()"><mat-icon>logout</mat-icon>Sign out</button>
          </mat-menu>
        </header>
        <router-outlet />
      </mat-sidenav-content>
    </mat-sidenav-container>
  `,
  styles: `
    :host { display: block; }
    .frame { height: 100vh; width: 100%; background: #f5f7f6; --mat-sidenav-container-shape: 0; }
    .rail { width: 232px; background: #0e3b43; color: #cfe0dd; border: 0; padding: 20px 12px; display: flex; flex-direction: column; }
    .logo { color: #fff; font-weight: 600; font-size: 1.25rem; letter-spacing: -0.02em; padding: 0 12px; }
    .org { font-size: 0.8rem; color: #8fb0ab; padding: 2px 12px 20px; }
    nav { display: flex; flex-direction: column; gap: 2px; }
    nav a { display: flex; align-items: center; gap: 12px; padding: 9px 12px; border-radius: 8px; color: #cfe0dd; text-decoration: none; }
    nav a mat-icon { font-size: 20px; width: 20px; height: 20px; }
    nav a:hover { background: #164c56; color: #fff; }
    nav a.active { background: #f5f7f6; color: #0e3b43; font-weight: 500; }
    .later { margin: 24px 12px 0; font-size: 0.78rem; color: #7fa29d; line-height: 1.4; }
    .bar { position: sticky; top: 0; z-index: 2; display: flex; align-items: center; gap: 16px; padding: 8px 20px; background: #fff; border-bottom: 1px solid #d9e0de; min-height: 56px; }
    .prop { font-weight: 600; font-size: 1rem; }
    .prop.static { padding: 0 8px; }
    .clock { display: flex; align-items: baseline; gap: 8px; padding: 4px 12px; border-left: 3px solid #8a2449; }
    .clock .day { color: #5b6b6e; }
    .clock .time { font-size: 1.15rem; font-weight: 600; color: #1b2a2e; }
    .clock .tz { font-size: 0.75rem; color: #5b6b6e; }
    .grow { flex: 1; }
    .user { height: auto; padding: 4px 8px; }
    .initials { display: inline-grid; place-items: center; width: 32px; height: 32px; border-radius: 50%; background: #0e3b43; color: #fff; font-size: 0.8rem; font-weight: 600; margin-right: 8px; }
    .who { display: inline-flex; flex-direction: column; text-align: left; line-height: 1.2; }
    .who small { color: #5b6b6e; font-size: 0.75rem; }
    @media (max-width: 760px) {
      .bar { padding: 6px 8px; gap: 6px; flex-wrap: wrap; }
      .clock .day, .clock .tz, .who { display: none; }
    }
  `,
})
export class Shell {
  auth = inject(AuthService);
  props = inject(PropertyContext);
  navOpen = signal(false);
  mobile = toSignal(inject(BreakpointObserver).observe('(max-width: 760px)').pipe(map(r => r.matches)), { initialValue: false });

  private now = signal(new Date());

  private all: NavItem[] = [
    { label: 'Today', icon: 'wb_sunny', link: '/dashboard' },
    { label: 'Rooms', icon: 'bed', link: '/rooms', perm: 'accommodation.read', exact: true },
    { label: 'Room status', icon: 'grid_view', link: '/rooms/status', perm: 'accommodation.read' },
    { label: 'Room types', icon: 'category', link: '/settings/room-types', perm: 'accommodation.read' },
    { label: 'Properties', icon: 'holiday_village', link: '/settings/properties', perm: 'property.read' },
    { label: 'Staff accounts', icon: 'badge', link: '/users', perm: 'user.read' },
    { label: 'Roles & permissions', icon: 'shield_person', link: '/roles', perm: 'role.read' },
    { label: 'Audit trail', icon: 'history', link: '/audit', perm: 'audit.read' },
  ];
  nav = computed(() => { this.auth.me(); return this.all.filter(i => !i.perm || this.auth.canAnywhere(i.perm)); });

  localDay = computed(() => { const p = this.props.current(); return p ? inZone(this.now(), p.timezone, { weekday: 'short', day: 'numeric', month: 'short' }) : ''; });
  localTime = computed(() => { const p = this.props.current(); return p ? inZone(this.now(), p.timezone, { hour: '2-digit', minute: '2-digit', hour12: false }) : ''; });
  tzLabel = computed(() => { const p = this.props.current(); return p?.timezone === 'Asia/Kolkata' ? 'IST' : p?.timezone ?? ''; });
  initials = computed(() => (this.auth.me()?.fullName ?? '').split(/\s+/).map(s => s[0]).slice(0, 2).join('').toUpperCase());

  constructor() {
    const t = setInterval(() => this.now.set(new Date()), 15_000);
    inject(DestroyRef).onDestroy(() => clearInterval(t));
  }
}
