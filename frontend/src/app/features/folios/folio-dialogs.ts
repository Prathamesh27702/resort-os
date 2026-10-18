import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { showFieldErrors, toProblem } from '../../core/http';
import { ChargeCategory, Folio, FolioItem, Package } from '../../core/models';
import { newKey } from '../reservations/reservations.api';
import { minTrimmed } from '../rooms/rooms.forms';
import { shortDate } from '../rooms/rooms.api';
import { RatesApi, rupeesExact } from '../settings/rates/rates.api';
import { CATEGORIES, FoliosApi, categoryLabel, liveCharges } from './folios.api';

// ------------------------------------------------------------------ post a charge

export interface PostChargeData { propertyId: string; folio: Folio; today: string; }

/**
 * Posts a charge (what, how many, at what price, which day) or sells a package, which goes on the bill as one line per
 * part (room, food, transport, activity). The amount is shown as it is typed. One Idempotency-Key per dialog, so a
 * double click or a retry after a lost connection charges once.
 */
@Component({
  selector: 'app-post-charge-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Post a charge to <span class="num">{{ data.folio.number }}</span></h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="charge-form">
      <mat-button-toggle-group formControlName="mode" aria-label="Charge or package" class="mode">
        <mat-button-toggle value="charge">Charge</mat-button-toggle>
        <mat-button-toggle value="package">Package</mat-button-toggle>
      </mat-button-toggle-group>

      @if (mode() === 'charge') {
        <mat-form-field appearance="outline" class="full">
          <mat-label>What for</mat-label>
          <mat-select formControlName="category">
            @for (c of categories; track c) { <mat-option [value]="c">{{ label(c) }}</mat-option> }
          </mat-select>
          <mat-error>{{ err('category') || 'Choose what the charge is for.' }}</mat-error>
        </mat-form-field>
        <mat-form-field appearance="outline" class="full">
          <mat-label>Description</mat-label>
          <input matInput formControlName="description" maxlength="200" placeholder="e.g. Dinner at the beach shack" />
          <mat-error>{{ err('description') || 'Say what the charge is for, e.g. Dinner at the beach shack.' }}</mat-error>
        </mat-form-field>
        <div class="row">
          <mat-form-field appearance="outline">
            <mat-label>Quantity</mat-label>
            <input matInput type="number" formControlName="quantity" min="0.001" step="1" />
            <mat-error>{{ err('quantity') || 'The quantity must be more than 0.' }}</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Price each (₹)</mat-label>
            <input matInput type="number" formControlName="unitPrice" min="0.01" step="0.01" />
            <mat-error>{{ err('unitPrice') || 'The price must be more than ₹0.' }}</mat-error>
          </mat-form-field>
        </div>
      } @else {
        <mat-form-field appearance="outline" class="full">
          <mat-label>Package</mat-label>
          <mat-select formControlName="packageId">
            @for (p of packages(); track p.id) { <mat-option [value]="p.id">{{ p.name }} · {{ money(p.price) }}</mat-option> }
          </mat-select>
          @if (!packages().length) { <mat-hint>No packages are switched on. Managers add them in Settings → Packages.</mat-hint> }
          <mat-error>{{ err('packageId') || 'Choose a package.' }}</mat-error>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>How many</mat-label>
          <input matInput type="number" formControlName="quantity" min="1" max="50" step="1" />
          <mat-error>{{ err('quantity') || 'A package is sold whole: enter how many, 1 to 50.' }}</mat-error>
        </mat-form-field>
        @if (chosenPackage(); as p) {
          <ul class="split" aria-label="Lines this package adds">
            @for (c of p.components; track c.id) {
              @if (c.amount > 0) { <li><span>{{ label(c.category) }} · {{ c.description }}</span><b class="num">{{ money(c.amount * qty()) }}</b></li> }
            }
          </ul>
        }
      }
      <mat-form-field appearance="outline">
        <mat-label>Day of the service</mat-label>
        <input matInput type="date" formControlName="serviceDate" [max]="data.today" />
        <mat-error>{{ err('serviceDate') || 'Choose the day.' }}</mat-error>
      </mat-form-field>
      <p class="amount">Adds <b class="num">{{ money(amount()) }}</b> to the bill</p>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="charge-form" [disabled]="busy()">Post charge</button>
    </div>
  `,
  styles: `
    form { min-width: min(460px, 82vw); display: flex; flex-direction: column; }
    .mode { margin-bottom: 16px; align-self: flex-start; }
    .full { width: 100%; }
    .row { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .split { list-style: none; margin: 0 0 12px; padding: 8px 12px; background: #f2f7f8; border-radius: 8px; }
    .split li { display: flex; justify-content: space-between; gap: 12px; padding: 2px 0; }
    .amount { margin: 0 0 8px; color: #0e3b43; }
    .err { color: #b3261e; }
  `,
})
export class PostChargeDialog {
  data = inject<PostChargeData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<PostChargeDialog, Folio>);
  private api = inject(FoliosApi);
  private key = newKey();
  categories = CATEGORIES;
  label = categoryLabel;
  money = rupeesExact;
  busy = signal(false);
  error = signal('');
  private serverErrors = signal<Record<string, string>>({});
  packages = signal<Package[]>([]);

  form = inject(FormBuilder).nonNullable.group({
    mode: ['charge' as 'charge' | 'package'],
    category: ['' as ChargeCategory | '', [Validators.required]],
    description: ['', [minTrimmed(1), Validators.maxLength(200)]],
    quantity: [1, [Validators.required, Validators.min(0.001)]],
    unitPrice: [null as number | null, [Validators.required, Validators.min(0.01)]],
    packageId: [''],
    serviceDate: [this.data.today, [Validators.required]],
  });
  private value = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });
  mode = computed(() => this.value().mode ?? 'charge');
  qty = computed(() => Number(this.value().quantity) || 0);
  chosenPackage = computed(() => this.packages().find(p => p.id === this.value().packageId) ?? null);
  amount = computed(() => this.mode() === 'package'
    ? (this.chosenPackage()?.price ?? 0) * Math.trunc(this.qty())
    : Math.round(this.qty() * (Number(this.value().unitPrice) || 0) * 100) / 100);

  constructor() {
    inject(RatesApi).packages(this.data.propertyId).subscribe({ next: p => this.packages.set(p.filter(x => x.isActive)) });
    this.form.controls.mode.valueChanges.subscribe(m => {
      const charge = m === 'charge';
      for (const c of [this.form.controls.category, this.form.controls.description, this.form.controls.unitPrice]) {
        if (charge) c.enable({ emitEvent: false }); else c.disable({ emitEvent: false });
      }
      if (charge) this.form.controls.packageId.clearValidators(); else this.form.controls.packageId.setValidators(Validators.required);
      this.form.controls.packageId.updateValueAndValidity({ emitEvent: false });
    });
  }

  err(field: string) { return this.serverErrors()[field] ?? ''; }

  save() {
    this.error.set(''); this.serverErrors.set({});
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    const pkg = v.mode === 'package';
    this.busy.set(true);
    this.api.postCharge(this.data.propertyId, this.data.folio.id, {
      category: pkg ? null : (v.category || null), description: pkg ? null : v.description.trim(), quantity: Number(v.quantity),
      unitPrice: pkg ? 0 : Number(v.unitPrice), serviceDate: v.serviceDate || null, packageId: pkg ? v.packageId : null,
    }, this.key).subscribe({
      next: f => this.ref.close(f),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const fields = showFieldErrors(this.form, p);
        this.serverErrors.set(fields);
        if (!Object.keys(fields).length) this.error.set(p.detail);
        else if (fields['idempotency-Key']) this.error.set(fields['idempotency-Key']);
      },
    });
  }
}

// ------------------------------------------------------------------ reverse a charge

export interface ReverseData { propertyId: string; folio: Folio; item: FolioItem; }

/** Cancels a charge with a reversal line. The charge stays on the bill, marked reversed, and the reason is kept. */
@Component({
  selector: 'app-reverse-charge-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Reverse a charge</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="reverse-form">
      <p class="line"><b>{{ data.item.description }}</b> · {{ shortDate(data.item.serviceDate) }} · <b class="num">{{ money(data.item.total) }}</b></p>
      <p class="hint">A reversal line of {{ money(-data.item.total) }} is added; nothing is deleted. The reason is kept on the bill and in the
        audit trail.</p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Reason</mat-label>
        <input matInput formControlName="reason" maxlength="500" placeholder="e.g. Posted to the wrong guest" />
        <mat-error>{{ serverError() || 'Say why the charge is reversed (at least 3 characters), e.g. Posted to the wrong guest.' }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button color="warn" type="submit" form="reverse-form" [disabled]="busy()">Reverse</button>
    </div>
  `,
  styles: `
    form { min-width: min(440px, 82vw); display: flex; flex-direction: column; }
    .line { margin: 0 0 8px; }
    .hint { color: #5b6b6e; margin: 0 0 12px; }
    .full { width: 100%; }
    .err { color: #b3261e; }
  `,
})
export class ReverseChargeDialog {
  data = inject<ReverseData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<ReverseChargeDialog, Folio>);
  private api = inject(FoliosApi);
  money = rupeesExact;
  shortDate = shortDate;
  busy = signal(false);
  error = signal('');
  serverError = signal('');
  form = inject(FormBuilder).nonNullable.group({ reason: ['', [minTrimmed(3), Validators.maxLength(500)]] });

  save() {
    this.error.set(''); this.serverError.set('');
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.api.reverse(this.data.propertyId, this.data.folio.id, this.data.item.id, this.form.controls.reason.value.trim()).subscribe({
      next: f => this.ref.close(f),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const f = showFieldErrors(this.form, p)['reason'];
        if (f) this.serverError.set(f); else this.error.set(p.detail);
      },
    });
  }
}

// ------------------------------------------------------------------ split the bill

export interface SplitData { propertyId: string; folio: Folio; }
export interface SplitResult { from: Folio; to: Folio; }

/** Moves chosen lines to another folio of the same stay (e.g. the company pays the room, the guest pays the rest). Each
 *  line is reversed here and posted there, so both bills show what happened and the stay's total doesn't change. */
@Component({
  selector: 'app-split-folio-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatRadioModule],
  template: `
    <h2 mat-dialog-title>Move lines from <span class="num">{{ data.folio.number }}</span></h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="split-form">
      <p class="hint">Choose the lines to move. They are reversed on this folio and posted on the other one.</p>
      <div class="lines" role="group" aria-label="Lines to move">
        @for (i of lines; track i.id) {
          <mat-checkbox [checked]="picked().has(i.id)" (change)="toggle(i.id)">
            {{ shortDate(i.serviceDate) }} · {{ i.description }} · <b class="num">{{ money(i.total) }}</b>
          </mat-checkbox>
        }
      </div>
      @if (itemsError()) { <p class="err" role="alert">{{ itemsError() }}</p> }

      <mat-radio-group formControlName="target" class="targets" aria-label="Move to">
        @for (f of others; track f.id) { <mat-radio-button [value]="f.id">{{ f.number }} · {{ f.name }}</mat-radio-button> }
        <mat-radio-button value="new">A new folio</mat-radio-button>
      </mat-radio-group>
      @if (form.controls.target.value === 'new') {
        <mat-form-field appearance="outline" class="full">
          <mat-label>Name of the new folio</mat-label>
          <input matInput formControlName="name" maxlength="100" placeholder="e.g. Company: Acme Travels" />
          <mat-error>{{ nameError() || 'Name the new folio, e.g. Company: Acme Travels.' }}</mat-error>
        </mat-form-field>
      }
      <p class="amount">Moves <b class="num">{{ money(total()) }}</b> ({{ picked().size }} line{{ picked().size === 1 ? '' : 's' }})</p>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="split-form" [disabled]="busy()">Move lines</button>
    </div>
  `,
  styles: `
    form { min-width: min(480px, 82vw); display: flex; flex-direction: column; }
    .hint { color: #5b6b6e; margin: 0 0 8px; }
    .lines { display: flex; flex-direction: column; max-height: 260px; overflow: auto; margin-bottom: 12px; }
    .targets { display: flex; flex-direction: column; gap: 4px; margin-bottom: 12px; }
    .full { width: 100%; }
    .amount { margin: 0 0 8px; color: #0e3b43; }
    .err { color: #b3261e; }
  `,
})
export class SplitFolioDialog {
  data = inject<SplitData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<SplitFolioDialog, SplitResult>);
  private api = inject(FoliosApi);
  money = rupeesExact;
  shortDate = shortDate;
  lines = liveCharges(this.data.folio.items);
  others = this.data.folio.folios.filter(f => f.id !== this.data.folio.id);
  picked = signal(new Set<string>());
  total = computed(() => this.lines.filter(i => this.picked().has(i.id)).reduce((s, i) => s + i.total, 0));
  busy = signal(false);
  error = signal('');
  itemsError = signal('');
  nameError = signal('');
  form = inject(FormBuilder).nonNullable.group({
    target: [this.others[0]?.id ?? 'new'],
    name: [''],
  });

  toggle(id: string) {
    const next = new Set(this.picked());
    if (next.has(id)) next.delete(id); else next.add(id);
    this.picked.set(next);
    this.itemsError.set('');
  }

  save() {
    this.error.set(''); this.itemsError.set(''); this.nameError.set('');
    const v = this.form.getRawValue();
    if (!this.picked().size) { this.itemsError.set('Choose at least one line to move.'); return; }
    if (v.target === 'new' && !v.name.trim()) {
      this.form.controls.name.setErrors({ required: true }); this.form.controls.name.markAsTouched(); return;
    }
    this.busy.set(true);
    this.api.split(this.data.propertyId, this.data.folio.id, {
      itemIds: [...this.picked()], targetFolioId: v.target === 'new' ? null : v.target, newFolioName: v.target === 'new' ? v.name.trim() : null,
    }).subscribe({
      next: r => this.ref.close(r),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const f = showFieldErrors(this.form, p);
        if (f['itemIds']) this.itemsError.set(f['itemIds']);
        if (f['newFolioName']) { this.nameError.set(f['newFolioName']); this.form.controls.name.setErrors({ server: f['newFolioName'] }); }
        if (f['targetFolioId']) this.error.set(f['targetFolioId']);
        if (!Object.keys(f).length) this.error.set(p.detail);
      },
    });
  }
}
