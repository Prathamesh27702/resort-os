import { Component, computed, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { Observable } from 'rxjs';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { fieldErrors, showFieldErrors, toProblem } from '../../core/http';
import { InHouseRoom, MenuItem, OrderLineRequest, PaymentMethod, Problem } from '../../core/models';
import { METHODS, methodLabel } from '../payments/payments.api';
import { rupees, rupeesExact } from '../settings/rates/rates.api';
import { FOOD_LABEL, linePrice, pickProblem } from './restaurant.api';

/** The server's message for a field, put there by showFieldErrors. */
const server = (c: AbstractControl) => (c.errors?.['server'] as string | undefined) ?? '';

/** Puts the API's field messages under their fields; anything that isn't about a field (or a field this dialog doesn't
 *  have) goes in the dialog's error line, word for word. */
function report(form: Parameters<typeof showFieldErrors>[0], p: Problem, error: (m: string) => void, known: string[]) {
  const errs = fieldErrors(p);
  showFieldErrors(form, p);
  const unknown = Object.keys(errs).filter(k => !known.includes(k));
  if (!Object.keys(errs).length) error(p.detail);
  else if (unknown.length) error(unknown.map(k => errs[k]).join(' '));
}

// ------------------------------------------------------------------ a dish with its portion and choices

export interface DishData { item: MenuItem; }

/** A dish that needs a pick before it can go on the order: its portion (Half / Full), the choices (spice level, add-ons),
 *  how many and a note for the kitchen. Closes with the line to add. */
@Component({
  selector: 'app-dish-dialog',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatRadioModule],
  template: `
    <h2 mat-dialog-title><span [class]="'food ' + item.foodType" [attr.aria-label]="food[item.foodType]"></span>{{ item.name }}</h2>
    <div mat-dialog-content class="dish">
      @if (item.description) { <p class="mute">{{ item.description }}</p> }
      @if (item.variants.length) {
        <h3>Portion</h3>
        <mat-radio-group [ngModel]="variantId()" (ngModelChange)="variantId.set($event)" aria-label="Portion" class="opts">
          @for (v of item.variants; track v.id) { <mat-radio-button [value]="v.id">{{ v.name }} · {{ rupees(v.price) }}</mat-radio-button> }
        </mat-radio-group>
      }
      @for (g of item.modifierGroups; track g.id) {
        <h3>{{ g.name }} <small class="mute">{{ rule(g.minSelect, g.maxSelect) }}</small></h3>
        <div class="opts" role="group" [attr.aria-label]="g.name">
          @for (m of g.modifiers; track m.id) {
            <mat-checkbox [checked]="picked().includes(m.id)" (change)="toggle(g.id, m.id, $event.checked)">
              {{ m.name }}@if (m.priceDelta) { · +{{ rupees(m.priceDelta) }} }
            </mat-checkbox>
          }
        </div>
      }
      <div class="qty" role="group" aria-label="How many">
        <button mat-icon-button type="button" (click)="qty.set(qty() - 1)" [disabled]="qty() <= 1" aria-label="One less"><mat-icon>remove</mat-icon></button>
        <b>{{ qty() }}</b>
        <button mat-icon-button type="button" (click)="qty.set(qty() + 1)" [disabled]="qty() >= 99" aria-label="One more"><mat-icon>add</mat-icon></button>
      </div>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Note for the kitchen (optional)</mat-label>
        <input matInput [(ngModel)]="note" maxlength="200" placeholder="e.g. No onion" />
      </mat-form-field>
      @if (problem(); as p) { <p class="err" role="alert">{{ p }}</p> }
    </div>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="button" (click)="add()">Add · {{ rupees(price() * qty()) }}</button>
    </div>
  `,
  styles: `
    .dish { min-width: min(420px, 84vw); }
    h3 { margin: 12px 0 4px; font-size: 1rem; }
    .opts { display: flex; flex-direction: column; }
    .qty { display: flex; align-items: center; gap: 12px; margin: 12px 0; }
    .qty b { font-size: 1.3rem; min-width: 2ch; text-align: center; }
    .full { width: 100%; }
    .mute { color: #5b6b6e; }
    .err { color: #b3261e; }
    .food { display: inline-block; width: 12px; height: 12px; border: 2px solid; border-radius: 2px; margin-right: 8px; vertical-align: middle; }
    .food.VEG { border-color: #2e7d32; background: radial-gradient(#2e7d32 40%, transparent 45%); }
    .food.NON_VEG { border-color: #8c1d18; background: radial-gradient(#8c1d18 40%, transparent 45%); }
    .food.EGG { border-color: #c77700; background: radial-gradient(#c77700 40%, transparent 45%); }
  `,
})
export class DishDialog {
  private ref = inject(MatDialogRef<DishDialog, OrderLineRequest>);
  item = inject<DishData>(MAT_DIALOG_DATA).item;
  food = FOOD_LABEL;
  rupees = rupees;
  variantId = signal<string | null>(this.item.variants.length === 1 ? this.item.variants[0].id : null);
  picked = signal<string[]>([]);
  qty = signal(1);
  note = '';
  tried = signal(false);
  price = computed(() => linePrice(this.item, this.variantId(), this.picked()));
  problem = computed(() => (this.tried() ? pickProblem(this.item, this.variantId(), this.picked()) : null));

