import { Component, DestroyRef, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MatBadgeModule } from '@angular/material/badge';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { AppNotification, NotificationList } from '../core/models';
import { PropertyContext, inZone } from '../core/property-context';
import { MessagesApi } from '../features/messages/messages.api';

/** How often the bell asks for new lines (a minute: the daily jobs and failed messages are not urgent to the second). */
const POLL_MS = 60_000;

/**
 * The bell in the header (Sprint 24): the signed-in person's own notifications, newest first: low stock, leave waiting for a
 * decision, a guest message that couldn't be sent. Opening one marks it read and goes to its page.
 */
@Component({
  selector: 'app-bell',
  imports: [MatBadgeModule, MatButtonModule, MatIconModule, MatMenuModule],
  template: `
    <button mat-icon-button [matMenuTriggerFor]="menu" (menuOpened)="load()" data-testid="bell"
            [attr.aria-label]="data().unread ? data().unread + ' unread notifications' : 'Notifications'">
      <mat-icon [matBadge]="data().unread || null" matBadgeColor="warn" matBadgeSize="small" [matBadgeHidden]="!data().unread"
                aria-hidden="false">notifications</mat-icon>
    </button>
    <mat-menu #menu="matMenu" class="bell-menu" xPosition="before">
      <div class="bhead" (click)="$event.stopPropagation()" (keydown)="$event.stopPropagation()" role="presentation">
        <b>Notifications</b>
        @if (data().unread) { <button mat-button (click)="readAll()">Mark all read</button> }
      </div>
      @for (n of data().items; track n.id) {
        <button mat-menu-item class="bitem" [class.unread]="!n.readAt" (click)="open(n)" [attr.data-notification]="n.title">
          <span class="bt"><b>{{ n.title }}</b><small>{{ time(n.createdAt) }}</small></span>
          <span class="bb">{{ n.body }}</span>
        </button>
      } @empty {
        <p class="bnone">Nothing yet. Low stock, leave waiting for you and messages that couldn't be sent show here.</p>
      }
    </mat-menu>
  `,
  styles: `
    :host { display: inline-flex; }
    ::ng-deep .bell-menu { max-width: min(380px, 94vw) !important; }
    ::ng-deep .bell-menu .bhead { display: flex; justify-content: space-between; align-items: center; padding: var(--space-1) var(--space-3) var(--space-1) var(--space-4); }
    ::ng-deep .bell-menu .bitem { height: auto !important; min-height: 56px; padding: var(--space-2) var(--space-4); line-height: 1.3; white-space: normal; }
    ::ng-deep .bell-menu .bitem .mat-mdc-menu-item-text { display: flex; flex-direction: column; gap: 2px; }
    ::ng-deep .bell-menu .bitem.unread { background: #eef6f5; }
    ::ng-deep .bell-menu .bitem.unread b::before { content: ''; display: inline-block; width: 8px; height: 8px; border-radius: 50%; background: var(--ro-kokum); margin-right: 6px; }
    ::ng-deep .bell-menu .bt { display: flex; justify-content: space-between; gap: var(--space-2); }
    ::ng-deep .bell-menu .bt small { color: var(--ro-mute); white-space: nowrap; }
    ::ng-deep .bell-menu .bb { color: var(--ro-ink-soft); font-size: .85rem; }
    ::ng-deep .bell-menu .bnone { color: var(--ro-mute); padding: var(--space-2) var(--space-4); margin: 0; font-size: .9rem; }
  `,
})
export class Bell {
  private api = inject(MessagesApi);
  private router = inject(Router);
  private props = inject(PropertyContext);
  data = signal<NotificationList>({ items: [], unread: 0 });

  constructor() {
    this.load();
    const t = setInterval(() => this.load(), POLL_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(t));
  }

  load() { this.api.mine().subscribe({ next: d => this.data.set(d), error: () => undefined }); }

  time(iso: string) { return inZone(iso, this.props.current()?.timezone ?? 'Asia/Kolkata', { day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit' }); }

  open(n: AppNotification) {
    if (!n.readAt) this.api.read(n.id).subscribe({ next: d => this.data.set(d), error: () => undefined });
    if (n.link) this.router.navigateByUrl(n.link);
  }

  readAll() { this.api.readAll().subscribe({ next: d => this.data.set(d), error: () => undefined }); }
}
