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
        <div class="sun"></div>
        <svg viewBox="0 0 400 120" class="tide" preserveAspectRatio="none">
          <path class="w1" d="M0 70 C 60 40, 120 100, 200 70 S 340 40, 400 70 L400 120 L0 120 Z"/>
          <path class="w2" d="M0 88 C 70 64, 140 112, 220 88 S 350 66, 400 86 L400 120 L0 120 Z"/>
        </svg>
        <div class="logo"><span class="glyph"><svg viewBox="0 0 24 24"><path d="M3 9c2 0 2-1.5 4.5-1.5S9.5 9 12 9s2.5-1.5 4.5-1.5S19 9 21 9M3 13c2 0 2-1.5 4.5-1.5S9.5 13 12 13s2.5-1.5 4.5-1.5S19 13 21 13M3 17c2 0 2-1.5 4.5-1.5S9.5 17 12 17s2.5-1.5 4.5-1.5S19 17 21 17"/></svg></span></div>
        <div class="mark">Resort&nbsp;OS</div>
        <p>Front desk, rooms, kitchen and crew — one place for the whole property.</p>
        <ul class="pts"><li>GST invoices and UPI payments</li><li>WhatsApp confirmations to guests</li><li>Works on any phone, light or dark</li></ul>
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
    .wrap { min-height: 100vh; min-height: 100dvh; display: grid; grid-template-columns: 1.1fr 1fr; }
    .brand { position: relative; margin: 14px; border-radius: 28px; background: var(--ro-brand-grad); color: var(--ro-brand-on); padding: 48px;
             display: flex; flex-direction: column; justify-content: center; overflow: hidden; box-shadow: var(--ro-shadow-card-lift); }
    .sun { position: absolute; width: 360px; height: 360px; right: -80px; top: 12%; border-radius: 50%;
           background: radial-gradient(circle, var(--ro-brand-sun) 0%, transparent 65%); opacity: .55; animation: rise 14s ease-in-out infinite alternate; }
    @keyframes rise { from { transform: translateY(30px); } to { transform: translateY(-20px); } }
    .logo .glyph { display: grid; place-items: center; width: 56px; height: 56px; border-radius: 18px; background: var(--ro-brand-glass); margin-bottom: var(--space-5);
                   backdrop-filter: blur(8px); border: 1px solid var(--ro-brand-edge); }
    .logo svg { width: 30px; height: 30px; fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; }
    .mark { position: relative; font-family: var(--ro-font-display); font-size: clamp(2.2rem, 5vw, 3.4rem); font-weight: 600; letter-spacing: -0.03em; color: var(--ro-brand-on); }
    .brand p { position: relative; max-width: 30ch; font-size: 1.1rem; margin: var(--space-3) 0 0; color: var(--ro-brand-mute); }
    .pts { position: relative; list-style: none; padding: 0; margin: var(--space-6) 0 0; display: flex; flex-direction: column; gap: var(--space-2); color: var(--ro-brand-on); }
    .pts li { display: flex; align-items: center; gap: var(--space-2); font-size: .95rem; }
    .pts li::before { content: ''; width: 8px; height: 8px; border-radius: 50%; background: var(--ro-coral-fill); }
    .tide { position: absolute; left: 0; bottom: 0; width: calc(100% + 40px); height: 140px; }
    .tide .w1 { fill: var(--ro-brand-wave1); animation: sway 9s ease-in-out infinite alternate; }
    .tide .w2 { fill: var(--ro-brand-wave2); animation: sway 7s ease-in-out infinite alternate-reverse; }
    @keyframes sway { from { transform: translateX(0); } to { transform: translateX(-18px); } }
    .card { align-self: center; justify-self: center; width: min(400px, calc(100% - 32px)); display: flex; flex-direction: column; padding: var(--space-8);
            box-sizing: border-box; background: var(--ro-glass-strong); backdrop-filter: var(--ro-blur); -webkit-backdrop-filter: var(--ro-blur);
            border: 1px solid var(--ro-glass-edge); border-radius: var(--ro-radius-lg); box-shadow: var(--ro-shadow-card-lift); }
    .hint { color: var(--ro-mute); margin: 6px 0 var(--space-5); }
    .error { color: var(--ro-error); margin: 0 0 var(--space-3); }
    button { height: 48px; }
    mat-spinner { margin: 0 auto; }
    .foot { color: var(--ro-mute); font-size: 0.85rem; margin-top: var(--space-4); }
    @media (max-width: 760px) {
      .wrap { grid-template-columns: 1fr; grid-template-rows: auto 1fr; }
      .brand { padding: 28px var(--space-5) 72px; margin: 10px; border-radius: 22px; }
      .logo .glyph { width: 44px; height: 44px; margin-bottom: var(--space-3); }
      .pts { display: none; }
      .tide { height: 60px; }
      .card { align-self: start; margin-top: -40px; position: relative; padding: var(--space-6) var(--space-5); }
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
