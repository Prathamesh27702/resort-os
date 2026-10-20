import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../../core/auth.service';
import { showFieldErrors, toProblem } from '../../../core/http';
import { ChargeCategory, GstRegistration, TaxCalculation, TaxCategory, TaxRule } from '../../../core/models';
import { PropertyContext } from '../../../core/property-context';
import { CATEGORIES, categoryLabel } from '../../folios/folios.api';
import { rupeesExact } from '../rates/rates.api';
import { CategoryDialog, RegistrationDialog, RuleDialog, RuleResult, closedNote } from './gst-dialogs';
import { STATES, TaxApi, fullDay, pct, periodPhrase, periodText, registrationTypeLabel, slabsText } from './gst.api';

/**
 * Settings → GST (Sprint 9). The property's GST registration, the organisation's tax categories with their rules over time,
 * and a calculator that shows what GST a charge gets on a given day, with the same engine the bills use. Rules are data
 * with dates: the accountant adds next month's rate here and it applies from its first day, with no deployment.
 */
@Component({
  selector: 'app-gst',
  imports: [ReactiveFormsModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatSnackBarModule],
  template: `
    <div class="page wide">
      <div class="page-head">
        <div>
          <h1>GST</h1>
          <p>Registration, what is taxed and at what rate. Rates are rules with dates: add next month's rule here and bills use it from its first day.
             A charge keeps the GST it was posted with.</p>
        </div>
      </div>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

      <!-- registration -->
      <section class="panel" aria-labelledby="reg-h">
        <div class="sec-head">
          <h2 id="reg-h">Registration · {{ propertyName() }}</h2>
          @if (canRegister()) { <button mat-stroked-button (click)="addRegistration()"><mat-icon>add</mat-icon>Add registration</button> }
        </div>
        @if (registrations().length) {
          <table class="data">
            <thead><tr><th>GSTIN</th><th>Legal name</th><th class="hide-sm">State</th><th class="hide-sm">Type</th><th>Valid</th></tr></thead>
            <tbody>
              @for (r of registrations(); track r.id) {
                <tr [attr.data-gstin]="r.gstin">
                  <td class="num"><b>{{ r.gstin }}</b> @if (r.current) { <span class="chip ok">Current</span> }</td>
                  <td>{{ r.legalName }}@if (r.tradeName) { <br /><small class="mute">{{ r.tradeName }}</small> }</td>
                  <td class="hide-sm">{{ r.stateName }} ({{ r.stateCode }})</td>
                  <td class="hide-sm">{{ typeLabel(r.registrationType) }}</td>
                  <td>{{ period(r.effectiveFrom, r.effectiveTo) }}</td>
                </tr>
              }
            </tbody>
          </table>
        } @else if (loaded()) {
          <p class="empty" role="note">{{ propertyName() }} has no GST registration, so its bills carry no GST. Add its GSTIN to start charging GST.</p>
        }
      </section>

      <!-- categories and rules -->
      <section class="panel" aria-labelledby="rules-h">
        <div class="sec-head">
          <h2 id="rules-h">What is taxed</h2>
          @if (canManage()) {
            <div class="actions">
              <button mat-stroked-button (click)="addCategory()"><mat-icon>add</mat-icon>Add category</button>
              <button mat-flat-button (click)="addRule()" [disabled]="!categories().length"><mat-icon>add</mat-icon>Add rule</button>
            </div>
          }
        </div>
        @if (unverified() > 0) {
          <p class="note" role="note" data-testid="unverified">{{ unverified() }} {{ unverified() === 1 ? 'rule is' : 'rules are' }} not verified yet.
            The rules shipped with Resort OS were entered from public notifications: your accountant must check each against its source and press Verify before go-live.</p>
        }
        @for (c of categories(); track c.id) {
          <article class="cat" [attr.data-category]="c.code">
            <header>
              <h3>{{ c.name }} <small class="mute num">SAC/HSN {{ c.hsnSac }}</small></h3>
              <span class="mute">{{ c.chargeCategory ? 'Bill charges: ' + label(c.chargeCategory) : 'Not linked to bill charges' }}</span>
            </header>
            <table class="data rules">
              <thead><tr><th>Applies</th><th>Rate</th><th class="hide-sm">Source</th><th class="right">Checked</th></tr></thead>
              <tbody>
                @for (r of rulesOf(c.id); track r.id) {
                  <tr [class.past]="r.state === 'PAST'" [attr.data-rule]="r.effectiveFrom">
                    <td>{{ period(r.effectiveFrom, r.effectiveTo) }}
                      @if (r.state !== 'PAST') { <span [class]="'chip ' + (r.state === 'CURRENT' ? 'ok' : 'wait')">{{ r.state === 'CURRENT' ? 'Now' : 'Upcoming' }}</span> }</td>
                    <td><b>{{ slabs(r.slabs) }}</b></td>
                    <td class="hide-sm"><small>{{ r.source }}</small>@if (r.notes) { <br /><small class="mute">{{ r.notes }}</small> }</td>
                    <td class="right">
                      @if (r.verifiedAt) { <small class="ok-text">✓ {{ r.verifiedByName ?? 'Verified' }}</small> }
                      @else if (canManage()) {
                        <button mat-button (click)="verify(r)" [disabled]="busy()" [attr.aria-label]="'Verify ' + c.name + ' ' + period(r.effectiveFrom, r.effectiveTo)">Verify</button>
                      } @else { <small class="mute">Not verified</small> }
                    </td>
                  </tr>
                } @empty { <tr><td colspan="4" class="empty">No rule yet: {{ c.chargeCategory ? label(c.chargeCategory).toLowerCase() + ' charges can’t be posted while the property is registered.' : 'nothing uses it.' }}</td></tr> }
              </tbody>
            </table>
          </article>
        }
        @if (untaxed().length) {
          <p class="mute small">No GST on: {{ untaxed() }}. Add a category for them if they should be taxed.</p>
        }
      </section>

      <!-- calculator -->
      <section class="panel" aria-labelledby="calc-h">
        <h2 id="calc-h">Try a charge</h2>
        <form [formGroup]="calc" (ngSubmit)="calculate()" class="calc">
          <mat-form-field appearance="outline">
            <mat-label>What</mat-label>
            <mat-select formControlName="chargeCategory">
              @for (c of chargeCategories; track c) { <mat-option [value]="c">{{ label(c) }}</mat-option> }
            </mat-select>
            <mat-error>{{ calcErr('chargeCategory') || 'Choose what is charged.' }}</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Day of the service</mat-label>
            <input matInput type="date" formControlName="date" />
            <mat-error>{{ calcErr('date') || 'Choose the day.' }}</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Amount (₹, before GST)</mat-label>
            <input matInput type="number" formControlName="amount" min="0" step="0.01" />
            <mat-error>{{ calcErr('amount') || 'Enter the amount in rupees.' }}</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Value per unit per night (₹)</mat-label>
            <input matInput type="number" formControlName="unitValue" min="0" step="0.01" />
            <mat-hint>Picks the slab; empty = the amount</mat-hint>
            <mat-error>{{ calcErr('unitValue') }}</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Place of supply</mat-label>
            <mat-select formControlName="placeOfSupply">
              <mat-option value="">This property's state</mat-option>
              @for (s of states; track s.code) { <mat-option [value]="s.code">{{ s.name }} ({{ s.code }})</mat-option> }
            </mat-select>
            <mat-error>{{ calcErr('placeOfSupply') }}</mat-error>
          </mat-form-field>
          <div class="go"><button mat-flat-button type="submit" [disabled]="busy()">Calculate</button></div>
        </form>
        @if (calcError()) { <p class="err" role="alert">{{ calcError() }}</p> }
        @if (result(); as r) {
          <div class="result" data-testid="tax-result" aria-live="polite">
            @if (r.rule) {
              <p>{{ r.category?.name }} on <b>{{ day(r.date) }}</b>: rule {{ phrase(r.rule.effectiveFrom, r.rule.effectiveTo) }} ·
                 <b>{{ r.slab ? pct(r.slab.rate) : '' }}</b>@if (r.rule.ruleType === 'SLAB') { (value per unit per night {{ money(r.unitValue) }}) }</p>
            }
            @if (r.taxed) {
              <table class="data">
                <tbody>
                  <tr><td>Amount</td><td class="right num">{{ money(r.amount) }}</td></tr>
                  @for (t of r.components; track t.code + t.rate) {
                    <tr [attr.data-tax]="t.code"><td>{{ t.code }} {{ pct(t.rate) }}</td><td class="right num">{{ money(t.amount) }}</td></tr>
                  }
                  <tr class="sum"><td><b>Total</b></td><td class="right num"><b>{{ money(r.total) }}</b></td></tr>
                </tbody>
              </table>
              <p class="mute small">{{ r.intraState ? 'Same state: CGST + SGST' : 'Another state: IGST' }} · supplier {{ r.supplierState }} ({{ r.gstin }}) ·
                 place of supply {{ r.placeOfSupply }}</p>
            } @else {
              <p class="note" role="note">{{ r.noTaxReason }}</p>
            }
          </div>
        }
      </section>
    </div>
  `,
  styles: `
    .wide { max-width: 1100px; }
    .panel { background: #fff; border: 1px solid #d9e0de; border-radius: 10px; padding: 14px 16px; margin-bottom: 16px; }
    .sec-head { display: flex; justify-content: space-between; align-items: center; gap: 12px; flex-wrap: wrap; margin-bottom: 8px; }
    h2 { margin: 0 0 8px; font-size: 1.1rem; }
    h3 { margin: 0; font-size: 1rem; }
    .actions { display: flex; gap: 8px; flex-wrap: wrap; }
    .cat { border-top: 1px solid #e4e9e8; padding: 12px 0 4px; }
    .cat header { display: flex; justify-content: space-between; gap: 12px; flex-wrap: wrap; align-items: baseline; margin-bottom: 6px; }
    tr.past td { color: #7a8a8c; }
    .chip { margin-left: 6px; }
    .chip.ok { background: #e1f2e8; color: #1d5b36; }
    .chip.wait { background: #fbefd8; color: #8a5a00; }
    .ok-text { color: #1d5b36; }
    .note { background: #fbefd8; color: #6b4600; border-radius: 8px; padding: 10px 12px; }
    .mute { color: #5b6b6e; }
    .small { font-size: .85rem; }
    .right { text-align: right; }
    .err { color: #b3261e; }
    .empty { color: #5b6b6e; padding: 12px 0; }
    .calc { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 0 12px; }
    .go { display: flex; align-items: flex-start; padding-top: 8px; }
    .result { margin-top: 8px; max-width: 520px; }
    .result tr.sum td { border-top: 2px solid #d9e0de; }
    @media (max-width: 760px) {
      .calc { grid-template-columns: 1fr; }
      table.data td, table.data th { padding: 10px 8px; }
    }
  `,
})
export class GstPage {
  props = inject(PropertyContext);
  private auth = inject(AuthService);
  private api = inject(TaxApi);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);

  registrations = signal<GstRegistration[]>([]);
  categories = signal<TaxCategory[]>([]);
  rules = signal<TaxRule[]>([]);
  result = signal<TaxCalculation | null>(null);
  loaded = signal(false);
  busy = signal(false);
  error = signal('');
  calcError = signal('');
  private calcErrors = signal<Record<string, string>>({});

  chargeCategories = CATEGORIES;
  states = STATES;
  label = categoryLabel;
  money = rupeesExact;
  pct = pct;
  slabs = slabsText;
  period = periodText;
  phrase = periodPhrase;
  day = fullDay;
  typeLabel = registrationTypeLabel;

  propertyName = computed(() => this.props.current()?.name ?? '');
  canManage = computed(() => this.auth.can('tax.manage'));
  canRegister = computed(() => this.auth.can('tax.manage', this.props.current()?.id));
  unverified = computed(() => this.rules().filter(r => !r.verifiedAt && r.state !== 'PAST').length);
  untaxed = computed(() => {
    const covered = new Set(this.categories().map(c => c.chargeCategory));
    return CATEGORIES.filter(c => !covered.has(c)).map(categoryLabel).join(', ');
  });

  calc = inject(FormBuilder).nonNullable.group({
    chargeCategory: ['ROOM' as ChargeCategory, [Validators.required]],
    date: ['', [Validators.required]],
    amount: [3500 as number | null, [Validators.required, Validators.min(0)]],
    unitValue: [null as number | null, [Validators.min(0)]],
    placeOfSupply: [''],
  });

  constructor() {
    effect(() => {
      const p = this.props.current();
      if (!p) return;
      if (!this.calc.controls.date.value) this.calc.controls.date.setValue(p.localDate);
      this.load(p.id);
    });
  }

  rulesOf(categoryId: string) { return this.rules().filter(r => r.taxCategoryId === categoryId); }
  calcErr(field: string) { return this.calcErrors()[field] ?? ''; }

  private load(pid: string) {
    this.error.set('');
    this.api.registrations(pid).subscribe({
      next: r => { this.registrations.set(r); this.loaded.set(true); },
      error: e => this.error.set(toProblem(e).detail),
    });
    this.reloadRules();
  }

  private reloadRules() {
    this.api.categories().subscribe({ next: c => this.categories.set(c), error: e => this.error.set(toProblem(e).detail) });
    this.api.rules().subscribe({ next: r => this.rules.set(r), error: e => this.error.set(toProblem(e).detail) });
  }

  addRegistration() {
    const property = this.props.current();
    if (!property) return;
    this.dialog.open(RegistrationDialog, { data: { property }, autoFocus: 'first-tabbable' }).afterClosed().subscribe((r?: GstRegistration) => {
      if (!r) return;
      this.snack.open(`Registered: ${r.gstin} (${r.stateName}) ${periodPhrase(r.effectiveFrom, r.effectiveTo)}`, 'OK', { duration: 5000 });
      this.load(property.id);
    });
  }

  addCategory() {
    this.dialog.open(CategoryDialog, { autoFocus: 'first-tabbable' }).afterClosed().subscribe((c?: TaxCategory) => {
      if (!c) return;
      this.snack.open(`Added ${c.name}. Add its rule next.`, 'OK', { duration: 5000 });
      this.reloadRules();
    });
  }

  addRule(categoryId?: string) {
    const property = this.props.current();
    if (!property) return;
    const categories = this.categories();
    this.dialog.open(RuleDialog, { data: { categories, categoryId: categoryId ?? categories[0]?.id ?? '', today: property.localDate }, autoFocus: 'first-tabbable' })
      .afterClosed().subscribe((r?: RuleResult) => {
        if (!r) return;
        this.snack.open(`${r.rule.categoryName}: ${slabsText(r.rule.slabs)} ${periodPhrase(r.rule.effectiveFrom, r.rule.effectiveTo)}.${closedNote(r)}`,
          'OK', { duration: 8000 });
        this.reloadRules();
      });
  }

  verify(rule: TaxRule) {
    this.busy.set(true);
    this.api.verify(rule.id).subscribe({
      next: r => {
        this.busy.set(false);
        this.rules.update(list => list.map(x => (x.id === r.id ? r : x)));
        this.snack.open(`Verified: ${r.categoryName} ${periodPhrase(r.effectiveFrom, r.effectiveTo)}`, 'OK', { duration: 4000 });
      },
      error: e => { this.busy.set(false); this.error.set(toProblem(e).detail); },
    });
  }

  calculate() {
    const property = this.props.current();
    this.calcErrors.set({});
    this.calcError.set('');
    if (!property) return;
    if (this.calc.invalid) { this.calc.markAllAsTouched(); return; }
    const v = this.calc.getRawValue();
    this.busy.set(true);
    this.api.calculate({
      propertyId: property.id, taxCategoryId: null, chargeCategory: v.chargeCategory, date: v.date || null, amount: Number(v.amount),
      unitValue: v.unitValue === null || (v.unitValue as unknown) === '' ? null : Number(v.unitValue), placeOfSupply: v.placeOfSupply || null,
    }).subscribe({
      next: r => { this.busy.set(false); this.result.set(r); },
      error: e => {
        this.busy.set(false);
        this.result.set(null);
        const p = toProblem(e);
        this.calcErrors.set(showFieldErrors(this.calc, p));
        this.calcError.set(p.detail);
      },
    });
  }
}
