import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormBuilder, FormsModule, ReactiveFormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { fieldErrors, toProblem } from '../../core/http';
import { GST_STATES, Guest, GuestPreference, PREFERENCE_CATEGORIES, PossibleDuplicate } from '../../core/models';
import { label } from '../rooms/rooms.api';
import { COUNTRIES, GuestsApi, formatPhone } from './guests.api';
import { fromGuest, guestForm, toGuestRequest } from './guests.forms';

/** Add a guest (/guests/new) or edit one (/guests/:id/edit). Warns before creating a likely duplicate. */
@Component({
  selector: 'app-guest-edit',
  imports: [ReactiveFormsModule, FormsModule, RouterLink, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule,
    MatInputModule, MatSelectModule, MatSnackBarModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <a [routerLink]="isNew() ? '/guests' : '/guests/' + id()" class="back">{{ isNew() ? 'Guests' : (loaded()?.fullName ?? 'Guest') }}</a>
          <h1>{{ isNew() ? 'Add guest' : 'Edit guest' }}</h1>
          <p>Only what the stay needs. ID documents are added on the guest's page.</p>
        </div>
      </div>

      @if (isNew() || loaded()) {
      <form class="panel box" [formGroup]="form" (ngSubmit)="save()">
        <h2>Who</h2>
        <div class="form-grid">
          <mat-form-field appearance="outline">
            <mat-label>First name</mat-label>
            <input matInput formControlName="firstName" autocomplete="off" />
            @if (err()['firstName']) { <mat-error>{{ err()['firstName'] }}</mat-error> } @else { <mat-error>Required</mat-error> }
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Last name</mat-label>
            <input matInput formControlName="lastName" autocomplete="off" />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Phone</mat-label>
            <input matInput formControlName="phone" type="tel" placeholder="98200 12345 or +44 20 7946 0958" (blur)="checkDuplicates()" />
            <mat-hint>Indian numbers as usual; others with + and country code</mat-hint>
            @if (err()['phone']) { <mat-error>{{ err()['phone'] }}</mat-error> } @else { <mat-error>Not a phone number</mat-error> }
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Email</mat-label>
            <input matInput formControlName="email" type="email" (blur)="checkDuplicates()" />
            @if (err()['email']) { <mat-error>{{ err()['email'] }}</mat-error> } @else { <mat-error>Not an email address</mat-error> }
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Nationality</mat-label>
            <mat-select formControlName="nationality">
              @for (c of countries; track c.code) { <mat-option [value]="c.code">{{ c.name }}</mat-option> }
            </mat-select>
            <mat-hint>Foreign nationals need Form-C at check-in</mat-hint>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Date of birth</mat-label>
            <input matInput type="date" formControlName="dateOfBirth" [max]="today" />
            <mat-hint>Only if your local rules ask for it</mat-hint>
            @if (err()['dateOfBirth']) { <mat-error>{{ err()['dateOfBirth'] }}</mat-error> }
          </mat-form-field>
        </div>
        <mat-checkbox formControlName="isVip">VIP guest</mat-checkbox>

        @if (duplicates().length) {
          <div class="dupes" role="alert">
            <b>This guest may already have a profile</b>
            <ul>
              @for (d of duplicates(); track d.id) {
                <li>
                  <a [routerLink]="'/guests/' + d.id">{{ d.fullName }}</a>
                  <span class="mute"> · {{ phone(d.phone) }} {{ d.email }}</span>
                  <span class="why">{{ d.reasons.join(', ') }}</span>
                </li>
              }
            </ul>
            <span class="mute">Open the existing profile to avoid two records for one person.</span>
          </div>
        }

        <h2>Address</h2>
        <div class="form-grid">
          <mat-form-field appearance="outline" class="full">
            <mat-label>Address line 1</mat-label>
            <input matInput formControlName="line1" />
          </mat-form-field>
          <mat-form-field appearance="outline" class="full">
            <mat-label>Address line 2</mat-label>
            <input matInput formControlName="line2" />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>City</mat-label>
            <input matInput formControlName="city" />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Country</mat-label>
            <mat-select formControlName="countryCode">
              @for (c of countries; track c.code) { <mat-option [value]="c.code">{{ c.name }}</mat-option> }
            </mat-select>
          </mat-form-field>
          @if (country() === 'IN') {
            <mat-form-field appearance="outline">
              <mat-label>State</mat-label>
              <mat-select formControlName="stateCode" (selectionChange)="stateChosen($event.value)">
                <mat-option value="">—</mat-option>
                @for (s of gstStates; track s.code) { <mat-option [value]="s.code">{{ s.name }}</mat-option> }
              </mat-select>
              <mat-hint>Used for GST on company invoices</mat-hint>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>PIN code</mat-label>
              <input matInput formControlName="postalCode" inputmode="numeric" maxlength="6" />
              @if (err()['address.PostalCode']) { <mat-error>{{ err()['address.PostalCode'] }}</mat-error> }
            </mat-form-field>
          } @else {
            <mat-form-field appearance="outline">
              <mat-label>State / region</mat-label>
              <input matInput formControlName="state" />
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Postal code</mat-label>
              <input matInput formControlName="postalCode" />
            </mat-form-field>
          }
        </div>

        <h2>Company (for a GST invoice)</h2>
        <div class="form-grid">
          <mat-form-field appearance="outline">
            <mat-label>Company name</mat-label>
            <input matInput formControlName="companyName" />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>GSTIN</mat-label>
            <input matInput formControlName="gstin" maxlength="15" (input)="upperGstin()" />
            @if (err()['gstin']) { <mat-error>{{ err()['gstin'] }}</mat-error> } @else { <mat-error>15 characters, starting with the 2-digit state code</mat-error> }
          </mat-form-field>
        </div>

        <h2>Preferences and notes</h2>
        <div class="prefs">
          @for (p of prefs(); track $index) {
            <span class="chip pref">{{ label(p.category) }}: {{ p.value }}
              <button type="button" mat-icon-button (click)="removePref($index)" [attr.aria-label]="'Remove ' + p.value"><mat-icon>close</mat-icon></button>
            </span>
          } @empty { <span class="mute">No preferences yet.</span> }
        </div>
        <div class="add-pref">
          <mat-form-field appearance="outline" class="cat">
            <mat-label>Type</mat-label>
            <mat-select [(ngModel)]="newPrefCat" [ngModelOptions]="{ standalone: true }">
              @for (c of categories; track c) { <mat-option [value]="c">{{ label(c) }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field appearance="outline" class="val">
            <mat-label>Preference</mat-label>
            <input matInput [(ngModel)]="newPrefValue" [ngModelOptions]="{ standalone: true }" maxlength="500"
                   placeholder="e.g. Jain food, ground floor, extra pillows" (keydown.enter)="$event.preventDefault(); addPref()" />
          </mat-form-field>
          <button mat-stroked-button type="button" (click)="addPref()" [disabled]="!newPrefValue.trim()">Add</button>
        </div>
        <mat-form-field appearance="outline" class="notes">
          <mat-label>Notes for the team</mat-label>
          <textarea matInput formControlName="notes" rows="3" maxlength="2000"></textarea>
          <mat-hint>Visible to everyone who can see guests. Don't write health or ID details here.</mat-hint>
        </mat-form-field>

        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
        <div class="actions">
          <button mat-flat-button type="submit" [disabled]="busy()">
            {{ isNew() ? (duplicates().length ? 'Create a new profile anyway' : 'Add guest') : 'Save changes' }}
          </button>
          <a mat-button [routerLink]="isNew() ? '/guests' : '/guests/' + id()">Cancel</a>
        </div>
      </form>
      } @else {
        <p class="mute">Loading…</p>
      }
    </div>
  `,
  styles: `
    .back { color: #5b6b6e; text-decoration: none; font-size: 0.9rem; } .back::before { content: '‹ '; }
    .box { padding: 24px; max-width: 860px; }
    h2 { margin: 8px 0 12px; font-size: 1rem; color: #0e3b43; }
    h2:not(:first-child) { margin-top: 20px; padding-top: 20px; border-top: 1px solid #edf1f0; }
    .mute { color: #5b6b6e; }
    .dupes { background: #fbefd8; border-left: 4px solid #8a5a00; border-radius: 6px; padding: 12px 16px; margin: 16px 0 4px; }
    .dupes ul { margin: 8px 0; padding-left: 18px; }
    .why { margin-left: 8px; font-size: .8rem; background: #fff; border-radius: 999px; padding: 1px 8px; color: #8a5a00; }
    .prefs { display: flex; flex-wrap: wrap; gap: 4px; margin-bottom: 12px; }
    .chip.pref { display: inline-flex; align-items: center; padding-right: 0; }
    .chip.pref button { width: 28px; height: 28px; padding: 2px; }
    .chip.pref mat-icon { font-size: 16px; width: 16px; height: 16px; }
    .add-pref { display: flex; gap: 8px; align-items: flex-start; flex-wrap: wrap; }
    .add-pref .cat { width: 160px; } .add-pref .val { flex: 1; min-width: 220px; }
    .add-pref button { margin-top: 8px; }
    .notes { width: 100%; margin-top: 8px; }
    .actions { display: flex; gap: 8px; margin-top: 16px; flex-wrap: wrap; }
    .error { color: #b3261e; }
  `,
})
export class GuestEditPage implements OnInit {
  id = input<string>();
  private api = inject(GuestsApi);
  private router = inject(Router);
  private snack = inject(MatSnackBar);

  label = label;
  phone = formatPhone;
  countries = COUNTRIES;
  gstStates = GST_STATES;
  categories = PREFERENCE_CATEGORIES;
  today = new Date().toISOString().slice(0, 10);

  isNew = computed(() => !this.id());
  form = guestForm(inject(FormBuilder));
  country = toSignal(this.form.controls.countryCode.valueChanges, { initialValue: 'IN' });
  loaded = signal<Guest | null>(null);
  prefs = signal<GuestPreference[]>([]);
  duplicates = signal<PossibleDuplicate[]>([]);
  busy = signal(false);
  error = signal('');
  err = signal<Record<string, string>>({});
  newPrefCat = 'ROOM';
  newPrefValue = '';

  async ngOnInit() {
    if (this.isNew()) return;
    const g = await firstValueFrom(this.api.get(this.id()!));
    if (g.status !== 'ACTIVE') { this.router.navigateByUrl(`/guests/${g.id}`); return; }
    this.loaded.set(g);
    this.prefs.set(g.preferences);
    this.form.patchValue(fromGuest(g));
  }

  /** Looks for existing profiles with the same phone, email or full name while the user is still typing. */
  checkDuplicates() {
    const v = this.form.getRawValue();
    if (!v.phone.trim() && !v.email.trim() && !(v.firstName.trim() && v.lastName.trim())) { this.duplicates.set([]); return; }
    this.api.possibleDuplicates({
      firstName: v.firstName, lastName: v.lastName, phone: this.form.controls.phone.valid ? v.phone : '',
      email: this.form.controls.email.valid ? v.email : '', excludeId: this.id(),
    }).subscribe({ next: d => this.duplicates.set(d), error: () => this.duplicates.set([]) });
  }

  stateChosen(code: string) {
    this.form.controls.state.setValue(GST_STATES.find(s => s.code === code)?.name ?? '');
  }

  upperGstin() {
    const c = this.form.controls.gstin;
    if (c.value !== c.value.toUpperCase()) c.setValue(c.value.toUpperCase());
  }

  addPref() {
    const value = this.newPrefValue.trim();
    if (!value) return;
    if (!this.prefs().some(p => p.category === this.newPrefCat && p.value === value))
      this.prefs.set([...this.prefs(), { category: this.newPrefCat, value }]);
    this.newPrefValue = '';
  }
  removePref(i: number) { this.prefs.set(this.prefs().filter((_, j) => j !== i)); }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true); this.error.set(''); this.err.set({});
    const body = toGuestRequest(this.form.getRawValue(), this.prefs());
    const req = this.isNew()
      // the duplicate warning was shown; saving again means "yes, it's a different person"
      ? this.api.create({ ...body, ignoreDuplicates: this.duplicates().length > 0 })
      : this.api.update(this.id()!, { ...body, version: this.loaded()!.version });
    req.subscribe({
      next: g => {
        this.busy.set(false);
        this.snack.open(this.isNew() ? 'Guest added' : 'Changes saved', undefined, { duration: 2500 });
        this.router.navigateByUrl(`/guests/${g.id}`);
      },
      error: e => {
        const p = toProblem(e);
        this.busy.set(false);
        if (p.code === 'POSSIBLE_DUPLICATE') {
          this.duplicates.set((e.error?.details ?? []) as PossibleDuplicate[]);
          this.error.set('');
          return;
        }
        this.err.set(fieldErrors(p));
        this.error.set(p.code === 'CONCURRENCY_CONFLICT'
          ? 'Someone else changed this guest while you were editing. Reload the page to see their changes.'
          : p.detail);
      },
    });
  }
}
