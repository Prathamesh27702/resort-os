import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { AbstractControl, FormArray, FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { controlPath, fieldErrors, toProblem } from '../../core/http';
import { Inventory, PurchaseOrder, PurchaseRequest, Supplier, SupplierInvoice, SupplierPaymentMethod } from '../../core/models';
import { qty } from '../inventory/inventory.api';
import { newKey } from '../reservations/reservations.api';
import { STATES } from '../settings/gst/gst.api';
import { rupees, rupeesExact } from '../settings/rates/rates.api';
import { GST_RATES, PAY_METHOD_LABEL, PurchasingApi, lineTotals, remapLines } from './purchasing.api';

const server = (c: AbstractControl | null) => (c?.errors?.['server'] as string | undefined) ?? '';
const num = (v: unknown) => (v === '' || v === null || v === undefined ? null : Number(v));
const text = (v: string | null | undefined) => (v ?? '').trim() || null;

/**
 * Shows the API's field messages under their fields (lines mapped back to the rows they came from); anything without a
 * field of its own goes to the dialog's error line, so every refusal says why.
 */
function report(form: FormGroup, e: unknown, error: (m: string) => void, rows?: number[]) {
  const p = toProblem(e);
  const errs = rows ? remapLines(fieldErrors(p), rows) : fieldErrors(p);
  const left: string[] = [];
  for (const [k, msg] of Object.entries(errs)) {
    const c = form.get(k) ?? form.get(controlPath(k));
    if (c) { c.setErrors({ ...c.errors, server: msg }); c.markAsTouched(); } else left.push(msg);
  }
  if (!Object.keys(errs).length) error(p.detail);
  else if (left.length) error(left.join(' '));
}

// ------------------------------------------------------------------ a supplier

/** A supplier: name, GSTIN (the state comes from it) or the state for an unregistered one, contact and payment terms. */
@Component({
  selector: 'app-supplier-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ data.supplier ? data.supplier.name : 'New supplier' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="supplier-form">
      <mat-form-field appearance="outline"><mat-label>Name (as on their bills)</mat-label><input matInput formControlName="name" maxlength="120" />
        <mat-error>{{ server(form.get('name')) || 'Give the name, 2–120 characters.' }}</mat-error></mat-form-field>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Code</mat-label><input matInput formControlName="code" maxlength="20" placeholder="e.g. FRESH-VEG" />
          <mat-error>{{ server(form.get('code')) || 'The code is 1–20 letters, digits, - or _.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Pays after (days)</mat-label><input matInput type="number" inputmode="numeric" formControlName="paymentTermsDays" />
          <mat-hint>0: on delivery</mat-hint>
          <mat-error>{{ server(form.get('paymentTermsDays')) || 'Enter 0–180 days.' }}</mat-error></mat-form-field>
      </div>
      <mat-form-field appearance="outline"><mat-label>GSTIN (empty if unregistered)</mat-label>
        <input matInput formControlName="gstin" maxlength="15" placeholder="e.g. 30AAACF1234A1ZS" />
        <mat-hint>{{ gstinHint() }}</mat-hint>
        <mat-error>{{ server(form.get('gstin')) }}</mat-error></mat-form-field>
      @if (!hasGstin()) {
        <mat-form-field appearance="outline"><mat-label>Supplies from (state)</mat-label>
          <mat-select formControlName="stateCode">@for (s of states; track s.code) { <mat-option [value]="s.code">{{ s.name }} ({{ s.code }})</mat-option> }</mat-select>
          <mat-error>{{ server(form.get('stateCode')) || 'Choose the state.' }}</mat-error></mat-form-field>
      } @else if (server(form.get('stateCode'))) { <p class="err">{{ server(form.get('stateCode')) }}</p> }
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Contact</mat-label><input matInput formControlName="contactName" maxlength="80" />
          <mat-error>{{ server(form.get('contactName')) }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Phone</mat-label><input matInput type="tel" formControlName="phone" maxlength="20" />
          <mat-error>{{ server(form.get('phone')) }}</mat-error></mat-form-field>
      </div>
      <mat-form-field appearance="outline"><mat-label>E-mail</mat-label><input matInput type="email" formControlName="email" maxlength="120" />
        <mat-error>{{ server(form.get('email')) }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Address</mat-label><input matInput formControlName="address" maxlength="300" />
        <mat-error>{{ server(form.get('address')) }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Note</mat-label><input matInput formControlName="note" maxlength="500" placeholder="e.g. Delivers before 9 am" />
        <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>
      @if (data.supplier) { <mat-checkbox formControlName="isActive">Buying from them</mat-checkbox> }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="supplier-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(500px, 84vw); } .row { display: flex; gap: 8px; flex-wrap: wrap; }
           .row > * { flex: 1 1 180px; } .err { color: #b3261e; }`,
})
export class SupplierDialog {
  data = inject<{ propertyId: string; supplier: Supplier | null }>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<SupplierDialog, string>);
  private api = inject(PurchasingApi);
  server = server;
  states = STATES;
  busy = signal(false);
  error = signal('');
  private s = this.data.supplier;
  form = inject(FormBuilder).nonNullable.group({
    name: [this.s?.name ?? '', [Validators.required, Validators.minLength(2)]],
    code: [this.s?.code ?? '', Validators.required],
    gstin: [this.s?.gstin ?? ''],
    stateCode: [this.s?.stateCode ?? '30'],
    contactName: [this.s?.contactName ?? ''],
    phone: [this.s?.phone ?? ''],
    email: [this.s?.email ?? ''],
    address: [this.s?.address ?? ''],
    note: [this.s?.note ?? ''],
    paymentTermsDays: [this.s?.paymentTermsDays ?? 7, [Validators.required, Validators.min(0), Validators.max(180)]],
    isActive: [this.s?.isActive ?? true],
  });
  private gstin = toSignal(this.form.controls.gstin.valueChanges, { initialValue: this.form.controls.gstin.value });
  hasGstin = computed(() => this.gstin().trim().length > 0);
  gstinHint = computed(() => {
    const g = this.gstin().trim().toUpperCase();
    if (!g) return 'Unregistered: their bills carry no GST.';
    const st = STATES.find(x => x.code === g.slice(0, 2));
    return st ? `Registered in ${st.name}: ${st.code === '30' ? 'CGST + SGST' : 'IGST'} on their bills to a Goa property.` : '15 characters: state, PAN, entity, Z, check digit.';
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    const v = this.form.getRawValue();
    const gstin = text(v.gstin);
    this.api.saveSupplier(this.data.propertyId, this.s?.id ?? null, {
      code: v.code.trim(), name: v.name.trim(), gstin, stateCode: gstin ? null : v.stateCode, contactName: text(v.contactName), phone: text(v.phone),
      email: text(v.email), address: text(v.address), paymentTermsDays: Number(v.paymentTermsDays), note: text(v.note), isActive: v.isActive,
      version: this.s?.version ?? null,
    }).subscribe({
      next: x => this.ref.close(`${x.name} saved${x.gstin ? '' : ' (unregistered: no GST)'}.`),
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ a purchase request

/** What a store needs: items and quantities in their units, with what is on hand there now. */
@Component({
  selector: 'app-request-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Ask for stock</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="request-form">
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>For store</mat-label>
          <mat-select formControlName="locationId">@for (l of stores; track l.id) { <mat-option [value]="l.id">{{ l.name }}</mat-option> }</mat-select>
          <mat-error>{{ server(form.get('locationId')) || 'Choose the store.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Needed by (optional)</mat-label><input matInput type="date" formControlName="neededBy" />
          <mat-error>{{ server(form.get('neededBy')) }}</mat-error></mat-form-field>
      </div>
      <div formArrayName="items">
        @for (g of items.controls; track $index; let n = $index) {
          <div class="line" [formGroupName]="n">
            <mat-form-field appearance="outline" class="it"><mat-label>Item</mat-label>
              <mat-select formControlName="itemId" [aria-label]="'Item ' + (n + 1)">@for (i of stock; track i.id) { <mat-option [value]="i.id">{{ i.name }} ({{ i.unit }})</mat-option> }</mat-select>
              @if (onHand(n); as h) { <mat-hint>On hand: {{ h }}</mat-hint> }
              <mat-error>{{ server(g.get('itemId')) || 'Choose the item.' }}</mat-error></mat-form-field>
            <mat-form-field appearance="outline" class="q"><mat-label>Quantity</mat-label>
              <input matInput type="number" inputmode="decimal" formControlName="quantity" [attr.aria-label]="'Quantity ' + (n + 1)" />
              <mat-error>{{ server(g.get('quantity')) || 'More than 0.' }}</mat-error></mat-form-field>
            <button mat-icon-button type="button" (click)="items.removeAt(n)" [disabled]="items.length === 1" aria-label="Remove line"><mat-icon>close</mat-icon></button>
          </div>
        }
      </div>
      <button mat-button type="button" (click)="add()"><mat-icon>add</mat-icon>Another item</button>
      <mat-form-field appearance="outline"><mat-label>Note</mat-label><input matInput formControlName="note" maxlength="500" placeholder="e.g. For the weekend" />
        <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="request-form" [disabled]="busy()">Send request</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(560px, 86vw); } .row { display: flex; gap: 8px; flex-wrap: wrap; }
           .row > * { flex: 1 1 180px; } .line { display: flex; gap: 8px; align-items: flex-start; flex-wrap: wrap; }
           .it { flex: 1 1 220px; } .q { flex: 0 1 130px; } .err { color: #b3261e; }`,
})
export class RequestDialog {
  data = inject<{ propertyId: string; inventory: Inventory }>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<RequestDialog, string>);
  private api = inject(PurchasingApi);
  private fb = inject(FormBuilder).nonNullable;
  server = server;
  busy = signal(false);
  error = signal('');
  stock = this.data.inventory.items.filter(i => i.isActive);
  stores = this.data.inventory.locations.filter(l => l.isActive);
  form = this.fb.group({
    locationId: [this.stores.find(l => l.isKitchen)?.id ?? this.stores[0]?.id ?? '', Validators.required],
    neededBy: [''],
    note: [''],
    items: this.fb.array([this.line()]),
  });
  get items() { return this.form.controls.items as FormArray<FormGroup>; }
  private value = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });

  private line() { return this.fb.group({ itemId: ['', Validators.required], quantity: [null as number | null, [Validators.required, Validators.min(0.001)]] }); }
  add() { this.items.push(this.line()); }

  onHand(n: number) {
    const v = this.value();
    const item = this.stock.find(i => i.id === v.items?.[n]?.['itemId']);
    return item ? qty(item.levels.find(l => l.locationId === v.locationId)?.onHand ?? 0, item.unit) : null;
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    const v = this.form.getRawValue();
    this.api.createRequest(this.data.propertyId, {
      locationId: v.locationId, neededBy: v.neededBy || null, note: text(v.note),
      items: v.items.map(i => ({ itemId: i['itemId'] as string, quantity: Number(i['quantity']), note: null })),
    }).subscribe({
      next: r => this.ref.close(`${r.number} sent for ${r.locationName}.`),
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ a purchase order (draft)

export interface OrderData { propertyId: string; inventory: Inventory; suppliers: Supplier[]; order: PurchaseOrder | null; request: PurchaseRequest | null; propertyState: string | null; }

/** A draft order: supplier, store, lines with price before GST and GST rate, and the totals as the server will work them out. */
@Component({
  selector: 'app-order-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ data.order ? 'Edit ' + data.order.number : data.request ? 'Order for ' + data.request.number : 'New purchase order' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="order-form">
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Supplier</mat-label>
          <mat-select formControlName="supplierId">@for (s of suppliers; track s.id) { <mat-option [value]="s.id">{{ s.name }}@if (!s.gstin) { (no GST) }</mat-option> }</mat-select>
          @if (supplier(); as s) { <mat-hint>{{ s.stateName }} · {{ intra() ? 'CGST + SGST' : 'IGST' }}@if (!s.gstin) { · unregistered: GST 0 % } </mat-hint> }
          <mat-error>{{ server(form.get('supplierId')) || 'Choose the supplier.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Deliver to</mat-label>
          <mat-select formControlName="locationId">@for (l of stores; track l.id) { <mat-option [value]="l.id">{{ l.name }}</mat-option> }</mat-select>
          <mat-error>{{ server(form.get('locationId')) || 'Choose the store.' }}</mat-error></mat-form-field>
      </div>
      <div formArrayName="lines">
        @for (g of lines.controls; track $index; let n = $index) {
          <div class="line" [formGroupName]="n">
            <mat-form-field appearance="outline" class="it"><mat-label>Item</mat-label>
              <mat-select formControlName="itemId" [aria-label]="'Item ' + (n + 1)" (selectionChange)="picked(n)">
                @for (i of stock; track i.id) { <mat-option [value]="i.id">{{ i.name }} ({{ i.unit }})</mat-option> }</mat-select>
              <mat-error>{{ server(g.get('itemId')) || 'Choose the item.' }}</mat-error></mat-form-field>
            <mat-form-field appearance="outline" class="q"><mat-label>Qty ({{ unit(n) }})</mat-label>
              <input matInput type="number" inputmode="decimal" formControlName="quantity" [attr.aria-label]="'Quantity ' + (n + 1)" />
              <mat-error>{{ server(g.get('quantity')) || 'More than 0.' }}</mat-error></mat-form-field>
            <mat-form-field appearance="outline" class="q"><mat-label>₹ per {{ unit(n) }}</mat-label>
              <input matInput type="number" inputmode="decimal" formControlName="unitPrice" [attr.aria-label]="'Price ' + (n + 1)" />
              <mat-error>{{ server(g.get('unitPrice')) || '0 or more.' }}</mat-error></mat-form-field>
            <mat-form-field appearance="outline" class="r"><mat-label>GST</mat-label>
              <mat-select formControlName="gstRate" [aria-label]="'GST ' + (n + 1)">@for (r of rates; track r) { <mat-option [value]="r">{{ r }} %</mat-option> }</mat-select>
              <mat-error>{{ server(g.get('gstRate')) }}</mat-error></mat-form-field>
            <span class="amt">{{ rupeesExact(lineValue(n)) }}</span>
            <button mat-icon-button type="button" (click)="lines.removeAt(n)" [disabled]="lines.length === 1" aria-label="Remove line"><mat-icon>close</mat-icon></button>
            @if (server(g.get('gstRate'))) { <p class="err full">{{ server(g.get('gstRate')) }}</p> }
          </div>
        }
      </div>
      <button mat-button type="button" (click)="add()"><mat-icon>add</mat-icon>Another item</button>
      <p class="tot" data-testid="order-total">Before GST {{ rupeesExact(totals().taxable) }} · GST {{ rupeesExact(totals().tax) }} · <b>Total {{ rupeesExact(totals().taxable + totals().tax) }}</b></p>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Expected on (optional)</mat-label><input matInput type="date" formControlName="expectedDate" />
          <mat-error>{{ server(form.get('expectedDate')) }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Note to the supplier</mat-label><input matInput formControlName="note" maxlength="500" />
          <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>
      </div>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="order-form" [disabled]="busy()">Save draft</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(760px, 88vw); } .row { display: flex; gap: 8px; flex-wrap: wrap; }
           .row > * { flex: 1 1 200px; } .line { display: flex; gap: 6px; align-items: flex-start; flex-wrap: wrap; border-bottom: 1px solid #eef2f1; padding-top: 6px; }
           .it { flex: 1 1 200px; } .q { flex: 0 1 110px; } .r { flex: 0 1 96px; } .amt { padding-top: 16px; min-width: 90px; text-align: right; font-variant-numeric: tabular-nums; }
           .tot { text-align: right; margin: 4px 0 8px; font-variant-numeric: tabular-nums; } .err { color: #b3261e; } .full { flex-basis: 100%; margin: 0 0 6px; }`,
})
export class OrderDialog {
  data = inject<OrderData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<OrderDialog, PurchaseOrder>);
  private api = inject(PurchasingApi);
  private fb = inject(FormBuilder).nonNullable;
  server = server;
  rupeesExact = rupeesExact;
  rates = GST_RATES;
  busy = signal(false);
  error = signal('');
  stock = this.data.inventory.items.filter(i => i.isActive);
  stores = this.data.inventory.locations.filter(l => l.isActive);
  suppliers = this.data.suppliers.filter(s => s.isActive || s.id === this.data.order?.supplierId);
  private o = this.data.order;
  form = this.fb.group({
    supplierId: [this.o?.supplierId ?? '', Validators.required],
    locationId: [this.o?.locationId ?? this.data.request?.locationId ?? this.stores.find(l => !l.isKitchen)?.id ?? this.stores[0]?.id ?? '', Validators.required],
    expectedDate: [this.o?.expectedDate ?? ''],
    note: [this.o?.note ?? ''],
    lines: this.fb.array(this.o
      ? this.o.lines.map(l => this.line(l.itemId, l.quantity, l.unitPrice, l.gstRate))
      : this.data.request
        ? this.data.request.items.map(i => this.line(i.itemId, i.quantity, i.unitCost, 0))
        : [this.line()]),
  });
  get lines() { return this.form.controls.lines as FormArray<FormGroup>; }
  private value = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });
  supplier = computed(() => this.suppliers.find(s => s.id === this.value().supplierId) ?? null);
  intra = computed(() => !this.supplier() || !this.data.propertyState || this.supplier()!.stateCode === this.data.propertyState);
  totals = computed(() => (this.value().lines ?? []).reduce((t: { taxable: number; tax: number }, l) => {
    const x = lineTotals(Number(l['quantity']) || 0, Number(l['unitPrice']) || 0, Number(l['gstRate']) || 0, this.intra());
    return { taxable: t.taxable + x.taxable, tax: t.tax + x.tax };
  }, { taxable: 0, tax: 0 }));

  private line(itemId = '', quantity: number | null = null, unitPrice: number | null = null, gstRate = 0) {
    return this.fb.group({
      itemId: [itemId, Validators.required], quantity: [quantity, [Validators.required, Validators.min(0.001)]],
      unitPrice: [unitPrice, [Validators.required, Validators.min(0)]], gstRate: [gstRate],
    });
  }
  add() { this.lines.push(this.line()); }

  unit(n: number) { return this.stock.find(i => i.id === this.value().lines?.[n]?.['itemId'])?.unit ?? 'unit'; }
  lineValue(n: number) {
    const l = this.value().lines?.[n];
    return l ? lineTotals(Number(l['quantity']) || 0, Number(l['unitPrice']) || 0, 0, true).taxable : 0;
  }
  /** A chosen item starts at its current cost. */
  picked(n: number) {
    const g = this.lines.at(n);
    const item = this.stock.find(i => i.id === g.get('itemId')!.value);
    if (item && num(g.get('unitPrice')!.value) === null) g.get('unitPrice')!.setValue(item.unitCost);
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.error.set('');
    const v = this.form.getRawValue();
    this.api.saveOrder(this.data.propertyId, this.o?.id ?? null, {
      supplierId: v.supplierId, locationId: v.locationId, requestId: this.o ? null : this.data.request?.id ?? null, expectedDate: v.expectedDate || null,
      note: text(v.note), version: this.o?.version ?? null,
      lines: v.lines.map(l => ({ itemId: l['itemId'] as string, quantity: Number(l['quantity']), unitPrice: Number(l['unitPrice']), gstRate: Number(l['gstRate']) })),
    }).subscribe({
      next: o => this.ref.close(o),
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ goods received

/** What came in on a delivery: each line still to come, how much arrived (empty: didn't come), the challan number. */
@Component({
  selector: 'app-receive-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Goods received on {{ data.order.number }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="receive-form">
      <p class="mute">{{ data.order.supplierName }}. Enter what came in today; leave a line empty if it didn't come. Stock goes into the store at the order's price.</p>
      <div formArrayName="lines">
        @for (g of lines.controls; track $index; let n = $index) {
          <div class="line" [formGroupName]="n" [attr.data-line]="open[n].itemName">
            <div class="nm"><b>{{ open[n].itemName }}</b><small class="mute">Still to come {{ qty(open[n].remaining, open[n].unit) }} of {{ qty(open[n].quantity, open[n].unit) }}</small></div>
            <mat-form-field appearance="outline" class="q"><mat-label>Came in ({{ open[n].unit }})</mat-label>
              <input matInput type="number" inputmode="decimal" formControlName="quantity" [attr.aria-label]="open[n].itemName + ' received'" />
              <mat-error>{{ server(g.get('quantity')) || 'More than 0, at most what is still to come.' }}</mat-error></mat-form-field>
            <button mat-button type="button" (click)="all(n)">All</button>
          </div>
        }
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Into store</mat-label>
          <mat-select formControlName="locationId">@for (l of stores; track l.id) { <mat-option [value]="l.id">{{ l.name }}</mat-option> }</mat-select>
          <mat-error>{{ server(form.get('locationId')) }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Delivery challan no.</mat-label><input matInput formControlName="challanNumber" maxlength="40" />
          <mat-error>{{ server(form.get('challanNumber')) }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Received on</mat-label><input matInput type="date" formControlName="receivedOn" />
          <mat-error>{{ server(form.get('receivedOn')) }}</mat-error></mat-form-field>
      </div>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="receive-form" [disabled]="busy()">Save delivery</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(600px, 86vw); } .mute { color: #5b6b6e; margin: 0 0 8px; }
           .line { display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-start; border-bottom: 1px solid #eef2f1; padding-top: 6px; }
           .nm { flex: 1 1 160px; display: flex; flex-direction: column; padding-top: 8px; } .q { flex: 0 1 150px; } button { margin-top: 8px; }
           .row { display: flex; gap: 8px; flex-wrap: wrap; } .row > * { flex: 1 1 160px; } .err { color: #b3261e; }`,
})
export class ReceiveDialog {
  data = inject<{ propertyId: string; order: PurchaseOrder; inventory: Inventory }>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<ReceiveDialog, PurchaseOrder>);
  private api = inject(PurchasingApi);
  private fb = inject(FormBuilder).nonNullable;
  server = server;
  qty = qty;
  busy = signal(false);
  error = signal('');
  open = this.data.order.lines.filter(l => l.remaining > 0);
  stores = this.data.inventory.locations.filter(l => l.isActive);
  form = this.fb.group({
    locationId: [this.data.order.locationId],
    challanNumber: [''],
    receivedOn: [''],
    lines: this.fb.array(this.open.map(() => this.fb.group({ quantity: [null as number | null, Validators.min(0.001)] }))),
  });
  get lines() { return this.form.controls.lines as FormArray<FormGroup>; }

  all(n: number) { this.lines.at(n).get('quantity')!.setValue(this.open[n].remaining); }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    const rows = v.lines.map((l, n) => ({ n, q: num(l['quantity']) })).filter(x => x.q !== null);
    if (!rows.length) { this.error.set('Enter how much came in for at least one line.'); return; }
    this.busy.set(true);
    this.error.set('');
    this.api.receive(this.data.propertyId, this.data.order.id, {
      receivedOn: v.receivedOn || null, locationId: v.locationId || null, challanNumber: text(v.challanNumber), note: null,
      lines: rows.map(x => ({ orderItemId: this.open[x.n].id, quantity: x.q! })),
    }).subscribe({
      next: o => this.ref.close(o),
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m), rows.map(x => x.n)); },
    });
  }
}

// ------------------------------------------------------------------ the supplier's bill

/** The supplier's bill: their number and date, and per received line what they billed, at what price and GST. */
@Component({
  selector: 'app-bill-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ data.order.supplierName }}'s bill</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="bill-form">
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Their bill number</mat-label><input matInput formControlName="supplierNumber" maxlength="40" />
          <mat-error>{{ server(form.get('supplierNumber')) || 'Enter the number printed on the bill.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Bill date</mat-label><input matInput type="date" formControlName="invoiceDate" />
          <mat-error>{{ server(form.get('invoiceDate')) || 'Enter the date on the bill.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Due (optional)</mat-label><input matInput type="date" formControlName="dueDate" />
          <mat-hint>Empty: {{ terms }}</mat-hint>
          <mat-error>{{ server(form.get('dueDate')) }}</mat-error></mat-form-field>
      </div>
      <p class="mute">Lines that came in and are not billed yet. Change the price if the bill says another; leave a line empty if it isn't on this bill.</p>
      <div formArrayName="lines">
        @for (g of lines.controls; track $index; let n = $index) {
          <div class="line" [formGroupName]="n" [attr.data-line]="open[n].itemName">
            <div class="nm"><b>{{ open[n].itemName }}</b><small class="mute">To bill {{ qty(open[n].toBill, open[n].unit) }} · ordered at {{ rupeesExact(open[n].unitPrice) }}</small></div>
            <mat-form-field appearance="outline" class="q"><mat-label>Billed ({{ open[n].unit }})</mat-label>
              <input matInput type="number" inputmode="decimal" formControlName="quantity" [attr.aria-label]="open[n].itemName + ' billed'" />
              <mat-error>{{ server(g.get('quantity')) || 'More than 0.' }}</mat-error></mat-form-field>
            <mat-form-field appearance="outline" class="q"><mat-label>₹ per {{ open[n].unit }}</mat-label>
              <input matInput type="number" inputmode="decimal" formControlName="unitPrice" [attr.aria-label]="open[n].itemName + ' price'" />
              <mat-error>{{ server(g.get('unitPrice')) || '0 or more.' }}</mat-error></mat-form-field>
            <mat-form-field appearance="outline" class="r"><mat-label>GST</mat-label>
              <mat-select formControlName="gstRate" [aria-label]="open[n].itemName + ' GST'">@for (r of rates; track r) { <mat-option [value]="r">{{ r }} %</mat-option> }</mat-select>
              <mat-error>{{ server(g.get('gstRate')) }}</mat-error></mat-form-field>
            @if (server(g.get('quantity')) || server(g.get('gstRate'))) { <p class="err full">{{ server(g.get('quantity')) || server(g.get('gstRate')) }}</p> }
          </div>
        }
      </div>
      <p class="tot" data-testid="bill-total">Before GST {{ rupeesExact(totals().taxable) }} · {{ data.order.intraState ? 'CGST + SGST' : 'IGST' }} {{ rupeesExact(totals().tax) }} ·
        <b>Total {{ rupeesExact(totals().taxable + totals().tax) }}</b></p>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="bill-form" [disabled]="busy()">Record bill</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(700px, 88vw); } .mute { color: #5b6b6e; margin: 0 0 8px; }
           .row { display: flex; gap: 8px; flex-wrap: wrap; } .row > * { flex: 1 1 160px; }
           .line { display: flex; gap: 6px; flex-wrap: wrap; align-items: flex-start; border-bottom: 1px solid #eef2f1; padding-top: 6px; }
           .nm { flex: 1 1 160px; display: flex; flex-direction: column; padding-top: 8px; } .q { flex: 0 1 120px; } .r { flex: 0 1 96px; }
           .tot { text-align: right; font-variant-numeric: tabular-nums; } .err { color: #b3261e; } .full { flex-basis: 100%; margin: 0 0 6px; }`,
})
export class BillDialog {
  data = inject<{ propertyId: string; order: PurchaseOrder; today: string; termsDays: number }>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<BillDialog, SupplierInvoice>);
  private api = inject(PurchasingApi);
  private fb = inject(FormBuilder).nonNullable;
  server = server;
  qty = qty;
  rupeesExact = rupeesExact;
  rates = GST_RATES;
  busy = signal(false);
  error = signal('');
  open = this.data.order.lines.filter(l => l.toBill > 0);
  terms = this.data.termsDays ? `${this.data.termsDays} days after the bill date` : 'on the bill date';
  form = this.fb.group({
    supplierNumber: ['', Validators.required],
    invoiceDate: [this.data.today, Validators.required],
    dueDate: [''],
    lines: this.fb.array(this.open.map(l => this.fb.group({
      quantity: [l.toBill as number | null, Validators.min(0.001)], unitPrice: [l.unitPrice, [Validators.required, Validators.min(0)]], gstRate: [l.gstRate],
    }))),
  });
  get lines() { return this.form.controls.lines as FormArray<FormGroup>; }
  private value = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });
  totals = computed(() => (this.value().lines ?? []).reduce((t: { taxable: number; tax: number }, l) => {
    const x = lineTotals(Number(l['quantity']) || 0, Number(l['unitPrice']) || 0, Number(l['gstRate']) || 0, this.data.order.intraState);
    return { taxable: t.taxable + x.taxable, tax: t.tax + x.tax };
  }, { taxable: 0, tax: 0 }));

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    const rows = v.lines.map((l, n) => ({ n, l })).filter(x => num(x.l['quantity']) !== null);
    if (!rows.length) { this.error.set('Enter the billed quantity of at least one line.'); return; }
    this.busy.set(true);
    this.error.set('');
    this.api.recordBill(this.data.propertyId, this.data.order.id, {
      supplierNumber: v.supplierNumber.trim(), invoiceDate: v.invoiceDate, dueDate: v.dueDate || null, note: null,
      lines: rows.map(x => ({ orderItemId: this.open[x.n].id, quantity: Number(x.l['quantity']), unitPrice: Number(x.l['unitPrice']), gstRate: Number(x.l['gstRate']) })),
    }).subscribe({
      next: b => this.ref.close(b),
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m), rows.map(x => x.n)); },
    });
  }
}

// ------------------------------------------------------------------ paying a supplier

/** A payment against a bill: how much (the balance by default), how, and the UTR or cheque number. Sent once (Idempotency-Key). */
@Component({
  selector: 'app-pay-supplier-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Pay {{ data.invoice.supplierName }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="pay-form">
      <p class="mute">{{ data.invoice.number }} · their bill {{ data.invoice.supplierNumber }} · {{ rupees(data.invoice.balance) }} left of {{ rupees(data.invoice.total) }}</p>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Amount (₹)</mat-label><input matInput type="number" inputmode="decimal" formControlName="amount" />
          <mat-error>{{ server(form.get('amount')) || 'More than ₹0.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Paid by</mat-label>
          <mat-select formControlName="method">@for (m of methods; track m) { <mat-option [value]="m">{{ label[m] }}</mat-option> }</mat-select>
          <mat-error>{{ server(form.get('method')) }}</mat-error></mat-form-field>
      </div>
      @if (server(form.get('method'))) { <p class="err" role="alert">{{ server(form.get('method')) }}</p> }
      <mat-form-field appearance="outline"><mat-label>{{ method() === 'CHEQUE' ? 'Cheque number' : method() === 'CASH' ? 'Reference (optional)' : 'UTR / transaction reference' }}</mat-label>
        <input matInput formControlName="reference" maxlength="60" />
        <mat-error>{{ server(form.get('reference')) }}</mat-error></mat-form-field>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Paid on</mat-label><input matInput type="date" formControlName="paidOn" />
          <mat-error>{{ server(form.get('paidOn')) }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Note</mat-label><input matInput formControlName="note" maxlength="300" />
          <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>
      </div>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="pay-form" [disabled]="busy()">Record payment</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(460px, 84vw); } .row { display: flex; gap: 8px; flex-wrap: wrap; }
           .row > * { flex: 1 1 160px; } .mute { color: #5b6b6e; margin: 0 0 8px; } .err { color: #b3261e; }`,
})
export class PaySupplierDialog {
  data = inject<{ propertyId: string; invoice: SupplierInvoice; today: string }>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<PaySupplierDialog, SupplierInvoice>);
  private api = inject(PurchasingApi);
  private key = newKey();
  server = server;
  rupees = rupees;
  label = PAY_METHOD_LABEL;
  methods = Object.keys(PAY_METHOD_LABEL) as SupplierPaymentMethod[];
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).nonNullable.group({
    amount: [this.data.invoice.balance as number | null, [Validators.required, Validators.min(0.01)]],
    method: ['BANK_TRANSFER' as SupplierPaymentMethod],
    reference: [''],
    paidOn: [this.data.today],
    note: [''],
  });
  method = toSignal(this.form.controls.method.valueChanges, { initialValue: this.form.controls.method.value });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.error.set('');
    const v = this.form.getRawValue();
    this.api.pay(this.data.propertyId, this.data.invoice.id, {
      amount: Number(v.amount), paidOn: v.paidOn || null, method: v.method, reference: text(v.reference), note: text(v.note), version: this.data.invoice.version,
    }, this.key).subscribe({
      next: b => this.ref.close(b),
      // A refused payment can be corrected and sent again: it is a new payment, so it needs a new key.
      error: e => { this.busy.set(false); this.key = newKey(); report(this.form, e, m => this.error.set(m)); },
    });
  }
}
