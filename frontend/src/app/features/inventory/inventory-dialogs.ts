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
import { controlPath, fieldErrors, showFieldErrors, toProblem } from '../../core/http';
import { AdjustmentKind, CostedDish, Inventory, InventoryCategory, InventoryItem, StockLocation, StockUnit } from '../../core/models';
import { rupees } from '../settings/rates/rates.api';
import { InventoryApi, KIND_LABEL, OUT_KINDS, lineCost, pct, qty, unitsLike } from './inventory.api';

const server = (c: AbstractControl | null) => (c?.errors?.['server'] as string | undefined) ?? '';

/** Shows the API's field messages under their fields; the rest in the dialog's error line. */
function report(form: FormGroup, e: unknown, error: (m: string) => void) {
  const p = toProblem(e);
  const errs = fieldErrors(p);
  const shown = showFieldErrors(form, p);
  const missing = Object.keys(errs).filter(k => !form.get(k) && !form.get(controlPath(k)));
  if (!Object.keys(shown).length) error(p.detail);
  else if (missing.length) error(missing.map(k => errs[k]).join(' '));
}

const num = (v: unknown) => (v === '' || v === null || v === undefined ? null : Number(v));

// ------------------------------------------------------------------ a stock item

/** A stocked item: its group, the unit it is counted in, what one unit costs before GST and when to reorder. A new item can
 *  start with its opening stock. */
