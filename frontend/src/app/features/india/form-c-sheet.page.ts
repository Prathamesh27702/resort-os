import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AuthService } from '../../core/auth.service';
import { pickerError } from '../../core/dates';
import { fieldErrors, showFieldErrors, toProblem } from '../../core/http';
import { PropertyContext, inZone } from '../../core/property-context';
import { FormCField, FormCSheet, IndiaApi, deadlineWords, urgency } from './india.api';

/** What goes on the clipboard for a field: dates the way the FRRO portal takes them (DD/MM/YYYY), everything else as typed. */
export function portalValue(kind: string, value: string | null | undefined): string {
  if (!value) return '';
  const m = kind === 'date' ? /^(\d{4})-(\d{2})-(\d{2})$/.exec(value) : null;
  return m ? `${m[3]}/${m[2]}/${m[1]}` : value;
}

/** The sheet's fields in their sections, in the portal's order. */
export function sections(fields: FormCField[]): { title: string; fields: FormCField[] }[] {
  const out: { title: string; fields: FormCField[] }[] = [];
  for (const f of fields) {
    const last = out[out.length - 1];
    if (last?.title === f.section) last.fields.push(f); else out.push({ title: f.section, fields: [f] });
  }
  return out;
}

// ------------------------------------------------------------------ dialogs

interface CloseData { propertyId: string; sheet: FormCSheet; }

/** "Mark as filed": asks for the acknowledgement number the FRRO portal showed (it can be added only now). */
@Component({
  selector: 'app-form-c-filed-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Mark as filed</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="go()" id="filed-form">
      <p class="mute">Do this after you have submitted {{ data.sheet.guestName }}’s Form C on the FRRO portal.</p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>FRRO acknowledgement number</mat-label>
        <input matInput formControlName="reference" maxlength="40" autocomplete="off" cdkFocusInitial />
        <mat-hint>The number the portal shows after you submit. Leave empty if it gave none.</mat-hint>
        <mat-error>{{ reference.errors?.['server'] }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="filed-form" [disabled]="busy()">Mark as filed</button>
    </div>
  `,
  styles: `form { min-width: min(420px, 82vw); } .full { width: 100%; } .mute { color: var(--ro-mute); margin-top: 0; } .err { color: var(--ro-error); }`,
})
export class FormCFiledDialog {
  data = inject<CloseData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<FormCFiledDialog, FormCSheet>);
  private api = inject(IndiaApi);
  reference = new FormControl('', { nonNullable: true });
  form = new FormGroup({ reference: this.reference });
  busy = signal(false);
  error = signal('');

  go() {
    this.busy.set(true); this.error.set('');
    this.api.submitFormC(this.data.propertyId, this.data.sheet, this.reference.value.trim() || null).subscribe({
      next: s => this.ref.close(s),
      error: e => {
        const p = toProblem(e); const f = fieldErrors(p)['frroReference'];
        if (f) { this.reference.setErrors({ server: f }); this.reference.markAsTouched(); } else this.error.set(p.detail);
        this.busy.set(false);
      },
    });
  }
}

/** "No Form C needed": for a guest entered as a foreign national by mistake. The reason is kept. */
@Component({
  selector: 'app-form-c-not-needed-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>No Form C needed</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="go()" id="not-needed-form">
      <p class="mute">Use this only when {{ data.sheet.guestName }} doesn’t have to be reported, for example an Indian citizen whose nationality was entered
        wrongly. The reason stays on the register.</p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Reason</mat-label>
        <input matInput formControlName="reason" maxlength="300" placeholder="e.g. Indian citizen, nationality was entered wrongly" cdkFocusInitial />
        <mat-error>{{ reason.errors?.['server'] ?? 'Say why no Form C is needed (at least 3 characters).' }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="not-needed-form" [disabled]="busy()">No Form C needed</button>
    </div>
  `,
  styles: `form { min-width: min(440px, 82vw); } .full { width: 100%; } .mute { color: var(--ro-mute); margin-top: 0; } .err { color: var(--ro-error); }`,
})
export class FormCNotNeededDialog {
  data = inject<CloseData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<FormCNotNeededDialog, FormCSheet>);
  private api = inject(IndiaApi);
  reason = new FormControl('', { nonNullable: true });
  form = new FormGroup({ reason: this.reason });
  busy = signal(false);
  error = signal('');

  go() {
    if (this.reason.value.trim().length < 3) { this.reason.setErrors({ short: true }); this.reason.markAsTouched(); return; }
    this.busy.set(true); this.error.set('');
    this.api.formCNotRequired(this.data.propertyId, this.data.sheet, this.reason.value.trim()).subscribe({
      next: s => this.ref.close(s),
      error: e => {
        const p = toProblem(e); const f = fieldErrors(p)['reason'];
        if (f) { this.reason.setErrors({ server: f }); this.reason.markAsTouched(); } else this.error.set(p.detail);
        this.busy.set(false);
      },
    });
  }
}

