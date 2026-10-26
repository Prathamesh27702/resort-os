import { Component, computed, effect, inject, signal } from '@angular/core';
import { AbstractControl, FormArray, FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { controlPath, fieldErrors, showFieldErrors, toProblem } from '../../core/http';
import { FoodType, Menu, MenuCategory, MenuItem, RestaurantTable } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { rupees } from '../settings/rates/rates.api';
import { FOOD_LABEL, RestaurantApi } from './restaurant.api';

const server = (c: AbstractControl | null) => (c?.errors?.['server'] as string | undefined) ?? '';

/**
 * The menu editor (Sprint 14, menu.update): sections and dishes with their portions and choices, sold out with one switch,
 * and the restaurant's tables. Nothing is deleted — a dish or table no longer used is switched off, because old orders
 * point to it. Prices are before GST.
 */
@Component({
  selector: 'app-restaurant-menu',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatIconModule, MatSlideToggleModule, MatSnackBarModule],
  template: `
    <div class="page menu">
      <a mat-button routerLink="/restaurant" class="back"><mat-icon>arrow_back</mat-icon>Floor</a>
      <div class="head">
        <h1>Menu and tables</h1>
        @if (canCreate()) {
          <div class="tools">
            <button mat-stroked-button (click)="editCategory(null)"><mat-icon>add</mat-icon>Section</button>
            <button mat-stroked-button (click)="editItem(null, null)" [disabled]="!menu()?.categories?.length"><mat-icon>add</mat-icon>Dish</button>
          </div>
        }
      </div>
      <p class="mute">Prices are before GST. Switch a dish off when it is sold out; waiters see it at once.</p>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

      @for (c of menu()?.categories ?? []; track c.id) {
        <section [class.off]="!c.isActive">
          <h2>{{ c.name }}@if (!c.isActive) { <small class="mute">(off)</small> }
            @if (canUpdate()) { <button mat-icon-button (click)="editCategory(c)" [attr.aria-label]="'Edit section ' + c.name"><mat-icon>edit</mat-icon></button> }
          </h2>
          <ul>
            @for (i of c.items; track i.id) {
              <li [class.off]="!i.isActive" [attr.data-dish]="i.code">
                <span [class]="'food ' + i.foodType" [attr.aria-label]="food[i.foodType]"></span>
                <button class="nm" (click)="editItem(i, c.id)" [disabled]="!canUpdate()">
                  <b>{{ i.name }}</b> <small class="mute">{{ i.code }}</small>
                  @if (i.variants.length) { <small>{{ portions(i) }}</small> }
                  @if (i.modifierGroups.length) { <small class="mute">{{ groups(i) }}</small> }
                  @if (!i.isActive) { <small class="mute">Switched off</small> }
                </button>
                <span class="pr">{{ rupees(i.price) }}</span>
                <mat-slide-toggle [checked]="i.isAvailable" (change)="available(i, $event.checked)" [disabled]="!canUpdate() || busy() === i.id"
                                  [aria-label]="i.name + ' available'">{{ i.isAvailable ? 'On' : 'Sold out' }}</mat-slide-toggle>
              </li>
            } @empty { <li class="mute">No dishes in this section yet.</li> }
          </ul>
        </section>
      } @empty { @if (menu()) { <p class="mute">No menu yet. Add a section, then its dishes.</p> } }

      <section>
        <h2>Tables
          @if (canCreate()) { <button mat-icon-button (click)="editTable(null)" aria-label="Add a table"><mat-icon>add</mat-icon></button> }
        </h2>
        <ul class="tables">
          @for (t of tables(); track t.id) {
            <li [class.off]="!t.isActive">
              <button class="nm" (click)="editTable(t)" [disabled]="!canUpdate()">
                <b>{{ t.code }}</b> · {{ t.seats }} seats@if (t.area) { · {{ t.area }} }@if (!t.isActive) { · off }
              </button>
            </li>
          } @empty { <li class="mute">No tables yet.</li> }
        </ul>
      </section>
    </div>
  `,
  styles: `
    .menu { max-width: 820px; padding-bottom: 60px; }
    .back { margin-left: -8px; }
    .head { display: flex; justify-content: space-between; align-items: center; gap: 8px; flex-wrap: wrap; }
    .tools { display: flex; gap: 8px; }
    .mute { color: #5b6b6e; } .err { color: #b3261e; }
    section { background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 4px 14px 10px; margin-top: 12px; }
    section.off { opacity: .7; }
    h2 { font-size: 1.05rem; display: flex; align-items: center; gap: 4px; margin: 8px 0; }
    ul { list-style: none; margin: 0; padding: 0; }
    li { display: flex; align-items: center; gap: 10px; padding: 8px 0; border-bottom: 1px solid #eef2f1; }
    li.off { opacity: .6; }
    .nm { all: unset; flex: 1; cursor: pointer; min-height: 40px; }
    .nm small { display: block; }
    .nm:disabled { cursor: default; }
    .pr { font-variant-numeric: tabular-nums; }
    .tables li { padding: 4px 0; }
    .food { display: inline-block; flex: 0 0 auto; width: 12px; height: 12px; border: 2px solid; border-radius: 2px; }
    .food.VEG { border-color: #2e7d32; background: radial-gradient(#2e7d32 40%, transparent 45%); }
    .food.NON_VEG { border-color: #8c1d18; background: radial-gradient(#8c1d18 40%, transparent 45%); }
    .food.EGG { border-color: #c77700; background: radial-gradient(#c77700 40%, transparent 45%); }
    @media (max-width: 640px) { h1 { font-size: 1.35rem; } li { flex-wrap: wrap; } }
  `,
})
export class MenuPage {
  private api = inject(RestaurantApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);

