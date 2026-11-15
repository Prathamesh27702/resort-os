import { Component, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { Router } from '@angular/router';
import { MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { Subject, catchError, debounceTime, distinctUntilChanged, forkJoin, of, switchMap } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AuthService } from '../core/auth.service';
import { PropertyContext } from '../core/property-context';
import { GuestsApi } from '../features/guests/guests.api';
import { ReservationsApi } from '../features/reservations/reservations.api';
import { dayText } from '../core/dates';
import { NAV, NavItem } from './nav';

interface Hit { kind: 'page' | 'guest' | 'booking'; icon: string; title: string; sub: string; link: string; }

/**
 * One search box for the whole app (Ctrl K, or the search field in the top bar): screens by name, guests by name or
 * phone, bookings by reference or guest. Only what the person may open is searched.
 */
@Component({
  selector: 'app-search',
  imports: [MatIconModule],
  template: `
    <div class="box">
      <mat-icon aria-hidden="true">search</mat-icon>
      <input #field type="search" [value]="q()" (input)="type($any($event.target).value)" (keydown)="key($event)"
             role="combobox" aria-expanded="true" aria-autocomplete="list"
             placeholder="Search guests, bookings, screens" aria-label="Search guests, bookings and screens"
             aria-controls="search-hits" [attr.aria-activedescendant]="hits().length ? 'hit-' + active() : null" autocomplete="off">
      <kbd>Esc</kbd>
    </div>
    <ul id="search-hits" role="listbox" aria-label="Results">
      @for (h of hits(); track h.link + h.title; let i = $index) {
        <li role="option" [id]="'hit-' + i" [class.on]="i === active()" [attr.aria-selected]="i === active()"
            tabindex="-1" (click)="go(h)" (keydown.enter)="go(h)" (mouseenter)="active.set(i)">
          <span class="ic" [class]="h.kind"><mat-icon aria-hidden="true">{{ h.icon }}</mat-icon></span>
          <span class="t"><b>{{ h.title }}</b><small>{{ h.sub }}</small></span>
          <span class="k">{{ kindLabel[h.kind] }}</span>
        </li>
      } @empty {
        <li class="none">
          @if (loading()) { Searching… } @else if (q().trim().length < 2) { Type a guest's name or phone, a booking number like R00012, or a screen. }
          @else { Nothing matches "{{ q().trim() }}". Try part of a name, the last digits of a phone, or a booking number. }
        </li>
      }
    </ul>
    <p class="hint"><span><kbd>↑</kbd><kbd>↓</kbd> to move</span><span><kbd>Enter</kbd> to open</span></p>
  `,
  styles: `
    :host { display: block; width: min(640px, 92vw); }
    .box { display: flex; align-items: center; gap: var(--space-3); padding: var(--space-3) var(--space-4); border-bottom: 1px solid var(--ro-line); }
    .box mat-icon { color: var(--ro-mute); }
    input { flex: 1; border: 0; background: transparent; font: inherit; font-size: 1.05rem; color: var(--ro-ink); outline: none; min-width: 0; padding: var(--space-2) 0; }
    input::placeholder { color: var(--ro-mute); }
    kbd { font: inherit; font-size: .72rem; color: var(--ro-mute); border: 1px solid var(--ro-line-strong); border-radius: 6px; padding: 1px 6px; }
    ul { list-style: none; margin: 0; padding: var(--space-2); max-height: min(420px, 60vh); overflow: auto; }
    li { display: flex; align-items: center; gap: var(--space-3); padding: var(--space-2) var(--space-3); border-radius: var(--ro-radius-sm); cursor: pointer; min-height: 44px; }
    li.on { background: var(--ro-accent-soft); }
    li.none { cursor: default; color: var(--ro-mute); padding: var(--space-4); }
    .ic { display: grid; place-items: center; width: 34px; height: 34px; border-radius: 10px; background: var(--ro-sunk); color: var(--ro-accent-strong); flex: none; }
    .ic.guest { background: var(--ro-kokum-wash); color: var(--ro-kokum); }
    .ic.booking { background: var(--ro-info-bg); color: var(--ro-info-strong); }
    .ic mat-icon { font-size: 20px; width: 20px; height: 20px; }
    .t { display: flex; flex-direction: column; min-width: 0; flex: 1; }
    .t b { font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .t small { color: var(--ro-mute); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .k { font-size: .75rem; color: var(--ro-mute); }
    .hint { display: flex; gap: var(--space-4); margin: 0; padding: var(--space-2) var(--space-4); border-top: 1px solid var(--ro-line); font-size: .78rem; color: var(--ro-mute); }
    .hint span { display: inline-flex; gap: 4px; align-items: center; }
    @media (max-width: 640px) { .hint { display: none; } }
  `,
})
export class SearchDialog {
  private auth = inject(AuthService);
  private props = inject(PropertyContext);
  private guests = inject(GuestsApi);
  private bookings = inject(ReservationsApi);
  private router = inject(Router);
  private ref = inject(MatDialogRef<SearchDialog>);
  private field = viewChild<ElementRef<HTMLInputElement>>('field');

  kindLabel = { page: 'Screen', guest: 'Guest', booking: 'Booking' };
  q = signal('');
  active = signal(0);
  loading = signal(false);
  private found = signal<Hit[]>([]);
  private typed = new Subject<string>();

  private pages = computed<NavItem[]>(() => {
    this.auth.me();
    return NAV.flatMap(s => s.items).filter(i => !i.perm || this.auth.canAnywhere(i.perm));
  });

  hits = computed<Hit[]>(() => {
    const t = this.q().trim().toLowerCase();
    if (!t) return this.pages().slice(0, 6).map(p => this.pageHit(p));
    const pages = this.pages()
      .filter(p => `${p.label} ${p.keywords ?? ''}`.toLowerCase().includes(t))
      .slice(0, 5).map(p => this.pageHit(p));
    return [...this.found(), ...pages];
  });

  constructor() {
    this.typed.pipe(
      debounceTime(220), distinctUntilChanged(),
      switchMap(t => {
        if (t.length < 2) { this.loading.set(false); return of<Hit[]>([]); }
        this.loading.set(true);
        const pid = this.props.current()?.id;
        const g$ = this.auth.canAnywhere('guest.read')
          ? this.guests.list(t, { pageSize: 5 }).pipe(catchError(() => of({ items: [] })))
          : of({ items: [] });
        const r$ = pid && this.auth.canAnywhere('reservation.read')
          ? this.bookings.list(pid, { q: t, pageSize: 5 }).pipe(catchError(() => of({ items: [] })))
          : of({ items: [] });
        return forkJoin([g$, r$]).pipe(switchMap(([g, r]) => of<Hit[]>([
          ...r.items.map(b => ({
            kind: 'booking' as const, icon: 'event_available', title: `${b.guestName}, ${b.reference}`,
            sub: `${dayText(b.checkIn)} to ${dayText(b.checkOut)}, ${b.rooms || 'no room yet'}, ${b.status.toLowerCase().replace(/_/g, ' ')}`,
            link: `/reservations/${b.id}`,
          })),
          ...g.items.map(x => ({
            kind: 'guest' as const, icon: 'person', title: x.fullName + (x.isVip ? ' (VIP)' : ''),
            sub: [x.phone, x.email, x.city].filter(Boolean).join(', ') || 'No phone or email', link: `/guests/${x.id}`,
          })),
        ])));
      }),
      takeUntilDestroyed(),
    ).subscribe(h => { this.found.set(h); this.loading.set(false); this.active.set(0); });
    setTimeout(() => this.field()?.nativeElement.focus());
  }

  type(v: string) { this.q.set(v); this.active.set(0); this.typed.next(v.trim()); }

  key(e: KeyboardEvent) {
    const n = this.hits().length;
    if (e.key === 'ArrowDown') { e.preventDefault(); this.active.set(n ? (this.active() + 1) % n : 0); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); this.active.set(n ? (this.active() - 1 + n) % n : 0); }
    else if (e.key === 'Enter') { const h = this.hits()[this.active()]; if (h) { e.preventDefault(); this.go(h); } }
  }

  go(h: Hit) { this.ref.close(); this.router.navigateByUrl(h.link); }

  private pageHit(p: NavItem): Hit {
    const section = NAV.find(s => s.items.includes(p))?.title ?? '';
    return { kind: 'page', icon: p.icon, title: p.label, sub: section, link: p.link };
  }
}
