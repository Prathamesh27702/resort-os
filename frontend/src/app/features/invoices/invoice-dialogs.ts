import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { fieldErrors, showFieldErrors, toProblem } from '../../core/http';
import { DocumentLine, GST_STATES, Invoice, InvoicePreview } from '../../core/models';
import { newKey } from '../reservations/reservations.api';
import { minTrimmed } from '../rooms/rooms.forms';
import { shortDate } from '../rooms/rooms.api';
import { rupeesExact } from '../settings/rates/rates.api';
import { InvoicesApi, creditTax, documentLabel, lineTaxText } from './invoices.api';

// ------------------------------------------------------------------ issue a folio's invoice

export interface IssueInvoiceData { propertyId: string; preview: InvoicePreview; }

/**
 * Issues a folio's GST invoice: the lines and totals it will carry, and who it is billed to. The customer starts as the
 * guest on file (their company and GSTIN make it a B2B invoice); change it to bill someone else. One Idempotency-Key per
 * dialog, so a double click or a retry issues one invoice and takes one number.
 */
@Component({
  selector: 'app-issue-invoice-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Issue the {{ title.toLowerCase() }} for <span class="num">{{ p.folioNumber }}</span></h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="invoice-form">
      <table class="lines" aria-label="Lines on the invoice">
        <thead><tr><th>Line</th><th class="right">Taxable</th><th class="right">GST</th><th class="right">Total</th></tr></thead>
        <tbody>
          @for (l of p.lines; track l.folioItemId) {
            <tr><td>{{ l.description }} <small class="mute">· {{ shortDate(l.serviceDate) }}@if (l.hsnSac) { · SAC {{ l.hsnSac }} }</small>
                  @if (l.discount) { <br /><small class="mute">{{ money(l.grossAmount) }} less {{ money(l.discount) }} credited</small> }</td>
              <td class="right num">{{ money(l.taxableValue) }}</td>
              <td class="right num">{{ money(l.taxAmount) }}@if (l.taxes.length) { <br /><small class="mute">{{ taxText(l) }}</small> }</td>
              <td class="right num">{{ money(l.total) }}</td></tr>
          }
        </tbody>
        <tfoot><tr><td><b>Total</b></td><td class="right num">{{ money(p.taxableValue) }}</td><td class="right num">{{ money(p.taxTotal) }}</td>
          <td class="right num"><b data-testid="invoice-total">{{ money(p.total) }}</b></td></tr></tfoot>
      </table>

      <h3>Bill to</h3>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Customer name</mat-label>
        <input matInput formControlName="customerName" maxlength="200" placeholder="The guest, or the company's legal name" />
        <mat-hint>For a company, its legal name as on the GST registration.</mat-hint>
        <mat-error>{{ err('customerName') || 'Enter who the invoice is made out to.' }}</mat-error>
      </mat-form-field>
      <div class="row">
        <mat-form-field appearance="outline">
          <mat-label>GSTIN (for a B2B invoice)</mat-label>
          <input matInput formControlName="customerGstin" maxlength="15" class="num upper" placeholder="e.g. 27AAPFU0939F1ZV" />
          <mat-hint>{{ gstin() ? 'B2B: the company can claim the GST.' : 'Leave empty for a guest (B2C).' }}</mat-hint>
          <mat-error>{{ err('customerGstin') }}</mat-error>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Customer's state</mat-label>
          <mat-select formControlName="customerStateCode">
            <mat-option value="">Not given</mat-option>
            @for (s of states; track s.code) { <mat-option [value]="s.code">{{ s.name }} ({{ s.code }})</mat-option> }
          </mat-select>
          <mat-hint>With a GSTIN, its first two digits.</mat-hint>
          <mat-error>{{ err('customerStateCode') }}</mat-error>
        </mat-form-field>
      </div>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Address</mat-label>
        <input matInput formControlName="customerAddress" maxlength="300" />
        <mat-error>{{ err('customerAddress') }}</mat-error>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Note on the invoice (optional)</mat-label>
        <input matInput formControlName="notes" maxlength="500" placeholder="e.g. Company booking via Acme" />
        <mat-error>{{ err('notes') }}</mat-error>
      </mat-form-field>
      <p class="hint">Place of supply is {{ supplyState() }}, where the resort is, so a hotel stay is charged CGST and SGST even
        for a company from another state. Once issued, the invoice can't be changed: correct it with a credit note.</p>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="invoice-form" [disabled]="busy()">Issue {{ title.toLowerCase() }}</button>
    </div>
  `,
  styles: `
    form { min-width: min(620px, 86vw); display: flex; flex-direction: column; }
    h3 { margin: 16px 0 8px; font-size: 1rem; }
    .lines { width: 100%; border-collapse: collapse; margin-bottom: 4px; }
    .lines th { text-align: left; color: #5b6b6e; font-weight: 500; font-size: .85rem; padding: 4px 6px; border-bottom: 1px solid #d9e0de; }
    .lines td { padding: 6px; border-bottom: 1px solid #edf1f0; vertical-align: top; }
    .lines tfoot td { border-bottom: 0; border-top: 2px solid #d9e0de; }
    .right { text-align: right !important; }
    .full { width: 100%; }
    .row { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .upper { text-transform: uppercase; }
    .hint, .mute { color: #5b6b6e; }
    .hint { margin: 4px 0 8px; font-size: .9rem; }
    .err { color: #b3261e; }
    @media (max-width: 600px) { .row { grid-template-columns: 1fr; } }
  `,
})
export class IssueInvoiceDialog {
  data = inject<IssueInvoiceData>(MAT_DIALOG_DATA);
  p = this.data.preview;
  private ref = inject(MatDialogRef<IssueInvoiceDialog, Invoice>);
  private api = inject(InvoicesApi);
  private key = newKey();
  title = documentLabel(this.p.documentType);
  states = GST_STATES;
  money = rupeesExact;
  shortDate = shortDate;
  taxText = lineTaxText;
  busy = signal(false);
  error = signal('');
  private serverErrors = signal<Record<string, string>>({});

  private defaults = {
    customerName: this.p.customerName, customerGstin: this.p.customerGstin ?? '', customerAddress: this.p.customerAddress ?? '',
    customerStateCode: this.p.customerStateCode ?? '',
  };
  form = inject(FormBuilder).nonNullable.group({
    ...this.defaults,
    customerName: [this.defaults.customerName, [minTrimmed(1), Validators.maxLength(200)]],
    notes: [''],
  });
  private value = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });
  gstin = computed(() => (this.value().customerGstin ?? '').trim());
  supplyState = computed(() => this.p.supplier?.stateName ?? 'the resort’s state');

  constructor() {
    // the GSTIN on file is wrong: show why under the field, so it is fixed (or cleared) before issuing
    if (this.p.gstinProblem) this.serverError('customerGstin', this.p.gstinProblem);
    this.form.controls.customerGstin.valueChanges.subscribe(g => {
      const code = (g ?? '').trim().slice(0, 2);
      if (/^\d{2}$/.test(code) && this.states.some(s => s.code === code)) this.form.controls.customerStateCode.setValue(code, { emitEvent: false });
    });
  }

  err(field: string) { return this.serverErrors()[field] ?? ''; }
  private serverError(field: string, message: string) {
    this.serverErrors.set({ ...this.serverErrors(), [field]: message });
    const c = this.form.get(field)!;
    c.setErrors({ server: message });
    c.markAsTouched();
  }

  save() {
    this.error.set(''); this.serverErrors.set({});
    for (const c of Object.values(this.form.controls)) if (c.hasError('server')) c.updateValueAndValidity();
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    const changed = v.customerName.trim() !== this.defaults.customerName || v.customerGstin.trim().toUpperCase() !== this.defaults.customerGstin
      || v.customerAddress.trim() !== this.defaults.customerAddress || v.customerStateCode !== this.defaults.customerStateCode
      || !!this.p.gstinProblem;
    this.busy.set(true);
    this.api.issue(this.data.propertyId, this.p.folioId, {
      // unchanged: bill the guest on file as the server has them; changed: exactly what is on the form
      customerName: changed ? v.customerName.trim() : null,
      customerGstin: changed ? (v.customerGstin.trim().toUpperCase() || null) : null,
      customerAddress: changed ? (v.customerAddress.trim() || null) : null,
      customerStateCode: changed ? (v.customerStateCode || null) : null,
      notes: v.notes.trim() || null,
    }, this.key).subscribe({
      next: i => this.ref.close(i),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const fields = showFieldErrors(this.form, p);
        this.serverErrors.set(fields);
        if (!Object.keys(fields).length) this.error.set(p.detail);
      },
    });
  }
}

// ------------------------------------------------------------------ credit note

export interface CreditNoteData { propertyId: string; invoice: Invoice; canCancel: boolean; }

/**
 * Issues a credit note: CANCEL takes the whole invoice back (it can then be issued again, e.g. to a company), REDUCE
 * takes chosen lines, or part of them, off it and off the guest's bill. GST comes off at each line's own rate. The
 * invoice itself never changes.
 */
@Component({
  selector: 'app-credit-note-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatButtonToggleModule, MatCheckboxModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Credit note on <span class="num">{{ inv.number }}</span></h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="credit-form">
      @if (data.canCancel) {
        <mat-button-toggle-group formControlName="type" aria-label="What the credit note does" class="mode">
          <mat-button-toggle value="REDUCE">Reduce lines</mat-button-toggle>
          <mat-button-toggle value="CANCEL">Cancel the invoice</mat-button-toggle>
        </mat-button-toggle-group>
      }
      @if (type() === 'CANCEL') {
        <p class="warn">Credits everything still on {{ inv.number }}@if (inv.debitNotes.length) { and its debit notes } :
          <b class="num">{{ money(cancelTotal()) }}</b>. The bill keeps its charges, so a new invoice can be issued for it (for example to a
          company with its GSTIN).</p>
      } @else {
        <p class="hint">Tick the lines to credit. Leave the amount as it is to credit the whole line, or enter part of it (before GST).</p>
        <div class="lines" role="group" aria-label="Lines to credit" formArrayName="lines">
          @for (l of creditable; track l.id; let k = $index) {
            <div class="line" [formGroupName]="k" [attr.data-credit-line]="l.description">
              <mat-checkbox formControlName="on">{{ l.description }}@if (l.debitNoteNumber) { <small class="mute"> · {{ l.debitNoteNumber }}</small> }
                <br /><small class="mute">{{ money(l.creditable) }} of {{ money(l.taxableValue) }} left · {{ taxText(l) || 'no GST' }}</small></mat-checkbox>
              @if (lineOn(k)) {
                <mat-form-field appearance="outline" class="amt">
                  <mat-label>Amount (₹)</mat-label>
                  <input matInput type="number" formControlName="amount" min="0.01" [max]="l.creditable" step="0.01" />
                  <mat-error>{{ lineError(k) || 'Enter ₹0.01 to ' + money(l.creditable) + '.' }}</mat-error>
                </mat-form-field>
              }
            </div>
          } @empty { <p class="mute">Nothing is left to credit on this invoice.</p> }
        </div>
        @if (linesError()) { <p class="err" role="alert">{{ linesError() }}</p> }
        <p class="amount">Credits <b class="num">{{ money(reduceTaxable()) }}</b> + GST <b class="num">{{ money(reduceTax()) }}</b> =
          <b class="num" data-testid="credit-total">{{ money(reduceTaxable() + reduceTax()) }}</b>, taken off the guest's bill.</p>
      }
      <mat-form-field appearance="outline" class="full">
        <mat-label>Reason</mat-label>
        <input matInput formControlName="reason" maxlength="500" [placeholder]="type() === 'CANCEL' ? 'e.g. Company asked for the invoice in its name' : 'e.g. Dinner was charged twice'" />
        <mat-error>{{ err('reason') || 'Say why the credit note is issued (at least 3 characters), e.g. Guest was charged for a dinner they didn\\'t have.' }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Close</button>
      <button mat-flat-button color="warn" type="submit" form="credit-form" [disabled]="busy()">Issue credit note</button>
    </div>
  `,
  styles: `
    form { min-width: min(560px, 86vw); display: flex; flex-direction: column; }
    .mode { margin-bottom: 12px; align-self: flex-start; }
    .lines { display: flex; flex-direction: column; gap: 4px; max-height: 300px; overflow: auto; margin-bottom: 8px; }
    .line { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
    .amt { width: 150px; }
    .full { width: 100%; }
    .hint, .mute { color: #5b6b6e; }
    .hint { margin: 0 0 8px; }
    .warn { color: #8a5a00; background: #fbefd8; border-radius: 8px; padding: 10px 12px; margin: 0 0 12px; }
    .amount { margin: 0 0 12px; color: #0e3b43; }
    .err { color: #b3261e; }
  `,
})
export class CreditNoteDialog {
  data = inject<CreditNoteData>(MAT_DIALOG_DATA);
  inv = this.data.invoice;
  private ref = inject(MatDialogRef<CreditNoteDialog, Invoice>);
  private api = inject(InvoicesApi);
  private key = newKey();
  private fb = inject(FormBuilder).nonNullable;
  money = rupeesExact;
  taxText = lineTaxText;
  creditable: DocumentLine[] = this.inv.items.filter(l => l.creditable > 0 && l.id);
  busy = signal(false);
  error = signal('');
  linesError = signal('');
  private serverErrors = signal<Record<string, string>>({});

  form = this.fb.group({
    type: ['REDUCE' as 'REDUCE' | 'CANCEL'],
    reason: ['', [minTrimmed(3), Validators.maxLength(500)]],
    lines: this.fb.array(this.creditable.map(l => this.fb.group({
      on: [false],
      amount: [l.creditable as number | null, [Validators.required, Validators.min(0.01), Validators.max(l.creditable)]],
    }))),
  });
  private value = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });
  type = computed(() => this.value().type ?? 'REDUCE');
  private picked = computed(() => (this.value().lines ?? []).map((v, k) => ({ line: this.creditable[k], on: !!v.on, amount: Number(v.amount) || 0 }))
    .filter(x => x.on));
  reduceTaxable = computed(() => this.picked().reduce((s, x) => s + Math.min(x.amount, x.line.creditable), 0));
  reduceTax = computed(() => this.picked().reduce((s, x) => s + creditTax(x.line, Math.min(x.amount, x.line.creditable)), 0));
  cancelTotal = computed(() => this.inv.outstanding);

  lineOn(k: number) { return !!this.value().lines?.[k]?.on; }
  lineError(k: number) { return this.serverErrors()[`lines.${k}.amount`] ?? this.serverErrors()[`lines.${k}.lineId`] ?? ''; }
  err(field: string) { return this.serverErrors()[field] ?? ''; }

  save() {
    this.error.set(''); this.linesError.set(''); this.serverErrors.set({});
    const v = this.form.getRawValue();
    const cancel = v.type === 'CANCEL';
    const picked = v.lines.map((l, k) => ({ ...l, k })).filter(l => l.on);
    if (this.form.controls.reason.invalid) { this.form.controls.reason.markAsTouched(); return; }
    if (!cancel) {
      if (!picked.length) { this.linesError.set('Tick at least one line to credit.'); return; }
      const bad = picked.find(l => this.form.controls.lines.at(l.k).controls.amount.invalid);
      if (bad) { this.form.controls.lines.at(bad.k).controls.amount.markAsTouched(); return; }
    }
    this.busy.set(true);
    this.api.credit(this.data.propertyId, this.inv.id, {
      type: v.type, reason: v.reason.trim(),
      lines: cancel ? null : picked.map(l => {
        const line = this.creditable[l.k];
        const amount = Number(l.amount);
        return { lineId: line.id!, amount: amount >= line.creditable ? null : amount };
      }),
    }, this.key).subscribe({
      next: i => this.ref.close(i),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        // the server numbers the lines it was sent; map them back to the rows of this form
        const raw = fieldErrors(p);
        const mapped: Record<string, string> = {};
        for (const [k, msg] of Object.entries(raw)) {
          const m = /^lines\[(\d+)\]\.(\w+)$/i.exec(k);
          if (m) {
            const row = picked[Number(m[1])]?.k ?? 0;
            const field = m[2].charAt(0).toLowerCase() + m[2].slice(1);
            mapped[`lines.${row}.${field}`] = msg;
            const c = this.form.controls.lines.at(row).controls.amount;
            c.setErrors({ server: msg }); c.markAsTouched();
          } else if (k === 'lines') this.linesError.set(msg);
          else mapped[k] = msg;
        }
        const reason = mapped['reason'];
        if (reason) { this.form.controls.reason.setErrors({ server: reason }); this.form.controls.reason.markAsTouched(); }
        this.serverErrors.set(mapped);
        if (!Object.keys(raw).length) this.error.set(p.detail);
        else if (mapped['type']) this.error.set(mapped['type']);
      },
    });
  }
}

// ------------------------------------------------------------------ debit note

export interface DebitNoteData { propertyId: string; invoice: Invoice; }

/** Bills charges posted to the folio after its invoice (a late minibar, a missed laundry bill) with a debit note on the
 *  invoice, under the same GST treatment. */
@Component({
  selector: 'app-debit-note-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Debit note on <span class="num">{{ data.invoice.number }}</span></h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="debit-form">
      <p>Bills the {{ data.invoice.unbilledCharges }} charge{{ data.invoice.unbilledCharges === 1 ? '' : 's' }} posted to
        <span class="num">{{ data.invoice.folioNumber }}</span> after the invoice: <b class="num">{{ money(data.invoice.unbilledAmount) }}</b>, GST included.</p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Reason</mat-label>
        <input matInput formControlName="reason" maxlength="500" placeholder="e.g. Minibar found after check-out" />
        <mat-error>{{ serverError() || 'Say why the debit note is issued (at least 3 characters), e.g. Minibar found after check-out.' }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="debit-form" [disabled]="busy()">Issue debit note</button>
    </div>
  `,
  styles: `
    form { min-width: min(440px, 82vw); display: flex; flex-direction: column; }
    .full { width: 100%; }
    .err { color: #b3261e; }
  `,
})
export class DebitNoteDialog {
  data = inject<DebitNoteData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<DebitNoteDialog, Invoice>);
  private api = inject(InvoicesApi);
  private key = newKey();
  money = rupeesExact;
  busy = signal(false);
  error = signal('');
  serverError = signal('');
  form = inject(FormBuilder).nonNullable.group({ reason: ['', [minTrimmed(3), Validators.maxLength(500)]] });

  save() {
    this.error.set(''); this.serverError.set('');
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.api.debit(this.data.propertyId, this.data.invoice.id, { reason: this.form.controls.reason.value.trim(), folioItemIds: null }, this.key).subscribe({
      next: i => this.ref.close(i),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const f = showFieldErrors(this.form, p)['reason'];
        if (f) this.serverError.set(f); else this.error.set(p.detail);
      },
    });
  }
}