  menu = signal<Menu | null>(null);
  tables = signal<RestaurantTable[]>([]);
  error = signal('');
  busy = signal<string | null>(null);
  food = FOOD_LABEL;
  rupees = rupees;
  private pid = computed(() => this.ctx.current()?.id ?? '');
  canCreate = computed(() => this.auth.can('menu.create', this.pid()));
  canUpdate = computed(() => this.auth.can('menu.update', this.pid()));

  constructor() { effect(() => { if (this.pid()) this.load(); }); }

  load() {
    this.api.menu(this.pid(), true).subscribe({ next: m => { this.menu.set(m); this.error.set(''); }, error: e => this.error.set(toProblem(e).detail) });
    this.api.tables(this.pid()).subscribe({ next: t => this.tables.set(t) });
  }

  portions(i: MenuItem) { return i.variants.map(v => `${v.name} ${rupees(v.price)}`).join(' · '); }
  groups(i: MenuItem) { return i.modifierGroups.map(g => `${g.name}: ${g.modifiers.map(m => m.name).join(', ')}`).join(' · '); }

  available(i: MenuItem, on: boolean) {
    this.busy.set(i.id);
    this.api.setAvailable(this.pid(), i.id, on).subscribe({
      next: () => { this.busy.set(null); this.snack.open(on ? `${i.name} is back on.` : `${i.name} is sold out.`, undefined, { duration: 2500 }); this.load(); },
      error: e => { this.busy.set(null); this.snack.open(toProblem(e).detail, 'OK', { duration: 8000 }); this.load(); },
    });
  }

  editCategory(c: MenuCategory | null) {
    this.dialog.open(CategoryDialog, { data: { propertyId: this.pid(), category: c, next: (this.menu()?.categories.length ?? 0) + 1 }, maxWidth: '100vw' })
      .afterClosed().subscribe(saved => { if (saved) this.load(); });
  }

  editItem(i: MenuItem | null, categoryId: string | null) {
    this.dialog.open(DishEditDialog, { data: { propertyId: this.pid(), item: i, categoryId, categories: this.menu()?.categories ?? [] }, maxWidth: '100vw', width: '640px' })
      .afterClosed().subscribe(saved => { if (saved) this.load(); });
  }

  editTable(t: RestaurantTable | null) {
    this.dialog.open(TableDialog, { data: { propertyId: this.pid(), table: t, next: this.tables().length + 1 }, maxWidth: '100vw' })
      .afterClosed().subscribe(saved => { if (saved) this.load(); });
  }
}

/** Shows the API's field messages under their fields; the rest in the dialog's error line. */
function report(form: FormGroup, e: unknown, error: (m: string) => void) {
  const p = toProblem(e);
  const errs = fieldErrors(p);
  const shown = showFieldErrors(form, p);
  const missing = Object.keys(errs).filter(k => !form.get(k) && !form.get(controlPath(k)));
  if (!Object.keys(shown).length) error(p.detail);
  else if (missing.length) error(missing.map(k => errs[k]).join(' '));
}