// ------------------------------------------------------------------ the sheet

/**
 * One guest's Form C sheet (Sprint 30). The app fills in what it already knows from the guest's profile, passport, visa
 * and booking; reception types the rest, copies each value into the FRRO portal (which has no way in for software), and
 * marks the form as filed with the number the portal gave. Every error sits under the field it is about.
 */
@Component({
  selector: 'app-form-c-sheet',
  imports: [ReactiveFormsModule, RouterLink, MatButtonModule, MatDatepickerModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule,
    MatSelectModule, MatSnackBarModule, MatTooltipModule],
  template: `
    <div class="page">
      <a class="back" routerLink="/form-c">← Form C</a>
      @if (loadError()) { <p class="err" role="alert">{{ loadError() }}</p> }
      @if (sheet(); as s) {
        <div class="page-head">
          <div>
            <h1>Form C: {{ s.guestName }}</h1>
            <p><a [routerLink]="['/guests', s.guestId]">Guest profile</a> · booking
              <a [routerLink]="['/reservations', s.reservationId]" class="num">{{ s.reference }}</a>@if (s.room) { , {{ s.room }} }
              · arrived {{ when(s.arrivedAt) }}</p>
          </div>
          <a mat-stroked-button [href]="s.portalUrl" target="_blank" rel="noopener"><mat-icon>open_in_new</mat-icon>Open the FRRO portal</a>
        </div>

        @switch (s.status) {
          @case ('PENDING') {
            <p [class]="'banner ' + urgency(s)" role="status" data-testid="form-c-deadline">
              <mat-icon>{{ s.overdue ? 'warning' : 'schedule' }}</mat-icon>
              <span><b>{{ deadline(s.hoursLeft) }}.</b> File by {{ when(s.dueAt) }}@if (s.overdue) { : the 24 hours have passed, so file it now }.</span>
            </p>
          }
          @case ('SUBMITTED') {
            <p class="banner done" role="status" data-testid="form-c-filed">
              <mat-icon>task_alt</mat-icon>
              <span><b>Filed</b> {{ when(s.submittedAt!) }}@if (s.submittedByName) { by {{ s.submittedByName }} }@if (s.frroReference) { . FRRO number <b class="num">{{ s.frroReference }}</b> }</span>
            </p>
          }
          @default {
            <p class="banner off" role="status" data-testid="form-c-not-needed"><mat-icon>block</mat-icon><span><b>No Form C needed.</b> {{ s.notRequiredReason }}</span></p>
          }
        }

        @if (s.status === 'PENDING') {
          <ol class="steps">
            <li><b>Check the details.</b> What we already know from the guest’s profile, passport and visa is filled in. Type the rest and save.</li>
            <li><b>Copy to the FRRO portal.</b> The copy button next to each field puts its value on the clipboard, with dates the way the portal wants them.</li>
            <li><b>Mark as filed</b> with the number the portal gives you.</li>
          </ol>
        }

        <form [formGroup]="form" (ngSubmit)="save()">
          <div formGroupName="values">
            @for (sec of secs(); track sec.title) {
              <section class="panel sec">
                <h2>{{ sec.title }}</h2>
                <div class="grid">
                  @for (f of sec.fields; track f.key) {
                    <div class="field" [class.wide]="f.maxLength > 120">
                      @if (f.kind === 'date') {
                        <mat-form-field appearance="outline" subscriptSizing="dynamic">
                          <mat-label>{{ f.label }}{{ f.required ? '' : ' (optional)' }}</mat-label>
                          <input matInput [matDatepicker]="dp" [formControlName]="f.key" />
                          <mat-datepicker-toggle matIconSuffix [for]="dp" />
                          <mat-datepicker #dp />
                          @if (hint(f); as h) { <mat-hint>{{ h }}</mat-hint> }
                          <mat-error>{{ errorOf(f) }}</mat-error>
                        </mat-form-field>
                      } @else {
                        <mat-form-field appearance="outline" subscriptSizing="dynamic">
                          <mat-label>{{ f.label }}{{ f.required ? '' : ' (optional)' }}</mat-label>
                          @switch (f.kind) {
                            @case ('time') { <input matInput type="time" [formControlName]="f.key" /> }
                            @case ('number') { <input matInput type="text" inputmode="numeric" [formControlName]="f.key" [attr.maxlength]="f.maxLength" /> }
                            @case ('sex') {
                              <mat-select [formControlName]="f.key">
                                <mat-option value="">—</mat-option><mat-option value="Male">Male</mat-option><mat-option value="Female">Female</mat-option><mat-option value="Other">Other</mat-option>
                              </mat-select>
                            }
                            @case ('yesno') {
                              <mat-select [formControlName]="f.key"><mat-option value="">—</mat-option><mat-option value="Yes">Yes</mat-option><mat-option value="No">No</mat-option></mat-select>
                            }
                            @default { <input matInput [formControlName]="f.key" [attr.maxlength]="f.maxLength" autocomplete="off" /> }
                          }
                          @if (hint(f); as h) { <mat-hint>{{ h }}</mat-hint> }
                          <mat-error>{{ errorOf(f) }}</mat-error>
                        </mat-form-field>
                      }
                      <button mat-icon-button type="button" (click)="copy(f)" [disabled]="!values.get(f.key)?.value"
                              [attr.aria-label]="'Copy ' + f.label" matTooltip="Copy for the FRRO portal"><mat-icon>content_copy</mat-icon></button>
                    </div>
                  }
                </div>
              </section>
            }
          </div>

          @if (error()) { <p class="err" role="alert" data-testid="form-c-error">{{ error() }}</p> }

          <div class="actions">
            @if (s.status === 'PENDING' && canChange()) {
              <button mat-flat-button type="button" (click)="filed()" [disabled]="busy()" data-testid="form-c-file"><mat-icon>task_alt</mat-icon>Mark as filed</button>
              <button mat-stroked-button type="submit" [disabled]="busy() || !form.dirty">Save</button>
              <span class="grow"></span>
              <button mat-button type="button" (click)="notNeeded()" [disabled]="busy()">No Form C needed</button>
            } @else if (canChange()) {
              <button mat-stroked-button type="button" (click)="reopen()" [disabled]="busy()">Reopen to correct</button>
            }
          </div>
        </form>
      } @else if (!loadError()) { <p class="mute">Loading…</p> }
    </div>
  `,
  styles: `
    .back { color: var(--ro-mute); text-decoration: none; font-size: .85rem; }
    .banner { display: flex; align-items: center; gap: var(--space-2); padding: var(--space-3) var(--space-4); border-radius: var(--ro-radius); margin: 0 0 var(--space-3); }
    .banner.late { background: var(--ro-error-bg); color: var(--ro-error-strong); border: 1px solid var(--ro-error-line); }
    .banner.soon { background: var(--ro-warn-bg); color: var(--ro-warn-strong); border: 1px solid var(--ro-warn-line); }
    .banner.fine { background: var(--ro-info-bg); color: var(--ro-info-strong); border: 1px solid var(--ro-info-line); }
    .banner.done { background: var(--ro-ok-bg); color: var(--ro-ok); border: 1px solid var(--ro-ok-line); }
    .banner.off { background: var(--ro-off-bg); color: var(--ro-off); }
    .steps { margin: 0 0 var(--space-4); padding-left: 1.3em; color: var(--ro-ink-soft); display: grid; gap: 4px; }
    .sec { padding: var(--space-4); margin-bottom: var(--space-3); }
    .sec h2 { margin: 0 0 var(--space-3); font-size: 1.05rem; }
    .grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-3) var(--space-4); }
    .field { display: flex; align-items: flex-start; gap: 2px; min-width: 0; }
    .field.wide { grid-column: 1 / -1; }
    .field mat-form-field { flex: 1; min-width: 0; }
    .field > button { margin-top: 4px; }
    .actions { display: flex; gap: var(--space-2); align-items: center; flex-wrap: wrap; margin-top: var(--space-3); }
    .grow { flex: 1; }
    .mute { color: var(--ro-mute); }
    .err { color: var(--ro-error); }
    @media (max-width: 720px) { .grid { grid-template-columns: 1fr; } }
  `,
})
export class FormCSheetPage {
  id = input.required<string>();
  private api = inject(IndiaApi);
  private props = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);

  sheet = signal<FormCSheet | null>(null);
  loadError = signal('');
  error = signal('');
  busy = signal(false);
  values = new FormGroup<Record<string, FormControl<string>>>({});
  form = new FormGroup({ values: this.values });
  secs = computed(() => sections(this.sheet()?.fields ?? []));
  canChange = computed(() => { const p = this.props.current(); return !!p && this.auth.can('frontdesk.checkin', p.id); });
  private tz = computed(() => this.props.current()?.timezone ?? 'Asia/Kolkata');
  urgency = urgency;
  deadline = deadlineWords;

  constructor() {
    effect(() => {
      const p = this.props.current(); const id = this.id();
      if (!p) return;
      this.api.formC(p.id, id).subscribe({ next: s => this.show(s), error: e => this.loadError.set(toProblem(e).detail) });
    });
  }

  /** Puts a sheet on the page: one control per field, locked unless the form is still to file and the user may change it. */
  private show(s: FormCSheet) {
    for (const f of s.fields) {
      const c = this.values.get(f.key);
      if (c) c.reset(f.value ?? ''); else this.values.addControl(f.key, new FormControl(f.value ?? '', { nonNullable: true }));
    }
    if (s.status === 'PENDING' && this.canChange()) this.values.enable(); else this.values.disable();
    this.form.markAsPristine();
    this.sheet.set(s);
  }

  when(iso: string) { return inZone(iso, this.tz(), { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false }); }
  hint(f: FormCField) { return f.fromProfile && f.value && !this.values.get(f.key)?.dirty ? 'Filled in for you. Check it against the passport.' : f.hint; }
  errorOf(f: FormCField) {
    const e = this.values.get(f.key)?.errors;
    return e?.['server'] ?? pickerError(e) ?? '';
  }

  async copy(f: FormCField) {
    const text = portalValue(f.kind, this.values.get(f.key)?.value);
    try {
      await navigator.clipboard.writeText(text);
      this.snack.open(`${f.label} copied: ${text}`, undefined, { duration: 2500 });
    } catch {
      this.snack.open(`This browser didn’t allow copying. Select ${f.label} and copy it by hand.`, 'OK', { duration: 6000 });
    }
  }

  /** Saves what was typed. Only the fields that were changed are sent, so profile values stay linked to the profile. */
  async save(): Promise<boolean> {
    const s = this.sheet(); const p = this.props.current();
    if (!s || !p) return false;
    // Drop the reasons of the last try (they sit on the controls until checked again); a date typed wrongly stays an error.
    for (const c of Object.values(this.values.controls)) c.updateValueAndValidity();
    if (this.values.invalid) { this.values.markAllAsTouched(); this.error.set('A date isn’t written as a date. See the field marked in red.'); return false; }
    const changed: Record<string, string | null> = {};
    for (const [k, c] of Object.entries(this.values.controls)) if (c.dirty) changed[k] = (c.value ?? '').toString().trim() || null;
    if (Object.keys(changed).length === 0) return true;
    this.busy.set(true); this.error.set('');
    try {
      this.show(await firstValueFrom(this.api.saveFormC(p.id, s, changed)));
      this.snack.open('Form C saved.', undefined, { duration: 2500 });
      return true;
    } catch (e) {
      const problem = toProblem(e);
      const shown = showFieldErrors(this.form, problem);
      this.error.set(Object.keys(shown).length ? 'Nothing was saved. See the reason under each field marked in red.' : problem.detail);
      return false;
    } finally { this.busy.set(false); }
  }

  async filed() {
    if (!(await this.save())) return;
    const s = this.sheet()!;
    if (s.missing.length) {
      for (const f of s.fields) {
        const c = this.values.get(f.key)!;
        if (f.required && !c.value) { c.setErrors({ server: 'The FRRO portal needs this. Fill it in before filing.' }); c.markAsTouched(); }
      }
      this.error.set(`${s.missing.length} required field${s.missing.length === 1 ? ' is' : 's are'} still empty: ${s.missing.join('; ')}.`);
      return;
    }
    this.close(FormCFiledDialog, 'Form C marked as filed.');
  }

  notNeeded() { this.close(FormCNotNeededDialog, 'Marked: no Form C needed.'); }

  private close(dialog: typeof FormCFiledDialog | typeof FormCNotNeededDialog, done: string) {
    const data: CloseData = { propertyId: this.props.current().id, sheet: this.sheet()! };
    this.dialog.open<unknown, CloseData, FormCSheet>(dialog, { data }).afterClosed().subscribe(s => {
      if (s) { this.error.set(''); this.show(s); this.snack.open(done, undefined, { duration: 3000 }); }
    });
  }

  reopen() {
    const s = this.sheet()!;
    this.busy.set(true); this.error.set('');
    this.api.reopenFormC(this.props.current().id, s).subscribe({
      next: r => { this.show(r); this.busy.set(false); this.snack.open('Form C reopened. Correct it and mark it as filed again.', undefined, { duration: 4000 }); },
      error: e => { this.error.set(toProblem(e).detail); this.busy.set(false); },
    });
  }
}
