import { Component, DestroyRef, HostListener, computed, inject, signal } from '@angular/core';
import { BreakpointObserver } from '@angular/cdk/layout';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatDialog } from '@angular/material/dialog';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatButtonModule } from '@angular/material/button';
import { MatSidenavModule } from '@angular/material/sidenav';
import { AuthService } from '../core/auth.service';
import { PropertyContext, inZone } from '../core/property-context';
import { InstallPrompt } from '../core/install';
import { ThemeChoice, ThemeService, THEME_ICONS, THEME_LABELS } from '../core/theme';
import { Bell } from './bell';
import { NAV, NavItem, NavSection, TAB_LABELS, TAB_PREFERENCE } from './nav';
import { SearchDialog } from './search';

const FOLDED_KEY = 'ro-nav-folded';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatIconModule, MatMenuModule, MatButtonModule, MatSidenavModule, MatTooltipModule,
    MatSlideToggleModule, MatButtonToggleModule, Bell],
  template: `
    <mat-sidenav-container class="frame" [class.phone]="mobile()" [class.wide]="wide()">
      <mat-sidenav [mode]="wide() ? 'side' : 'over'" [opened]="wide() || navOpen()" (closed)="navOpen.set(false)" class="rail">
        <div class="brand">
          <span class="mark" aria-hidden="true"><mat-icon>waves</mat-icon></span>
          <span class="names"><span class="logo">Resort OS</span><span class="org">{{ auth.me()?.organization?.name }}</span></span>
        </div>
        <nav aria-label="Main menu">
          @for (sec of sections(); track sec.id) {
            <div class="sec" [class.folded]="folded().has(sec.id)">
              <button type="button" class="sec-head" (click)="fold(sec.id)" [attr.aria-expanded]="!folded().has(sec.id)"
                      [attr.aria-controls]="'sec-' + sec.id">
                <span>{{ sec.title }}</span><mat-icon aria-hidden="true">expand_more</mat-icon>
              </button>
              <div class="sec-items" [id]="'sec-' + sec.id">
                @for (item of sec.items; track item.link) {
                  <a [routerLink]="item.link" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: !!item.exact }"
                     (click)="navOpen.set(false)">
                    <mat-icon>{{ item.icon }}</mat-icon><span>{{ item.label }}</span>
                  </a>
                }
              </div>
            </div>
          }
        </nav>
      </mat-sidenav>

      <mat-sidenav-content (scroll)="onScroll($event)">
        <header class="bar" [class.scrolled]="scrolled()">
          @if (!wide()) {
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
          <button type="button" class="search" (click)="openSearch()" aria-label="Jump to a guest, stay or screen (Ctrl K)">
            <mat-icon aria-hidden="true">search</mat-icon><span class="ph">Search guests, bookings, screens</span><kbd>Ctrl K</kbd>
          </button>
          @if (!mobile()) {
            <button mat-icon-button class="theme" (click)="theme.cycle($any($event.currentTarget))"
                    [matTooltip]="'Theme: ' + themeLabels[theme.choice()]"
                    [attr.aria-label]="'Theme: ' + themeLabels[theme.choice()] + '. Change theme'">
              <mat-icon>{{ themeIcons[theme.choice()] }}</mat-icon>
            </button>
          }
          <app-bell />
          <button mat-button [matMenuTriggerFor]="umenu" class="user" [attr.aria-label]="'Your account: ' + (auth.me()?.fullName ?? '')">
            <span class="initials">{{ initials() }}</span>
            <span class="who"><b>{{ auth.me()?.fullName }}</b><small>{{ auth.roleLabel() }}</small></span>
          </button>
          <mat-menu #umenu="matMenu" class="umenu">
            <div class="theme-pick" (click)="$event.stopPropagation()" (keydown)="$event.stopPropagation()" role="presentation">
              <span class="lbl">Theme</span>
              <mat-button-toggle-group [value]="theme.choice()" (change)="theme.set($event.value)" aria-label="Theme" hideSingleSelectionIndicator>
                @for (c of themeChoices; track c) {
                  <mat-button-toggle [value]="c" [attr.aria-label]="themeLabels[c]"><mat-icon>{{ themeIcons[c] }}</mat-icon>
                    <span class="tl">{{ c === 'system' ? 'Phone' : themeLabels[c] }}</span></mat-button-toggle>
                }
              </mat-button-toggle-group>
              <mat-slide-toggle [checked]="theme.solid()" (change)="theme.setSolid($event.checked)">Solid panels (easier to read in sun, lighter on old phones)</mat-slide-toggle>
            </div>
            @if (install.canInstall()) { <button mat-menu-item (click)="install.install()"><mat-icon>install_mobile</mat-icon>Install the app</button> }
            <a mat-menu-item routerLink="/account/password"><mat-icon>key</mat-icon>Change password</a>
            <button mat-menu-item (click)="auth.logout()"><mat-icon>logout</mat-icon>Sign out</button>
          </mat-menu>
        </header>
        <main class="content">
          <router-outlet />
        </main>
      </mat-sidenav-content>
    </mat-sidenav-container>

    @if (mobile()) {
      <div class="tabbar" role="navigation" aria-label="Quick menu">
        @for (t of tabs(); track t.link) {
          <a [routerLink]="t.link" routerLinkActive="on" [routerLinkActiveOptions]="{ exact: !!t.exact }" class="tab">
            <mat-icon aria-hidden="true">{{ t.icon }}</mat-icon><span>{{ tabLabel(t) }}</span>
          </a>
        }
        <button type="button" class="tab" (click)="navOpen.set(true)" aria-label="More: open the full menu">
          <mat-icon aria-hidden="true">apps</mat-icon><span>More</span>
        </button>
      </div>
    }
  `,
  styles: `
    :host { display: block; }
    .frame { height: 100vh; height: 100dvh; width: 100%; background: transparent; --mat-sidenav-container-shape: 0; --mat-sidenav-content-background-color: transparent; }

    /* Floating glass sidebar */
    .rail { width: 250px; margin: 14px 0 14px 14px; height: calc(100% - 28px); border-radius: 26px !important;
            background: var(--ro-glass); backdrop-filter: var(--ro-blur); -webkit-backdrop-filter: var(--ro-blur);
            border: 1px solid var(--ro-glass-edge) !important; box-shadow: var(--ro-shadow-card), var(--ro-glass-sheen);
            color: var(--ro-ink); padding: var(--space-4) var(--space-3); }
    .frame.phone .rail, .frame:not(.phone) .rail.mat-drawer-over { margin: 0; height: 100%; border-radius: 0 26px 26px 0 !important; background: var(--ro-glass-strong); }
    .brand { display: flex; align-items: center; gap: var(--space-3); padding: var(--space-1) var(--space-2) var(--space-4); }
    .mark { display: grid; place-items: center; width: 40px; height: 40px; border-radius: 13px; background: var(--ro-accent-grad); color: var(--ro-on-accent); box-shadow: var(--ro-accent-glow); flex: none; }
    .names { display: flex; flex-direction: column; min-width: 0; }
    .logo { font-family: var(--ro-font-display); font-weight: 600; font-size: 1.05rem; letter-spacing: -0.02em; color: var(--ro-ink); }
    .org { font-size: 0.78rem; color: var(--ro-mute); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    nav { display: flex; flex-direction: column; gap: var(--space-1); padding-bottom: var(--space-4); }
    .sec-head { all: unset; box-sizing: border-box; display: flex; align-items: center; justify-content: space-between; width: 100%;
                padding: var(--space-3) var(--space-3) var(--space-1); font-size: 0.72rem; font-weight: 600; letter-spacing: .06em;
                text-transform: uppercase; color: var(--ro-mute); cursor: pointer; border-radius: 8px; }
    .sec-head:focus-visible { outline: 2px solid var(--ro-accent); }
    .sec-head mat-icon { font-size: 18px; width: 18px; height: 18px; transition: transform 200ms ease; }
    .sec.folded .sec-head mat-icon { transform: rotate(-90deg); }
    .sec.folded .sec-items { display: none; }
    .sec-items { display: flex; flex-direction: column; gap: 2px; }
    nav a { display: flex; align-items: center; gap: var(--space-3); padding: 8px var(--space-3); border-radius: 12px; color: var(--ro-ink-soft);
            text-decoration: none; font-weight: 500; transition: background-color 150ms ease, color 150ms ease; min-height: 40px; box-sizing: border-box; }
    nav a mat-icon { font-size: 20px; width: 20px; height: 20px; color: var(--ro-mute); }
    nav a:hover { background: var(--ro-hover); color: var(--ro-ink); }
    nav a.active { background: var(--ro-accent-grad); color: var(--ro-on-accent); box-shadow: var(--ro-accent-glow); }
    nav a.active mat-icon { color: var(--ro-on-accent); }

    .frame.wide mat-sidenav-content { padding-left: 18px; }

    /* Top bar */
    .bar { position: sticky; top: 0; z-index: 3; display: flex; align-items: center; gap: var(--space-3); padding: var(--space-3) var(--space-6);
           min-height: 60px; box-sizing: border-box; transition: background-color 200ms ease, box-shadow 200ms ease; }
    .bar.scrolled { background: var(--ro-glass-strong); backdrop-filter: var(--ro-blur); -webkit-backdrop-filter: var(--ro-blur);
                    box-shadow: 0 1px 0 var(--ro-line); }
    .prop { font-weight: 600; font-size: 1rem; color: var(--ro-ink); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 260px; }
    .prop.static { padding: 0 var(--space-2); }
    .clock { display: flex; align-items: baseline; white-space: nowrap; gap: var(--space-2); padding: var(--space-1) var(--space-3); border-left: 1px solid var(--ro-line-strong); }
    .clock .day { color: var(--ro-mute); }
    .clock .time { font-size: 1.1rem; font-weight: 600; color: var(--ro-ink); }
    .clock .tz { font-size: 0.75rem; color: var(--ro-mute); }
    .grow { flex: 1; }
    .search { display: flex; align-items: center; gap: var(--space-2); height: 40px; padding: 0 var(--space-2) 0 var(--space-3); min-width: 260px;
              border-radius: 999px; border: 1px solid var(--ro-line-strong); background: var(--ro-glass-strong); color: var(--ro-mute); font: inherit; cursor: text; }
    .search:hover { border-color: var(--ro-accent); }
    .search mat-icon { font-size: 20px; width: 20px; height: 20px; }
    .search .ph { flex: 1; text-align: left; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; font-size: .9rem; }
    .search kbd { font: inherit; font-size: .7rem; border: 1px solid var(--ro-line-strong); border-radius: 6px; padding: 1px 6px; }
    .user { height: auto; padding: var(--space-1) var(--space-2); border-radius: 999px; }
    .initials { display: inline-grid; place-items: center; width: 34px; height: 34px; border-radius: 50%; background: var(--ro-coral-fill); color: var(--ro-on-coral); font-size: 0.8rem; font-weight: 700; margin-right: var(--space-2); }
    .who { display: inline-flex; flex-direction: column; text-align: left; line-height: 1.2; color: var(--ro-ink); }
    .who small { color: var(--ro-mute); font-size: 0.75rem; }
    .content { display: block; animation: ro-enter 450ms cubic-bezier(.2,.8,.2,1); }
    @keyframes ro-enter { from { transform: translateY(10px); } to { transform: none; } }

    @media (max-width: 1200px) { .search { min-width: 0; } .search .ph, .search kbd { display: none; } .search { width: 40px; padding: 0; justify-content: center; } }
    @media (max-width: 760px) {
      .bar { padding: 6px var(--space-2); gap: var(--space-1); background: var(--ro-glass-strong); backdrop-filter: var(--ro-blur); -webkit-backdrop-filter: var(--ro-blur); box-shadow: 0 1px 0 var(--ro-line); }
      .prop { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: .95rem; }
      .grow { display: none; }
      .clock { padding: 0 var(--space-2); }
      .clock .day, .clock .tz, .who { display: none; }
      .search { border: 0; background: transparent; }
      .initials { margin-right: 0; }
      .user { min-width: 0; padding: 0 2px; }
      .content { padding-bottom: calc(84px + env(safe-area-inset-bottom)); }
    }

    /* Phone tab bar */
    .tabbar { position: fixed; z-index: 5; left: 10px; right: 10px; bottom: calc(10px + env(safe-area-inset-bottom)); display: flex; justify-content: space-around;
              padding: 6px; border-radius: 22px; background: var(--ro-glass-strong); backdrop-filter: var(--ro-blur); -webkit-backdrop-filter: var(--ro-blur);
              border: 1px solid var(--ro-glass-edge); box-shadow: var(--ro-shadow-card-lift); }
    .tab { all: unset; box-sizing: border-box; flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 2px;
           min-height: 52px; border-radius: 16px; color: var(--ro-mute); font-size: .7rem; font-weight: 600; cursor: pointer; text-align: center; }
    .tab mat-icon { font-size: 22px; width: 22px; height: 22px; }
    .tab.on { color: var(--ro-accent-strong); background: var(--ro-accent-soft); }
    .tab:focus-visible { outline: 2px solid var(--ro-accent); }

    /* Theme picker inside the account menu */
    .theme-pick { padding: var(--space-3) var(--space-4) var(--space-2); display: flex; flex-direction: column; gap: var(--space-2); max-width: 280px; }
    .theme-pick .lbl { font-size: .75rem; font-weight: 600; color: var(--ro-mute); letter-spacing: .04em; }
    .theme-pick mat-icon { font-size: 18px; width: 18px; height: 18px; vertical-align: -4px; }
    .theme-pick .tl { margin-left: 4px; font-size: .82rem; }
    .theme-pick mat-slide-toggle { font-size: .82rem; }
  `,
})
export class Shell {
  auth = inject(AuthService);
  props = inject(PropertyContext);
  install = inject(InstallPrompt);
  theme = inject(ThemeService);
  private dialog = inject(MatDialog);
  themeLabels = THEME_LABELS;
  themeIcons = THEME_ICONS;
  themeChoices: ThemeChoice[] = ['light', 'dark', 'system'];
  navOpen = signal(false);
  private bp = inject(BreakpointObserver);
  /** Phone layout: tab bar at the bottom, no sidebar. */
  mobile = toSignal(this.bp.observe('(max-width: 760px)').pipe(map(r => r.matches)), { initialValue: false });
  /** Wide enough to keep the sidebar open beside the page. */
  wide = toSignal(this.bp.observe('(min-width: 1024px)').pipe(map(r => r.matches)), { initialValue: true });

