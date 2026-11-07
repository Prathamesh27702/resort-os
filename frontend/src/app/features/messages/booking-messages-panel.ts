import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { MessageListItem } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { MessageDialog } from './message-dialogs';
import { CHANNEL, CHANNEL_ICON, MessagesApi, statusClass } from './messages.api';

/**
 * A booking's messages (Sprint 24): the confirmation and reminders sent to the guest, each with what became of it (read,
 * delivered, failed and why, or why it wasn't sent). Opening one shows its words and its steps; a failed one can be sent again.
 */
@Component({
  selector: 'app-booking-messages-panel',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatIconModule],
  template: `
    <section class="panel card" aria-label="Messages to the guest" data-testid="booking-messages">
      <div class="head">
        <h2>Messages to the guest</h2>
        <span class="grow"></span>
        <button mat-icon-button (click)="load()" aria-label="Refresh messages" title="Refresh"><mat-icon>refresh</mat-icon></button>
      </div>
      @for (m of items(); track m.id) {
        <button type="button" class="line" (click)="open(m)" [attr.data-status]="m.status" [attr.data-template]="m.templateCode">
          <mat-icon class="ch" [attr.aria-label]="channel[m.channel]">{{ icon[m.channel] }}</mat-icon>
          <span class="txt"><span class="top"><b>{{ m.templateName }}</b> <small class="mute">{{ channel[m.channel] }} · {{ time(m.createdAt) }}</small></span>
            @if (m.reason) { <small [class.bad]="m.status === 'FAILED'" class="why">{{ m.reason }}</small> }</span>
          <span [class]="'chip ' + cls(m.status)">{{ m.statusWords }}</span>
        </button>
      } @empty {
        <p class="mute">Nothing sent yet. The confirmation goes when the booking is confirmed, and a reminder the day before arrival.</p>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      <a routerLink="/messages" class="all">All messages</a>
    </section>
  `,
  styles: `
    .card { padding: 16px 20px; margin-bottom: 16px; }
    .head { display: flex; align-items: center; gap: 8px; } .grow { flex: 1; }
    h2 { font-size: 1rem; color: #0e3b43; margin: 0; }
    .line { display: flex; gap: 10px; align-items: center; width: 100%; border: 0; border-top: 1px solid #edf1f0; background: none; padding: 8px 0;
            font: inherit; text-align: left; cursor: pointer; color: inherit; }
    .ch { flex: none; color: #0e3b43; }
    .txt { flex: 1 1 0; min-width: 0; display: flex; flex-direction: column; }
    .line .chip { flex: none; margin: 0; }
    .top { display: flex; gap: 6px; flex-wrap: wrap; align-items: baseline; }
    .mute { color: #5b6b6e; } .why { color: #5b6b6e; } .bad, .err { color: #b3261e; }
    .all { display: inline-block; margin-top: 8px; font-size: .9rem; }
    .chip.ok { background: #e1f2e8; color: #1d5b36; } .chip.wait { background: #fbefd8; color: #8a5a00; }
    .chip.off { background: #eceff0; color: #5b6b6e; } .chip.bad { background: #f3e3e3; color: #7a1f1f; }
  `,
})
export class BookingMessagesPanel implements OnInit {
  reservationId = input.required<string>();
  private api = inject(MessagesApi);
  private props = inject(PropertyContext);
  private dialog = inject(MatDialog);
  private auth = inject(AuthService);
  items = signal<MessageListItem[]>([]);
  error = signal('');
  channel = CHANNEL; icon = CHANNEL_ICON; cls = statusClass;
  private pid = () => this.props.current()!.id;
  private canSend = computed(() => { this.auth.me(); return this.auth.can('message.send', this.props.current()?.id); });

  ngOnInit() { this.load(); }

  load() {
    this.api.list(this.pid(), { reservationId: this.reservationId(), pageSize: 50 }).subscribe({
      next: l => { this.items.set(l.items); this.error.set(''); },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  time(iso: string) { return inZone(iso, this.props.current()?.timezone ?? 'Asia/Kolkata', { day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit' }); }

  open(m: MessageListItem) {
    this.api.get(this.pid(), m.id).subscribe({
      next: full => this.dialog.open(MessageDialog, {
        data: { propertyId: this.pid(), timezone: this.props.current()?.timezone ?? 'Asia/Kolkata', message: full, canSend: this.canSend() },
        maxWidth: '100vw', autoFocus: false,
      }).afterClosed().subscribe(changed => { if (changed) this.load(); }),
      error: e => this.error.set(toProblem(e).detail),
    });
  }
}
