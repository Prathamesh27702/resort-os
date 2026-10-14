import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom, map, startWith } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../../core/auth.service';
import { showFieldErrors, toProblem } from '../../../core/http';
import { AccommodationType, Package, REVENUE_CATEGORIES } from '../../../core/models';
import { PropertyContext } from '../../../core/property-context';
import { RoomsApi, label } from '../../rooms/rooms.api';
import { RatesApi, rupeesExact, splitByPercent } from '../rates/rates.api';
import { componentGroup, moneyError, packageForm, splitError } from '../rates/rates.forms';

/** Add or edit a package: what it includes, its price, and how that price is split into revenue categories. */
@Component({
  selector: 'app-package-edit',
  imports: [ReactiveFormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule,
    MatSlideToggleModule, MatSnackBarModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <a routerLink="/settings/packages" class="back">Packages</a>
          <h1>{{ isNew() ? 'Add package' : (loaded()?.name ?? '') }}</h1>
          @if (!isNew() && !canManage()) { <p>You can view this package but not change it.</p> }
        </div>
      </div>

      @if (isNew() || loaded()) {
      <form class="panel box" [formGroup]="form" (ngSubmit)="save()">
        <h2>What it includes</h2>
        <div class="form-grid">
          <mat-form-field appearance="outline">
            <mat-label>Name</mat-label>
            <input matInput formControlName="name" placeholder="Couple getaway" />
            <mat-error>{{ form.controls.name.errors?.['server'] ?? 'Enter a name, e.g. Couple getaway.' }}</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Code</mat-label>
            <input matInput formControlName="code" placeholder="COUPLE" (input)="upperCode()" />
            <mat-hint>Letters, digits and _. Can't be changed later.</mat-hint>
            <mat-error>{{ form.controls.code.errors?.['server'] ?? 'Code: 2–30 letters, digits or underscore, e.g. COUPLE.' }}</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Room type</mat-label>
            <mat-select formControlName="accommodationTypeId">
              @for (t of types(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }
            </mat-select>
            <mat-error>{{ form.controls.accommodationTypeId.errors?.['server'] ?? 'Choose the room type this package includes.' }}</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Nights</mat-label>
            <input matInput type="number" min="1" max="30" formControlName="nights" />
            <mat-error>{{ form.controls.nights.errors?.['server'] ?? 'Nights must be between 1 and 30.' }}</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Adults</mat-label>
            <input matInput type="number" min="1" max="50" formControlName="adults" />
            @if (maxGuests(); as m) { <mat-hint>{{ selectedType()?.name }} sleeps up to {{ m }}</mat-hint> }
            <mat-error>{{ form.controls.adults.errors?.['server'] ?? 'Adults must be between 1 and 50.' }}</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Children</mat-label>
            <input matInput type="number" min="0" max="50" formControlName="children" />
            <mat-error>{{ form.controls.children.errors?.['server'] ?? 'Children must be between 0 and 50.' }}</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Package price (₹)</mat-label>
            <input matInput type="number" min="0" step="0.01" formControlName="price" />
            <mat-hint>For the whole stay, before tax</mat-hint>
            <mat-error>{{ moneyError(form.controls.price, 'Package price') }}</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Display order</mat-label>
            <input matInput type="number" min="0" formControlName="sortOrder" />
            <mat-error>{{ form.controls.sortOrder.errors?.['server'] ?? 'Sort order must be between 0 and 100000.' }}</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline" class="full">
            <mat-label>Description</mat-label>
            <textarea matInput formControlName="description" rows="2" maxlength="2000"></textarea>
            <mat-error>{{ form.controls.description.errors?.['server'] ?? 'Description can be at most 2000 characters.' }}</mat-error>
          </mat-form-field>
        </div>

        <h2>Revenue split</h2>
        <p class="hint">Give each part a share of the price. The shares must add up to 100%. Amounts are rounded to the paisa so they always add up to exactly the package price.</p>
        <div class="parts" formArrayName="components">
          @for (c of components.controls; track c; let i = $index) {
            <div class="part" [formGroupName]="i">
              <mat-form-field appearance="outline" class="cat">
                <mat-label>Category</mat-label>
                <mat-select formControlName="category">
                  @for (k of categories; track k) { <mat-option [value]="k">{{ label(k) }}</mat-option> }
                </mat-select>
                <mat-error>{{ c.controls.category.errors?.['server'] ?? 'Category must be Room, Food, Transport or Activity.' }}</mat-error>
              </mat-form-field>
              <mat-form-field appearance="outline" class="desc">
                <mat-label>What it includes</mat-label>
                <input matInput formControlName="description" placeholder="Breakfast for two" />
                <mat-error>{{ c.controls.description.errors?.['server'] ?? 'Say what this part includes, e.g. Breakfast for two.' }}</mat-error>
              </mat-form-field>
              <mat-form-field appearance="outline" class="share">
                <mat-label>Share %</mat-label>
                <input matInput type="number" min="0.01" max="100" step="0.01" formControlName="sharePercent" />
                <mat-error>{{ shareError(c.controls.sharePercent) }}</mat-error>
              </mat-form-field>
              <span class="amount num" [attr.aria-label]="'Amount ' + (i + 1)">{{ amountAt(i) }}</span>
              @if (canManage()) {
                <button mat-icon-button type="button" (click)="removePart(i)" [attr.aria-label]="'Remove part ' + (i + 1)"><mat-icon>delete</mat-icon></button>
              }
            </div>
          }
        </div>
        <div class="split-foot">
          @if (canManage()) { <button mat-stroked-button type="button" (click)="addPart()">Add part</button> }
          <span class="num" [class.error]="sharesTotal() !== 100">Shares: {{ sharesTotal() }}%</span>
          @if (amounts(); as a) { <span class="num">Amounts: {{ rupees(sum(a)) }}</span> }
        </div>
        @if (components.invalid && (components.touched || components.errors?.['server'])) {
          <p class="error" role="alert" data-split-error>{{ splitError(components) }}</p>
        }

        @if (!isNew()) {
          <h2>Status</h2>
          <mat-slide-toggle formControlName="isActive">Active (can be sold)</mat-slide-toggle>
        }

        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
        @if (canManage()) {
          <div class="actions">
            <button mat-flat-button type="submit" [disabled]="busy()">{{ isNew() ? 'Add package' : 'Save changes' }}</button>
            <a mat-button routerLink="/settings/packages">Cancel</a>
          </div>
        }
      </form>
      } @else {
        <p class="loading">Loading…</p>
      }
    </div>
  `,
  styles: `
    .loading { color: #5b6b6e; }
    .back { color: #5b6b6e; text-decoration: none; font-size: 0.9rem; } .back::before { content: '‹ '; }
    .box { padding: 24px; max-width: 900px; }
    h2 { margin: 8px 0 12px; font-size: 1rem; color: #0e3b43; }
    h2:not(:first-child) { margin-top: 20px; padding-top: 20px; border-top: 1px solid #edf1f0; }
    .hint { color: #5b6b6e; font-size: .9rem; margin: 0 0 12px; }
    .part { display: grid; grid-template-columns: 160px 1fr 110px 110px 40px; gap: 8px; align-items: start; }
    .amount { padding-top: 16px; text-align: right; }
    .split-foot { display: flex; gap: 16px; align-items: center; flex-wrap: wrap; margin: 4px 0 8px; }
    .actions { display: flex; gap: 8px; margin-top: 16px; }
    .error { color: #b3261e; }
    mat-slide-toggle { margin: 8px 0; }
    @media (max-width: 640px) {
      .part { grid-template-columns: 1fr 1fr; padding-bottom: 8px; border-bottom: 1px solid #edf1f0; margin-bottom: 8px; }
      .part .desc { grid-column: 1 / -1; order: -1; }
      .amount { padding-top: 0; text-align: left; }
    }
  `,
})
export class PackageEditPage implements OnInit {
  id = input<string>();
  private api = inject(RatesApi);
  private roomsApi = inject(RoomsApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private router = inject(Router);
  private snack = inject(MatSnackBar);
  private fb = inject(FormBuilder);

  readonly label = label;
  readonly rupees = rupeesExact;
  readonly moneyError = moneyError;
  readonly splitError = splitError;
  readonly categories = REVENUE_CATEGORIES;

  isNew = computed(() => !this.id());
  private pid = computed(() => this.ctx.current()?.id ?? '');
  canManage = computed(() => this.auth.can('rate.manage', this.pid()));

  form = packageForm(this.fb);
  get components() { return this.form.controls.components; }
  loaded = signal<Package | null>(null);
  types = signal<AccommodationType[]>([]);
  busy = signal(false);
  error = signal('');

  private value = toSignal(this.form.valueChanges.pipe(startWith(null), map(() => this.form.getRawValue())), { requireSync: true });
  selectedType = computed(() => this.types().find(t => t.id === this.value().accommodationTypeId) ?? null);
  maxGuests = computed(() => { const t = this.selectedType(); return t && t.inventoryMode === 'UNIT' ? t.maxOccupancy : null; });
  sharesTotal = computed(() => this.value().components.reduce((s, c) => s + Math.round(Number(c.sharePercent ?? 0) * 100), 0) / 100);
  /** Live amounts with the API's rounding rule; null until the price is valid and the shares add up to 100. */
  amounts = computed(() => {
    const v = this.value();
    const price = Number(v.price);
    if (v.price === null || Number.isNaN(price) || price <= 0) return null;
    return splitByPercent(price, v.components.map(c => Number(c.sharePercent ?? 0)));
  });

  async ngOnInit() {
    const types = await firstValueFrom(this.roomsApi.types(this.pid()));
    this.types.set(types.filter(t => t.isActive || t.id === this.loaded()?.accommodationTypeId));
    if (this.isNew()) {
      this.components.push(componentGroup(this.fb, { category: 'ROOM', description: '', sharePercent: 100 }));
      return;
    }
    this.form.controls.code.disable();
    const p = await firstValueFrom(this.api.package(this.pid(), this.id()!));
    if (!this.types().some(t => t.id === p.accommodationTypeId))
      this.types.set([...this.types(), ...types.filter(t => t.id === p.accommodationTypeId)]);
    this.show(p);
    if (!this.canManage()) this.form.disable();
  }

  private show(p: Package) {
    this.loaded.set(p);
    this.components.clear();
    for (const c of p.components) this.components.push(componentGroup(this.fb, c));
    this.form.patchValue({ ...p, description: p.description ?? '' });
  }

  upperCode() {
    const c = this.form.controls.code;
    const v = c.value.toUpperCase().replace(/[\s-]/g, '_');
    if (v !== c.value) c.setValue(v);
  }

  addPart() {
    const left = Math.round((100 - this.sharesTotal()) * 100) / 100;
    this.components.push(componentGroup(this.fb, { category: 'FOOD', description: '', sharePercent: left > 0 ? left : null }));
  }

  removePart(i: number) { this.components.removeAt(i); this.components.markAsTouched(); }

  amountAt(i: number): string {
    const a = this.amounts();
    return a && a[i] !== undefined ? this.rupees(a[i]) : '—';
  }

  sum(a: number[]) { return Math.round(a.reduce((s, x) => s + x * 100, 0)) / 100; }

  shareError(c: { errors: Record<string, unknown> | null }): string {
    const e = c.errors ?? {};
    if (e['server']) return e['server'] as string;
    if (e['required']) return 'Enter this part’s share in percent.';
    if (e['min']) return 'Share must be more than 0%.';
    if (e['max']) return 'Share can’t be more than 100%.';
    if (e['decimals']) return 'Share can have at most 2 decimal places, e.g. 12.50.';
    return '';
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); this.components.markAsTouched(); return; }
    this.busy.set(true); this.error.set('');
    const v = this.form.getRawValue();
    const body = {
      ...v,
      description: v.description.trim() || null,
      nights: Number(v.nights), adults: Number(v.adults), children: Number(v.children) || 0,
      price: Number(v.price), sortOrder: Number(v.sortOrder) || 0,
      components: v.components.map(c => ({ category: c.category, description: c.description.trim(), sharePercent: Number(c.sharePercent) })),
    };
    const req = this.isNew()
      ? this.api.createPackage(this.pid(), body)
      : this.api.updatePackage(this.pid(), this.id()!, { ...body, version: this.loaded()!.version });
    req.subscribe({
      next: p => {
        this.busy.set(false);
        if (this.isNew()) {
          this.snack.open(`${p.name} added`, undefined, { duration: 3000 });
          this.router.navigate(['/settings/packages']);
        } else {
          this.show(p);
          this.snack.open('Changes saved', undefined, { duration: 2500 });
        }
      },
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        if (p.code === 'CONCURRENCY_CONFLICT') {
          this.error.set('Someone else changed this package while you were editing. Reload the page to see their changes.');
          return;
        }
        showFieldErrors(this.form, p);
        this.error.set(p.detail);
      },
    });
  }
}
