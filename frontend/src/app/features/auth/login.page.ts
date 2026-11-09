import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { PropertyContext } from '../../core/property-context';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressSpinnerModule],
  template: `
    <main class="wrap">
      <section class="brand" aria-hidden="true">
        <svg viewBox="0 0 400 120" class="tide" preserveAspectRatio="none">
          <path d="M0 70 C 60 40, 120 100, 200 70 S 340 40, 400 70 L400 120 L0 120 Z" style="fill: var(--ro-tide-soft)"/>
          <path d="M0 88 C 70 64, 140 112, 220 88 S 350 66, 400 86 L400 120 L0 120 Z" style="fill: var(--ro-tide-mid)"/>
        </svg>
        <div class="mark">Resort&nbsp;OS</div>
        <p>Front desk, rooms, kitchen and crew — one place for the whole property.</p>
      </section>

      <form class="card" [formGroup]="form" (ngSubmit)="submit()">
        <h1>Sign in</h1>
        <p class="hint">Use the email your manager set up for you.</p>

        <mat-form-field appearance="outline">
          <mat-label>Email</mat-label>
          <input matInput type="email" formControlName="email" autocomplete="username" />
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Password</mat-label>
          <input matInput type="password" formControlName="password" autocomplete="current-password" />
        </mat-form-field>

        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }

        <button mat-flat-button type="submit" [disabled]="busy()">
          @if (busy()) { <mat-spinner diameter="18" /> } @else { Sign in }
        </button>
        <p class="foot">Forgot your password? Ask a manager to reset it.</p>
      </form>
    </main>
  `,
  styles: `
    .wrap { min-height: 100vh; display: grid; grid-template-columns: 1.1fr 1fr; }
    .brand { position: relative; background: var(--ro-tide); color: var(--ro-ok-bg); padding: 48px; display: flex; flex-direction: column; justify-content: center; overflow: hidden; }
    .mark { font-size: clamp(2.4rem, 5vw, 3.6rem); font-weight: 600; letter-spacing: -0.03em; color: var(--ro-paper); }
    .brand p { max-width: 30ch; font-size: 1.1rem; margin: var(--space-3) 0 0; color: var(--ro-line-strong); }
    .tide { position: absolute; left: 0; right: 0; bottom: 0; width: 100%; height: 140px; }
    .card { align-self: center; justify-self: center; width: min(380px, calc(100% - 32px)); display: flex; flex-direction: column; padding: var(--space-8) 0; }
    .hint { color: var(--ro-mute); margin: 6px 0 var(--space-5); }
    .error { color: var(--ro-error); margin: 0 0 var(--space-3); }
    button { height: 44px; }
    mat-spinner { margin: 0 auto; }
    .foot { color: var(--ro-mute); font-size: 0.85rem; margin-top: var(--space-4); }
    @media (max-width: 760px) {
      .wrap { grid-template-columns: 1fr; grid-template-rows: auto 1fr; }
      .brand { padding: 28px var(--space-5) 64px; }
      .tide { height: 60px; }
      .card { align-self: start; }
    }
  `,
})
export class LoginPage {
  private fb = inject(FormBuilder);
  private auth = inject(AuthService);
  private props = inject(PropertyContext);
  private router = inject(Router);
  private route = inject(ActivatedRoute);

  busy = signal(false);
  error = signal('');
  form = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });

  submit() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.error.set('');
    const { email, password } = this.form.getRawValue();
    this.auth.login(email, password).subscribe({
      next: async r => {
        if (r.me.mustChangePassword) { await this.router.navigateByUrl('/account/password'); return; }
        await this.props.load();
        await this.router.navigateByUrl(this.route.snapshot.queryParamMap.get('next') ?? '/');
      },
      error: e => { this.error.set(toProblem(e).detail); this.busy.set(false); },
    });
  }
}
