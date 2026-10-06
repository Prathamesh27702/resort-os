import { Component, computed, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { AuthService } from '../../core/auth.service';
import { fieldErrors, toProblem } from '../../core/http';

const strong = (c: AbstractControl): ValidationErrors | null =>
  /[A-Za-z]/.test(c.value ?? '') && /\d/.test(c.value ?? '') ? null : { weak: true };

@Component({
  selector: 'app-password',
  imports: [ReactiveFormsModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>{{ forced() ? 'Choose your own password' : 'Change password' }}</h1>
          <p>{{ forced() ? 'You signed in with a temporary password. Set your own to continue.' : 'You’ll be signed out everywhere and asked to sign in again.' }}</p>
        </div>
      </div>
      <form class="panel box" [formGroup]="form" (ngSubmit)="save()">
        <mat-form-field appearance="outline">
          <mat-label>Current password</mat-label>
          <input matInput type="password" formControlName="currentPassword" autocomplete="current-password" />
          @if (serverErr()['currentPassword']) { <mat-error>{{ serverErr()['currentPassword'] }}</mat-error> }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>New password</mat-label>
          <input matInput type="password" formControlName="newPassword" autocomplete="new-password" />
          <mat-hint>At least 10 characters, with letters and numbers.</mat-hint>
        </mat-form-field>
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
        <button mat-flat-button type="submit" [disabled]="busy()">Change password</button>
      </form>
    </div>
  `,
  styles: `.box { padding: 24px; max-width: 420px; display: flex; flex-direction: column; gap: 8px; } .error { color: #b3261e; margin: 0; }`,
})
export class PasswordPage {
  private auth = inject(AuthService);
  forced = computed(() => !!this.auth.me()?.mustChangePassword);
  busy = signal(false);
  error = signal('');
  serverErr = signal<Record<string, string>>({});
  form = inject(FormBuilder).nonNullable.group({
    currentPassword: ['', Validators.required],
    newPassword: ['', [Validators.required, Validators.minLength(10), strong]],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); this.error.set('Use at least 10 characters, with letters and numbers.'); return; }
    this.busy.set(true); this.error.set('');
    const v = this.form.getRawValue();
    this.auth.changePassword(v.currentPassword, v.newPassword).subscribe({
      // Password change revokes every session (including this one), so sign in again.
      next: () => { this.auth.clear(); location.assign('/login'); },
      error: e => { const p = toProblem(e); this.serverErr.set(fieldErrors(p)); this.error.set(p.detail); this.busy.set(false); },
    });
  }
}