  rule(min: number, max: number) {
    if (min === max) return min === 1 ? 'choose one' : `choose ${min}`;
    return min === 0 ? `up to ${max}, optional` : `choose ${min}–${max}`;
  }

  /** A group that allows only one choice swaps the pick, like a radio button. */
  toggle(groupId: string, id: string, on: boolean) {
    const group = this.item.modifierGroups.find(g => g.id === groupId)!;
    const others = group.maxSelect === 1 ? group.modifiers.map(m => m.id) : [];
    this.picked.update(p => on ? [...p.filter(x => !others.includes(x)), id] : p.filter(x => x !== id));
  }

  add() {
    this.tried.set(true);
    if (this.problem()) return;
    this.ref.close({ menuItemId: this.item.id, variantId: this.variantId(), modifierIds: this.picked(), quantity: this.qty(), note: this.note.trim() || null });
  }
}

// ------------------------------------------------------------------ a reason (void a line or the whole order)

export interface ReasonData<T> { title: string; intro: string; button: string; placeholder: string; run: (reason: string) => Observable<T>; }

/** Asks why, then runs the step; the server's reason rule shows under the field. Closes with the step's result. */
@Component({
  selector: 'app-reason-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="reason-form">
      <p>{{ data.intro }}</p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Reason</mat-label>
        <input matInput formControlName="reason" maxlength="500" [placeholder]="data.placeholder" cdkFocusInitial />
        <mat-error>{{ server(form.controls.reason) || 'Say why (at least 3 characters), e.g. ' + data.placeholder + '.' }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button color="warn" type="submit" form="reason-form" [disabled]="busy()">{{ data.button }}</button>
    </div>
  `,
  styles: `form { min-width: min(400px, 84vw); } .full { width: 100%; } .err { color: #b3261e; }`,
})
export class ReasonDialog<T> {
  data = inject<ReasonData<T>>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<ReasonDialog<T>, T>);
  server = server;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).nonNullable.group({ reason: ['', [Validators.required, Validators.minLength(3)]] });

  save() {
    this.form.controls.reason.setValue(this.form.controls.reason.value.trim());
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.error.set('');
    this.data.run(this.form.getRawValue().reason).subscribe({
      next: r => this.ref.close(r),
      error: e => { this.busy.set(false); report(this.form, toProblem(e), m => this.error.set(m), ['reason']); },
    });
  }
}

// ------------------------------------------------------------------ a discount

export interface DiscountData<T> {
  subtotal: number; kind: 'PERCENT' | 'AMOUNT' | null; value: number | null; reason: string | null;
  run: (kind: 'PERCENT' | 'AMOUNT' | null, value: number | null, reason: string | null) => Observable<T>;
}

/** A share of the bill or rupees off, with why (pos.discount). "Remove discount" takes it off again. */
@Component({
  selector: 'app-discount-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Discount</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="discount-form">
      <p class="mute">Food before GST: {{ rupeesExact(data.subtotal) }}. GST is worked out on the bill after the discount.</p>
      <mat-button-toggle-group formControlName="kind" aria-label="Kind of discount">
        <mat-button-toggle value="PERCENT">% of the bill</mat-button-toggle>
        <mat-button-toggle value="AMOUNT">₹ off</mat-button-toggle>
      </mat-button-toggle-group>
      @if (server(form.controls.kind)) { <p class="err">{{ server(form.controls.kind) }}</p> }
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ form.controls.kind.value === 'PERCENT' ? 'Percent' : 'Rupees off' }}</mat-label>
        <input matInput type="number" inputmode="decimal" formControlName="value" min="0" step="0.01" />
        <mat-error>{{ server(form.controls.value) || 'Enter the discount, more than 0.' }}</mat-error>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Reason</mat-label>
        <input matInput formControlName="reason" maxlength="500" placeholder="e.g. Regular guest" />
        <mat-error>{{ server(form.controls.reason) || 'Say why the discount is given (at least 3 characters), e.g. Regular guest.' }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      @if (data.kind) { <button mat-button type="button" (click)="remove()" [disabled]="busy()">Remove discount</button> }
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="discount-form" [disabled]="busy()">Give discount</button>
    </div>
  `,
  styles: `form { min-width: min(400px, 84vw); display: flex; flex-direction: column; gap: 8px; } .full { width: 100%; }
           .err { color: #b3261e; margin: 0; } .mute { color: #5b6b6e; }`,
})
export class DiscountDialog<T> {
  data = inject<DiscountData<T>>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<DiscountDialog<T>, T>);
  server = server;
  rupeesExact = rupeesExact;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({
    kind: [this.data.kind ?? 'PERCENT' as 'PERCENT' | 'AMOUNT', Validators.required],
    value: [this.data.value, [Validators.required, Validators.min(0.01)]],
    reason: [this.data.reason ?? '', [Validators.required, Validators.minLength(3)]],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.go(v.kind, v.value, v.reason?.trim() || null);
  }

  remove() { this.go(null, null, null); }

  private go(kind: 'PERCENT' | 'AMOUNT' | null, value: number | null, reason: string | null) {
    this.busy.set(true);
    this.error.set('');
    this.data.run(kind, value, reason).subscribe({
      next: r => this.ref.close(r),
      error: e => { this.busy.set(false); report(this.form, toProblem(e), m => this.error.set(m), ['kind', 'value', 'reason']); },
    });
  }
}

