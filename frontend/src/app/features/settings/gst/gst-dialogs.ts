import { Component, computed, inject, signal } from '@angular/core';
import { AbstractControl, FormArray, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { controlPath, showFieldErrors, toProblem } from '../../../core/http';
import { ChargeCategory, GstRegistration, Property, RegistrationType, TaxCategory, TaxRule, TaxRuleType } from '../../../core/models';
import { CATEGORIES, categoryLabel } from '../../folios/folios.api';
import { minTrimmed } from '../../rooms/rooms.forms';
import { STATES, TaxApi, fullDay } from './gst.api';

/** GSTIN shape (the API also checks the check digit and the state). */
const GSTIN = /^[0-9]{2}[A-Za-z]{5}[0-9]{4}[A-Za-z][1-9A-Za-z][Zz][0-9A-Za-z]$/;
const gstinShape = (c: AbstractControl): ValidationErrors | null =>
  GSTIN.test(String(c.value ?? '').replace(/\s/g, '')) ? null : { gstin: true };

// ------------------------------------------------------------------ registration

export interface RegistrationData { property: Property; }

/** Adds the GSTIN a property bills under, from the date on its certificate. A registration that runs on is ended the day before. */
@Component({
  selector: 'app-gst-registration-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>GST registration of {{ data.property.name }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="reg-form">
      <mat-form-field appearance="outline" class="full">
        <mat-label>GSTIN</mat-label>
        <input matInput formControlName="gstin" maxlength="20" placeholder="e.g. 30AABCT1234C1Z9" class="num caps" autocomplete="off" />
        <mat-hint>15 characters: state code, PAN, entity number, Z, check digit{{ stateHint() }}</mat-hint>
        <mat-error>{{ err('gstin') || 'A GSTIN has 15 characters: 2-digit state code, 10-character PAN, a digit or letter, Z, a check character.' }}</mat-error>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Legal name</mat-label>
        <input matInput formControlName="legalName" maxlength="200" placeholder="As on the GST certificate" />
        <mat-error>{{ err('legalName') || 'Enter the legal name exactly as on the GST certificate.' }}</mat-error>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Trade name (optional)</mat-label>
        <input matInput formControlName="tradeName" maxlength="200" />
        <mat-error>{{ err('tradeName') }}</mat-error>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Registration type</mat-label>
        <mat-select formControlName="registrationType">
          <mat-option value="REGULAR">Regular: charges GST</mat-option>
          <mat-option value="COMPOSITION">Composition scheme: charges guests no GST</mat-option>
        </mat-select>
        <mat-error>{{ err('registrationType') }}</mat-error>
      </mat-form-field>
      <div class="row">
        <mat-form-field appearance="outline">
          <mat-label>Valid from</mat-label>
          <input matInput type="date" formControlName="effectiveFrom" />
          <mat-error>{{ err('effectiveFrom') || 'Enter the date the registration is valid from.' }}</mat-error>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Valid until (optional)</mat-label>
          <input matInput type="date" formControlName="effectiveTo" />
          <mat-error>{{ err('effectiveTo') }}</mat-error>
        </mat-form-field>
      </div>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="reg-form" [disabled]="busy()">Save registration</button>
    </div>
  `,
  styles: `
    form { min-width: min(460px, 82vw); display: flex; flex-direction: column; gap: 4px; }
    .full { width: 100%; }
    .row { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .caps { text-transform: uppercase; }
    .err { color: #b3261e; }
    @media (max-width: 520px) { .row { grid-template-columns: 1fr; } }
  `,
})
export class RegistrationDialog {
  data = inject<RegistrationData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<RegistrationDialog, GstRegistration>);
  private api = inject(TaxApi);
  busy = signal(false);
  error = signal('');
  private serverErrors = signal<Record<string, string>>({});

  form = inject(FormBuilder).nonNullable.group({
    gstin: ['', [gstinShape]],
    legalName: [this.data.property.legalName ?? '', [minTrimmed(1), Validators.maxLength(200)]],
    tradeName: [this.data.property.name, [Validators.maxLength(200)]],
    registrationType: ['REGULAR' as RegistrationType],
    effectiveFrom: ['', [Validators.required]],
    effectiveTo: [''],
  });
  private gstin = toSignal(this.form.controls.gstin.valueChanges, { initialValue: '' });
  stateHint = computed(() => {
    const s = STATES.find(x => x.code === this.gstin().trim().slice(0, 2));
    return s ? ` · ${s.name}` : '';
  });

  err(field: string) { return this.serverErrors()[field] ?? ''; }

  save() {
    this.serverErrors.set({});
    this.error.set('');
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.api.addRegistration(this.data.property.id, {
      gstin: v.gstin.replace(/\s/g, '').toUpperCase(), legalName: v.legalName.trim(), tradeName: v.tradeName.trim() || null,
      registrationType: v.registrationType, effectiveFrom: v.effectiveFrom, effectiveTo: v.effectiveTo || null,
    }).subscribe({
      next: r => this.ref.close(r),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        this.serverErrors.set(showFieldErrors(this.form, p));
        this.error.set(p.detail);
      },
    });
  }
}

// ------------------------------------------------------------------ category

/** Adds a tax category (what is taxed, its SAC/HSN code and which bill charges it covers). */
@Component({
  selector: 'app-tax-category-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>New tax category</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="cat-form">
      <div class="row">
        <mat-form-field appearance="outline">
          <mat-label>Name</mat-label>
          <input matInput formControlName="name" maxlength="100" placeholder="e.g. Passenger transport" />
          <mat-error>{{ err('name') || 'Enter a name, e.g. Passenger transport.' }}</mat-error>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Code</mat-label>
          <input matInput formControlName="code" maxlength="30" placeholder="e.g. TRANSPORT" class="caps" />
          <mat-error>{{ err('code') || 'Enter a code: 2–30 letters, digits or underscore, starting with a letter.' }}</mat-error>
        </mat-form-field>
      </div>
      <div class="row">
        <mat-form-field appearance="outline">
          <mat-label>SAC / HSN code</mat-label>
          <input matInput formControlName="hsnSac" maxlength="8" inputmode="numeric" class="num" placeholder="e.g. 996412" />
          <mat-error>{{ err('hsnSac') || 'Enter the SAC or HSN code: 4, 6 or 8 digits.' }}</mat-error>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Covers bill charges</mat-label>
          <mat-select formControlName="chargeCategory">
            <mat-option value="">None</mat-option>
            @for (c of categories; track c) { <mat-option [value]="c">{{ label(c) }}</mat-option> }
          </mat-select>
          <mat-hint>Charges in it get this category's GST</mat-hint>
          <mat-error>{{ err('chargeCategory') }}</mat-error>
        </mat-form-field>
      </div>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Description (optional)</mat-label>
        <input matInput formControlName="description" maxlength="500" />
        <mat-error>{{ err('description') }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="cat-form" [disabled]="busy()">Add category</button>
    </div>
  `,
  styles: `
    form { min-width: min(500px, 82vw); display: flex; flex-direction: column; gap: 4px; }
    .full { width: 100%; }
    .row { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .caps { text-transform: uppercase; }
    .err { color: #b3261e; }
    @media (max-width: 520px) { .row { grid-template-columns: 1fr; } }
  `,
})
export class CategoryDialog {
  private ref = inject(MatDialogRef<CategoryDialog, TaxCategory>);
  private api = inject(TaxApi);
  categories = CATEGORIES;
  label = categoryLabel;
  busy = signal(false);
  error = signal('');
  private serverErrors = signal<Record<string, string>>({});

  form = inject(FormBuilder).nonNullable.group({
    name: ['', [minTrimmed(1), Validators.maxLength(100)]],
    code: ['', [Validators.pattern(/^[A-Za-z][A-Za-z0-9_]{1,29}$/)]],
    hsnSac: ['', [Validators.pattern(/^([0-9]{4}|[0-9]{6}|[0-9]{8})$/)]],
    chargeCategory: ['' as ChargeCategory | ''],
    description: ['', [Validators.maxLength(500)]],
  });

  err(field: string) { return this.serverErrors()[field] ?? ''; }

  save() {
    this.serverErrors.set({});
    this.error.set('');
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.api.addCategory({
      code: v.code.trim().toUpperCase(), name: v.name.trim(), hsnSac: v.hsnSac.trim(), chargeCategory: v.chargeCategory || null,
      description: v.description.trim() || null,
    }).subscribe({
      next: c => this.ref.close(c),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        this.serverErrors.set(showFieldErrors(this.form, p));
        this.error.set(p.detail);
      },
    });
  }
}

// ------------------------------------------------------------------ rule

export interface RuleData { categories: TaxCategory[]; categoryId: string; today: string; }
export interface RuleResult { rule: TaxRule; closedRule: TaxRule | null; }

/**
 * Adds a rule to a category from a date (today or later): one rate (flat), or rates by the value of one unit for one night
 * (slabs, e.g. up to ₹7,500 at 5% and above it at 18%). The category's current rule ends the day before. Nothing changes on
 * charges already posted.
 */
@Component({
  selector: 'app-tax-rule-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatButtonToggleModule, MatCheckboxModule, MatFormFieldModule, MatIconModule,
            MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>New GST rule</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="rule-form">
      <mat-form-field appearance="outline" class="full">
        <mat-label>Tax category</mat-label>
        <mat-select formControlName="taxCategoryId">
          @for (c of data.categories; track c.id) { <mat-option [value]="c.id">{{ c.name }} · SAC {{ c.hsnSac }}</mat-option> }
        </mat-select>
        <mat-error>{{ err('taxCategoryId') || 'Choose the tax category the rule is for.' }}</mat-error>
      </mat-form-field>
      <div class="row">
        <mat-form-field appearance="outline">
          <mat-label>Applies from</mat-label>
          <input matInput type="date" formControlName="effectiveFrom" [min]="data.today" />
          <mat-hint>The rule running now ends the day before</mat-hint>
          <mat-error>{{ err('effectiveFrom') || 'Enter the first day the rule applies (today or later).' }}</mat-error>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Until (optional)</mat-label>
          <input matInput type="date" formControlName="effectiveTo" />
          <mat-error>{{ err('effectiveTo') }}</mat-error>
        </mat-form-field>
      </div>

      <mat-button-toggle-group formControlName="ruleType" aria-label="Rule type" class="mode">
        <mat-button-toggle value="FLAT">One rate</mat-button-toggle>
        <mat-button-toggle value="SLAB">Rates by value per night</mat-button-toggle>
      </mat-button-toggle-group>

      <div formArrayName="slabs" class="slabs" aria-label="Rates">
        @for (s of slabs.controls; track s; let i = $index; let last = $last) {
          <div class="slab" [formGroupName]="i">
            @if (type() === 'SLAB') {
              @if (!last) {
                <mat-form-field appearance="outline">
                  <mat-label>Up to (₹ per unit per night)</mat-label>
                  <input matInput type="number" formControlName="upTo" min="0.01" step="0.01" />
                  <mat-error>{{ err('slabs.' + i + '.upTo') || 'Enter the upper limit in rupees, e.g. 7500.' }}</mat-error>
                </mat-form-field>
              } @else { <span class="above">Above that</span> }
            }
            <mat-form-field appearance="outline" class="rate">
              <mat-label>GST rate (%)</mat-label>
              <input matInput type="number" formControlName="rate" min="0" max="100" step="0.01" />
              <mat-error>{{ err('slabs.' + i + '.rate') || 'Enter a rate from 0 to 100.' }}</mat-error>
            </mat-form-field>
            <mat-checkbox formControlName="inputTaxCredit">With input tax credit</mat-checkbox>
            @if (type() === 'SLAB' && slabs.length > 2) {
              <button mat-icon-button type="button" (click)="removeSlab(i)" [attr.aria-label]="'Remove rate ' + (i + 1)"><mat-icon>close</mat-icon></button>
            }
          </div>
        }
        @if (type() === 'SLAB') { <button mat-button type="button" (click)="addSlab()"><mat-icon>add</mat-icon>Add a rate</button> }
        @if (err('slabs')) { <p class="err" role="alert">{{ err('slabs') }}</p> }
      </div>

      <mat-form-field appearance="outline" class="full">
        <mat-label>Source</mat-label>
        <input matInput formControlName="source" maxlength="500" placeholder="e.g. Notification 05/2025-Central Tax (Rate)" />
        <mat-hint>Where the rule comes from, so it can be checked later</mat-hint>
        <mat-error>{{ err('source') || 'Say where the rule comes from, e.g. the notification number.' }}</mat-error>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Notes (optional)</mat-label>
        <textarea matInput formControlName="notes" maxlength="1000" rows="2"></textarea>
        <mat-error>{{ err('notes') }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="rule-form" [disabled]="busy()">Add rule</button>
    </div>
  `,
  styles: `
    form { min-width: min(560px, 84vw); display: flex; flex-direction: column; gap: 4px; }
    .full { width: 100%; }
    .row { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .mode { margin: 4px 0 12px; align-self: flex-start; flex-wrap: wrap; }
    .slabs { display: flex; flex-direction: column; gap: 4px; margin-bottom: 8px; }
    .slab { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; }
    .slab mat-form-field { flex: 1 1 160px; }
    .slab .rate { flex: 0 1 140px; }
    .above { flex: 1 1 160px; color: #5b6b6e; }
    .err { color: #b3261e; }
    @media (max-width: 520px) { .row { grid-template-columns: 1fr; } }
  `,
})
export class RuleDialog {
  data = inject<RuleData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<RuleDialog, RuleResult>);
  private api = inject(TaxApi);
  private fb = inject(FormBuilder).nonNullable;
  busy = signal(false);
  error = signal('');
  private serverErrors = signal<Record<string, string>>({});

  form = this.fb.group({
    taxCategoryId: [this.data.categoryId, [Validators.required]],
    effectiveFrom: ['', [Validators.required]],
    effectiveTo: [''],
    ruleType: ['FLAT' as TaxRuleType],
    slabs: this.fb.array([this.slab()]),
    source: ['', [minTrimmed(3), Validators.maxLength(500)]],
    notes: ['', [Validators.maxLength(1000)]],
  });
  get slabs() { return this.form.controls.slabs as FormArray; }
  type = toSignal(this.form.controls.ruleType.valueChanges, { initialValue: 'FLAT' as TaxRuleType });

  constructor() {
    this.form.controls.ruleType.valueChanges.subscribe(t => {
      // a slab rule starts as "up to … / above that"; a flat rule keeps only one rate
      if (t === 'SLAB' && this.slabs.length < 2) this.slabs.insert(0, this.slab());
      if (t === 'FLAT') while (this.slabs.length > 1) this.slabs.removeAt(0);
      this.syncLimits();
    });
  }

  private slab() {
    return this.fb.group({ upTo: [null as number | null], rate: [null as number | null, [Validators.required, Validators.min(0), Validators.max(100)]],
                           inputTaxCredit: [false] });
  }
  addSlab() { this.slabs.insert(this.slabs.length - 1, this.slab()); this.syncLimits(); }
  removeSlab(i: number) { this.slabs.removeAt(i); this.syncLimits(); }

  /** Every rate but the last needs an upper limit in a slab rule. */
  private syncLimits() {
    this.slabs.controls.forEach((c, i) => {
      const upTo = c.get('upTo')!;
      upTo.setValidators(this.form.controls.ruleType.value === 'SLAB' && i < this.slabs.length - 1 ? [Validators.required, Validators.min(0.01)] : []);
      upTo.updateValueAndValidity();
    });
  }

  err(field: string) { return this.serverErrors()[field] ?? ''; }

  save() {
    this.serverErrors.set({});
    this.error.set('');
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    const slabs = (v.slabs as { upTo: number | null; rate: number; inputTaxCredit: boolean }[]).map((s, i, all) => ({
      upTo: v.ruleType === 'SLAB' && i < all.length - 1 ? Number(s.upTo) : null, rate: Number(s.rate), inputTaxCredit: s.inputTaxCredit,
    }));
    this.busy.set(true);
    this.api.addRule({
      taxCategoryId: v.taxCategoryId, effectiveFrom: v.effectiveFrom, effectiveTo: v.effectiveTo || null, ruleType: v.ruleType, slabs,
      source: v.source.trim(), notes: v.notes.trim() || null,
    }).subscribe({
      next: r => this.ref.close(r),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const errs = showFieldErrors(this.form, p);
        // "Slabs[0].Rate" → "slabs.0.rate", the way the template asks for it
        this.serverErrors.set(Object.fromEntries(Object.entries(errs).map(([k, m]) => [controlPath(k), m])));
        this.error.set(p.detail);
      },
    });
  }
}

/** "Accommodation now ends on 30 Nov 2026" — what adding a rule did to the one before it. */
export function closedNote(r: RuleResult): string {
  return r.closedRule?.effectiveTo ? ` The rule before it now ends on ${fullDay(r.closedRule.effectiveTo)}.` : '';
}