// ------------------------------------------------------------------ a section

@Component({
  selector: 'app-category-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ data.category ? 'Edit section' : 'New menu section' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="cat-form">
      <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" maxlength="60" placeholder="e.g. Starters" />
        <mat-error>{{ server(form.get('name')) || 'Give the section a name of 2–60 characters, e.g. Starters.' }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Order on the menu</mat-label><input matInput type="number" formControlName="sortOrder" />
        <mat-error>{{ server(form.get('sortOrder')) || 'The order must be 0–10000.' }}</mat-error></mat-form-field>
      <mat-checkbox formControlName="isActive">Shown to waiters</mat-checkbox>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="cat-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(360px, 84vw); } .err { color: #b3261e; }`,
})
export class CategoryDialog {
  data = inject<{ propertyId: string; category: MenuCategory | null; next: number }>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<CategoryDialog, boolean>);
  private api = inject(RestaurantApi);
  server = server;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).nonNullable.group({
    name: [this.data.category?.name ?? '', [Validators.required, Validators.minLength(2)]],
    sortOrder: [this.data.category?.sortOrder ?? this.data.next, [Validators.min(0), Validators.max(10000)]],
    isActive: [this.data.category?.isActive ?? true],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    const v = this.form.getRawValue();
    this.api.saveCategory(this.data.propertyId, this.data.category?.id ?? null, { ...v, name: v.name.trim() }).subscribe({
      next: () => this.ref.close(true),
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ a table

@Component({
  selector: 'app-table-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ data.table ? 'Table ' + data.table.code : 'New table' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="table-form">
      <mat-form-field appearance="outline"><mat-label>Code</mat-label><input matInput formControlName="code" maxlength="10" placeholder="e.g. T5" />
        <mat-error>{{ server(form.get('code')) || 'The table code is 1–10 letters or digits, e.g. T4.' }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Seats</mat-label><input matInput type="number" formControlName="seats" />
        <mat-error>{{ server(form.get('seats')) || 'Seats must be 1–50.' }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Area (optional)</mat-label><input matInput formControlName="area" maxlength="40" placeholder="e.g. Garden" />
        <mat-error>{{ server(form.get('area')) }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Order on the floor</mat-label><input matInput type="number" formControlName="sortOrder" />
        <mat-error>{{ server(form.get('sortOrder')) || 'The order must be 0–10000.' }}</mat-error></mat-form-field>
      <mat-checkbox formControlName="isActive">In use</mat-checkbox>
      @if (server(form.get('isActive'))) { <p class="err">{{ server(form.get('isActive')) }}</p> }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="table-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(360px, 84vw); } .err { color: #b3261e; }`,
})
export class TableDialog {
  data = inject<{ propertyId: string; table: RestaurantTable | null; next: number }>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<TableDialog, boolean>);
  private api = inject(RestaurantApi);
  server = server;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).nonNullable.group({
    code: [this.data.table?.code ?? '', Validators.required],
    seats: [this.data.table?.seats ?? 4, [Validators.min(1), Validators.max(50)]],
    area: [this.data.table?.area ?? ''],
    sortOrder: [this.data.table?.sortOrder ?? this.data.next, [Validators.min(0), Validators.max(10000)]],
    isActive: [this.data.table?.isActive ?? true],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    const v = this.form.getRawValue();
    this.api.saveTable(this.data.propertyId, this.data.table?.id ?? null, { ...v, code: v.code.trim(), area: v.area.trim() || null }).subscribe({
      next: () => this.ref.close(true),
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ a dish

interface DishEditData { propertyId: string; item: MenuItem | null; categoryId: string | null; categories: MenuCategory[]; }

/** A dish with its portions (Half / Full, each its own price) and groups of choices (spice level: pick 1; add-ons: up
 *  to 2, each adding to the price). */
@Component({
  selector: 'app-dish-edit-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ data.item ? data.item.name : 'New dish' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="dish-form" class="dish">
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" maxlength="80" placeholder="e.g. Paneer Tikka" />
          <mat-error>{{ server(form.get('name')) || 'Give the dish a name of 2–80 characters.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Code</mat-label><input matInput formControlName="code" maxlength="20" placeholder="e.g. PANEER-TIKKA" />
          <mat-error>{{ server(form.get('code')) || 'The code is 1–20 letters, digits, - or _.' }}</mat-error></mat-form-field>
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Section</mat-label>
          <mat-select formControlName="categoryId">@for (c of data.categories; track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select>
          <mat-error>{{ server(form.get('categoryId')) || 'Choose the menu section.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Veg or non-veg</mat-label>
          <mat-select formControlName="foodType">@for (f of foodTypes; track f) { <mat-option [value]="f">{{ food[f] }}</mat-option> }</mat-select>
          <mat-error>{{ server(form.get('foodType')) }}</mat-error></mat-form-field>
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Price before GST (₹)</mat-label><input matInput type="number" inputmode="decimal" formControlName="price" />
          <mat-error>{{ server(form.get('price')) || 'Enter the price.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Order in the section</mat-label><input matInput type="number" formControlName="sortOrder" />
          <mat-error>{{ server(form.get('sortOrder')) || 'The order must be 0–10000.' }}</mat-error></mat-form-field>
      </div>
      <mat-form-field appearance="outline" class="full"><mat-label>Description (optional)</mat-label><input matInput formControlName="description" maxlength="300" />
        <mat-error>{{ server(form.get('description')) }}</mat-error></mat-form-field>
      <mat-checkbox formControlName="isActive">On the menu</mat-checkbox>

      <h3>Portions <small class="mute">(optional; each has its own price)</small></h3>
      @if (server(form.get('variants'))) { <p class="err">{{ server(form.get('variants')) }}</p> }
      <div formArrayName="variants">
        @for (v of variants.controls; track v; let n = $index) {
          <div class="row" [formGroupName]="n">
            <mat-form-field appearance="outline"><mat-label>Portion</mat-label><input matInput formControlName="name" maxlength="40" placeholder="e.g. Half" />
              <mat-error>{{ server(v.get('name')) || 'Name the portion.' }}</mat-error></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Price (₹)</mat-label><input matInput type="number" formControlName="price" />
              <mat-error>{{ server(v.get('price')) || 'Enter the price.' }}</mat-error></mat-form-field>
            <button mat-icon-button type="button" (click)="variants.removeAt(n)" aria-label="Remove portion"><mat-icon>close</mat-icon></button>
          </div>
        }
      </div>
      <button mat-button type="button" (click)="addVariant()"><mat-icon>add</mat-icon>Portion</button>

      <h3>Choices <small class="mute">(e.g. spice level, add-ons)</small></h3>
      @if (server(form.get('modifierGroups'))) { <p class="err">{{ server(form.get('modifierGroups')) }}</p> }
      <div formArrayName="modifierGroups">
        @for (g of groups.controls; track g; let n = $index) {
          <fieldset [formGroupName]="n">
            <div class="row">
              <mat-form-field appearance="outline"><mat-label>Group</mat-label><input matInput formControlName="name" maxlength="40" placeholder="e.g. Spice level" />
                <mat-error>{{ server(g.get('name')) || 'Name the group.' }}</mat-error></mat-form-field>
              <mat-form-field appearance="outline" class="num"><mat-label>At least</mat-label><input matInput type="number" formControlName="minSelect" />
                <mat-error>{{ server(g.get('minSelect')) }}</mat-error></mat-form-field>
              <mat-form-field appearance="outline" class="num"><mat-label>At most</mat-label><input matInput type="number" formControlName="maxSelect" />
                <mat-error>{{ server(g.get('maxSelect')) }}</mat-error></mat-form-field>
              <button mat-icon-button type="button" (click)="groups.removeAt(n)" aria-label="Remove group"><mat-icon>close</mat-icon></button>
            </div>
            @if (server(g.get('modifiers'))) { <p class="err">{{ server(g.get('modifiers')) }}</p> }
            <div formArrayName="modifiers">
              @for (m of mods(n).controls; track m; let k = $index) {
                <div class="row" [formGroupName]="k">
                  <mat-form-field appearance="outline"><mat-label>Choice</mat-label><input matInput formControlName="name" maxlength="40" placeholder="e.g. Extra cheese" />
                    <mat-error>{{ server(m.get('name')) || 'Name the choice.' }}</mat-error></mat-form-field>
                  <mat-form-field appearance="outline" class="num"><mat-label>Adds ₹</mat-label><input matInput type="number" formControlName="priceDelta" />
                    <mat-error>{{ server(m.get('priceDelta')) }}</mat-error></mat-form-field>
                  <button mat-icon-button type="button" (click)="mods(n).removeAt(k)" aria-label="Remove choice"><mat-icon>close</mat-icon></button>
                </div>
              }
            </div>
            <button mat-button type="button" (click)="addModifier(n)"><mat-icon>add</mat-icon>Choice</button>
          </fieldset>
        }
      </div>
      <button mat-button type="button" (click)="addGroup()"><mat-icon>add</mat-icon>Group of choices</button>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="dish-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `
    .dish { display: flex; flex-direction: column; gap: 2px; }
    .row { display: flex; gap: 8px; align-items: flex-start; flex-wrap: wrap; }
    .row mat-form-field { flex: 1 1 160px; } .row .num { flex: 0 1 100px; }
    .full { width: 100%; }
    h3 { margin: 12px 0 4px; font-size: 1rem; }
    fieldset { border: 1px solid #d9e0de; border-radius: 10px; padding: 8px 10px; margin: 0 0 8px; }
    .mute { color: #5b6b6e; } .err { color: #b3261e; margin: 0; }
  `,
})
export class DishEditDialog {
  data = inject<DishEditData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<DishEditDialog, boolean>);
  private api = inject(RestaurantApi);
  private fb = inject(FormBuilder).nonNullable;
  server = server;
  food = FOOD_LABEL;
  foodTypes: FoodType[] = ['VEG', 'NON_VEG', 'EGG'];
  busy = signal(false);
  error = signal('');
  private i = this.data.item;
  form = this.fb.group({
    name: [this.i?.name ?? '', [Validators.required, Validators.minLength(2)]],
    code: [this.i?.code ?? '', Validators.required],
    categoryId: [this.i?.categoryId ?? this.data.categoryId ?? this.data.categories[0]?.id ?? '', Validators.required],
    foodType: [this.i?.foodType ?? 'VEG' as FoodType],
    price: [this.i?.price ?? 0, [Validators.required, Validators.min(0)]],
    sortOrder: [this.i?.sortOrder ?? 0, [Validators.min(0), Validators.max(10000)]],
    description: [this.i?.description ?? ''],
    isActive: [this.i?.isActive ?? true],
    variants: this.fb.array((this.i?.variants ?? []).map(v => this.variant(v.id, v.name, v.price))),
    modifierGroups: this.fb.array((this.i?.modifierGroups ?? []).map(g =>
      this.group(g.id, g.name, g.minSelect, g.maxSelect, g.modifiers.map(m => this.modifier(m.id, m.name, m.priceDelta))))),
  });
  get variants() { return this.form.controls.variants; }
  get groups() { return this.form.controls.modifierGroups; }
  mods(n: number) { return this.groups.at(n).controls.modifiers; }

  private variant(id: string | null, name: string, price: number) {
    return this.fb.group({ id: [id], name: [name, Validators.required], price: [price, [Validators.required, Validators.min(0)]] });
  }
  private modifier(id: string | null, name: string, priceDelta: number) {
    return this.fb.group({ id: [id], name: [name, Validators.required], priceDelta: [priceDelta, Validators.min(0)] });
  }
  private group(id: string | null, name: string, min: number, max: number, modifiers: ReturnType<DishEditDialog['modifier']>[]) {
    return this.fb.group({ id: [id], name: [name, Validators.required], minSelect: [min], maxSelect: [max], modifiers: this.fb.array(modifiers) });
  }
  addVariant() { this.variants.push(this.variant(null, '', this.form.controls.price.value)); }
  addGroup() { this.groups.push(this.group(null, '', 0, 1, [this.modifier(null, '', 0)])); }
  addModifier(n: number) { (this.mods(n) as FormArray).push(this.modifier(null, '', 0)); }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.error.set('');
    this.api.saveItem(this.data.propertyId, this.i?.id ?? null, {
      ...v, code: v.code.trim().toUpperCase(), name: v.name.trim(), description: v.description.trim() || null, version: this.i?.version ?? null,
      modifierGroups: v.modifierGroups,
    }).subscribe({
      next: () => this.ref.close(true),
      error: e => { this.busy.set(false); report(this.form, e, m => this.error.set(m)); },
    });
  }
}
