import { Component, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../../core/auth.service';
import { showFieldErrors, toProblem } from '../../../core/http';
import { AccommodationType, MEAL_PLAN_LABELS, MEAL_PLANS, Problem, RatePlan, RatePrice, RateQuote, Room } from '../../../core/models';
import { PropertyContext } from '../../../core/property-context';
import { RoomsApi, addDays } from '../../rooms/rooms.api';
import { RatesApi, dayLabel, rupees, weekday } from './rates.api';
import { moneyError, planForm, pricesForm, quoteForm } from './rates.forms';

const GRID_DAYS = 14;
const DAYS = [
  { value: 'Monday', short: 'Mon' }, { value: 'Tuesday', short: 'Tue' }, { value: 'Wednesday', short: 'Wed' },
  { value: 'Thursday', short: 'Thu' }, { value: 'Friday', short: 'Fri' }, { value: 'Saturday', short: 'Sat' },
  { value: 'Sunday', short: 'Sun' },
];

/** Rate plans, the rate grid (room type × night) with bulk edit, and a quote to check the result. */
@Component({
  selector: 'app-rates',
  imports: [ReactiveFormsModule, RouterLink, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatInputModule, MatSelectModule,
    MatSlideToggleModule, MatSnackBarModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Rates</h1>
          <p>Nightly prices at {{ propertyName() }}, before tax. Dormitory prices are per bed.</p>
        </div>
        <div class="head-actions">
          <a mat-button routerLink="/settings/packages">Packages</a>
          @if (canManage()) { <button mat-stroked-button type="button" (click)="newPlan()">Add rate plan</button> }
        </div>
      </div>

      @if (loadError()) { <p class="error" role="alert">{{ loadError() }}</p> }

      @if (loaded() && !plans().length && planMode() === 'none') {
        <section class="panel empty">
          <p>No rate plans yet. Most resorts start with <b>Best available rate (BAR)</b>, room only, and set prices for each room type.</p>
          @if (canManage()) { <button mat-flat-button type="button" (click)="newPlan()">Add the first rate plan</button> }
        </section>
      }

      <!-- ------------------------------------------------------------ plan editor -->
      @if (planMode() !== 'none') {
        <form class="panel box" [formGroup]="plan" (ngSubmit)="savePlan()" aria-label="Rate plan">
          <h2>{{ planMode() === 'new' ? 'Add rate plan' : 'Edit ' + selectedPlan()?.name }}</h2>
          <div class="form-grid">
            <mat-form-field appearance="outline">
              <mat-label>Name</mat-label>
              <input matInput formControlName="name" placeholder="Best available rate" />
              <mat-error>{{ plan.controls.name.errors?.['server'] ?? 'Enter a name, e.g. Best available rate.' }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Code</mat-label>
              <input matInput formControlName="code" placeholder="BAR" (input)="upper(plan.controls.code)" />
              <mat-hint>Letters, digits and _. Can't be changed later.</mat-hint>
              <mat-error>{{ plan.controls.code.errors?.['server'] ?? 'Code: 2–30 letters, digits or underscore, e.g. BAR.' }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Meal plan</mat-label>
              <mat-select formControlName="mealPlan">
                @for (m of mealPlans; track m) { <mat-option [value]="m">{{ mealLabel[m] }}</mat-option> }
              </mat-select>
              <mat-error>{{ plan.controls.mealPlan.errors?.['server'] ?? 'Choose a meal plan.' }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Display order</mat-label>
              <input matInput type="number" min="0" formControlName="sortOrder" />
              <mat-error>{{ plan.controls.sortOrder.errors?.['server'] ?? 'Sort order must be between 0 and 100000.' }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline" class="full">
              <mat-label>Description</mat-label>
              <textarea matInput formControlName="description" rows="2" maxlength="1000"></textarea>
              <mat-error>{{ plan.controls.description.errors?.['server'] ?? 'Description can be at most 1000 characters.' }}</mat-error>
            </mat-form-field>
          </div>
          <mat-slide-toggle formControlName="isDefault">Default plan (quotes use it when no plan is chosen)</mat-slide-toggle>
          @if (plan.controls.isDefault.errors?.['server']) { <p class="error">{{ plan.controls.isDefault.errors?.['server'] }}</p> }
          @if (planMode() === 'edit') {
            <mat-slide-toggle formControlName="isActive">Active</mat-slide-toggle>
            @if (plan.controls.isActive.errors?.['server']) { <p class="error">{{ plan.controls.isActive.errors?.['server'] }}</p> }
          }
          @if (planError()) { <p class="error" role="alert">{{ planError() }}</p> }
          <div class="actions">
            <button mat-flat-button type="submit" [disabled]="busy()">{{ planMode() === 'new' ? 'Add rate plan' : 'Save plan' }}</button>
            <button mat-button type="button" (click)="planMode.set('none')">Cancel</button>
          </div>
        </form>
      }

      @if (plans().length) {
        <!-- ---------------------------------------------------------- grid -->
        <section class="panel box">
          <div class="toolbar">
            <mat-form-field appearance="outline" class="plan-pick">
              <mat-label>Rate plan</mat-label>
              <mat-select [value]="planId()" (selectionChange)="planId.set($event.value)" aria-label="Rate plan">
                @for (p of plans(); track p.id) {
                  <mat-option [value]="p.id">{{ p.name }} ({{ p.code }}){{ p.isDefault ? ' — default' : '' }}{{ p.isActive ? '' : ' — inactive' }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
            @if (canManage() && selectedPlan()) { <button mat-button type="button" (click)="editPlan()">Edit plan</button> }
            <span class="spacer"></span>
            <button mat-stroked-button type="button" (click)="move(-7)" aria-label="Previous week">‹ Week</button>
            <input class="start" type="date" [value]="start()" (change)="setStart($any($event.target).value)" aria-label="First night shown" />
            <button mat-stroked-button type="button" (click)="move(7)" aria-label="Next week">Week ›</button>
          </div>
          @if (selectedPlan(); as sp) {
            <p class="mute small">{{ mealLabel[sp.mealPlan] }}{{ sp.description ? ' · ' + sp.description : '' }}</p>
          }

          <div class="grid-wrap">
            <table class="grid" aria-label="Rate grid">
              <thead>
                <tr>
                  <th scope="col" class="type-col">Room type</th>
                  @for (d of dates(); track d) {
                    <th scope="col" [class.weekend]="isWeekend(d)" [class.today]="d === today()">{{ dayLabel(d) }}</th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (t of types(); track t.id) {
                  <tr>
                    <th scope="row" class="type-col">{{ t.name }} <span class="mute small">{{ t.inventoryMode === 'BED' ? 'per bed' : 'per room' }}</span></th>
                    @for (d of dates(); track d) {
                      @let cell = priceAt(t.id, d);
                      <td [class.weekend]="isWeekend(d)" [class.missing]="!cell" [class.clickable]="canManage()"
                          [attr.tabindex]="canManage() ? 0 : null" (click)="pick(t, d)" (keydown.enter)="pick(t, d)"
                          [attr.aria-label]="t.name + ' ' + dayLabel(d) + ': ' + (cell ? rupees(cell.price) : 'no price')"
                          [attr.data-cell]="t.code + '|' + d">
                        @if (cell) {
                          <span class="price">{{ rupees(cell.price) }}</span>
                          @if (cell.minStay > 1) { <span class="min" title="Minimum stay for arrivals this night">min {{ cell.minStay }}</span> }
                        } @else { <span class="mute">—</span> }
                      </td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <p class="mute small">Shaded columns are Friday and Saturday nights. — means no price yet: those nights can't be quoted or booked in this plan.
            @if (canManage()) { Click a cell to change it. }</p>
        </section>

        <!-- ---------------------------------------------------------- bulk edit -->
        @if (canManage()) {
          <form #bulk class="panel box" [formGroup]="prices" (ngSubmit)="applyPrices()" aria-label="Set prices">
            <h2>Set prices in {{ selectedPlan()?.name }}</h2>
            <div class="form-grid">
              <mat-form-field appearance="outline" class="full">
                <mat-label>Room types</mat-label>
                <mat-select formControlName="accommodationTypeIds" multiple>
                  @for (t of types(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }
                </mat-select>
                <mat-error>{{ prices.controls.accommodationTypeIds.errors?.['server'] ?? 'Choose at least one room type.' }}</mat-error>
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>First night</mat-label>
                <input matInput type="date" formControlName="firstNight" />
                <mat-error>{{ prices.controls.firstNight.errors?.['server'] ?? 'Enter the first night.' }}</mat-error>
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>Last night</mat-label>
                <input matInput type="date" formControlName="lastNight" />
                <mat-hint>{{ nightsHint() }}</mat-hint>
                <mat-error>{{ prices.controls.lastNight.errors?.['server'] ?? 'Enter the last night.' }}</mat-error>
              </mat-form-field>
            </div>
            @if (prices.hasError('lastBeforeFirst')) { <p class="error">The last night can't be before the first night.</p> }
            @if (prices.hasError('tooManyNights')) { <p class="error">One change can cover at most 366 nights; this one covers {{ prices.errors?.['tooManyNights'].nights }}.</p> }

            <div class="days">
              <span class="mute small">Only these nights (none chosen = every night):</span>
              <mat-button-toggle-group formControlName="daysOfWeek" multiple aria-label="Nights of the week">
                @for (d of days; track d.value) { <mat-button-toggle [value]="d.value">{{ d.short }}</mat-button-toggle> }
              </mat-button-toggle-group>
              <button mat-button type="button" (click)="weekendOnly()">Weekend (Fri, Sat)</button>
            </div>
            @if (prices.controls.daysOfWeek.errors?.['server']) { <p class="error">{{ prices.controls.daysOfWeek.errors?.['server'] }}</p> }

            <div class="form-grid four">
              <mat-form-field appearance="outline">
                <mat-label>Price per night (₹)</mat-label>
                <input matInput type="number" min="0" step="0.01" formControlName="price" />
                <mat-hint>{{ anyDorm() ? 'Per bed for dormitories' : 'For the standard guests' }}</mat-hint>
                <mat-error>{{ moneyError(prices.controls.price, 'Price') }}</mat-error>
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>Extra adult (₹)</mat-label>
                <input matInput type="number" min="0" step="0.01" formControlName="extraAdult" />
                <mat-hint>Per night, above standard guests</mat-hint>
                <mat-error>{{ moneyError(prices.controls.extraAdult, 'Extra adult price') }}</mat-error>
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>Extra child (₹)</mat-label>
                <input matInput type="number" min="0" step="0.01" formControlName="extraChild" />
                <mat-error>{{ moneyError(prices.controls.extraChild, 'Extra child price') }}</mat-error>
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>Minimum stay</mat-label>
                <input matInput type="number" min="1" max="30" formControlName="minStay" />
                <mat-hint>Nights, for arrivals on these dates</mat-hint>
                <mat-error>{{ prices.controls.minStay.errors?.['server'] ?? 'Minimum stay must be between 1 and 30 nights.' }}</mat-error>
              </mat-form-field>
            </div>
            @if (anyDorm()) { <p class="mute small">Dormitories are sold per bed: leave the extra adult and child prices at 0 for them.</p> }
            @if (pricesError()) { <p class="error" role="alert">{{ pricesError() }}</p> }
            <div class="actions">
              <button mat-flat-button type="submit" [disabled]="busy()">Apply prices</button>
            </div>
          </form>
        }

        <!-- ---------------------------------------------------------- quote -->
        <form class="panel box" [formGroup]="quoteF" (ngSubmit)="runQuote()" aria-label="Quote">
          <h2>Quote a stay</h2>
          <div class="form-grid four">
            <mat-form-field appearance="outline">
              <mat-label>Room or room type</mat-label>
              <mat-select formControlName="target">
                <mat-optgroup label="Rooms">
                  @for (r of rooms(); track r.id) { <mat-option [value]="'room:' + r.code">{{ r.name }} · {{ r.accommodationTypeName }}</mat-option> }
                </mat-optgroup>
                <mat-optgroup label="Room types">
                  @for (t of types(); track t.id) { <mat-option [value]="'type:' + t.id">{{ t.name }}</mat-option> }
                </mat-optgroup>
              </mat-select>
              <mat-error>{{ quoteF.controls.target.errors?.['server'] ?? 'Choose a room or a room type to quote.' }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Arrival</mat-label>
              <input matInput type="date" formControlName="from" />
              <mat-error>{{ quoteF.controls.from.errors?.['server'] ?? 'Enter the arrival date.' }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Nights</mat-label>
              <input matInput type="number" min="1" max="30" formControlName="nights" />
              <mat-error>{{ quoteF.controls.nights.errors?.['server'] ?? 'A quote covers 1 to 30 nights.' }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Adults</mat-label>
              <input matInput type="number" min="1" max="50" formControlName="adults" />
              <mat-error>{{ quoteF.controls.adults.errors?.['server'] ?? 'Adults must be between 1 and 50.' }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Children</mat-label>
              <input matInput type="number" min="0" max="50" formControlName="children" />
              <mat-error>{{ quoteF.controls.children.errors?.['server'] ?? 'Children must be between 0 and 50.' }}</mat-error>
            </mat-form-field>
          </div>
          <div class="actions">
            <button mat-flat-button type="submit" [disabled]="busy()">Get quote in {{ selectedPlan()?.name }}</button>
          </div>
          @if (quoteError()) { <p class="error" role="alert">{{ quoteError() }}</p> }

          @if (quote(); as q) {
            <div class="quote" aria-label="Quote result">
              <p><b>{{ q.roomCode ? 'Room ' + q.roomCode + ' · ' : '' }}{{ q.accommodationTypeName }}</b>, {{ q.nights }} night{{ q.nights === 1 ? '' : 's' }},
                {{ q.adults }} adult{{ q.adults === 1 ? '' : 's' }}{{ q.children ? ', ' + q.children + ' child' + (q.children === 1 ? '' : 'ren') : '' }}
                · {{ q.ratePlanName }} ({{ q.mealPlan }}){{ q.inventoryMode === 'BED' ? ' · ' + q.units + ' bed' + (q.units === 1 ? '' : 's') : '' }}</p>
              <table class="data">
                <thead><tr><th>Night</th><th class="num">{{ q.inventoryMode === 'BED' ? 'Beds' : 'Room' }}</th><th class="num">Extra adults</th><th class="num">Extra children</th><th class="num">Total</th></tr></thead>
                <tbody>
                  @for (n of q.breakdown; track n.date) {
                    <tr>
                      <td>{{ dayLabel(n.date) }}</td>
                      <td class="num">{{ rupees(n.baseAmount) }}{{ n.units > 1 ? ' (' + n.units + ' × ' + rupees(n.unitPrice) + ')' : '' }}</td>
                      <td class="num">{{ n.extraAdults ? n.extraAdults + ' × ' + rupees(n.extraAdultRate) : '—' }}</td>
                      <td class="num">{{ n.extraChildren ? n.extraChildren + ' × ' + rupees(n.extraChildRate) : '—' }}</td>
                      <td class="num">{{ rupees(n.total) }}</td>
                    </tr>
                  }
                  <tr class="total"><td colspan="4">Total before tax</td><td class="num" data-total>{{ rupees(q.total) }}</td></tr>
                </tbody>
              </table>
            </div>
          }
        </form>
      }
    </div>
  `,
  styles: `
    .box { padding: 20px 24px; margin-bottom: 16px; }
    .empty { padding: 24px; margin-bottom: 16px; } .empty p { color: #5b6b6e; margin-top: 0; }
    h2 { font-size: 1rem; color: #0e3b43; margin-bottom: 12px; }
    .head-actions { display: flex; gap: 8px; align-items: center; }
    .toolbar { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
    .toolbar .spacer { flex: 1; }
    .plan-pick { min-width: 280px; }
    .start { height: 36px; border: 1px solid #c5cfcc; border-radius: 6px; padding: 0 8px; font: inherit; }
    .grid-wrap { overflow-x: auto; margin: 8px 0; }
    table.grid { border-collapse: collapse; min-width: 100%; font-variant-numeric: tabular-nums; }
    table.grid th, table.grid td { border: 1px solid #edf1f0; padding: 8px 10px; text-align: right; white-space: nowrap; font-size: .9rem; }
    table.grid thead th { color: #5b6b6e; font-weight: 500; font-size: .8rem; }
    table.grid .type-col { text-align: left; position: sticky; left: 0; background: #fff; z-index: 1; }
    table.grid .weekend { background: #fbf4e6; }
    table.grid .today { color: #8a2449; font-weight: 600; }
    table.grid td.missing { background: #fbeaea; }
    table.grid td.clickable { cursor: pointer; }
    table.grid td.clickable:hover { outline: 2px solid #0e3b43; outline-offset: -2px; }
    .min { display: block; font-size: .7rem; color: #8a5a00; }
    .days { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin: 4px 0 16px; }
    .form-grid.four { grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); }
    .actions { display: flex; gap: 8px; margin-top: 8px; }
    .quote { margin-top: 16px; }
    .quote tr.total td { font-weight: 600; border-top: 2px solid #d9e0de; }
    .error { color: #b3261e; }
    .mute { color: #5b6b6e; } .small { font-size: .85rem; }
    mat-slide-toggle { display: block; margin: 8px 0; }
  `,
})
export class RatesPage {
  private api = inject(RatesApi);
  private roomsApi = inject(RoomsApi);
  private auth = inject(AuthService);
  private snack = inject(MatSnackBar);
  private fb = inject(FormBuilder);
  ctx = inject(PropertyContext);

  readonly rupees = rupees;
  readonly dayLabel = dayLabel;
  readonly moneyError = moneyError;
  readonly mealPlans = MEAL_PLANS;
  readonly mealLabel = MEAL_PLAN_LABELS;
  readonly days = DAYS;

  private pid = computed(() => this.ctx.current()?.id ?? null);
  propertyName = computed(() => this.ctx.current()?.name ?? 'this property');
  today = computed(() => this.ctx.current()?.localDate ?? new Date().toISOString().slice(0, 10));
  canManage = computed(() => this.auth.can('rate.manage', this.pid()));

  plans = signal<RatePlan[]>([]);
  types = signal<AccommodationType[]>([]);
  rooms = signal<Room[]>([]);
  planId = signal<string | null>(null);
  selectedPlan = computed(() => this.plans().find(p => p.id === this.planId()) ?? null);
  start = signal<string>('');
  dates = computed(() => this.start() ? Array.from({ length: GRID_DAYS }, (_, i) => addDays(this.start(), i)) : []);
  private grid = signal(new Map<string, RatePrice>());
  loaded = signal(false);
  loadError = signal('');
  busy = signal(false);

  planMode = signal<'none' | 'new' | 'edit'>('none');
  plan = planForm(this.fb);
  planError = signal('');

  prices = pricesForm(this.fb, this.today());
  pricesError = signal('');
  private bulkForm = viewChild<ElementRef<HTMLFormElement>>('bulk');
  private selectedTypes = signal<string[]>([]);
  anyDorm = computed(() => this.types().some(t => t.inventoryMode === 'BED' && this.selectedTypes().includes(t.id)));
  private range = signal<{ first: string; last: string }>({ first: '', last: '' });
  nightsHint = computed(() => {
    const { first, last } = this.range();
    if (!first || !last || last < first) return '';
    const n = Math.round((Date.parse(last) - Date.parse(first)) / 86_400_000) + 1;
    return `${n} night${n === 1 ? '' : 's'}, ${dayLabel(first)} to ${dayLabel(last)}`;
  });

  quoteF = quoteForm(this.fb, this.today());
  quote = signal<RateQuote | null>(null);
  quoteError = signal('');

  constructor() {
    this.prices.controls.accommodationTypeIds.valueChanges.subscribe(v => this.selectedTypes.set(v ?? []));
    this.prices.valueChanges.subscribe(() => {
      const v = this.prices.getRawValue();
      this.range.set({ first: v.firstNight, last: v.lastNight });
    });

    effect(() => {
      const pid = this.pid();
      if (!pid) return;
      const today = this.ctx.current()!.localDate;
      if (!this.start()) this.start.set(today);
      this.prices.patchValue({ firstNight: today, lastNight: addDays(today, 6) });
      this.quoteF.patchValue({ from: today });
      this.roomsApi.types(pid).subscribe({ next: t => this.types.set(t.filter(x => x.isActive)), error: e => this.loadError.set(toProblem(e).detail) });
      this.roomsApi.rooms(pid).subscribe({ next: r => this.rooms.set(r.filter(x => x.isActive)), error: () => this.rooms.set([]) });
      this.loadPlans(pid);
    });

    // the grid follows the chosen plan and week
    effect(() => this.reloadGrid());
  }

  private loadPlans(pid: string, select?: string) {
    this.api.plans(pid).subscribe({
      next: plans => {
        this.plans.set(plans);
        this.loaded.set(true);
        const keep = select ?? this.planId();
        this.planId.set(plans.find(p => p.id === keep)?.id ?? plans.find(p => p.isDefault)?.id ?? plans[0]?.id ?? null);
      },
      error: e => this.loadError.set(toProblem(e).detail),
    });
  }

  priceAt(typeId: string, date: string) { return this.grid().get(`${typeId}|${date}`); }
  isWeekend(d: string) { const w = weekday(d); return w === 5 || w === 6; }

  setStart(d: string) { if (d) this.start.set(d); }
  move(days: number) { this.start.set(addDays(this.start(), days)); }

  upper(c: { value: string; setValue(v: string): void }) {
    const v = c.value.toUpperCase().replace(/[\s-]/g, '_');
    if (v !== c.value) c.setValue(v);
  }

  // ------------------------------------------------------------------ plans

  newPlan() {
    this.plan.reset({ code: '', name: '', description: '', mealPlan: 'EP', isDefault: !this.plans().length, sortOrder: 0, isActive: true });
    this.plan.controls.code.enable();
    this.planError.set('');
    this.planMode.set('new');
  }

  editPlan() {
    const p = this.selectedPlan();
    if (!p) return;
    this.plan.reset({ ...p, description: p.description ?? '' });
    this.plan.controls.code.disable();
    this.planError.set('');
    this.planMode.set('edit');
  }

  savePlan() {
    if (this.plan.invalid) { this.plan.markAllAsTouched(); return; }
    const pid = this.pid()!;
    const v = this.plan.getRawValue();
    const body = { ...v, description: v.description.trim() || null, sortOrder: Number(v.sortOrder) || 0 };
    const editing = this.planMode() === 'edit' ? this.selectedPlan() : null;
    const req = editing
      ? this.api.updatePlan(pid, editing.id, { ...body, version: editing.version })
      : this.api.createPlan(pid, body);
    this.busy.set(true); this.planError.set('');
    req.subscribe({
      next: p => {
        this.busy.set(false);
        this.planMode.set('none');
        this.snack.open(editing ? `${p.name} saved` : `${p.name} added. Now set its prices.`, undefined, { duration: 3000 });
        this.loadPlans(pid, p.id);
      },
      error: e => { this.busy.set(false); this.planError.set(this.explain(toProblem(e), this.plan)); },
    });
  }

  // ------------------------------------------------------------------ prices

  pick(t: AccommodationType, date: string) {
    if (!this.canManage()) return;
    const cell = this.priceAt(t.id, date);
    this.prices.reset({
      accommodationTypeIds: [t.id], firstNight: date, lastNight: date, daysOfWeek: [],
      price: cell?.price ?? null, extraAdult: cell?.extraAdult ?? 0, extraChild: cell?.extraChild ?? 0, minStay: cell?.minStay ?? 1,
    });
    this.pricesError.set('');
    this.bulkForm()?.nativeElement.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  weekendOnly() { this.prices.controls.daysOfWeek.setValue(['Friday', 'Saturday']); }

  applyPrices() {
    if (this.prices.invalid) { this.prices.markAllAsTouched(); return; }
    const pid = this.pid()!, plan = this.selectedPlan()!;
    const v = this.prices.getRawValue();
    const body = {
      accommodationTypeIds: v.accommodationTypeIds, from: v.firstNight, to: addDays(v.lastNight, 1),
      daysOfWeek: v.daysOfWeek, price: Number(v.price), extraAdult: Number(v.extraAdult), extraChild: Number(v.extraChild),
      minStay: Number(v.minStay),
    };
    this.busy.set(true); this.pricesError.set('');
    this.api.setPrices(pid, plan.id, body).subscribe({
      next: r => {
        this.busy.set(false);
        const parts = [r.created && `${r.created} new`, r.changed && `${r.changed} changed`, r.unchanged && `${r.unchanged} already at that price`].filter(Boolean);
        this.snack.open(`Prices saved in ${plan.name}: ${parts.join(', ')}`, undefined, { duration: 4000 });
        this.reloadGrid();
      },
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        // The API's end date is the morning after the last night; show its reasons on the fields the user sees.
        const errs = { ...(p.errors ?? {}) };
        if (errs['To']) { errs['LastNight'] = errs['To']; delete errs['To']; }
        if (errs['From']) { errs['FirstNight'] = errs['From']; delete errs['From']; }
        this.pricesError.set(this.explain({ ...p, errors: errs }, this.prices));
      },
    });
  }

  private reloadGrid() {
    const pid = this.pid(), planId = this.planId(), dates = this.dates();
    if (!pid || !planId || !dates.length) return;
    this.api.prices(pid, planId, dates[0], addDays(dates[dates.length - 1], 1)).subscribe({
      next: rows => this.grid.set(new Map(rows.map(r => [`${r.accommodationTypeId}|${r.date}`, r]))),
      error: e => this.loadError.set(toProblem(e).detail),
    });
  }

  // ------------------------------------------------------------------ quote

  runQuote() {
    if (this.quoteF.invalid) { this.quoteF.markAllAsTouched(); return; }
    const v = this.quoteF.getRawValue();
    const [kind, key] = v.target.split(':');
    this.busy.set(true); this.quoteError.set(''); this.quote.set(null);
    this.api.quote(this.pid()!, {
      type: kind === 'type' ? key : undefined, room: kind === 'room' ? key : undefined, ratePlan: this.planId() ?? undefined,
      from: v.from, to: addDays(v.from, Number(v.nights)), adults: Number(v.adults), children: Number(v.children) || 0,
    }).subscribe({
      next: q => { this.busy.set(false); this.quote.set(q); },
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const errs = { ...(p.errors ?? {}) };
        for (const k of ['Type', 'Room']) if (errs[k]) { errs['Target'] = errs[k]; delete errs[k]; }
        if (errs['To']) { errs['Nights'] = errs['To']; delete errs['To']; }
        this.quoteError.set(this.explain({ ...p, errors: errs }, this.quoteF));
      },
    });
  }

  /** Field reasons go under their fields; the banner repeats the API's own message, which names the reasons too. */
  private explain(p: Problem, form: Parameters<typeof showFieldErrors>[0]): string {
    if (p.code === 'CONCURRENCY_CONFLICT') return 'Someone else changed this rate plan while you were editing. Reload the page to see their changes.';
    showFieldErrors(form, p);
    return p.detail;
  }
}
