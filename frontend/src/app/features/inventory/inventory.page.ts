import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { fieldErrors, toProblem } from '../../core/http';
import { AdjustmentKind, CostedDish, FoodCostReport, Inventory, InventoryItem, MovementType, RecipeList, StockMovement } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { rupees } from '../settings/rates/rates.api';
import { AdjustDialog, CountDialog, RecipeDialog, StockItemDialog, StockSetupDialog } from './inventory-dialogs';
import { InventoryApi, MOVEMENT_LABEL, marginBand, pct, qty } from './inventory.api';

type View = 'stock' | 'recipes' | 'cost' | 'ledger';

/**
 * Inventory (Sprint 15): stock on hand by store, the ledger of every change, recipes with their cost, and the food-cost
 * report. Stock changes only through the ledger: stock received, wastage, a correction, a transfer, a count — each with a
 * reason — or a served dish using its recipe from the kitchen. Works at 360 px: rows wrap into cards.
 */
@Component({
  selector: 'app-inventory',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatSnackBarModule],
  template: `
    <div class="page inv">
      <div class="head">
        <h1>Inventory</h1>
        @if (inventory(); as inv) {
          <p class="mute">Stock worth {{ rupees(inv.value) }}@if (inv.lowCount) { · <b class="low">{{ inv.lowCount }} to reorder</b> }</p>
        }
      </div>
      <mat-button-toggle-group [value]="view()" (change)="go($event.value)" aria-label="Inventory view" class="views">
        @if (canStock()) { <mat-button-toggle value="stock">Stock</mat-button-toggle> }
        @if (canRecipes()) { <mat-button-toggle value="recipes">Recipes</mat-button-toggle> <mat-button-toggle value="cost">Food cost</mat-button-toggle> }
        @if (canStock()) { <mat-button-toggle value="ledger">Ledger</mat-button-toggle> }
      </mat-button-toggle-group>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

      @switch (view()) {
        @case ('stock') {
          <div class="tools">
            <mat-form-field appearance="outline" class="search" subscriptSizing="dynamic"><mat-label>Find an item</mat-label>
              <input matInput [ngModel]="search()" (ngModelChange)="search.set($event)" placeholder="Name or code" /></mat-form-field>
            @if (canAdjust()) {
              <button mat-flat-button (click)="adjust(null, null)"><mat-icon>edit_note</mat-icon>Record</button>
              <button mat-stroked-button (click)="count()"><mat-icon>fact_check</mat-icon>Count</button>
            }
            @if (canManage()) {
              <button mat-stroked-button (click)="editItem(null)"><mat-icon>add</mat-icon>Item</button>
              <button mat-icon-button (click)="setup()" aria-label="Groups and stores"><mat-icon>settings</mat-icon></button>
            }
          </div>
          @for (g of groups(); track g.name) {
            <section>
              <h2>{{ g.name }}</h2>
              <ul class="items">
                @for (i of g.items; track i.id) {
                  <li [class.off]="!i.isActive" [attr.data-item]="i.code">
                    <button class="nm" (click)="ledgerOf(i)" [attr.aria-label]="'Ledger of ' + i.name">
                      <b>{{ i.name }}</b> <small class="mute">{{ i.code }} · {{ rupees(i.unitCost) }}/{{ i.unit }}</small>
                      <small class="at">@for (l of i.levels; track l.locationId) { <span [class.neg]="l.onHand < 0">{{ l.locationCode }} {{ qty(l.onHand, i.unit) }}</span> }</small>
                    </button>
                    <span class="oh">
                      <b [class.neg]="i.onHand < 0">{{ qty(i.onHand, i.unit) }}</b>
                      @if (i.negative) { <small class="neg">Below zero: count it</small> }
                      @else if (i.low) { <small class="low">Reorder (at {{ qty(i.reorderLevel!, i.unit) }})</small> }
                      @if (!i.isActive) { <small class="mute">Switched off</small> }
                    </span>
                    <span class="acts">
                      @if (canAdjust() && i.isActive) { <button mat-icon-button (click)="adjust(i, 'WASTAGE')" [attr.aria-label]="'Record for ' + i.name"><mat-icon>edit_note</mat-icon></button> }
                      @if (canManage()) { <button mat-icon-button (click)="editItem(i)" [attr.aria-label]="'Edit ' + i.name"><mat-icon>edit</mat-icon></button> }
                    </span>
                  </li>
                }
              </ul>
            </section>
          } @empty { @if (inventory()) { <p class="mute">No stock items yet.@if (canManage()) { Add the stores and groups (⚙), then the items. }</p> } }
        }

        @case ('recipes') {
          <p class="mute">What one portion uses, at today's cost. Food cost under 35 % is healthy for most dishes. Served dishes use their recipe from the kitchen store.</p>
          @for (d of recipes()?.dishes ?? []; track d.menuItemId + (d.variantId ?? '')) {
            <article class="dish" [attr.data-dish]="d.name">
              <button class="nm" (click)="editRecipe(d)" [disabled]="!canManageRecipes()">
                <b>{{ d.name }}</b> <small class="mute">{{ d.category }} · {{ rupees(d.price) }}</small>
                @if (d.recipe) {
                  <small class="ings">{{ ingredients(d) }}@if (!d.ownRecipe && d.variantId) { <i> (the dish's recipe)</i> }</small>
                } @else { <small class="mute">No recipe: uses nothing from stock</small> }
              </button>
              @if (d.cost !== null) {
                <span class="nums"><b>{{ rupees(d.cost) }}</b><small>food cost {{ pct(d.foodCostPercent) }}</small>
                  <small [class]="'band ' + band(d.marginPercent)">margin {{ pct(d.marginPercent) }}</small></span>
              }
            </article>
          } @empty { @if (recipes()) { <p class="mute">No dishes on the menu yet.</p> } }
        }

        @case ('cost') {
          <div class="tools">
            <mat-form-field appearance="outline" subscriptSizing="dynamic"><mat-label>From</mat-label>
              <input matInput type="date" [ngModel]="from()" (ngModelChange)="from.set($event); loadCost()" />
              @if (rangeErr()['from']) { <mat-hint class="err" role="alert">{{ rangeErr()['from'] }}</mat-hint> }</mat-form-field>
            <mat-form-field appearance="outline" subscriptSizing="dynamic"><mat-label>To</mat-label>
              <input matInput type="date" [ngModel]="to()" (ngModelChange)="to.set($event); loadCost()" />
              @if (rangeErr()['to']) { <mat-hint class="err" role="alert">{{ rangeErr()['to'] }}</mat-hint> }</mat-form-field>
          </div>
          @if (report(); as r) {
            <div class="tiles">
              <div><small>Sold</small><b>{{ r.sold }}</b></div>
              <div><small>Sales before GST</small><b>{{ rupees(r.revenue) }}</b></div>
              <div><small>Cost of sales</small><b>{{ rupees(r.costOfSales) }}</b></div>
              <div><small>Food cost</small><b>{{ pct(r.foodCostPercent) }}</b></div>
              <div><small>Written off</small><b>{{ rupees(r.wastage) }}</b></div>
            </div>
            <div class="best">
              <section aria-label="Highest margin"><h2>Highest margin</h2>
                <ol>@for (l of r.highest; track l.name) { <li><span>{{ l.name }}</span><b class="band good">{{ pct(l.marginPercent) }}</b></li> }</ol></section>
              <section aria-label="Lowest margin"><h2>Lowest margin</h2>
                <ol>@for (l of r.lowest; track l.name) { <li><span>{{ l.name }}</span><b [class]="'band ' + band(l.marginPercent)">{{ pct(l.marginPercent) }}</b></li> }</ol></section>
            </div>
            <section>
              <h2>Every dish with a recipe <small class="mute">{{ r.from }} – {{ r.to }}</small></h2>
              <div class="table" role="table" aria-label="Food cost by dish">
                <div class="tr th" role="row"><span role="columnheader" class="dn">Dish</span><span role="columnheader">Price</span><span role="columnheader">Cost</span>
                  <span role="columnheader">Food cost</span><span role="columnheader">Margin</span><span role="columnheader">Sold</span><span role="columnheader">Sales</span>
                  <span role="columnheader">Cost of sales</span></div>
                @for (l of r.lines; track l.name) {
                  <div class="tr" role="row" [attr.data-line]="l.name">
                    <span role="cell" class="dn">{{ l.name }}</span><span role="cell" data-l="Price">{{ rupees(l.price) }}</span>
                    <span role="cell" data-l="Cost">{{ rupees(l.cost) }}</span><span role="cell" data-l="Food cost">{{ pct(l.foodCostPercent) }}</span>
                    <span role="cell" data-l="Margin" [class]="'band ' + band(l.marginPercent)">{{ pct(l.marginPercent) }}</span>
                    <span role="cell" data-l="Sold">{{ l.sold }}</span><span role="cell" data-l="Sales">{{ rupees(l.revenue) }}</span>
                    <span role="cell" data-l="Cost of sales">{{ rupees(l.costOfSales) }}@if (l.actualFoodCostPercent !== null) { <small> ({{ pct(l.actualFoodCostPercent) }})</small> }</span>
                  </div>
                }
              </div>
              @if (r.noRecipe.length) { <p class="mute">No recipe (not costed, stock not tracked): {{ r.noRecipe.join(', ') }}.</p> }
            </section>
          }
        }

        @case ('ledger') {
          <div class="tools">
            <mat-form-field appearance="outline" subscriptSizing="dynamic" class="search"><mat-label>Item</mat-label>
              <mat-select [value]="ledgerItem()" (selectionChange)="ledgerItem.set($event.value); loadLedger()">
                <mat-option [value]="null">All items</mat-option>
                @for (i of inventory()?.items ?? []; track i.id) { <mat-option [value]="i.id">{{ i.name }}</mat-option> }
              </mat-select></mat-form-field>
            <mat-form-field appearance="outline" subscriptSizing="dynamic"><mat-label>Kind</mat-label>
              <mat-select [value]="ledgerType()" (selectionChange)="ledgerType.set($event.value); loadLedger()">
                <mat-option [value]="null">All</mat-option>
                @for (t of types; track t) { <mat-option [value]="t">{{ moveLabel[t] }}</mat-option> }
              </mat-select></mat-form-field>
          </div>
          <ul class="ledger">
            @for (m of movements(); track m.id) {
              <li [attr.data-type]="m.type">
                <span class="when">{{ when(m.at) }}</span>
                <span class="what"><b>{{ moveLabel[m.type] }}</b> {{ m.itemName }} <small class="mute">{{ m.locationCode }}@if (m.source) { · {{ m.source }} }@if (m.byName) { · {{ m.byName }} }</small>
                  @if (m.note) { <small>{{ m.note }}</small> }</span>
                <span class="q" [class.neg]="m.quantity < 0">{{ m.quantity > 0 ? '+' : '' }}{{ qty(m.quantity, m.unit) }}<small>{{ rupees(m.value) }} · left {{ qty(m.onHandAfter, m.unit) }}</small></span>
              </li>
            } @empty { <li class="mute">No movements.</li> }
          </ul>
          @if (more()) { <p class="mute">Showing the latest {{ movements().length }}. Choose an item to see all of its movements.</p> }
        }
      }
    </div>
  `,
  styles: `
    .inv { max-width: 980px; padding-bottom: 60px; }
    .head { display: flex; justify-content: space-between; align-items: baseline; gap: 8px; flex-wrap: wrap; }
    .mute { color: #5b6b6e; } .err { color: #b3261e; } .low { color: #8a5a00; } .neg { color: #b3261e; }
    .views { margin: 4px 0 10px; flex-wrap: wrap; }
    .tools { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 8px; }
    .search { flex: 1 1 200px; }
    section { background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 4px 14px 10px; margin-top: 12px; }
    h2 { font-size: 1.02rem; margin: 8px 0; }
    ul { list-style: none; margin: 0; padding: 0; }
    .items li { display: flex; align-items: center; gap: 10px; padding: 8px 0; border-bottom: 1px solid #eef2f1; }
    .items li.off { opacity: .6; }
    .nm { all: unset; flex: 1; cursor: pointer; min-height: 40px; }
    .nm:disabled { cursor: default; }
    .nm small { display: block; }
    .at span { margin-right: 10px; }
    .oh { display: flex; flex-direction: column; align-items: flex-end; text-align: right; font-variant-numeric: tabular-nums; }
    .acts { display: flex; }
    .dish { display: flex; gap: 10px; align-items: flex-start; background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 10px 14px; margin-top: 8px; }
    .ings { color: #34484b; }
    .nums { display: flex; flex-direction: column; align-items: flex-end; text-align: right; font-variant-numeric: tabular-nums; min-width: 110px; }
    .band.good { color: #2e7d32; } .band.mid { color: #8a5a00; } .band.low { color: #b3261e; }
    .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 8px; }
    .tiles div { background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 10px 12px; display: flex; flex-direction: column; }
    .tiles b { font-size: 1.25rem; font-variant-numeric: tabular-nums; }
    .best { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 8px; }
    .best section { margin-top: 8px; }
    ol { margin: 0; padding-left: 20px; } ol li { display: flex; justify-content: space-between; gap: 8px; padding: 3px 0; }
    .table { display: flex; flex-direction: column; }
    .tr { display: grid; grid-template-columns: 2fr repeat(7, 1fr); gap: 6px; padding: 6px 0; border-bottom: 1px solid #eef2f1; font-variant-numeric: tabular-nums; }
    .tr span:not(.dn) { text-align: right; }
    .th { font-weight: 600; font-size: .85rem; color: #5b6b6e; }
    .ledger li { display: flex; gap: 10px; padding: 8px 0; border-bottom: 1px solid #eef2f1; align-items: flex-start; }
    .when { flex: 0 0 92px; color: #5b6b6e; font-size: .85rem; }
    .what { flex: 1; } .what small { display: block; }
    .q { text-align: right; font-variant-numeric: tabular-nums; } .q small { display: block; color: #5b6b6e; }
    @media (max-width: 640px) {
      h1 { font-size: 1.35rem; }
      .items li, .dish { flex-wrap: wrap; }
      .th { display: none; }
      .tr { grid-template-columns: 1fr 1fr; }
      .tr .dn { grid-column: 1 / -1; font-weight: 600; }
      .tr span[data-l]::before { content: attr(data-l) ': '; color: #5b6b6e; float: left; }
      .ledger li { flex-wrap: wrap; } .when { flex-basis: 100%; }
    }
  `,
})
export class InventoryPage {
  private api = inject(InventoryApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  private route = inject(ActivatedRoute);
  private router = inject(Router);

  inventory = signal<Inventory | null>(null);
  recipes = signal<RecipeList | null>(null);
  report = signal<FoodCostReport | null>(null);
  rangeErr = signal<Record<string, string>>({});
  movements = signal<StockMovement[]>([]);
  more = signal(false);
  error = signal('');
  search = signal('');
  from = signal<string | null>(null);
  to = signal<string | null>(null);
  ledgerItem = signal<string | null>(null);
  ledgerType = signal<string | null>(null);
  rupees = rupees;
  qty = qty;
  pct = pct;
  band = marginBand;
  moveLabel = MOVEMENT_LABEL;
  types = Object.keys(MOVEMENT_LABEL) as MovementType[];

  private pid = computed(() => this.ctx.current()?.id ?? '');
  canStock = computed(() => this.auth.can('inventory.read', this.pid()));
  canAdjust = computed(() => this.auth.can('inventory.adjust', this.pid()));
  canManage = computed(() => this.auth.can('inventory.manage', this.pid()));
  canRecipes = computed(() => this.auth.can('recipe.read', this.pid()));
  canManageRecipes = computed(() => this.auth.can('recipe.manage', this.pid()));
  private requested = signal<View>((this.route.snapshot.queryParamMap.get('view') as View) ?? 'stock');
  view = computed<View>(() => {
    const v = this.requested();
    if ((v === 'recipes' || v === 'cost') && !this.canRecipes()) return 'stock';
    if ((v === 'stock' || v === 'ledger') && !this.canStock()) return 'recipes';
    return v;
  });

  groups = computed(() => {
    const inv = this.inventory();
    if (!inv) return [];
    const s = this.search().trim().toLowerCase();
    const items = inv.items.filter(i => !s || i.name.toLowerCase().includes(s) || i.code.toLowerCase().includes(s));
    return inv.categories.map(c => ({ name: c.name, items: items.filter(i => i.categoryId === c.id) })).filter(g => g.items.length);
  });

  constructor() {
    this.route.queryParamMap.subscribe(q => {
      this.requested.set((q.get('view') as View) ?? 'stock');
      this.ledgerItem.set(q.get('item'));
    });
    effect(() => {
      if (!this.pid()) return;
      this.load(this.view());
    });
  }

  go(v: View) { this.router.navigate([], { queryParams: { view: v, item: null }, queryParamsHandling: 'merge' }); }

  load(v: View) {
    if (this.canStock()) this.api.get(this.pid()).subscribe({ next: i => { this.inventory.set(i); this.error.set(''); }, error: e => this.error.set(toProblem(e).detail) });
    if (v === 'recipes') this.api.recipes(this.pid()).subscribe({ next: r => this.recipes.set(r), error: e => this.error.set(toProblem(e).detail) });
    if (v === 'cost') this.loadCost();
    if (v === 'ledger') this.loadLedger();
  }

  loadCost() {
    this.api.foodCost(this.pid(), this.from() || null, this.to() || null).subscribe({
      next: r => { this.report.set(r); this.from.set(r.from); this.to.set(r.to); this.error.set(''); this.rangeErr.set({}); },
      // A wrong range is said under the date it concerns.
      error: e => {
        const p = toProblem(e), f = fieldErrors(p);
        if (f['from'] || f['to']) { this.rangeErr.set(f); this.error.set(''); } else this.error.set(p.detail);
      },
    });
  }

  loadLedger() {
    this.api.movements(this.pid(), { itemId: this.ledgerItem(), type: this.ledgerType(), take: this.ledgerItem() ? 500 : 100 }).subscribe({
      next: l => { this.movements.set(l.items); this.more.set(l.more); this.error.set(''); },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  ledgerOf(i: InventoryItem) { this.router.navigate([], { queryParams: { view: 'ledger', item: i.id } }); }

  ingredients(d: CostedDish) { return d.recipe!.ingredients.map(i => `${qty(i.quantity, i.unit)} ${i.itemName}`).join(' · '); }

  when(at: string) {
    return new Date(at).toLocaleString('en-IN', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false });
  }

  adjust(i: InventoryItem | null, kind: AdjustmentKind | null) {
    this.open(AdjustDialog, { propertyId: this.pid(), inventory: this.inventory(), itemId: i?.id ?? null, kind });
  }

  count() { this.open(CountDialog, { propertyId: this.pid(), inventory: this.inventory() }, '720px'); }

  editItem(i: InventoryItem | null) { this.open(StockItemDialog, { propertyId: this.pid(), item: i, inventory: this.inventory() }); }

  setup() { this.open(StockSetupDialog, { propertyId: this.pid(), inventory: this.inventory() }); }

  editRecipe(d: CostedDish) {
    if (!this.canManageRecipes()) return;
    const go = (inv: Inventory) => this.open(RecipeDialog, { propertyId: this.pid(), dish: d, inventory: inv, units: this.recipes()!.units }, '700px');
    const inv = this.inventory();
    if (inv) go(inv);
    else this.api.get(this.pid()).subscribe({ next: i => { this.inventory.set(i); go(i); }, error: e => this.error.set(toProblem(e).detail) });
  }

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  private open(component: any, data: unknown, width?: string) {
    this.dialog.open(component, { data, maxWidth: '100vw', width }).afterClosed().subscribe((done: string | boolean | undefined) => {
      if (!done) return;
      if (typeof done === 'string') this.snack.open(done, 'OK', { duration: 6000 });
      this.load(this.view());
    });
  }
}
