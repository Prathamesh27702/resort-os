import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { showFieldErrors, toProblem } from '../../core/http';
import { BookingPaymentLinks, PaymentLink } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { newKey } from '../reservations/reservations.api';
import { minTrimmed } from '../rooms/rooms.forms';
import { rupeesExact } from '../settings/rates/rates.api';
import { PaymentsApi, linkStatusClass, linkStatusLabel, looksLikeCardNumber, payUrl } from './payments.api';

/**
 * A booking's Razorpay payment links (Sprint 11): what it owes, the links sent and what became of them. Staff send a
 * link for an amount; the guest pays on their own page with UPI or a card; Razorpay's webhook records the payment, puts it
 * on the bill and confirms a pending booking. Nothing here marks a link paid.
 */
@Component({
  selector: 'app-payment-links-panel',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    @if (data(); as d) {
      <section class="panel card" aria-label="Payment links" data-testid="payment-links">
        <div class="head">
          <h2>Online payment links</h2>
          <span class="sums">Owes <b class="num" data-testid="owed">{{ money(d.owed) }}</b> of {{ money(d.due) }}
            @if (d.open > 0) { · links ask for <b class="num">{{ money(d.open) }}</b> }</span>
          <button mat-icon-button (click)="load()" aria-label="Refresh payment links" title="Refresh"><mat-icon>refresh</mat-icon></button>
          @if (canCreate()) {
            <button mat-flat-button (click)="send(d)" [disabled]="!d.canCreate" [title]="d.cantCreateReason ?? ''" data-testid="send-link">
              <mat-icon>link</mat-icon>Send payment link</button>
          }
        </div>
        @if (!d.canCreate && d.cantCreateReason && canCreate()) { <p class="hint">{{ d.cantCreateReason }}</p> }
        @if (d.fake) {
          <p class="hint" data-testid="fake-note">Test mode: no Razorpay keys are set, so links use the built-in test Razorpay and no real money moves.</p>
        }
        @for (l of d.links; track l.id) {
          <div class="link" [attr.data-link]="l.id">
            <div class="main">
              <span><b class="num">{{ money(l.amount) }}</b> <span [class]="'chip ' + statusClass(l.status)">{{ statusLabel(l.status) }}</span>
                @if (l.status === 'CREATED' && l.callbackAt) { <span class="chip wait">Guest says paid; waiting for Razorpay</span> }
                <br /><small class="mute">Sent {{ time(l.createdAt) }}@if (l.createdByName) { by {{ l.createdByName }} }
                  @if (l.note) { · {{ l.note }} }</small>
                @if (l.status === 'PAID' && l.paymentId) {
                  <br /><small>Paid {{ time(l.paidAt!) }} ·
                    <a [routerLink]="['/payments', l.paymentId]" class="num">{{ l.paymentNumber }}</a></small>
                }
                @if (l.status === 'CANCELLED') {
                  <br /><small class="mute">Cancelled {{ time(l.cancelledAt!) }}@if (l.cancelledByName) { by {{ l.cancelledByName }} }: {{ l.cancelReason }}</small>
                }
                @if (l.status === 'CREATED' && l.lastError) {
                  <br /><small class="bad">Last try failed ({{ l.attempts }} so far): {{ l.lastError }}</small>
                }
              </span>
              <span class="actions">
                @if (l.status === 'CREATED') {
                  <button mat-stroked-button (click)="copy(l)"><mat-icon>content_copy</mat-icon>Copy link</button>
                  <a mat-button [href]="whatsApp(l)" target="_blank" rel="noopener">WhatsApp</a>
                  <a mat-button [href]="l.payPath" target="_blank" rel="noopener">Open</a>
                  @if (canCreate()) { <button mat-button class="danger" (click)="cancel(l)">Cancel</button> }
                }
                <button mat-button (click)="toggle(l)" [attr.aria-expanded]="shown() === l.id">History</button>
              </span>
            </div>
            @if (shown() === l.id) {
              <div class="events" data-testid="link-events">
                <small class="mute">Razorpay order <span class="num">{{ l.gatewayOrderId }}</span></small>
                @for (e of events(); track e.id) {
                  <p><b>{{ e.eventType }}</b> <span [class]="'chip ' + (e.status === 'PROCESSED' ? 'ok' : e.status === 'FAILED' ? 'bad' : 'off')">{{ eventLabel(e.status) }}</span>
                    <small class="mute"> {{ time(e.receivedAt) }}@if (e.attempts > 1) { · {{ e.attempts }} deliveries }</small>
                    @if (e.outcome) { <br /><small>{{ e.outcome }}</small> }</p>
                } @empty { <p class="mute">Nothing from Razorpay yet.</p> }
              </div>
            }
          </div>
        } @empty {
          <p class="mute">No links sent. Send one so the guest can pay before arrival with UPI or a card.</p>
        }
        @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      </section>
    } @else if (error()) { <p class="err" role="alert">{{ error() }}</p> }
  `,
  styles: `
    .card { padding: 16px 20px; margin-bottom: 16px; }
    .head { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    h2 { font-size: 1rem; color: #0e3b43; margin: 0; }
    .sums { flex: 1; color: #5b6b6e; }
    .sums b { color: #0e3b43; }
    .link { border-top: 1px solid #edf1f0; padding: 10px 0; }
    .main { display: flex; justify-content: space-between; gap: 12px; flex-wrap: wrap; align-items: center; }
    .actions { display: flex; gap: 4px; flex-wrap: wrap; }
    .events { background: #f6f8f7; border-radius: 8px; padding: 8px 12px; margin-top: 8px; }
    .events p { margin: 6px 0; }
    .hint { color: #5b6b6e; font-size: .85rem; margin: 8px 0; }
    .mute { color: #5b6b6e; }
    .bad, .danger, .err { color: #b3261e; }
    .chip.ok { background: #e1f2e8; color: #1d5b36; }
    .chip.wait { background: #fbefd8; color: #8a5a00; }
    .chip.off { background: #eceff0; color: #5b6b6e; }
    .chip.bad { background: #f3e3e3; color: #7a1f1f; }
  `,
})
export class PaymentLinksPanel implements OnInit {
  reservationId = input.required<string>();
  /** A link was sent or cancelled, or a payment came in: the booking page reloads (its status and bill may have changed). */
  changed = output<void>();
  private api = inject(PaymentsApi);
  private props = inject(PropertyContext);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  private auth = inject(AuthService);

  data = signal<BookingPaymentLinks | null>(null);
  error = signal('');
  shown = signal<string | null>(null);
  events = signal<NonNullable<PaymentLink['events']>>([]);
  canCreate = computed(() => { this.auth.me(); return this.auth.can('payment.create', this.props.current()?.id); });
  money = rupeesExact;
  statusLabel = linkStatusLabel;
  statusClass = linkStatusClass;
  private pid = () => this.props.current()!.id;

  ngOnInit() { this.load(); }

  load() {
    const before = this.data()?.paid;
    this.api.links(this.pid(), this.reservationId()).subscribe({
      next: d => {
        this.data.set(d); this.error.set('');
        if (before !== undefined && before !== d.paid) this.changed.emit();
        const open = this.shown();
        if (open) this.loadEvents(open);
      },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  send(d: BookingPaymentLinks) {
    this.dialog.open(SendPaymentLinkDialog, { data: { propertyId: this.pid(), links: d } satisfies SendLinkData, autoFocus: 'first-tabbable' })
      .afterClosed().subscribe((l?: PaymentLink) => {
        if (!l) return;
        this.load(); this.changed.emit();
        void this.copy(l, `Link for ${rupeesExact(l.amount)} created and copied. Send it to the guest.`);
      });
  }

  cancel(l: PaymentLink) {
    this.dialog.open(CancelPaymentLinkDialog, { data: { propertyId: this.pid(), link: l } satisfies CancelLinkData })
      .afterClosed().subscribe((x?: PaymentLink) => {
        if (!x) return;
        this.load(); this.changed.emit();
        this.snack.open(`Link for ${rupeesExact(x.amount)} cancelled; it can't be paid any more`, undefined, { duration: 3000 });
      });
  }

  async copy(l: PaymentLink, message = 'Link copied. Paste it in a message to the guest.') {
    const url = payUrl(l.payPath);
    try {
      await navigator.clipboard.writeText(url);
      this.snack.open(message, undefined, { duration: 3000 });
    } catch {
      this.snack.open(`Copy this link to the guest: ${url}`, 'Close');
    }
  }

  whatsApp(l: PaymentLink) {
    const text = `Please pay ${rupeesExact(l.amount)} for booking ${l.reference} here: ${payUrl(l.payPath)}`;
    return `https://wa.me/?text=${encodeURIComponent(text)}`;
  }

  toggle(l: PaymentLink) {
    if (this.shown() === l.id) { this.shown.set(null); return; }
    this.shown.set(l.id); this.events.set([]);
    this.loadEvents(l.id);
  }

  private loadEvents(id: string) {
    this.api.link(this.pid(), id).subscribe({ next: x => this.events.set(x.events ?? []), error: e => this.error.set(toProblem(e).detail) });
  }

  eventLabel(s: string) { return s === 'PROCESSED' ? 'Done' : s === 'IGNORED' ? 'Nothing to do' : 'Failed; Razorpay will retry'; }
  time(iso: string) { return inZone(iso, this.props.current()!.timezone, { dateStyle: 'medium', timeStyle: 'short' }); }
}

// ------------------------------------------------------------------ send a link

export interface SendLinkData { propertyId: string; links: BookingPaymentLinks; }

/** Creates a payment link for an amount (at most what the booking owes minus open links; ₹1 or more). One
 *  Idempotency-Key per dialog, so a double click makes one link. */
@Component({
  selector: 'app-send-payment-link-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Send a payment link · <span class="num">{{ data.links.reference }}</span></h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="link-form">
      <p class="hint">The guest opens the link and pays with UPI or a card through Razorpay. The payment goes on the bill by itself
        when Razorpay confirms it, and a pending booking is confirmed. Owes <b class="num">{{ money(data.links.owed) }}</b>
        @if (data.links.open > 0) {; open links already ask for <b class="num">{{ money(data.links.open) }}</b> }.</p>
      <mat-form-field appearance="outline">
        <mat-label>Amount (₹)</mat-label>
        <input matInput type="number" formControlName="amount" min="1" step="0.01" [max]="max" />
        <mat-hint>Up to {{ money(max) }}, e.g. an advance of {{ money(advance) }}</mat-hint>
        <mat-error>{{ err('amount') || amountMessage() }}</mat-error>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Note for the guest</mat-label>
        <input matInput formControlName="note" maxlength="500" placeholder="Optional, e.g. Advance to hold your booking" autocomplete="off" />
        <mat-error>{{ err('note') || noteMessage() }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="link-form" [disabled]="busy()">Create link</button>
    </div>
  `,
  styles: `
    form { min-width: min(460px, 82vw); display: flex; flex-direction: column; }
    .hint { color: #5b6b6e; margin: 0 0 12px; }
    .full { width: 100%; margin-top: 8px; }
    .err { color: #b3261e; }
  `,
})
export class SendPaymentLinkDialog {
  data = inject<SendLinkData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<SendPaymentLinkDialog, PaymentLink>);
  private api = inject(PaymentsApi);
  private key = newKey();
  money = rupeesExact;
  max = Math.max(0, Math.round((this.data.links.owed - this.data.links.open) * 100) / 100);
  /** A suggestion for the hint: half of what is left, rounded to ₹100 (at least ₹1). */
  advance = Math.max(1, Math.round(this.max / 2 / 100) * 100);
  busy = signal(false);
  error = signal('');
  private serverErrors = signal<Record<string, string>>({});

  form = inject(FormBuilder).nonNullable.group({
    amount: [this.max as number | null, [Validators.required, Validators.min(1), Validators.max(this.max)]],
    note: ['', [Validators.maxLength(500)]],
  });

  err(field: string) { return this.serverErrors()[field] ?? ''; }
  amountMessage() {
    const c = this.form.controls.amount;
    if (c.hasError('required')) return 'Enter the amount the guest should pay.';
    if (c.hasError('min')) return 'The amount must be at least ₹1, Razorpay’s smallest payment.';
    if (c.hasError('max')) return `The link can be for at most ${rupeesExact(this.max)}: what ${this.data.links.reference} still owes, less open links.`;
    return '';
  }
  noteMessage() {
    return this.form.controls.note.hasError('card') ? 'This looks like a card number, and card details are never stored. Take it out of the note.'
      : 'The note can be at most 500 characters.';
  }

  save() {
    this.error.set(''); this.serverErrors.set({});
    if (looksLikeCardNumber(this.form.controls.note.value)) this.form.controls.note.setErrors({ card: true });
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.api.createLink(this.data.propertyId, { reservationId: this.data.links.reservationId, amount: Number(v.amount), note: v.note.trim() || null }, this.key)
      .subscribe({
        next: l => this.ref.close(l),
        error: e => {
          this.busy.set(false);
          const p = toProblem(e);
          const f = showFieldErrors(this.form, p);
          this.serverErrors.set(f);
          if (!Object.keys(f).some(k => this.form.get(k))) this.error.set(Object.values(f)[0] ?? p.detail);
        },
      });
  }
}

// ------------------------------------------------------------------ cancel a link

export interface CancelLinkData { propertyId: string; link: PaymentLink; }

/** Cancels a link the guest hasn't paid, with a reason. If the guest pays anyway, the money is still recorded. */
@Component({
  selector: 'app-cancel-payment-link-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Cancel the link for {{ money(data.link.amount) }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="cancel-link-form">
      <p class="hint">The guest's page will say the link was cancelled. If they already paid on it, Razorpay still tells us and the
        money is recorded.</p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Reason</mat-label>
        <input matInput formControlName="reason" maxlength="500" placeholder="e.g. Guest will pay at the desk" />
        <mat-error>{{ serverError() || 'Say why the link is cancelled (at least 3 characters), e.g. Guest will pay at the desk.' }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Keep it</button>
      <button mat-flat-button color="warn" type="submit" form="cancel-link-form" [disabled]="busy()">Cancel link</button>
    </div>
  `,
  styles: `
    form { min-width: min(440px, 82vw); }
    .hint { color: #5b6b6e; margin: 0 0 12px; }
    .full { width: 100%; }
    .err { color: #b3261e; }
  `,
})
export class CancelPaymentLinkDialog {
  data = inject<CancelLinkData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<CancelPaymentLinkDialog, PaymentLink>);
  private api = inject(PaymentsApi);
  money = rupeesExact;
  busy = signal(false);
  error = signal('');
  serverError = signal('');
  form = inject(FormBuilder).nonNullable.group({ reason: ['', [minTrimmed(3), Validators.maxLength(500)]] });

  save() {
    this.error.set(''); this.serverError.set('');
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.api.cancelLink(this.data.propertyId, this.data.link.id, this.form.controls.reason.value.trim()).subscribe({
      next: l => this.ref.close(l),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const f = showFieldErrors(this.form, p);
        if (f['reason']) this.serverError.set(f['reason']); else this.error.set(Object.values(f)[0] ?? p.detail);
      },
    });
  }
}
