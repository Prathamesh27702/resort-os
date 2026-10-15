import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { Subject, debounceTime, switchMap } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { DOCUMENT_TYPES, Guest, GuestDocument, GuestListItem } from '../../core/models';
import { pickerError } from '../../core/dates';
import { showFieldErrors, toProblem } from '../../core/http';
import { label } from '../rooms/rooms.api';
import { minTrimmed } from '../rooms/rooms.forms';
import { COUNTRIES, GuestsApi, formatPhone } from './guests.api';
import { aadhaarNumber, idNumber } from './guests.forms';

const MAX_BYTES = 5 * 1024 * 1024;

export interface DocumentDialogData { guest: Guest; }

/** Add an ID document: type, number and/or a scan (JPEG, PNG or PDF up to 5 MB). Returns the created document. */
@Component({
  selector: 'app-document-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatDatepickerModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Add ID document</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="doc-form">
      <p class="hint">The number and scan are encrypted. Only staff allowed to see ID documents can open them, and every view is recorded.</p>
      <mat-form-field appearance="outline">
        <mat-label>Document</mat-label>
        <mat-select formControlName="documentType">
          @for (t of types; track t) { <mat-option [value]="t">{{ label(t) }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field appearance="outline">
        <mat-label>{{ isAadhaar() ? 'Aadhaar number (12 digits; only the last 4 are kept)' : 'Document number' }}</mat-label>
        <input matInput formControlName="number" autocomplete="off" [attr.inputmode]="isAadhaar() ? 'numeric' : null" />
        <mat-error>{{ numberError() }}</mat-error>
        @if (isAadhaar()) { <mat-hint>Keep a masked Aadhaar copy only: the first 8 digits hidden</mat-hint> }
      </mat-form-field>
      <div class="two">
        <mat-form-field appearance="outline">
          <mat-label>Issued by</mat-label>
          <mat-select formControlName="issuingCountry">
            <mat-option value="">—</mat-option>
            @for (c of countries; track c.code) { <mat-option [value]="c.code">{{ c.name }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Expires on</mat-label>
          <input matInput [matDatepicker]="dpExpiresOn" formControlName="expiresOn" />
          <mat-datepicker-toggle matIconSuffix [for]="dpExpiresOn" />
          <mat-datepicker #dpExpiresOn />
          <mat-error>{{ pickerError(form.controls.expiresOn.errors) }}</mat-error>
        </mat-form-field>
      </div>
      @if (form.value.documentType === 'VISA' || form.value.documentType === 'PASSPORT') {
        <mat-form-field appearance="outline">
          <mat-label>Visa number (foreign nationals, for Form-C)</mat-label>
          <input matInput formControlName="visaNumber" autocomplete="off" />
        </mat-form-field>
      }
      <label class="file">
        <span>Scan or photo (JPEG, PNG or PDF, up to 5 MB)</span>
        <input type="file" accept="image/jpeg,image/png,application/pdf" capture="environment" (change)="pick($event)" />
      </label>
      @if (fileError()) { <p class="err">{{ fileError() }}</p> }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="doc-form" [disabled]="busy()">Save document</button>
    </div>
  `,
  styles: `
    form { display: flex; flex-direction: column; min-width: min(440px, 80vw); }
    .hint { color: #5b6b6e; margin: 0 0 12px; }
    .two { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .file { display: flex; flex-direction: column; gap: 6px; margin: 4px 0 8px; color: #1b2a2e; }
    .err { color: #b3261e; }
    @media (max-width: 480px) { .two { grid-template-columns: 1fr; } }
  `,
})
export class DocumentDialog {
  data = inject<DocumentDialogData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<DocumentDialog, GuestDocument>);
  private api = inject(GuestsApi);
  types = DOCUMENT_TYPES;
  countries = COUNTRIES;
  label = label;
  pickerError = pickerError;
  busy = signal(false);
  error = signal('');
  fileError = signal('');
  private file: File | null = null;

  form = inject(FormBuilder).nonNullable.group({
    documentType: [this.data.guest.nationality && this.data.guest.nationality !== 'IN' ? 'PASSPORT' : 'AADHAAR', Validators.required],
    number: ['', this.data.guest.nationality && this.data.guest.nationality !== 'IN' ? idNumber : aadhaarNumber],
    issuingCountry: [this.data.guest.nationality ?? ''],
    expiresOn: [''],
    visaNumber: [''],
  });
  isAadhaar = () => this.form.controls.documentType.value === 'AADHAAR';

  constructor() {
    // Aadhaar has its own rule (12 digits or the last 4); every other document takes 4–30 letters or digits.
    this.form.controls.documentType.valueChanges.pipe(takeUntilDestroyed()).subscribe(t => {
      const number = this.form.controls.number;
      number.setValidators(t === 'AADHAAR' ? aadhaarNumber : idNumber);
      number.updateValueAndValidity();
    });
  }

  /** Message under the number field: the API's own message first, then the client-side rule. */
  numberError() {
    const e = this.form.controls.number.errors;
    if (!e) return '';
    if (e['server']) return e['server'];
    if (e['aadhaar']) return e['aadhaar'];
    return 'Document number: 4–30 letters or digits.';
  }

  pick(e: Event) {
    const f = (e.target as HTMLInputElement).files?.[0] ?? null;
    this.fileError.set('');
    if (f && f.size > MAX_BYTES) { this.fileError.set('That file is larger than 5 MB.'); this.file = null; return; }
    if (f && !['image/jpeg', 'image/png', 'application/pdf'].includes(f.type)) { this.fileError.set('Use a JPEG or PNG photo, or a PDF.'); this.file = null; return; }
    this.file = f;
  }

  save() {
    const v = this.form.getRawValue();
    if (this.form.invalid) { this.form.markAllAsTouched(); this.error.set(''); return; }
    if (!v.number.trim() && !this.file) { this.error.set('Enter the number, attach a scan, or both.'); return; }
    const fd = new FormData();
    fd.set('documentType', v.documentType);
    if (v.number.trim()) fd.set('number', v.number.trim());
    if (v.issuingCountry) fd.set('issuingCountry', v.issuingCountry);
    if (v.expiresOn) fd.set('expiresOn', v.expiresOn);
    if (v.visaNumber.trim()) fd.set('details', JSON.stringify({ visaNumber: v.visaNumber.trim() }));
    if (this.file) fd.set('file', this.file, this.file.name);
    this.busy.set(true); this.error.set('');
    this.api.uploadDocument(this.data.guest.id, fd).subscribe({
      next: d => this.ref.close(d),
      error: e => {
        const p = toProblem(e);
        showFieldErrors(this.form, p);
        this.error.set(p.detail);
        this.busy.set(false);
      },
    });
  }
}

export interface MergeDialogData { survivor: Guest; }

/** Fold another profile of the same person into this one. This profile is kept; the other is marked merged. */
@Component({
  selector: 'app-merge-dialog',
  imports: [FormsModule, ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatRadioModule],
  template: `
    <h2 mat-dialog-title>Merge a duplicate into {{ data.survivor.fullName }}</h2>
    <div mat-dialog-content class="body">
      <p class="hint">
        The profile you pick is folded into this one: its ID documents, consents and preferences move here, and empty fields here are
        filled from it. It then disappears from search. This can't be undone.
      </p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Find the other profile</mat-label>
        <input matInput [(ngModel)]="q" (ngModelChange)="search$.next($event)" placeholder="Name, phone or email" autocomplete="off" />
      </mat-form-field>
      <mat-radio-group [(ngModel)]="pickedId" class="list" aria-label="Profile to merge">
        @for (g of candidates(); track g.id) {
          <mat-radio-button [value]="g.id">
            <b>{{ g.fullName }}</b> <span class="mute">{{ phone(g.phone) }} {{ g.email }}</span>
          </mat-radio-button>
        } @empty { @if (q.trim()) { <p class="mute">No other active profile matches.</p> } }
      </mat-radio-group>
      <form [formGroup]="form">
        <mat-form-field appearance="outline" class="full">
          <mat-label>Reason (saved in the audit trail)</mat-label>
          <input matInput formControlName="reason" maxlength="500" placeholder="e.g. Same person, booked twice by phone" />
          <mat-error>At least 3 characters.</mat-error>
        </mat-form-field>
      </form>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="button" (click)="merge()" [disabled]="!pickedId || busy()">Merge</button>
    </div>
  `,
  styles: `
    .body { min-width: min(480px, 82vw); }
    .hint { color: #5b6b6e; margin: 0 0 12px; }
    .full { width: 100%; }
    .list { display: flex; flex-direction: column; gap: 4px; margin-bottom: 12px; max-height: 240px; overflow: auto; }
    .mute { color: #5b6b6e; margin-left: 6px; }
    .err { color: #b3261e; }
  `,
})
export class MergeDialog {
  data = inject<MergeDialogData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<MergeDialog, Guest>);
  private api = inject(GuestsApi);
  phone = formatPhone;
  q = '';
  pickedId: string | null = null;
  busy = signal(false);
  error = signal('');
  private found = signal<GuestListItem[]>([]);
  candidates = computed(() => this.found().filter(g => g.id !== this.data.survivor.id && g.status === 'ACTIVE'));
  search$ = new Subject<string>();
  form = inject(FormBuilder).nonNullable.group({ reason: ['', [minTrimmed(3)]] });

  constructor() {
    this.search$.pipe(debounceTime(250), switchMap(q => this.api.list(q, { pageSize: 10 })), takeUntilDestroyed())
      .subscribe(r => this.found.set(this.q.trim() ? r.items : []));
  }

  merge() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true); this.error.set('');
    this.api.get(this.pickedId!).subscribe({
      next: dup => this.api.merge({
        survivorId: this.data.survivor.id, survivorVersion: this.data.survivor.version,
        duplicateId: dup.id, duplicateVersion: dup.version, reason: this.form.getRawValue().reason.trim(),
      }).subscribe({
        next: g => this.ref.close(g),
        error: e => { this.error.set(toProblem(e).detail); this.busy.set(false); },
      }),
      error: e => { this.error.set(toProblem(e).detail); this.busy.set(false); },
    });
  }
}