// ------------------------------------------------------------------ paid at the restaurant

export interface PayData<T> { total: number; run: (method: PaymentMethod, reference: string | null) => Observable<T>; }

/** How a walk-in bill was paid. Cash goes into the open cash drawer; UPI / card / bank transfer take their reference. */
@Component({
  selector: 'app-pay-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Paid · {{ rupeesExact(data.total) }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="pay-form">
      <mat-button-toggle-group formControlName="method" aria-label="How it was paid" class="methods">
        @for (m of methods; track m) { <mat-button-toggle [value]="m">{{ label(m) }}</mat-button-toggle> }
      </mat-button-toggle-group>
      @if (server(form.controls.method)) { <p class="err">{{ server(form.controls.method) }}</p> }
      @if (form.controls.method.value === 'CASH') {
        <p class="mute">The cash goes into the open cash drawer.</p>
      } @else {
        <mat-form-field appearance="outline" class="full">
          <mat-label>Reference (UPI ref, card slip or bank ref)</mat-label>
          <input matInput formControlName="reference" maxlength="100" placeholder="e.g. UPI 4123 5678 9012" />
          <mat-error>{{ server(form.controls.reference) }}</mat-error>
        </mat-form-field>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="pay-form" [disabled]="busy()">Settle the bill</button>
    </div>
  `,
  styles: `form { min-width: min(400px, 84vw); display: flex; flex-direction: column; gap: 8px; } .full { width: 100%; }
           .methods { flex-wrap: wrap; } .err { color: #b3261e; margin: 0; } .mute { color: #5b6b6e; }`,
})
export class PayDialog<T> {
  data = inject<PayData<T>>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<PayDialog<T>, T>);
  server = server;
  rupeesExact = rupeesExact;
  methods = METHODS;
  label = methodLabel;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).nonNullable.group({ method: ['CASH' as PaymentMethod], reference: [''] });

  save() {
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.error.set('');
    this.data.run(v.method, v.method === 'CASH' ? null : v.reference.trim() || null).subscribe({
      next: r => this.ref.close(r),
      error: e => { this.busy.set(false); report(this.form, toProblem(e), m => this.error.set(m), ['method', 'reference']); },
    });
  }
}

// ------------------------------------------------------------------ an in-house room (room service, charge to room)

export interface RoomData<T> {
  title: string; intro: string; button: string; rooms: InHouseRoom[]; selected: string | null; field: 'unitId' | 'reservationId';
  run: (room: InHouseRoom) => Observable<T>;
}

/** Picks a room with a guest checked in, then runs the step (open a room-service order, or charge the bill to the room). */
@Component({
  selector: 'app-room-dialog',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatRadioModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <div mat-dialog-content class="rooms">
      <p>{{ data.intro }}</p>
      @if (data.rooms.length) {
        <mat-radio-group [(ngModel)]="picked" aria-label="Room" class="opts">
          @for (r of data.rooms; track r.reservationId) {
            <mat-radio-button [value]="r.reservationId" [attr.data-room]="r.unitCode">
              <b>{{ r.unitName }}</b> · {{ r.guestName }} <span class="mute">({{ r.reference }})</span>
            </mat-radio-button>
          }
        </mat-radio-group>
      } @else {
        <p class="mute">No guest is checked in right now, so there is no room to choose.</p>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="button" (click)="save()" [disabled]="busy() || !data.rooms.length">{{ data.button }}</button>
    </div>
  `,
  styles: `.rooms { min-width: min(420px, 84vw); } .opts { display: flex; flex-direction: column; gap: 4px; } .mute { color: #5b6b6e; } .err { color: #b3261e; }`,
})
export class RoomDialog<T> {
  data = inject<RoomData<T>>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<RoomDialog<T>, T>);
  picked: string | null = this.data.selected ?? (this.data.rooms.length === 1 ? this.data.rooms[0].reservationId : null);
  busy = signal(false);
  error = signal('');

  save() {
    const room = this.data.rooms.find(r => r.reservationId === this.picked);
    if (!room) { this.error.set('Choose the room.'); return; }
    this.busy.set(true);
    this.error.set('');
    this.data.run(room).subscribe({
      next: r => this.ref.close(r),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const errs = fieldErrors(p);
        this.error.set(errs[this.data.field] ?? (Object.values(errs)[0] || p.detail));
      },
    });
  }
}