  private now = signal(new Date());

  sections = computed<NavSection[]>(() => {
    this.auth.me();
    return NAV.map(sec => ({ ...sec, items: sec.items.filter(i => !i.perm || this.auth.canAnywhere(i.perm)) }))
      .filter(sec => sec.items.length > 0);
  });
  /** Every link the person may open, in menu order. */
  nav = computed<NavItem[]>(() => this.sections().flatMap(s => s.items));
  tabs = computed<NavItem[]>(() => {
    const mine = this.nav();
    return TAB_PREFERENCE.map(l => mine.find(i => i.link === l)).filter((i): i is NavItem => !!i).slice(0, 4);
  });
  tabLabel = (i: NavItem) => TAB_LABELS[i.link] ?? i.label;

  folded = signal<Set<string>>(this.readFolded());
  scrolled = signal(false);

  fold(id: string) {
    const next = new Set(this.folded());
    if (next.has(id)) next.delete(id); else next.add(id);
    this.folded.set(next);
    try { localStorage.setItem(FOLDED_KEY, JSON.stringify([...next])); } catch { /* storage blocked: lasts until reload */ }
  }
  private readFolded(): Set<string> {
    try { return new Set(JSON.parse(localStorage.getItem(FOLDED_KEY) ?? '[]') as string[]); } catch { return new Set(); }
  }

