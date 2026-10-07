import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../../core/auth.service';
import { fieldErrors, toProblem } from '../../../core/http';
import { AccommodationType, Amenity, InventoryMode } from '../../../core/models';
import { PropertyContext } from '../../../core/property-context';
import { RoomsApi, label } from '../../rooms/rooms.api';
import { typeForm } from '../../rooms/rooms.forms';

const CATEGORY_ORDER = ['ROOM', 'BATHROOM', 'VIEW', 'DORMITORY', 'ACCESSIBILITY', 'OTHER'];

@Component({
  selector: 'app-room-type-edit',
  imports: [ReactiveFormsModule, RouterLink, MatButtonModule, MatButtonToggleModule, MatCheckboxModule, MatFormFieldModule, MatInputModule,
    MatSlideToggleModule, MatSnackBarModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <a routerLink="/settings/room-types" class="back">Room types</a>
          <h1>{{ isNew() ? 'Add room type' : (loaded()?.name ?? '') }}</h1>
          @if (!isNew() && !canManage()) { <p>You can view this room type but not change it.</p> }
        </div>
      </div>

      @if (isNew() || loaded()) {
      <form class="panel box" [formGroup]="form" (ngSubmit)="save()">
        <h2>How it is sold</h2>
        <mat-button-toggle-group formControlName="inventoryMode" (change)="modeChanged($event.value)" aria-label="How it is sold">
          <mat-button-toggle value="UNIT">Per room</mat-button-toggle>
          <mat-button-toggle value="BED">Per bed (dormitory)</mat-button-toggle>
        </mat-button-toggle-group>
        <p class="hint">
          @if (form.controls.inventoryMode.value === 'BED') { Each bed is booked on its own and sleeps one guest. Add the beds on the room page. }
          @else { The whole room is booked by one party. }
          @if (lockedMode()) { <br /><b>Can't change:</b> rooms of this type already have beds or are in use. }
        </p>

        <h2>Details</h2>
        <div class="form-grid">
          <mat-form-field appearance="outline">
            <mat-label>Name</mat-label>
            <input matInput formControlName="name" placeholder="Deluxe garden room" />
            @if (err()['name']) { <mat-error>{{ err()['name'] }}</mat-error> }
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Code</mat-label>
            <input matInput formControlName="code" placeholder="DELUXE" (input)="upperCode()" />
            <mat-hint>Letters, digits and _ (saved in capitals). Can't be changed later.</mat-hint>
            @if (err()['code']) { <mat-error>{{ err()['code'] }}</mat-error> } @else { <mat-error>2–30 letters, digits or _</mat-error> }
          </mat-form-field>
          <mat-form-field appearance="outline" class="full">
            <mat-label>Description</mat-label>
            <textarea matInput formControlName="description" rows="2" maxlength="2000"></textarea>
          </mat-form-field>
          @if (form.controls.inventoryMode.value === 'UNIT') {
            <mat-form-field appearance="outline">
              <mat-label>Standard guests</mat-label>
              <input matInput type="number" min="1" max="50" formControlName="baseOccupancy" />
              <mat-hint>Included in the room rate</mat-hint>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Maximum guests</mat-label>
              <input matInput type="number" min="1" max="50" formControlName="maxOccupancy" />
              <mat-hint>Including extra beds</mat-hint>
            </mat-form-field>
          }
          <mat-form-field appearance="outline">
            <mat-label>Size (m²)</mat-label>
            <input matInput type="number" min="0" step="0.5" formControlName="sizeSqm" />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Display order</mat-label>
            <input matInput type="number" min="0" formControlName="sortOrder" />
          </mat-form-field>
        </div>
        @if (form.hasError('maxBelowBase')) { <p class="error">Maximum guests can't be below standard guests.</p> }

        <h2>Amenities</h2>
        <div class="amenities">
          @for (g of amenityGroups(); track g.category) {
            <fieldset>
              <legend>{{ label(g.category) }}</legend>
              @for (a of g.items; track a.id) {
                <mat-checkbox [checked]="selected().has(a.id)" (change)="toggle(a.id, $event.checked)" [disabled]="form.disabled">{{ a.name }}</mat-checkbox>
              }
            </fieldset>
          }
        </div>

        @if (!isNew()) {
          <h2>Status</h2>
          <mat-slide-toggle formControlName="isActive">Active (rooms of this type can be sold)</mat-slide-toggle>
          <p class="hint">To deactivate a type, first deactivate its rooms.</p>
        }

        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
        @if (canManage()) {
          <div class="actions">
            <button mat-flat-button type="submit" [disabled]="busy()">{{ isNew() ? 'Add room type' : 'Save changes' }}</button>
            <a mat-button routerLink="/settings/room-types">Cancel</a>
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
    .box { padding: 24px; max-width: 820px; }
    h2 { margin: 8px 0 12px; font-size: 1rem; color: #0e3b43; }
    h2:not(:first-child) { margin-top: 20px; padding-top: 20px; border-top: 1px solid #edf1f0; }
    .hint { color: #5b6b6e; font-size: .9rem; margin: 8px 0 0; }
    .amenities { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 12px; }
    fieldset { border: 1px solid #edf1f0; border-radius: 8px; padding: 8px 12px; margin: 0; display: flex; flex-direction: column; }
    legend { color: #5b6b6e; font-size: .85rem; padding: 0 4px; }
    .actions { display: flex; gap: 8px; margin-top: 16px; }
    .error { color: #b3261e; }
    mat-slide-toggle { margin: 8px 0; }
  `,
})
export class RoomTypeEditPage implements OnInit {
  id = input<string>();
  private api = inject(RoomsApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private router = inject(Router);
  private snack = inject(MatSnackBar);

  label = label;
  isNew = computed(() => !this.id());
  private pid = computed(() => this.ctx.current()?.id ?? '');
  canManage = computed(() => this.auth.can('accommodation.manage', this.pid()));

  form = typeForm(inject(FormBuilder));
  loaded = signal<AccommodationType | null>(null);
  amenities = signal<Amenity[]>([]);
  selected = signal(new Set<string>());
  busy = signal(false);
  error = signal('');
  err = signal<Record<string, string>>({});
  /** The API refuses mode changes once rooms exist; lock the toggle so nobody tries. */
  lockedMode = computed(() => (this.loaded()?.unitCount ?? 0) > 0);

  amenityGroups = computed(() => {
    const byCat = new Map<string, Amenity[]>();
    for (const a of this.amenities()) byCat.set(a.category, [...(byCat.get(a.category) ?? []), a]);
    return [...byCat.entries()]
      .sort(([a], [b]) => CATEGORY_ORDER.indexOf(a) - CATEGORY_ORDER.indexOf(b))
      .map(([category, items]) => ({ category, items }));
  });

  async ngOnInit() {
    this.amenities.set(await firstValueFrom(this.api.amenities(this.pid())));
    if (this.isNew()) return;
    this.form.controls.code.disable();
    this.show(await firstValueFrom(this.api.type(this.pid(), this.id()!)));
    if (!this.canManage()) this.form.disable();
  }

  private show(t: AccommodationType) {
    this.loaded.set(t);
    this.selected.set(new Set(t.amenityIds));
    this.form.patchValue({ ...t, description: t.description ?? '', amenityIds: t.amenityIds });
    if (this.lockedMode()) this.form.controls.inventoryMode.disable();
  }

  upperCode() {
    const c = this.form.controls.code;
    const v = c.value.toUpperCase().replace(/[\s-]/g, '_');
    if (v !== c.value) c.setValue(v);
  }

  modeChanged(mode: InventoryMode) {
    if (mode === 'BED') this.form.patchValue({ baseOccupancy: 1, maxOccupancy: 1 });
  }

  toggle(id: string, on: boolean) {
    const s = new Set(this.selected());
    if (on) s.add(id); else s.delete(id);
    this.selected.set(s);
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true); this.error.set(''); this.err.set({});
    const v = this.form.getRawValue();
    const body = {
      ...v,
      description: v.description?.trim() || null,
      sizeSqm: v.sizeSqm === null || (v.sizeSqm as unknown) === '' ? null : Number(v.sizeSqm),
      baseOccupancy: Number(v.baseOccupancy), maxOccupancy: Number(v.maxOccupancy),
      sortOrder: Number(v.sortOrder) || 0,
      amenityIds: [...this.selected()],
    };
    const req = this.isNew()
      ? this.api.createType(this.pid(), body)
      : this.api.updateType(this.pid(), this.id()!, { ...body, version: this.loaded()!.version });
    req.subscribe({
      next: t => {
        this.busy.set(false);
        if (this.isNew()) {
          this.snack.open(`${t.name} added. Now add its rooms.`, undefined, { duration: 3000 });
          this.router.navigate(['/rooms/new'], { queryParams: { type: t.id } });
        } else {
          this.show(t);
          this.snack.open('Changes saved', undefined, { duration: 2500 });
        }
      },
      error: e => {
        const p = toProblem(e);
        this.err.set(fieldErrors(p));
        this.error.set(p.code === 'CONCURRENCY_CONFLICT'
          ? 'Someone else changed this room type while you were editing. Reload the page to see their changes.'
          : p.detail);
        this.busy.set(false);
      },
    });
  }
}
