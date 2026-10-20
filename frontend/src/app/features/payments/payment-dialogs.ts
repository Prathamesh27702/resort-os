import { Component, computed, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, ValidatorFn, Validators } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { showFieldErrors, toProblem } from '../../core/http';
import { CashDrawerSession, Folio, Payment, PaymentMethod } from '../../core/models';
import { newKey } from '../reservations/reservations.api';
import { minTrimmed } from '../rooms/rooms.forms';
import { rupeesExact } from '../settings/rates/rates.api';
import { METHODS, PaymentsApi, looksLikeCardNumber, methodLabel, referenceHint, varianceOf } from './payments.api';

/** No card numbers in a text field (brief §3): refused in the form, and again by the API. */
const noCardNumber: ValidatorFn = (c: AbstractControl): ValidationErrors | null =>
  looksLikeCardNumber(c.value) ? { card: true } : null;
const CARD_MESSAGE = 'This looks like a card number, and card details are never stored. Take it out.';

/** Shared error handling: API field messages go under their fields, anything else above the buttons. */
function apiError(form: Parameters<typeof showFieldErrors>[0], e: unknown, fields: (f: Record<string, string>) => void, general: (m: string) => void) {
  const p = toProblem(e);
  const f = showFieldErrors(form, p);
  fields(f);
  const known = Object.keys(f).filter(k => form.get(k));
  if (!known.length) general(Object.values(f)[0] ?? p.detail);
}

// ------------------------------------------------------------------ take a payment

export interface TakePaymentData { propertyId: string; folio: Folio; }

/**
 * Takes money for a stay: onto this bill (at most what it owes), or on account for the booking (an advance before the
 * nights are on the bill). Cash goes into the open cash drawer. One Idempotency-Key per dialog, so a double click or a
 * retry after a lost connection takes the money once.
 */