@Component({
  selector: 'app-stock-item-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ data.item ? data.item.name : 'New stock item' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="item-form">
      <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" maxlength="80" placeholder="e.g. Paneer" />
        <mat-error>{{ server(form.get('name')) || 'Give the item a name of 2–80 characters.' }}</mat-error></mat-form-field>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Code</mat-label><input matInput formControlName="code" maxlength="20" placeholder="e.g. PANEER" />
          <mat-error>{{ server(form.get('code')) || 'The code is 1–20 letters, digits, - or _.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Group</mat-label>
          <mat-select formControlName="categoryId">@for (c of groups; track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select>
          <mat-error>{{ server(form.get('categoryId')) || 'Choose the group.' }}</mat-error></mat-form-field>
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Counted in</mat-label>
          <mat-select formControlName="unitId">@for (u of data.inventory.units; track u.id) { <mat-option [value]="u.id">{{ u.name }} ({{ u.code }})</mat-option> }</mat-select>
          <mat-error>{{ server(form.get('unitId')) || 'Choose the unit.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Cost of one {{ unit() }} before GST (₹)</mat-label>
          <input matInput type="number" inputmode="decimal" formControlName="unitCost" />
          <mat-error>{{ server(form.get('unitCost')) || 'Enter the cost (0 or more).' }}</mat-error></mat-form-field>
      </div>
      <mat-form-field appearance="outline"><mat-label>Reorder at or below ({{ unit() }}, optional)</mat-label>
        <input matInput type="number" inputmode="decimal" formControlName="reorderLevel" />
        <mat-error>{{ server(form.get('reorderLevel')) || 'Enter 0 or more, or leave it empty.' }}</mat-error></mat-form-field>
      @if (!data.item) {
        <div class="row">
          <mat-form-field appearance="outline"><mat-label>Opening stock ({{ unit() }}, optional)</mat-label>
            <input matInput type="number" inputmode="decimal" formControlName="openingQuantity" />
            <mat-error>{{ server(form.get('openingQuantity')) || 'Enter 0 or more, or leave it empty.' }}</mat-error></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>In store</mat-label>
            <mat-select formControlName="openingLocationId">@for (l of stores; track l.id) { <mat-option [value]="l.id">{{ l.name }}</mat-option> }</mat-select>
            <mat-error>{{ server(form.get('openingLocationId')) }}</mat-error></mat-form-field>
        </div>
      } @else {
        <mat-checkbox formControlName="isActive">In use</mat-checkbox>
        @if (server(form.get('isActive'))) { <p class="err">{{ server(form.get('isActive')) }}</p> }
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="item-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(480px, 84vw); } .row { display: flex; gap: 8px; flex-wrap: wrap; }
           .row > * { flex: 1 1 180px; } .err { color: #b3261e; }`,
})
export class StockItemDialog {
  data = inject<{ propertyId: string; item: InventoryItem | null; inventory: Inventory }>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<StockItemDialog, boolean>);
  private api = inject(InventoryApi);
  server = server;
  busy = signal(false);
  error = signal('');
  groups = this.data.inventory.categories.filter(c => c.isActive || c.id === this.data.item?.categoryId);
  stores = this.data.inventory.locations.filter(l => l.isActive);
  form = inject(FormBuilder).nonNullable.group({
    name: [this.data.item?.name ?? '', [Validators.required, Validators.minLength(2)]],
    code: [this.data.item?.code ?? '', Validators.required],
    categoryId: [this.data.item?.categoryId ?? this.groups[0]?.id ?? '', Validators.required],
    unitId: [this.data.item?.unitId ?? this.data.inventory.units[0]?.id ?? '', Validators.required],
    unitCost: [this.data.item?.unitCost ?? 0, [Validators.required, Validators.min(0)]],
    reorderLevel: [this.data.item?.reorderLevel ?? (null as number | null), Validators.min(0)],
    openingQuantity: [null as number | null, Validators.min(0)],
    openingLocationId: [this.stores.find(l => l.isKitchen)?.id ?? this.stores[0]?.id ?? ''],
    isActive: [this.data.item?.isActive ?? true],
  });
  private unitId = toSignal(this.form.controls.unitId.valueChanges, { initialValue: this.form.controls.unitId.value });
  unit = computed(() => this.data.inventory.units.find(u => u.id === this.unitId())?.code ?? 'unit');

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    const v = this.form.getRawValue();
    this.api.saveItem(this.data.propertyId, this.data.item?.id ?? null, {
      categoryId: v.categoryId, code: v.code.trim(), name: v.name.trim(), unitId: v.unitId, unitCost: Number(v.unitCost), reorderLevel: num(v.reorderLevel),
      isActive: v.isActive, openingQuantity: this.data.item ? null : num(v.openingQuantity), openingLocationId: this.data.item ? null : v.openingLocationId || null,
      version: this.data.item?.version ?? null,
    }).subscribe({
      next: () => this.ref.close(true),
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ a change of stock

/** Stock received, wastage (or expired, damaged), a correction or a transfer — always with a reason. */
@Component({
  selector: 'app-adjust-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Record a stock change</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="adjust-form">
      <mat-form-field appearance="outline"><mat-label>What happened</mat-label>
        <mat-select formControlName="kind">@for (k of kinds; track k) { <mat-option [value]="k">{{ label[k] }}</mat-option> }</mat-select>
        <mat-error>{{ server(form.get('kind')) }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Item</mat-label>
        <mat-select formControlName="itemId">@for (i of items; track i.id) { <mat-option [value]="i.id">{{ i.name }} ({{ i.unit }})</mat-option> }</mat-select>
        <mat-error>{{ server(form.get('itemId')) || 'Choose the item.' }}</mat-error></mat-form-field>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>{{ kind() === 'TRANSFER' ? 'From store' : kind() === 'RECEIVED' ? 'Into store' : 'Store' }}</mat-label>
          <mat-select formControlName="locationId">@for (l of stores; track l.id) { <mat-option [value]="l.id">{{ l.name }}</mat-option> }</mat-select>
          @if (have() !== null) { <mat-hint>On hand: {{ have() }}</mat-hint> }
          <mat-error>{{ server(form.get('locationId')) || 'Choose the store.' }}</mat-error></mat-form-field>
        @if (kind() === 'TRANSFER') {
          <mat-form-field appearance="outline"><mat-label>To store</mat-label>
            <mat-select formControlName="toLocationId">@for (l of stores; track l.id) { <mat-option [value]="l.id">{{ l.name }}</mat-option> }</mat-select>
            <mat-error>{{ server(form.get('toLocationId')) || 'Choose the store the stock goes to.' }}</mat-error></mat-form-field>
        }
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Quantity ({{ item()?.unit ?? 'unit' }})</mat-label>
          <input matInput type="number" inputmode="decimal" formControlName="quantity" />
          @if (kind() === 'CORRECTION') { <mat-hint>+ to add, − to take off</mat-hint> } @else if (out()) { <mat-hint>Taken out of the store</mat-hint> }
          <mat-error>{{ server(form.get('quantity')) || 'Enter the quantity.' }}</mat-error></mat-form-field>
        @if (kind() === 'RECEIVED') {
          <mat-form-field appearance="outline"><mat-label>Cost of one {{ item()?.unit ?? 'unit' }} (₹)</mat-label>
            <input matInput type="number" inputmode="decimal" formControlName="unitCost" />
            <mat-hint>Empty: {{ item() ? rupees(item()!.unitCost) : 'current cost' }}</mat-hint>
            <mat-error>{{ server(form.get('unitCost')) || 'Enter 0 or more, or leave it empty.' }}</mat-error></mat-form-field>
        }
      </div>
      <mat-form-field appearance="outline"><mat-label>Reason</mat-label>
        <input matInput formControlName="reason" maxlength="300" [placeholder]="kind() === 'RECEIVED' ? 'e.g. Market delivery, bill 4411' : 'e.g. Spoilt in the cold room'" />
        <mat-error>{{ server(form.get('reason')) || 'Say why, in 3–300 characters. Every stock change needs a reason.' }}</mat-error></mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="adjust-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(460px, 84vw); } .row { display: flex; gap: 8px; flex-wrap: wrap; }
           .row > * { flex: 1 1 180px; } .err { color: #b3261e; }`,
})
export class AdjustDialog {
  data = inject<{ propertyId: string; inventory: Inventory; itemId: string | null; kind: AdjustmentKind | null }>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<AdjustDialog, string>);
  private api = inject(InventoryApi);
  server = server;
  label = KIND_LABEL;
  rupees = rupees;
  kinds = Object.keys(KIND_LABEL) as AdjustmentKind[];
  busy = signal(false);
  error = signal('');
  items = this.data.inventory.items.filter(i => i.isActive);
  stores = this.data.inventory.locations.filter(l => l.isActive);
  private kitchen = this.stores.find(l => l.isKitchen)?.id ?? this.stores[0]?.id ?? '';
  form = inject(FormBuilder).nonNullable.group({
    kind: [this.data.kind ?? ('WASTAGE' as AdjustmentKind), Validators.required],
    itemId: [this.data.itemId ?? '', Validators.required],
    locationId: [this.kitchen, Validators.required],
    toLocationId: [''],
    quantity: [null as number | null, Validators.required],
    unitCost: [null as number | null, Validators.min(0)],
    reason: ['', [Validators.required, Validators.minLength(3)]],
  });
  kind = toSignal(this.form.controls.kind.valueChanges, { initialValue: this.form.controls.kind.value });
  private itemId = toSignal(this.form.controls.itemId.valueChanges, { initialValue: this.form.controls.itemId.value });
  private locationId = toSignal(this.form.controls.locationId.valueChanges, { initialValue: this.form.controls.locationId.value });
  item = computed(() => this.items.find(i => i.id === this.itemId()) ?? null);
  /** Stock out is entered as a positive quantity. */
  out = computed(() => OUT_KINDS.includes(this.kind()));
  have = computed(() => {
    const i = this.item();
    return i ? qty(i.levels.find(l => l.locationId === this.locationId())?.onHand ?? 0, i.unit) : null;
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    const v = this.form.getRawValue();
    this.api.adjust(this.data.propertyId, {
      kind: v.kind, itemId: v.itemId, locationId: v.locationId, toLocationId: v.kind === 'TRANSFER' ? v.toLocationId || null : null,
      quantity: Number(v.quantity), unitCost: v.kind === 'RECEIVED' ? num(v.unitCost) : null, reason: v.reason.trim(),
    }).subscribe({
      next: a => this.ref.close(`${a.number} saved: ${qty(Math.abs(a.quantity), a.unit)} ${a.itemName} — ${a.locationName} now has ${qty(a.onHandAfter, a.unit)}.`),
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ a physical count

/** Counts one store: what is on the shelf for each item; a difference needs a reason. Items left empty are not counted. */
@Component({
  selector: 'app-count-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Count a store</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="count-form">
      <mat-form-field appearance="outline"><mat-label>Store</mat-label>
        <mat-select formControlName="locationId">@for (l of stores; track l.id) { <mat-option [value]="l.id">{{ l.name }}</mat-option> }</mat-select>
        <mat-error>{{ server(form.get('locationId')) }}</mat-error></mat-form-field>
      <p class="mute">Enter what is on the shelf. Leave an item empty to skip it. A count that differs needs a reason.</p>
      <div formArrayName="lines" class="lines">
        @for (g of lines.controls; track $index; let n = $index) {
          <div class="line" [formGroupName]="n" [attr.data-item]="items[n].code">
            <div class="nm"><b>{{ items[n].name }}</b><small class="mute">System: {{ system(n) }}</small></div>
            <mat-form-field appearance="outline" class="q"><mat-label>Counted ({{ items[n].unit }})</mat-label>
              <input matInput type="number" inputmode="decimal" formControlName="countedQuantity" [attr.aria-label]="items[n].name + ' counted'" />
              <mat-error>{{ server(g.get('countedQuantity')) || 'Enter 0 or more.' }}</mat-error></mat-form-field>
            <mat-form-field appearance="outline" class="r"><mat-label>Reason if different</mat-label>
              <input matInput formControlName="reason" maxlength="300" [attr.aria-label]="items[n].name + ' reason'" />
              <mat-error>{{ server(g.get('reason')) }}</mat-error></mat-form-field>
          </div>
        }
      </div>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="count-form" [disabled]="busy()">Save count</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(620px, 86vw); } .mute { color: #5b6b6e; margin: 0 0 8px; }
           .line { display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-start; border-bottom: 1px solid #eef2f1; padding-top: 6px; }
           .nm { flex: 1 1 140px; display: flex; flex-direction: column; padding-top: 8px; } .q { flex: 0 1 140px; } .r { flex: 1 1 180px; }
           .err { color: #b3261e; }`,
})
export class CountDialog {
  data = inject<{ propertyId: string; inventory: Inventory }>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<CountDialog, string>);
  private api = inject(InventoryApi);
  private fb = inject(FormBuilder).nonNullable;
  server = server;
  busy = signal(false);
  error = signal('');
  items = this.data.inventory.items.filter(i => i.isActive);
  stores = this.data.inventory.locations.filter(l => l.isActive);
  form = this.fb.group({
    locationId: [this.stores.find(l => l.isKitchen)?.id ?? this.stores[0]?.id ?? '', Validators.required],
    lines: this.fb.array(this.items.map(() => this.fb.group({ countedQuantity: [null as number | null, Validators.min(0)], reason: [''] }))),
  });
  get lines() { return this.form.controls.lines as FormArray<FormGroup>; }
  private locationId = toSignal(this.form.controls.locationId.valueChanges, { initialValue: this.form.controls.locationId.value });

  system(n: number) {
    const i = this.items[n];
    return qty(i.levels.find(l => l.locationId === this.locationId())?.onHand ?? 0, i.unit);
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    // The API numbers lines as sent; map its Lines[k] back to the item's row.
    const rows = v.lines.map((l, n) => ({ n, l })).filter(x => num(x.l['countedQuantity']) !== null);
    if (!rows.length) { this.error.set('Enter what you counted for at least one item.'); return; }
    this.busy.set(true);
    this.error.set('');
    this.api.count(this.data.propertyId, {
      locationId: v.locationId,
      lines: rows.map(x => ({ itemId: this.items[x.n].id, countedQuantity: Number(x.l['countedQuantity']), reason: (x.l['reason'] as string).trim() || null })),
    }).subscribe({
      next: r => {
        const changed = r.lines.filter(l => l.difference !== 0).length;
        this.ref.close(`Count of ${r.locationName} saved: ${r.lines.length} counted, ${changed} changed (${rupees(r.value)}).`);
      },
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const errs = fieldErrors(p);
        const left: string[] = [];
        for (const [k, msg] of Object.entries(errs)) {
          const m = /^lines\[(\d+)\]\.(\w+)$/i.exec(k);
          const c = m ? this.lines.at(rows[Number(m[1])]?.n ?? -1)?.get(m[2].charAt(0).toLowerCase() + m[2].slice(1)) : this.form.get(k);
          if (c) { c.setErrors({ server: msg }); c.markAsTouched(); } else left.push(msg);
        }
        if (!Object.keys(errs).length) this.error.set(p.detail);
        else if (left.length) this.error.set(left.join(' '));
      },
    });
  }
}

// ------------------------------------------------------------------ a recipe

/** What one portion uses, in any unit of each item's kind (g or kg of an item kept in kg), with its cost as you type. */
@Component({
  selector: 'app-recipe-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Recipe: {{ data.dish.name }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="recipe-form">
      <p class="mute">One portion, sold at {{ rupees(data.dish.price) }} before GST.
        @if (data.dish.recipe && !data.dish.ownRecipe) { It is made by the dish's recipe now; saving gives this portion its own. }</p>
      <div formArrayName="ingredients">
        @for (g of ingredients.controls; track $index; let n = $index) {
          <div class="line" [formGroupName]="n">
            <mat-form-field appearance="outline" class="it"><mat-label>Item</mat-label>
              <mat-select formControlName="itemId" (selectionChange)="itemChanged(n)" [aria-label]="'Ingredient ' + (n + 1)">
                @for (i of items; track i.id) { <mat-option [value]="i.id">{{ i.name }}</mat-option> }</mat-select>
              <mat-error>{{ server(g.get('itemId')) || 'Choose the item.' }}</mat-error></mat-form-field>
            <mat-form-field appearance="outline" class="q"><mat-label>Quantity</mat-label>
              <input matInput type="number" inputmode="decimal" formControlName="quantity" [attr.aria-label]="'Quantity ' + (n + 1)" />
              <mat-error>{{ server(g.get('quantity')) || 'More than 0.' }}</mat-error></mat-form-field>
            <mat-form-field appearance="outline" class="u"><mat-label>Unit</mat-label>
              <mat-select formControlName="unitId" [aria-label]="'Unit ' + (n + 1)">
                @for (u of unitsFor(n); track u.id) { <mat-option [value]="u.id">{{ u.code }}</mat-option> }</mat-select>
              <mat-error>{{ server(g.get('unitId')) || 'Choose the unit.' }}</mat-error></mat-form-field>
            <span class="c">{{ cost(n) }}</span>
            <button mat-icon-button type="button" (click)="remove(n)" [attr.aria-label]="'Remove ingredient ' + (n + 1)"><mat-icon>close</mat-icon></button>
          </div>
        }
      </div>
      <button mat-stroked-button type="button" (click)="add()"><mat-icon>add</mat-icon>Ingredient</button>
      <p class="sum" aria-live="polite">Cost {{ rupees(total()) }} · food cost {{ pct(foodCost()) }} · margin {{ rupees(data.dish.price - total()) }}</p>
      <mat-form-field appearance="outline"><mat-label>Note (optional)</mat-label><input matInput formControlName="note" maxlength="500" placeholder="e.g. Marinate overnight" />
        <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>
      @if (server(form.get('ingredients'))) { <p class="err">{{ server(form.get('ingredients')) }}</p> }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      @if (data.dish.recipe && (data.dish.ownRecipe || !data.dish.variantId)) {
        <button mat-button type="button" class="off" (click)="takeOff()" [disabled]="busy()">Take recipe off</button>
      }
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="recipe-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(640px, 86vw); } .mute { color: #5b6b6e; margin: 0 0 8px; }
           .line { display: flex; gap: 6px; align-items: flex-start; flex-wrap: wrap; } .it { flex: 1 1 170px; } .q { flex: 0 1 100px; } .u { flex: 0 1 90px; }
           .c { min-width: 64px; padding-top: 18px; text-align: right; font-variant-numeric: tabular-nums; }
           .sum { font-weight: 600; margin: 8px 0; } .err { color: #b3261e; } .off { color: #b3261e; margin-right: auto; }`,
})
export class RecipeDialog {
  data = inject<{ propertyId: string; dish: CostedDish; inventory: Inventory; units: StockUnit[] }>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<RecipeDialog, string>);
  private api = inject(InventoryApi);
  private fb = inject(FormBuilder).nonNullable;
  server = server;
  rupees = rupees;
  pct = pct;
  busy = signal(false);
  error = signal('');
  items = this.data.inventory.items.filter(i => i.isActive);
  private own = this.data.dish.ownRecipe || !this.data.dish.variantId ? this.data.dish.recipe : null;
  form = this.fb.group({
    note: [this.own?.note ?? ''],
    ingredients: this.fb.array((this.data.dish.recipe?.ingredients ?? []).map(i => this.line(i.itemId, i.quantity, i.unitId))),
  });
  get ingredients() { return this.form.controls.ingredients as FormArray<FormGroup>; }
  private values = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });
  total = computed(() => {
    this.values();
    return Math.round(this.ingredients.controls.reduce((s, _, n) => s + (this.costOf(n) ?? 0), 0) * 100) / 100;
  });
  foodCost = computed(() => (this.data.dish.price > 0 ? Math.round(this.total() / this.data.dish.price * 10000) / 100 : null));

  constructor() { if (!this.ingredients.length) this.add(); }

  private line(itemId: string, quantity: number | null, unitId: string) {
    return this.fb.group({ itemId: [itemId, Validators.required], quantity: [quantity, [Validators.required, Validators.min(0.001)]], unitId: [unitId, Validators.required] });
  }

  add() { this.ingredients.push(this.line('', null, '')); }
  remove(n: number) { this.ingredients.removeAt(n); }

  unitsFor(n: number) {
    const item = this.items.find(i => i.id === this.ingredients.at(n).get('itemId')?.value);
    return item ? unitsLike(this.data.units, item.unitId) : [];
  }

  itemChanged(n: number) {
    const g = this.ingredients.at(n);
    const item = this.items.find(i => i.id === g.get('itemId')?.value);
    if (item && !this.unitsFor(n).some(u => u.id === g.get('unitId')?.value)) {
      // A recipe usually measures in the small unit: g for an item kept in kg, ml for l.
      const small = this.unitsFor(n).find(u => u.factor === 1) ?? this.unitsFor(n)[0];
      g.get('unitId')?.setValue(small?.id ?? item.unitId);
    }
  }

  private costOf(n: number) {
    const g = this.ingredients.at(n).getRawValue();
    return lineCost(Number(g['quantity']), this.data.units.find(u => u.id === g['unitId']), this.items.find(i => i.id === g['itemId']), this.data.units);
  }

  cost(n: number) { this.values(); const c = this.costOf(n); return c === null ? '' : rupees(c); }

  save() { this.send(false); }
  takeOff() { this.send(true); }

  private send(off: boolean) {
    if (!off && this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    const v = this.form.getRawValue();
    this.api.saveRecipe(this.data.propertyId, {
      menuItemId: this.data.dish.menuItemId, variantId: this.data.dish.variantId,
      note: (v.note as string).trim() || null,
      ingredients: off ? [] : v.ingredients.map(i => ({ itemId: i['itemId'] as string, quantity: Number(i['quantity']), unitId: i['unitId'] as string })),
      version: this.own?.version ?? null,
    }).subscribe({
      next: rows => {
        const row = rows.find(r => r.variantId === this.data.dish.variantId);
        this.ref.close(off ? `Recipe of ${this.data.dish.name} taken off.` : `Recipe of ${this.data.dish.name} saved: ${rupees(row?.cost ?? 0)}, food cost ${pct(row?.foodCostPercent)}.`);
      },
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ groups and stores

/** The stock groups and stores of the property; one store is the kitchen (served dishes take their ingredients from it). */
@Component({
  selector: 'app-stock-setup-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Groups and stores</h2>
    <div mat-dialog-content class="setup">
      <h3>Stores</h3>
      <ul>
        @for (l of locations(); track l.id) {
          <li><button class="nm" type="button" (click)="editStore(l)"><b>{{ l.name }}</b> <small>{{ l.code }}@if (l.isKitchen) { · kitchen }@if (!l.isActive) { · off }</small></button></li>
        }
      </ul>
      <form [formGroup]="store" (ngSubmit)="saveStore()" class="f">
        <mat-form-field appearance="outline"><mat-label>Code</mat-label><input matInput formControlName="code" maxlength="20" placeholder="e.g. BAR" />
          <mat-error>{{ server(store.get('code')) || 'The code is 1–20 letters, digits, - or _.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" maxlength="60" placeholder="e.g. Bar" />
          <mat-error>{{ server(store.get('name')) || 'Give the store a name of 2–60 characters.' }}</mat-error></mat-form-field>
        <mat-checkbox formControlName="isKitchen">The kitchen</mat-checkbox>
        <mat-checkbox formControlName="isActive">In use</mat-checkbox>
        @if (server(store.get('isActive'))) { <p class="err">{{ server(store.get('isActive')) }}</p> }
        <button mat-stroked-button type="submit" [disabled]="busy()">{{ storeId() ? 'Save store' : 'Add store' }}</button>
      </form>
      <h3>Groups</h3>
      <ul>
        @for (c of categories(); track c.id) {
          <li><button class="nm" type="button" (click)="editGroup(c)"><b>{{ c.name }}</b>@if (!c.isActive) { <small> · off</small> }</button></li>
        }
      </ul>
      <form [formGroup]="group" (ngSubmit)="saveGroup()" class="f">
        <mat-form-field appearance="outline"><mat-label>Group name</mat-label><input matInput formControlName="name" maxlength="60" placeholder="e.g. Dairy" />
          <mat-error>{{ server(group.get('name')) || 'Give the group a name of 2–60 characters.' }}</mat-error></mat-form-field>
        <mat-checkbox formControlName="isActive">In use</mat-checkbox>
        <button mat-stroked-button type="submit" [disabled]="busy()">{{ groupId() ? 'Save group' : 'Add group' }}</button>
      </form>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>
    <div mat-dialog-actions align="end"><button mat-flat-button [mat-dialog-close]="changed">Done</button></div>
  `,
  styles: `.setup { min-width: min(460px, 84vw); } h3 { margin: 8px 0 4px; font-size: 1rem; } ul { list-style: none; padding: 0; margin: 0 0 6px; }
           .nm { all: unset; cursor: pointer; display: block; padding: 6px 0; } .nm small { color: #5b6b6e; }
           .f { display: flex; flex-wrap: wrap; gap: 6px 10px; align-items: center; border-top: 1px solid #eef2f1; padding-top: 8px; }
           .f mat-form-field { flex: 1 1 160px; } .err { color: #b3261e; }`,
})
export class StockSetupDialog {
  data = inject<{ propertyId: string; inventory: Inventory }>(MAT_DIALOG_DATA);
  private api = inject(InventoryApi);
  private fb = inject(FormBuilder).nonNullable;
  server = server;
  busy = signal(false);
  error = signal('');
  changed = false;
  locations = signal<StockLocation[]>(this.data.inventory.locations);
  categories = signal<InventoryCategory[]>(this.data.inventory.categories);
  storeId = signal<string | null>(null);
  groupId = signal<string | null>(null);
  store = this.fb.group({ code: ['', Validators.required], name: ['', [Validators.required, Validators.minLength(2)]], isKitchen: [false], isActive: [true] });
  group = this.fb.group({ name: ['', [Validators.required, Validators.minLength(2)]], isActive: [true] });

  editStore(l: StockLocation) { this.storeId.set(l.id); this.store.reset({ code: l.code, name: l.name, isKitchen: l.isKitchen, isActive: l.isActive }); }
  editGroup(c: InventoryCategory) { this.groupId.set(c.id); this.group.reset({ name: c.name, isActive: c.isActive }); }

  saveStore() {
    if (this.store.invalid) { this.store.markAllAsTouched(); return; }
    const v = this.store.getRawValue();
    const id = this.storeId();
    const sortOrder = id ? this.locations().find(l => l.id === id)!.sortOrder : this.locations().length + 1;
    this.busy.set(true);
    this.api.saveLocation(this.data.propertyId, id, { ...v, code: v.code.trim(), name: v.name.trim(), sortOrder }).subscribe({
      next: () => { this.done(); this.storeId.set(null); this.store.reset({ code: '', name: '', isKitchen: false, isActive: true }); },
      error: e => { this.busy.set(false); report(this.store, e, m => this.error.set(m)); },
    });
  }

  saveGroup() {
    if (this.group.invalid) { this.group.markAllAsTouched(); return; }
    const v = this.group.getRawValue();
    const id = this.groupId();
    const sortOrder = id ? this.categories().find(c => c.id === id)!.sortOrder : this.categories().length + 1;
    this.busy.set(true);
    this.api.saveCategory(this.data.propertyId, id, { name: v.name.trim(), isActive: v.isActive, sortOrder }).subscribe({
      next: () => { this.done(); this.groupId.set(null); this.group.reset({ name: '', isActive: true }); },
      error: e => { this.busy.set(false); report(this.group, e, m => this.error.set(m)); },
    });
  }

  private done() {
    this.changed = true;
    this.error.set('');
    this.api.get(this.data.propertyId).subscribe(i => { this.busy.set(false); this.locations.set(i.locations); this.categories.set(i.categories); });
  }
}
