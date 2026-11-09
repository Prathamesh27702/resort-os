import { Component, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { ActivityBooking, EquipmentAssignment, EquipmentItem, EquipmentStatus, EquipmentType } from '../../core/models';
import { showError } from '../leave/leave.api';
import { ActivitiesApi, EQUIPMENT_STATUS } from './activities.api';

const server = (c: AbstractControl | null) => (c?.errors?.['server'] as string | undefined) ?? '';
const text = (v: string | null | undefined) => (v ?? '').trim() || null;

// ------------------------------------------------------------------ issuing

export interface IssueData {
  propertyId: string; types: EquipmentType[];
  /** Issuing for an activity booking: the name, stay and due-back time come from it. */
  booking?: ActivityBooking;
  /** The kind and how many to start with (an activity's equipment, one a person). */
  typeId?: string | null; quantity?: number;
}

/** Hands out (or keeps aside) a number of one kind of equipment: the first available by tag. */
@Component({
  selector: 'app-issue-equipment-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Issue equipment</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="issue-form">
      @if (data.booking; as b) {
        <p>For <b>{{ b.guestName }}</b> · {{ b.activityName }} {{ b.startTime }} · {{ b.code }}</p>
      } @else {
        <mat-form-field appearance="outline"><mat-label>Who it is for</mat-label>
          <input matInput formControlName="issuedTo" maxlength="100" placeholder="e.g. Asha Rao, Room 104" />
          <mat-error>{{ server(form.get('issuedTo')) || 'Say who it is for.' }}</mat-error></mat-form-field>
      }
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>What</mat-label>
          <mat-select formControlName="equipmentTypeId" [aria-label]="'What'">
            @for (t of data.types; track t.id) { <mat-option [value]="t.id">{{ t.name }} ({{ t.available }} available)</mat-option> }
          </mat-select>
          <mat-error>{{ server(form.get('equipmentTypeId')) || 'Choose what to issue.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline" class="qty"><mat-label>How many</mat-label><input matInput type="number" formControlName="quantity" min="1" />
          <mat-error>{{ server(form.get('quantity')) || 'How many, 1–50.' }}</mat-error></mat-form-field>
      </div>
      <mat-checkbox formControlName="reserveOnly">Only keep them aside (hand over later)</mat-checkbox>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="issue-form" [disabled]="busy()">{{ form.value.reserveOnly ? 'Keep aside' : 'Issue' }}</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: var(--space-1); min-width: min(440px, 84vw); } .row { display: flex; gap: var(--space-2); flex-wrap: wrap; }
           .row > * { flex: 1 1 180px; } .row .qty { flex: 0 1 110px; } .err { color: var(--ro-error); } p { margin: var(--space-1) 0 10px; }`,
})
export class IssueEquipmentDialog {
  data = inject<IssueData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<IssueEquipmentDialog, EquipmentAssignment[]>);
  private api = inject(ActivitiesApi);
  server = server;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({
    issuedTo: ['', this.data.booking ? [] : [Validators.required]],
    equipmentTypeId: [this.data.typeId ?? this.data.types[0]?.id ?? null, Validators.required],
    quantity: [this.data.quantity ?? 1, [Validators.required, Validators.min(1), Validators.max(50)]],
    reserveOnly: [false],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.error.set('');
    this.api.issue(this.data.propertyId, {
      activityBookingId: this.data.booking?.id ?? null, reservationId: null, issuedTo: this.data.booking ? null : text(v.issuedTo),
      equipmentIds: null, equipmentTypeId: v.equipmentTypeId, quantity: Number(v.quantity), reserveOnly: !!v.reserveOnly,
    }).subscribe({
      next: r => this.ref.close(r),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ taking back

export interface ReturnData { propertyId: string; assignment: EquipmentAssignment; }

@Component({
  selector: 'app-return-equipment-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Return {{ x.tag }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="return-form">
      <p>{{ x.typeName }} from <b>{{ x.issuedTo }}</b>@if (x.bookingCode) { · {{ x.bookingCode }} }</p>
      <mat-button-toggle-group formControlName="condition" aria-label="How it came back">
        <mat-button-toggle value="GOOD">Good</mat-button-toggle>
        <mat-button-toggle value="DAMAGED">Damaged</mat-button-toggle>
        <mat-button-toggle value="LOST">Lost</mat-button-toggle>
      </mat-button-toggle-group>
      @if (form.value.condition !== 'GOOD') {
        <mat-form-field appearance="outline"><mat-label>What happened</mat-label>
          <input matInput formControlName="note" maxlength="300" [placeholder]="form.value.condition === 'LOST' ? 'e.g. Dropped in the sea' : 'e.g. Strap torn'" />
          <mat-error>{{ server(form.get('note')) || 'Say what happened (at least 3 characters).' }}</mat-error></mat-form-field>
        <p class="mute">It is out of use until someone puts it back.</p>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="return-form" [disabled]="busy()">Return</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: var(--space-2); min-width: min(400px, 84vw); } .err { color: var(--ro-error); } .mute { color: var(--ro-mute); }
           p { margin: var(--space-1) 0; } mat-button-toggle-group { align-self: flex-start; }`,
})
export class ReturnEquipmentDialog {
  data = inject<ReturnData>(MAT_DIALOG_DATA);
  x = this.data.assignment;
  private ref = inject(MatDialogRef<ReturnEquipmentDialog, EquipmentAssignment>);
  private api = inject(ActivitiesApi);
  server = server;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({ condition: ['GOOD' as 'GOOD' | 'DAMAGED' | 'LOST'], note: [''] });

  save() {
    const v = this.form.getRawValue();
    if (v.condition !== 'GOOD' && (v.note ?? '').trim().length < 3) {
      this.form.controls.note.setErrors({ required: true });
      this.form.controls.note.markAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set('');
    this.api.returnItem(this.data.propertyId, this.x, v.condition!, text(v.note)).subscribe({
      next: r => this.ref.close(r),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ an item's status by hand

export interface StatusData { propertyId: string; item: EquipmentItem; }

@Component({
  selector: 'app-equipment-status-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ item.tag }} · {{ label[item.status] }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="status-form">
      @if (item.statusNote) { <p class="mute">{{ item.statusNote }}</p> }
      <mat-form-field appearance="outline"><mat-label>Set to</mat-label>
        <mat-select formControlName="status" [aria-label]="'Set to'">
          @for (m of item.moves; track m) { <mat-option [value]="m">{{ label[m] }}</mat-option> }
        </mat-select>
        <mat-error>{{ server(form.get('status')) || 'Choose the new status.' }}</mat-error></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>{{ form.value.status === 'AVAILABLE' ? 'Note (optional)' : 'What is wrong, or what was done' }}</mat-label>
        <input matInput formControlName="note" maxlength="300" placeholder="e.g. Strap stitched" />
        <mat-error>{{ server(form.get('note')) || 'Say what is wrong or what was done (at least 3 characters).' }}</mat-error></mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="status-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; min-width: min(400px, 84vw); } .err { color: var(--ro-error); } .mute { color: var(--ro-mute); }`,
})
export class EquipmentStatusDialog {
  data = inject<StatusData>(MAT_DIALOG_DATA);
  item = this.data.item;
  label = EQUIPMENT_STATUS;
  private ref = inject(MatDialogRef<EquipmentStatusDialog, EquipmentItem>);
  private api = inject(ActivitiesApi);
  server = server;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({ status: [this.item.moves[0] ?? null as EquipmentStatus | null, Validators.required], note: [''] });

  save() {
    const v = this.form.getRawValue();
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    if (v.status !== 'AVAILABLE' && (v.note ?? '').trim().length < 3) {
      this.form.controls.note.setErrors({ required: true });
      this.form.controls.note.markAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set('');
    this.api.setStatus(this.data.propertyId, this.item, v.status!, text(v.note)).subscribe({
      next: r => this.ref.close(r),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ kinds and new items

export interface TypeData { propertyId: string; type: EquipmentType | null; }

/** A kind of equipment and, for a new one, how many items to add with which tag prefix. */
@Component({
  selector: 'app-equipment-type-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ t ? t.name : 'New kind of equipment' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="type-form">
      <div class="row">
        <mat-form-field appearance="outline" class="code"><mat-label>Code</mat-label><input matInput formControlName="code" maxlength="10" placeholder="LJ" />
          <mat-error>{{ server(form.get('code')) || 'Give a short code, e.g. LJ.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" maxlength="60" placeholder="Life jacket" />
          <mat-error>{{ server(form.get('name')) || 'Give it a name.' }}</mat-error></mat-form-field>
      </div>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Add items</mat-label><input matInput type="number" formControlName="count" min="0" />
          <mat-hint>{{ t ? 'More of these, numbered after the last' : 'How many you have' }}</mat-hint>
          <mat-error>{{ server(form.get('count')) || '0–50 at a time.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Tag prefix</mat-label><input matInput formControlName="prefix" maxlength="8" placeholder="LJ" />
          <mat-hint>Tags like {{ (form.value.prefix || 'LJ').toUpperCase() }}-01</mat-hint>
          <mat-error>{{ server(form.get('prefix')) || '1–8 letters or digits.' }}</mat-error></mat-form-field>
      </div>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="type-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 6px; min-width: min(440px, 84vw); } .row { display: flex; gap: var(--space-2); flex-wrap: wrap; }
           .row > * { flex: 1 1 150px; } .code { flex: 0 1 120px; } .err { color: var(--ro-error); }`,
})
export class EquipmentTypeDialog {
  data = inject<TypeData>(MAT_DIALOG_DATA);
  t = this.data.type;
  private ref = inject(MatDialogRef<EquipmentTypeDialog, string>);
  private api = inject(ActivitiesApi);
  server = server;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({
    code: [this.t?.code ?? '', Validators.required],
    name: [this.t?.name ?? '', Validators.required],
    count: [this.t ? 0 : 1, [Validators.min(0), Validators.max(50)]],
    prefix: [this.t?.code ?? ''],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    const count = Number(v.count || 0);
    this.busy.set(true);
    this.error.set('');
    this.api.saveType(this.data.propertyId, this.t?.id ?? null, {
      code: v.code!.trim().toUpperCase(), name: v.name!.trim(), sortOrder: this.t?.sortOrder ?? 0, isActive: this.t?.isActive ?? true, version: this.t?.version ?? null,
    }).subscribe({
      next: type => {
        if (!count) { this.ref.close(type.name); return; }
        this.api.addItems(this.data.propertyId, { equipmentTypeId: type.id, tag: null, prefix: (text(v.prefix) ?? type.code).toUpperCase(), count, note: null })
          .subscribe({
            next: items => this.ref.close(`${type.name}: ${items.map(i => i.tag).join(', ')} added`),
            error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
          });
      },
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}