@Component({
  selector: 'app-take-payment-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Take a payment · <span class="num">{{ data.folio.number }}</span></h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="pay-form">
      <p class="owed">{{ data.folio.guestName }} · {{ data.folio.reference }} ·
        @if (data.folio.balance > 0) { owes <b class="num">{{ money(data.folio.balance) }}</b> } @else { owes nothing on this bill }</p>
      <mat-button-toggle-group formControlName="method" aria-label="How was it paid" class="methods">
        @for (m of methods; track m) { <mat-button-toggle [value]="m">{{ methodLabel(m) }}</mat-button-toggle> }
      </mat-button-toggle-group>
      @if (err('method')) { <p class="err field" role="alert">{{ err('method') }}</p> }

      <mat-button-toggle-group formControlName="target" aria-label="Where the money goes" class="target">
        <mat-button-toggle value="folio">Pay this bill</mat-button-toggle>
        <mat-button-toggle value="account">On account (advance)</mat-button-toggle>
      </mat-button-toggle-group>
      <mat-form-field appearance="outline">
        <mat-label>Amount (₹)</mat-label>
        <input matInput type="number" formControlName="amount" min="0.01" step="0.01" />
        @if (target() === 'account') { <mat-hint>Kept for {{ data.folio.reference }} until it is put on a bill</mat-hint> }
        <mat-error>{{ err('amount') || 'The amount must be more than ₹0.' }}</mat-error>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ ref().label }}</mat-label>
        <input matInput formControlName="paymentReference" maxlength="100" autocomplete="off" />
        <mat-hint>{{ ref().hint }}</mat-hint>
        <mat-error>{{ err('paymentReference') || cardMessage }}</mat-error>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Note</mat-label>
        <input matInput formControlName="note" maxlength="500" placeholder="Optional" />
        <mat-error>{{ err('note') || cardMessage }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="pay-form" [disabled]="busy()">Take {{ money(amount()) }}</button>
    </div>
  `,
  styles: `
    form { min-width: min(460px, 82vw); display: flex; flex-direction: column; }
    .owed { margin: 0 0 12px; color: #1b2a2e; }
    .methods, .target { margin-bottom: 14px; align-self: flex-start; flex-wrap: wrap; }
    .full { width: 100%; }
    .err { color: #b3261e; }
    .err.field { margin: -8px 0 12px; font-size: .85rem; }
  `,
})
export class TakePaymentDialog {
  data = inject<TakePaymentData>(MAT_DIALOG_DATA);
  private ref$ = inject(MatDialogRef<TakePaymentDialog, Payment>);
  private api = inject(PaymentsApi);
  private key = newKey();
  methods = METHODS;
  methodLabel = methodLabel;
  money = rupeesExact;
  cardMessage = CARD_MESSAGE;
  busy = signal(false);
  error = signal('');
  private serverErrors = signal<Record<string, string>>({});

  form = inject(FormBuilder).nonNullable.group({
    method: ['' as PaymentMethod | '', [Validators.required]],
    target: [(this.data.folio.balance > 0 ? 'folio' : 'account') as 'folio' | 'account'],
    amount: [this.data.folio.balance > 0 ? this.data.folio.balance : (null as number | null), [Validators.required, Validators.min(0.01)]],
    paymentReference: ['', [Validators.maxLength(100), noCardNumber]],
    note: ['', [Validators.maxLength(500), noCardNumber]],
  });
  private value = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });
  target = computed(() => this.value().target ?? 'folio');
  amount = computed(() => Number(this.value().amount) || 0);
  ref = computed(() => referenceHint(this.value().method ?? ''));

  err(field: string) { return this.serverErrors()[field] ?? ''; }

  save() {
    this.error.set(''); this.serverErrors.set({});
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      if (!this.form.controls.method.value) this.serverErrors.set({ method: 'Choose how the money was paid: Cash, UPI, Card or Bank transfer.' });
      return;
    }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.api.take(this.data.propertyId, {
      method: v.method as PaymentMethod, amount: Number(v.amount),
      folioId: v.target === 'folio' ? this.data.folio.id : null, reservationId: v.target === 'account' ? this.data.folio.reservationId : null,
      paymentReference: v.paymentReference.trim() || null, note: v.note.trim() || null,
    }, this.key).subscribe({
      next: p => this.ref$.close(p),
      error: e => {
        this.busy.set(false);
        apiError(this.form, e, f => this.serverErrors.set(f), m => this.error.set(m));
      },
    });
  }
}

// ------------------------------------------------------------------ refund

export interface RefundData { propertyId: string; payment: Payment; }

/** Gives money back from a payment, with a reason. At most what is left to refund; by default the same way it was paid.
 *  Without a folio chosen the money comes off what is on account first, then the bills it paid. */
@Component({
  selector: 'app-refund-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Refund from <span class="num">{{ data.payment.number }}</span></h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="refund-form">
      <p class="hint">{{ methodLabel(data.payment.method) }} {{ money(data.payment.amount) }} from {{ data.payment.guestName }} ·
        up to <b class="num">{{ money(data.payment.refundable) }}</b> can be refunded. Nothing is deleted; the reason is kept with the refund
        and in the audit trail.</p>
      <div class="row">
        <mat-form-field appearance="outline">
          <mat-label>Amount (₹)</mat-label>
          <input matInput type="number" formControlName="amount" min="0.01" step="0.01" [max]="data.payment.refundable" />
          <mat-error>{{ err('amount') || 'Enter an amount from ₹0.01 to ' + money(data.payment.refundable) + '.' }}</mat-error>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Paid back by</mat-label>
          <mat-select formControlName="method">
            @for (m of methods; track m) { <mat-option [value]="m">{{ methodLabel(m) }}</mat-option> }
          </mat-select>
          @if (form.controls.method.value === 'CASH') { <mat-hint>Comes out of the open cash drawer</mat-hint> }
          @if (form.controls.method.value === 'ONLINE') { <mat-hint>Razorpay sends it back to the guest's card or UPI</mat-hint> }
          <mat-error>{{ err('method') }}</mat-error>
        </mat-form-field>
      </div>
      @if (data.payment.allocations.length > 1 || (data.payment.allocations.length && data.payment.onAccount > 0)) {
        <mat-form-field appearance="outline" class="full">
          <mat-label>Take it off</mat-label>
          <mat-select formControlName="folioId">
            <mat-option value="">On account first, then the bills it paid</mat-option>
            @for (a of data.payment.allocations; track a.folioId) {
              <mat-option [value]="a.folioId">{{ a.folioNumber }} · {{ a.folioName }} ({{ money(a.amount - a.refunded) }})</mat-option>
            }
          </mat-select>
          <mat-error>{{ err('folioId') }}</mat-error>
        </mat-form-field>
      }
      <mat-form-field appearance="outline" class="full">
        <mat-label>Reason</mat-label>
        <input matInput formControlName="reason" maxlength="500" placeholder="e.g. Guest cancelled the airport transfer" />
        <mat-error>{{ err('reason') || 'Say why the money is refunded (at least 3 characters), e.g. Guest cancelled the airport transfer.' }}</mat-error>
      </mat-form-field>
      @if (form.controls.method.value === 'ONLINE') {
        <p class="hint" data-online-refund>The refund shows as pending until Razorpay confirms it has sent the money (usually within minutes;
          the guest's bank can take 5–7 working days to show it). If Razorpay can't send it, the money goes back on the payment.</p>
      } @else {
      <mat-form-field appearance="outline" class="full">
        <mat-label>Refund reference</mat-label>
        <input matInput formControlName="reference" maxlength="100" placeholder="Optional: UTR or terminal approval code" autocomplete="off" />
        <mat-error>{{ err('reference') || cardMessage }}</mat-error>
      </mat-form-field>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button color="warn" type="submit" form="refund-form" [disabled]="busy()">Refund</button>
    </div>
  `,
  styles: `
    form { min-width: min(480px, 82vw); display: flex; flex-direction: column; }
    .hint { color: #5b6b6e; margin: 0 0 12px; }
    .row { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .full { width: 100%; }
    .err { color: #b3261e; }
    @media (max-width: 520px) { .row { grid-template-columns: 1fr; } }
  `,
})
export class RefundDialog {
  data = inject<RefundData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<RefundDialog, Payment>);
  private api = inject(PaymentsApi);
  private key = newKey();
  /** An online payment can go back through Razorpay (the default) or be paid back at the desk. */
  methods: PaymentMethod[] = this.data.payment.method === 'ONLINE' ? ['ONLINE', ...METHODS] : METHODS;
  methodLabel = methodLabel;
  money = rupeesExact;
  cardMessage = CARD_MESSAGE;
  busy = signal(false);
  error = signal('');
  private serverErrors = signal<Record<string, string>>({});

  form = inject(FormBuilder).nonNullable.group({
    amount: [this.data.payment.refundable as number | null, [Validators.required, Validators.min(0.01), Validators.max(this.data.payment.refundable)]],
    method: [this.data.payment.method as PaymentMethod],
    folioId: [''],
    reason: ['', [minTrimmed(3), Validators.maxLength(500)]],
    reference: ['', [Validators.maxLength(100), noCardNumber]],
  });

  err(field: string) { return this.serverErrors()[field] ?? ''; }

  save() {
    this.error.set(''); this.serverErrors.set({});
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.api.refund(this.data.propertyId, this.data.payment.id, {
      amount: Number(v.amount), reason: v.reason.trim(), method: v.method, folioId: v.folioId || null,
      reference: v.method === 'ONLINE' ? null : v.reference.trim() || null,
    }, this.key).subscribe({
      next: p => this.ref.close(p),
      error: e => { this.busy.set(false); apiError(this.form, e, f => this.serverErrors.set(f), m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ void

/** Voids a payment entered by mistake: it stays on record, marked voided, and counts for nothing. */
@Component({
  selector: 'app-void-payment-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Void <span class="num">{{ data.payment.number }}</span></h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="void-form">
      <p class="hint">{{ methodLabel(data.payment.method) }} {{ money(data.payment.amount) }} · for a payment entered by mistake. It stays on record,
        marked voided, and comes off the bill.@if (data.payment.method === 'CASH') { The cash comes back out of the drawer's count. }</p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Reason</mat-label>
        <input matInput formControlName="reason" maxlength="500" placeholder="e.g. Entered twice by mistake" />
        <mat-error>{{ serverError() || 'Say why the payment is voided (at least 3 characters), e.g. Entered twice by mistake.' }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button color="warn" type="submit" form="void-form" [disabled]="busy()">Void payment</button>
    </div>
  `,
  styles: `
    form { min-width: min(440px, 82vw); display: flex; flex-direction: column; }
    .hint { color: #5b6b6e; margin: 0 0 12px; }
    .full { width: 100%; }
    .err { color: #b3261e; }
  `,
})
export class VoidPaymentDialog {
  data = inject<RefundData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<VoidPaymentDialog, Payment>);
  private api = inject(PaymentsApi);
  methodLabel = methodLabel;
  money = rupeesExact;
  busy = signal(false);
  error = signal('');
  serverError = signal('');
  form = inject(FormBuilder).nonNullable.group({ reason: ['', [minTrimmed(3), Validators.maxLength(500)]] });

  save() {
    this.error.set(''); this.serverError.set('');
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.api.void(this.data.propertyId, this.data.payment.id, this.form.controls.reason.value.trim()).subscribe({
      next: p => this.ref.close(p),
      error: e => { this.busy.set(false); apiError(this.form, e, f => this.serverError.set(f['reason'] ?? ''), m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ allocate money on account

/** Puts money that is on account (an advance) onto one of the booking's bills, at most what that bill owes. */
@Component({
  selector: 'app-allocate-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Put <span class="num">{{ data.payment.number }}</span> on a bill</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="allocate-form">
      <p class="hint"><b class="num">{{ money(data.payment.onAccount) }}</b> of this payment is on account for {{ data.payment.reference }}.</p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Bill</mat-label>
        <mat-select formControlName="folioId">
          @for (f of data.payment.folios; track f.id) {
            <mat-option [value]="f.id" [disabled]="f.balance <= 0">{{ f.number }} · {{ f.name }} · owes {{ money(f.balance) }}</mat-option>
          }
        </mat-select>
        <mat-error>Choose the bill.</mat-error>
      </mat-form-field>
      <mat-form-field appearance="outline">
        <mat-label>Amount (₹)</mat-label>
        <input matInput type="number" formControlName="amount" min="0.01" step="0.01" />
        <mat-error>{{ serverError() || 'The amount must be more than ₹0.' }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="allocate-form" [disabled]="busy()">Put on bill</button>
    </div>
  `,
  styles: `
    form { min-width: min(440px, 82vw); display: flex; flex-direction: column; }
    .hint { color: #5b6b6e; margin: 0 0 12px; }
    .full { width: 100%; }
    .err { color: #b3261e; }
  `,
})
export class AllocateDialog {
  data = inject<RefundData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<AllocateDialog, Payment>);
  private api = inject(PaymentsApi);
  money = rupeesExact;
  busy = signal(false);
  error = signal('');
  serverError = signal('');
  private first = this.data.payment.folios.find(f => f.balance > 0);
  form = inject(FormBuilder).nonNullable.group({
    folioId: [this.first?.id ?? '', [Validators.required]],
    amount: [this.first ? Math.min(this.first.balance, this.data.payment.onAccount) : (null as number | null), [Validators.required, Validators.min(0.01)]],
  });

  save() {
    this.error.set(''); this.serverError.set('');
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.api.allocate(this.data.propertyId, this.data.payment.id, [{ folioId: v.folioId, amount: Number(v.amount) }]).subscribe({
      next: p => this.ref.close(p),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const msg = p.errors?.['Allocations']?.[0];
        if (msg) { this.serverError.set(msg); this.form.controls.amount.setErrors({ server: msg }); this.form.controls.amount.markAsTouched(); }
        else this.error.set(p.detail);
      },
    });
  }
}

// ------------------------------------------------------------------ cash drawer

export interface DrawerData { propertyId: string; session?: CashDrawerSession; }

/** Opens a shift with the cash counted in the drawer now (the float). */
@Component({
  selector: 'app-open-drawer-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Open the cash drawer</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="open-drawer-form">
      <p class="hint">Count the cash in the drawer before the shift starts. Every cash payment and refund is added to it, so the
        drawer knows what it should hold when you close.</p>
      <mat-form-field appearance="outline">
        <mat-label>Cash in the drawer (₹)</mat-label>
        <input matInput type="number" formControlName="openingFloat" min="0" step="1" />
        <mat-error>{{ err('openingFloat') || 'Enter the cash counted, ₹0 or more.' }}</mat-error>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Note</mat-label>
        <input matInput formControlName="note" maxlength="500" placeholder="Optional, e.g. Morning shift" />
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="open-drawer-form" [disabled]="busy()">Open drawer</button>
    </div>
  `,
  styles: `
    form { min-width: min(420px, 82vw); display: flex; flex-direction: column; }
    .hint { color: #5b6b6e; margin: 0 0 12px; }
    .full { width: 100%; }
    .err { color: #b3261e; }
  `,
})
export class OpenDrawerDialog {
  data = inject<DrawerData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<OpenDrawerDialog, CashDrawerSession>);
  private api = inject(PaymentsApi);
  busy = signal(false);
  error = signal('');
  private serverErrors = signal<Record<string, string>>({});
  form = inject(FormBuilder).nonNullable.group({
    openingFloat: [null as number | null, [Validators.required, Validators.min(0)]],
    note: ['', [Validators.maxLength(500)]],
  });

  err(field: string) { return this.serverErrors()[field] ?? ''; }

  save() {
    this.error.set(''); this.serverErrors.set({});
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.api.openDrawer(this.data.propertyId, Number(v.openingFloat), v.note.trim() || null).subscribe({
      next: s => this.ref.close(s),
      error: e => { this.busy.set(false); apiError(this.form, e, f => this.serverErrors.set(f), m => this.error.set(m)); },
    });
  }
}

/** Closes the shift with the cash counted now. The difference from what the drawer should hold shows as it is typed; a
 *  shift that is short or over needs a note saying why, and then waits for a manager to approve it. */
@Component({
  selector: 'app-close-drawer-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Close the cash drawer</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="close-drawer-form">
      <dl class="sum">
        <dt>Float at the open</dt><dd class="num">{{ money(s.openingFloat) }}</dd>
        <dt>Cash taken</dt><dd class="num">+ {{ money(s.cashIn) }}</dd>
        <dt>Cash paid out</dt><dd class="num">− {{ money(s.cashOut) }}</dd>
        <dt><b>Should be in the drawer</b></dt><dd class="num"><b data-testid="expected">{{ money(s.expected) }}</b></dd>
      </dl>
      <mat-form-field appearance="outline">
        <mat-label>Cash counted now (₹)</mat-label>
        <input matInput type="number" formControlName="countedAmount" min="0" step="1" />
        <mat-error>{{ err('countedAmount') || 'Enter the cash counted, ₹0 or more.' }}</mat-error>
      </mat-form-field>
      @if (variance(); as v) {
        <p [class]="'variance ' + (v.amount === 0 ? 'ok' : 'bad')" data-testid="variance">
          {{ v.words }}@if (v.amount !== 0) { by <b class="num">{{ money(abs(v.amount)) }}</b>. Say why below; a manager approves it. }
        </p>
      }
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ variance()?.amount ? 'Why it is ' + (variance()!.amount < 0 ? 'short' : 'over') : 'Note' }}</mat-label>
        <input matInput formControlName="note" maxlength="500" [placeholder]="variance()?.amount ? 'e.g. Gave too much change to a guest' : 'Optional'" />
        <mat-error>{{ err('note') || 'Say why the drawer is ' + ((variance()?.amount ?? 0) < 0 ? 'short' : 'over') + ' (at least 3 characters).' }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="close-drawer-form" [disabled]="busy()">Close drawer</button>
    </div>
  `,
  styles: `
    form { min-width: min(440px, 82vw); display: flex; flex-direction: column; }
    .sum { display: grid; grid-template-columns: 1fr auto; gap: 4px 16px; margin: 0 0 16px; }
    .sum dt { color: #5b6b6e; }
    .sum dd { margin: 0; text-align: right; }
    .variance { margin: -6px 0 12px; font-weight: 500; }
    .variance.ok { color: #1d5b36; }
    .variance.bad { color: #b3261e; }
    .full { width: 100%; }
    .err { color: #b3261e; }
  `,
})
export class CloseDrawerDialog {
  data = inject<DrawerData>(MAT_DIALOG_DATA);
  s = this.data.session!;
  private ref = inject(MatDialogRef<CloseDrawerDialog, CashDrawerSession>);
  private api = inject(PaymentsApi);
  money = rupeesExact;
  abs = Math.abs;
  busy = signal(false);
  error = signal('');
  private serverErrors = signal<Record<string, string>>({});
  form = inject(FormBuilder).nonNullable.group({
    countedAmount: [null as number | null, [Validators.required, Validators.min(0)]],
    note: ['', [Validators.maxLength(500)]],
  });
  private value = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });
  variance = computed(() => {
    const c = this.value().countedAmount;
    return varianceOf(this.s.expected, c === null || c === undefined || (c as unknown) === '' ? null : Number(c));
  });

  err(field: string) { return this.serverErrors()[field] ?? ''; }

  save() {
    this.error.set(''); this.serverErrors.set({});
    const v = this.form.getRawValue();
    if (this.variance()?.amount && v.note.trim().length < 3) this.form.controls.note.setErrors({ required: true });
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.api.closeDrawer(this.data.propertyId, Number(v.countedAmount), v.note.trim() || null).subscribe({
      next: s => this.ref.close(s),
      error: e => { this.busy.set(false); apiError(this.form, e, f => this.serverErrors.set(f), m => this.error.set(m)); },
    });
  }
}

/** Approves a short or over shift with a note (needs cash_drawer.approve_variance). */
@Component({
  selector: 'app-approve-variance-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Approve the variance</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="approve-form">
      <p class="hint">{{ s.drawerName }} · closed by {{ s.closedByName ?? 'someone' }} · should have held {{ money(s.expected) }}, counted
        {{ money(s.counted ?? 0) }}: <b [class]="(s.variance ?? 0) < 0 ? 'bad' : 'over'">{{ money(abs(s.variance ?? 0)) }} {{ (s.variance ?? 0) < 0 ? 'short' : 'over' }}</b>.
        <br />Their reason: “{{ s.closeNote }}”</p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Your note</mat-label>
        <input matInput formControlName="note" maxlength="500" placeholder="e.g. Change given wrongly to a guest; staff told" />
        <mat-error>{{ serverError() || 'Say why the variance is approved (at least 3 characters), e.g. Change given wrongly to a guest; staff told.' }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="approve-form" [disabled]="busy()">Approve</button>
    </div>
  `,
  styles: `
    form { min-width: min(460px, 82vw); display: flex; flex-direction: column; }
    .hint { color: #1b2a2e; margin: 0 0 12px; }
    .bad { color: #b3261e; }
    .over { color: #8a5a00; }
    .full { width: 100%; }
    .err { color: #b3261e; }
  `,
})
export class ApproveVarianceDialog {
  data = inject<DrawerData>(MAT_DIALOG_DATA);
  s = this.data.session!;
  private ref = inject(MatDialogRef<ApproveVarianceDialog, CashDrawerSession>);
  private api = inject(PaymentsApi);
  money = rupeesExact;
  abs = Math.abs;
  busy = signal(false);
  error = signal('');
  serverError = signal('');
  form = inject(FormBuilder).nonNullable.group({ note: ['', [minTrimmed(3), Validators.maxLength(500)]] });

  save() {
    this.error.set(''); this.serverError.set('');
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.api.approveVariance(this.data.propertyId, this.s.id, this.form.controls.note.value.trim()).subscribe({
      next: s => this.ref.close(s),
      error: e => { this.busy.set(false); apiError(this.form, e, f => this.serverError.set(f['note'] ?? ''), m => this.error.set(m)); },
    });
  }
}
