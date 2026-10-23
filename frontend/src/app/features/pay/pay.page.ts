import { Component, OnDestroy, OnInit, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { toProblem } from '../../core/http';
import { PayPage } from '../../core/models';
import { PayPageApi } from '../payments/payments.api';
import { dayLabel, rupeesExact } from '../settings/rates/rates.api';

/** What Razorpay Checkout's script puts on window (https://razorpay.com/docs/payments/payment-gateway/web-integration/standard/). */
interface RazorpayCheckout { open(): void; on(event: 'payment.failed', handler: (r: { error?: { description?: string } }) => void): void; }
type RazorpayCtor = new (options: Record<string, unknown>) => RazorpayCheckout;
declare global { interface Window { Razorpay?: RazorpayCtor; } }

export const CHECKOUT_SCRIPT = 'https://checkout.razorpay.com/v1/checkout.js';

/** Loads Razorpay Checkout's script once. */
let checkoutLoading: Promise<RazorpayCtor> | null = null;
function loadCheckout(): Promise<RazorpayCtor> {
  if (window.Razorpay) return Promise.resolve(window.Razorpay);
  checkoutLoading ??= new Promise<RazorpayCtor>((resolve, reject) => {
    const s = document.createElement('script');
    s.src = CHECKOUT_SCRIPT;
    s.async = true;
    s.onload = () => (window.Razorpay ? resolve(window.Razorpay) : reject(new Error('no Razorpay')));
    s.onerror = () => { checkoutLoading = null; reject(new Error('blocked')); };
    document.head.appendChild(s);
  });
  return checkoutLoading;
}

/**
 * The guest's pay page (Sprint 11): no sign-in, the link's token is the proof. Shows the booking and the amount, and opens
 * Razorpay Checkout (UPI, cards). What the browser hears back only changes what the page says ("confirming"): the payment
 * is recorded when Razorpay's webhook arrives, and the page checks every few seconds until it has. With the fake Razorpay
 * (no keys on the server) test buttons pay or fail the link instead.
 */
@Component({
  selector: 'app-pay',
  imports: [MatButtonModule, MatButtonToggleModule, MatIconModule],
  template: `
    <main class="wrap">
      @if (page(); as p) {
        <section class="card" [attr.data-status]="p.status">
          <p class="prop">{{ p.propertyName }}</p>
          <h1>{{ money(p.amount) }}</h1>
          <p class="mute">Booking <b class="num">{{ p.bookingReference }}</b> · {{ p.guestName }}<br />
            {{ day(p.checkIn) }} – {{ day(p.checkOut) }}</p>

          @switch (p.status) {
            @case ('PAID') {
              <p class="state ok" role="status"><mat-icon>check_circle</mat-icon>{{ p.message }}</p>
            }
            @case ('CONFIRMING') {
              <p class="state wait" role="status"><mat-icon>hourglass_top</mat-icon>{{ p.message }}</p>
              <p class="mute small">This page updates by itself. You can close it; the resort will see the payment.</p>
            }
            @case ('AWAITING_PAYMENT') {
              <!-- The message says why the last try failed, if one did. -->
              <p [class]="p.lastError ? 'state bad' : 'msg'" [attr.role]="p.lastError ? 'alert' : null">{{ p.message }}</p>
              @if (p.fake) {
                <p class="test" data-testid="test-mode">Test mode: the resort hasn't set up Razorpay keys, so no real money moves. Choose how the
                  test payment goes.</p>
                <mat-button-toggle-group [value]="method()" (change)="method.set($event.value)" aria-label="Test payment method" class="methods">
                  <mat-button-toggle value="upi">UPI</mat-button-toggle>
                  <mat-button-toggle value="card">Card</mat-button-toggle>
                </mat-button-toggle-group>
                <div class="actions">
                  <button mat-flat-button (click)="fakePay(true)" [disabled]="busy()">Pay {{ money(p.amount) }} (test)</button>
                  <button mat-stroked-button (click)="fakePay(false)" [disabled]="busy()">Fail the payment (test)</button>
                </div>
              } @else {
                <div class="actions">
                  <button mat-flat-button (click)="pay(p)" [disabled]="busy()" data-testid="pay"><mat-icon>lock</mat-icon>Pay {{ money(p.amount) }}</button>
                </div>
                <p class="mute small">Secure payment by Razorpay: UPI, cards, net banking and wallets. Card details go to Razorpay, never to
                  {{ p.propertyName }}.</p>
              }
            }
            @default {
              <p class="state off" role="status"><mat-icon>block</mat-icon>{{ p.message }}</p>
            }
          }
          @if (error()) { <p class="state bad" role="alert"><mat-icon>error</mat-icon>{{ error() }}</p> }
        </section>
      } @else if (error()) {
        <section class="card"><p class="state bad" role="alert"><mat-icon>error</mat-icon>{{ error() }}</p></section>
      } @else {
        <p class="mute">Loading…</p>
      }
    </main>
  `,
  styles: `
    :host { display: block; min-height: 100vh; background: #eef3f2; }
    .wrap { max-width: 460px; margin: 0 auto; padding: 32px 16px; }
    .card { background: #fff; border: 1px solid #d9e0de; border-radius: 14px; padding: 24px; }
    .prop { color: #0e3b43; font-weight: 600; margin: 0; }
    h1 { font-size: 2.2rem; color: #0e3b43; margin: 8px 0; }
    .mute { color: #5b6b6e; }
    .small { font-size: .85rem; }
    .msg { margin: 16px 0; }
    .actions { display: flex; flex-direction: column; gap: 8px; margin: 16px 0 8px; }
    .actions button { height: 48px; font-size: 1rem; }
    .methods { margin-top: 4px; }
    .test { background: #fbefd8; color: #6b4600; border-radius: 8px; padding: 8px 12px; font-size: .9rem; }
    .state { display: flex; gap: 8px; align-items: flex-start; border-radius: 8px; padding: 10px 12px; margin: 16px 0 8px; }
    .state.ok { background: #e1f2e8; color: #1d5b36; }
    .state.wait { background: #fbefd8; color: #6b4600; }
    .state.off { background: #eceff0; color: #3c4a4c; }
    .state.bad { background: #f3e3e3; color: #7a1f1f; }
  `,
})
export class PayPageComponent implements OnInit, OnDestroy {
  token = input.required<string>();
  private api = inject(PayPageApi);

  page = signal<PayPage | null>(null);
  error = signal('');
  busy = signal(false);
  method = signal<'upi' | 'card'>('upi');
  money = rupeesExact;
  day = dayLabel;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private polls = 0;

  ngOnInit() { this.load(); }
  ngOnDestroy() { this.stop(); }

  load() {
    this.api.get(this.token()).subscribe({
      next: p => this.show(p),
      error: e => { this.busy.set(false); this.error.set(toProblem(e).detail); },
    });
  }

  /** Opens Razorpay Checkout. Success only moves the page to "confirming"; the webhook records the payment. */
  async pay(p: PayPage) {
    this.error.set(''); this.busy.set(true);
    let Razorpay: RazorpayCtor;
    try {
      Razorpay = await loadCheckout();
    } catch {
      this.busy.set(false);
      this.error.set('Razorpay’s payment window couldn’t load. Check your internet connection or turn off an ad blocker for this page, then try again.');
      return;
    }
    const checkout = new Razorpay({
      key: p.keyId, order_id: p.gatewayOrderId, amount: Math.round(p.amount * 100), currency: p.currency,
      name: p.propertyName, description: `Booking ${p.bookingReference}`, prefill: { name: p.guestName },
      handler: (r: { razorpay_payment_id: string; razorpay_order_id: string; razorpay_signature: string }) =>
        this.api.callback(this.token(), { razorpayPaymentId: r.razorpay_payment_id, razorpayOrderId: r.razorpay_order_id, razorpaySignature: r.razorpay_signature })
          .subscribe({ next: x => this.show(x), error: () => this.load() }),
      modal: { ondismiss: () => this.load() },
    });
    checkout.on('payment.failed', r => this.error.set(`The payment didn't go through: ${r.error?.description ?? 'Razorpay gave no reason'}. You can try again.`));
    checkout.open();
  }

  fakePay(succeed: boolean) {
    this.error.set(''); this.busy.set(true);
    this.api.fakeCheckout(this.token(), succeed, this.method()).subscribe({
      next: p => this.show(p),
      error: e => { this.busy.set(false); this.error.set(toProblem(e).detail); },
    });
  }

  private show(p: PayPage) {
    this.page.set(p); this.busy.set(false);
    this.stop();
    if (p.status === 'CONFIRMING') {
      // Every 3 s for the first minute, then every 15 s.
      this.timer = setTimeout(() => { this.polls++; this.load(); }, this.polls < 20 ? 3000 : 15000);
    } else this.polls = 0;
  }

  private stop() { if (this.timer) { clearTimeout(this.timer); this.timer = null; } }
}