  onScroll(e: Event) { this.scrolled.set((e.target as HTMLElement).scrollTop > 4); }

  openSearch() {
    if (this.dialog.openDialogs.some(d => d.componentInstance instanceof SearchDialog)) return;
    this.dialog.open(SearchDialog, { position: { top: '12vh' }, autoFocus: false, panelClass: 'search-panel', maxWidth: '96vw' });
  }

  @HostListener('document:keydown', ['$event'])
  shortcut(e: KeyboardEvent) {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); this.openSearch(); }
  }

  localDay = computed(() => { const p = this.props.current(); return p ? inZone(this.now(), p.timezone, { weekday: 'short', day: 'numeric', month: 'short' }) : ''; });
  localTime = computed(() => { const p = this.props.current(); return p ? inZone(this.now(), p.timezone, { hour: '2-digit', minute: '2-digit', hour12: false }) : ''; });
  tzLabel = computed(() => { const p = this.props.current(); return p?.timezone === 'Asia/Kolkata' ? 'IST' : p?.timezone ?? ''; });
  initials = computed(() => (this.auth.me()?.fullName ?? '').split(/\s+/).map(s => s[0]).slice(0, 2).join('').toUpperCase());

  constructor() {
    // A new screen starts at the top with a plain top bar.
    inject(Router).events.pipe(takeUntilDestroyed()).subscribe(e => { if (e instanceof NavigationEnd) this.scrolled.set(false); });
    const t = setInterval(() => this.now.set(new Date()), 15_000);
    inject(DestroyRef).onDestroy(() => clearInterval(t));
  }
}
