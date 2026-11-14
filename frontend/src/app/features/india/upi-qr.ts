import { Component, DestroyRef, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Subject, catchError, debounceTime, distinctUntilChanged, of, switchMap } from 'rxjs';
import { toProblem } from '../../core/http';
import { rupeesExact } from '../settings/rates/rates.api';
import { IndiaApi, UpiQr } from './india.api';

/**
 * The property's UPI QR for an amount (Sprint 30): the guest scans it with any UPI app and the amount and bill number are
 * already filled in. Shown where staff take a UPI payment. The money goes straight to the property's bank; staff then
 * type the UPI reference the guest's app shows. When the property has no UPI ID yet it says where to add one.
 */
@Component({
  selector: 'app-upi-qr',
  imports: [RouterLink],
  template: `
    @if (qr(); as q) {
      <div class="upi" data-testid="upi-qr">
        <img [src]="q.qrPng" [alt]="'UPI QR for ' + (q.amount ? money(q.amount) : 'any amount') + ' to ' + q.payeeName" width="168" height="168" />
        <div class="txt">
          <b>Scan to pay @if (q.amount) { {{ money(q.amount) }} }</b>
          <span>Any UPI app: GPay, PhonePe, Paytm, BHIM</span>
          <span class="id">{{ q.upiId }}</span>
          <small>{{ q.payeeName }}@if (q.note) {, {{ q.note }} }</small>
        </div>
      </div>
    } @else if (missing()) {
      <p class="none" data-testid="upi-missing">{{ missing() }}
        @if (canFix()) { <a [routerLink]="['/settings/properties', pid()]">Add the UPI ID</a> }</p>
    } @else if (error()) { <p class="none" role="alert">{{ error() }}</p> }
    @else { <p class="none">Making the QR…</p> }
  `,
  styles: `
    .upi { display: flex; align-items: center; gap: var(--space-4); padding: var(--space-3); border: 1px solid var(--ro-line); border-radius: var(--ro-radius-lg); background: var(--ro-paper); }
    img { flex: 0 0 auto; width: 168px; height: 168px; border-radius: 8px; background: #fff; padding: 6px; image-rendering: pixelated; }
    .txt { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
    .txt b { font-size: 1.05rem; } .txt span, .txt small { color: var(--ro-mute); font-size: .85rem; overflow-wrap: anywhere; }
    .txt .id { color: var(--ro-ink); font-weight: 600; }
    .none { margin: 0; padding: var(--space-3); border: 1px dashed var(--ro-line-strong); border-radius: var(--ro-radius-lg); color: var(--ro-mute); font-size: .9rem; }
    @media (max-width: 420px) { .upi { flex-direction: column; text-align: center; } }
  `,
})
export class UpiQrComponent {
  pid = input.required<string>();
  amount = input<number | null>(null);
  note = input<string | null>(null);
  /** Whether to offer the link to the property's settings when no UPI ID is set. */
  canFix = input(false);

  private api = inject(IndiaApi);
  money = rupeesExact;
  qr = signal<UpiQr | null>(null);
  missing = signal('');
  error = signal('');
  private ask = new Subject<{ pid: string; amount: number | null; note: string | null }>();

  constructor() {
    this.ask.pipe(
      debounceTime(250),
      distinctUntilChanged((a, b) => a.pid === b.pid && a.amount === b.amount && a.note === b.note),
      switchMap(a => this.api.upiQr(a.pid, a.amount, a.note).pipe(catchError(e => of(toProblem(e))))),
      takeUntilDestroyed(inject(DestroyRef)),
    ).subscribe(r => {
      if ('qrPng' in r) { this.qr.set(r); this.missing.set(''); this.error.set(''); return; }
      this.qr.set(null);
      if (r.code === 'UPI_NOT_SET') { this.missing.set(r.detail); this.error.set(''); } else { this.error.set(r.detail); this.missing.set(''); }
    });
    effect(() => this.ask.next({ pid: this.pid(), amount: this.amount() && this.amount()! > 0 ? Math.round(this.amount()! * 100) / 100 : null, note: this.note() }));
  }
}
