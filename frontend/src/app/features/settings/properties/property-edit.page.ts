import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../../core/auth.service';
import { fieldErrors, toProblem } from '../../../core/http';
import { GST_STATES, PROPERTY_TYPES, Property } from '../../../core/models';
import { PropertyContext } from '../../../core/property-context';

const TIMEZONES = ['Asia/Kolkata', 'Asia/Dubai', 'Asia/Colombo', 'Asia/Kathmandu', 'Asia/Singapore', 'Europe/London', 'UTC'];

@Component({
  selector: 'app-property-edit',
  imports: [ReactiveFormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatSlideToggleModule, MatSnackBarModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <a routerLink="/settings/properties" class="back">Properties</a>
          <h1>{{ isNew() ? 'Add property' : (loaded()?.name ?? '') }}</h1>
          @if (!isNew() && !editable()) { <p>You can view this property but not change it.</p> }
        </div>
      </div>

      <form class="panel box" [formGroup]="form" (ngSubmit)="save()">
        <h2>Identity</h2>
        <div class="form-grid">
          <mat-form-field appearance="outline">
            <mat-label>Property name</mat-label>
            <input matInput formControlName="name" />
            @if (err()['name']) { <mat-error>{{ err()['name'] }}</mat-error> }
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Short code</mat-label>
            <input matInput formControlName="code" placeholder="BEACH01" />
            <mat-hint>Used on invoice numbers. Can’t be changed later.</mat-hint>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Legal name (as on GST registration)</mat-label>
            <input matInput formControlName="legalName" />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Type</mat-label>
            <mat-select formControlName="propertyType">
              @for (t of types; track t) { <mat-option [value]="t">{{ t.charAt(0) + t.slice(1).toLowerCase() }}</mat-option> }
            </mat-select>
          </mat-form-field>
        </div>

        <h2>Address</h2>
        <div class="form-grid">
          <mat-form-field appearance="outline" class="full"><mat-label>Address line 1</mat-label><input matInput formControlName="addressLine1" /></mat-form-field>
          <mat-form-field appearance="outline" class="full"><mat-label>Address line 2</mat-label><input matInput formControlName="addressLine2" /></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>City / town</mat-label><input matInput formControlName="city" /></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>District</mat-label><input matInput formControlName="district" /></mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>State (GST)</mat-label>
            <mat-select formControlName="gstStateCode">
              <mat-option [value]="null">Not set</mat-option>
              @for (s of states; track s.code) { <mat-option [value]="s.code">{{ s.name }} ({{ s.code }})</mat-option> }
            </mat-select>
            <mat-hint>Decides CGST + SGST vs IGST on invoices.</mat-hint>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>PIN code</mat-label><input matInput formControlName="postalCode" inputmode="numeric" />
          </mat-form-field>
        </div>

        <h2>Operations</h2>
        <div class="form-grid">
          <mat-form-field appearance="outline">
            <mat-label>Timezone</mat-label>
            <mat-select formControlName="timezone">
              @for (z of timezones; track z) { <mat-option [value]="z">{{ z }}</mat-option> }
            </mat-select>
            <mat-hint>All dates, night audit and reports use this.</mat-hint>
          </mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Currency</mat-label><input matInput formControlName="currency" maxlength="3" /></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Check-in time</mat-label><input matInput type="time" formControlName="checkInTime" /></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Check-out time</mat-label><input matInput type="time" formControlName="checkOutTime" /></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Front desk phone</mat-label><input matInput formControlName="phone" inputmode="tel" /></mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Front desk email</mat-label><input matInput formControlName="email" type="email" />
            @if (err()['email']) { <mat-error>{{ err()['email'] }}</mat-error> }
          </mat-form-field>
          @if (!isNew()) {
            <mat-slide-toggle formControlName="isActive" class="full">Property is active</mat-slide-toggle>
          }
        </div>

        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
        @if (editable()) {
          <div class="actions">
            <button mat-flat-button type="submit" [disabled]="busy()">{{ isNew() ? 'Add property' : 'Save changes' }}</button>
            <a mat-button routerLink="/settings/properties">Cancel</a>
          </div>
        }
      </form>
    </div>
  `,
  styles: `
    .back { color: #5b6b6e; text-decoration: none; font-size: 0.9rem; }
    .back::before { content: '‹ '; }
    .box { padding: 24px; max-width: 820px; }
    h2 { margin: 8px 0 12px; font-size: 1rem; color: #0e3b43; }
    h2:not(:first-child) { margin-top: 20px; padding-top: 20px; border-top: 1px solid #edf1f0; }
    .actions { display: flex; gap: 8px; margin-top: 16px; }
    .error { color: #b3261e; }
    mat-slide-toggle { margin: 8px 0; }
  `,
})
export class PropertyEditPage implements OnInit {
  id = input<string>();
  private http = inject(HttpClient);
  private auth = inject(AuthService);
  private ctx = inject(PropertyContext);
  private router = inject(Router);
  private snack = inject(MatSnackBar);

  types = PROPERTY_TYPES;
  states = GST_STATES;
  timezones = TIMEZONES;

  isNew = computed(() => !this.id());
  loaded = signal<Property | null>(null);
  editable = computed(() => this.isNew() ? this.auth.can('property.create', null) : this.auth.can('property.update', this.id()));
  busy = signal(false);
  error = signal('');
  err = signal<Record<string, string>>({});

  form = inject(FormBuilder).group({
    code: ['', [Validators.required, Validators.pattern(/^[A-Za-z0-9_]{2,30}$/)]],
    name: ['', Validators.required],
    legalName: [null as string | null],
    propertyType: ['RESORT'],
    timezone: ['Asia/Kolkata'],
    currency: ['INR', Validators.required],
    addressLine1: [null as string | null], addressLine2: [null as string | null],
    city: [null as string | null], district: [null as string | null],
    gstStateCode: [null as string | null], postalCode: [null as string | null],
    phone: [null as string | null], email: [null as string | null, Validators.email],
    checkInTime: ['14:00'], checkOutTime: ['11:00'],
    isActive: [true],
  });

  ngOnInit() {
    if (this.isNew()) return;
    this.form.controls.code.disable();
    this.http.get<Property>(`/api/v1/properties/${this.id()}`).subscribe(p => {
      this.loaded.set(p);
      this.form.patchValue({ ...p, checkInTime: p.checkInTime.slice(0, 5), checkOutTime: p.checkOutTime.slice(0, 5) });
      if (!this.editable()) this.form.disable();
    });
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true); this.error.set(''); this.err.set({});
    const v = this.form.getRawValue();
    const stateName = this.states.find(s => s.code === v.gstStateCode)?.name ?? null;
    const body = {
      ...v, stateName, countryCode: 'IN', latitude: null, longitude: null,
      checkInTime: `${v.checkInTime}:00`, checkOutTime: `${v.checkOutTime}:00`,
      ...(this.isNew() ? {} : { version: this.loaded()!.version }),
    };
    const req = this.isNew()
      ? this.http.post<Property>('/api/v1/properties', body)
      : this.http.put<Property>(`/api/v1/properties/${this.id()}`, body);
    req.subscribe({
      next: p => {
        this.ctx.upsert(p);
        this.snack.open(this.isNew() ? 'Property added' : 'Changes saved', undefined, { duration: 2500 });
        this.router.navigateByUrl('/settings/properties');
      },
      error: e => {
        const p = toProblem(e);
        this.err.set(fieldErrors(p));
        this.error.set(p.code === 'CONCURRENCY_CONFLICT'
          ? 'Someone else saved this property while you were editing. Reload the page to see their changes, then edit again.'
          : p.detail);
        this.busy.set(false);
      },
    });
  }
}
