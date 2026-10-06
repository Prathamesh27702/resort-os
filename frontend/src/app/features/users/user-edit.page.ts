import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { fieldErrors, toProblem } from '../../core/http';
import { Role, RoleGrant, User } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { ROLE_ORDER, roleName } from './roles';

@Component({
  selector: 'app-user-edit',
  imports: [ReactiveFormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <a routerLink="/users" class="back">Staff accounts</a>
          <h1>{{ isNew() ? 'Add staff' : (user()?.fullName ?? '') }}</h1>
          @if (user(); as u) { <p>{{ u.email }}@if (!u.isActive) { · deactivated }</p> }
        </div>
      </div>

      <form class="panel box" [formGroup]="form" (ngSubmit)="saveDetails()">
        <h2>Details</h2>
        <div class="form-grid">
          <mat-form-field appearance="outline">
            <mat-label>Full name</mat-label><input matInput formControlName="fullName" />
            @if (err()['fullName']) { <mat-error>{{ err()['fullName'] }}</mat-error> }
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Mobile</mat-label><input matInput formControlName="phone" inputmode="tel" />
          </mat-form-field>
          @if (isNew()) {
            <mat-form-field appearance="outline">
              <mat-label>Email (used to sign in)</mat-label><input matInput formControlName="email" type="email" />
              @if (err()['email']) { <mat-error>{{ err()['email'] }}</mat-error> }
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Temporary password</mat-label><input matInput formControlName="temporaryPassword" />
              <mat-hint>Share it privately. They must change it at first sign-in.</mat-hint>
            </mat-form-field>
          }
        </div>
        @if (!isNew() && canUpdate()) {
          <div class="actions"><button mat-stroked-button type="submit" [disabled]="busy()">Save details</button></div>
        }
      </form>

      <section class="panel box">
        <h2>Roles</h2>
        <p class="sub">Give a role for all properties, or limit it to one. You can only give roles whose permissions you have yourself.</p>
        @for (g of grants(); track $index) {
          <div class="grant">
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>Role</mat-label>
              <mat-select [value]="g.roleCode" (valueChange)="setGrant($index, { roleCode: $event })" [disabled]="!canEditRoles()">
                @for (r of roles(); track r.code) { <mat-option [value]="r.code">{{ r.name }}</mat-option> }
              </mat-select>
            </mat-form-field>
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>Where</mat-label>
              <mat-select [value]="g.propertyId" (valueChange)="setGrant($index, { propertyId: $event })" [disabled]="!canEditRoles()">
                <mat-option [value]="null">All properties</mat-option>
                @for (p of ctx.properties(); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }
              </mat-select>
            </mat-form-field>
            @if (canEditRoles()) {
              <button mat-icon-button type="button" (click)="removeGrant($index)" aria-label="Remove role"><mat-icon>close</mat-icon></button>
            }
          </div>
          @if (roleDesc(g.roleCode); as d) { <p class="desc">{{ d }}</p> }
        }
        @if (canEditRoles()) {
          <button mat-button type="button" (click)="addGrant()"><mat-icon>add</mat-icon>Add a role</button>
        }
        @if (isSelf()) { <p class="sub">You can’t change your own roles. Ask another owner.</p> }
        @if (!isNew() && canEditRoles()) {
          <div class="actions"><button mat-stroked-button (click)="saveRoles()" [disabled]="busy()">Save roles</button></div>
        }
      </section>

      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }

      @if (isNew()) {
        <div class="actions"><button mat-flat-button (click)="create()" [disabled]="busy()">Create account</button><a mat-button routerLink="/users">Cancel</a></div>
      } @else if (!isSelf()) {
        <section class="panel box danger">
          <h2>Access</h2>
          <mat-form-field appearance="outline" class="reason-field" subscriptSizing="dynamic">
            <mat-label>Reason (saved in the audit trail)</mat-label>
            <input matInput [value]="reason()" (input)="reason.set($any($event.target).value)" maxlength="500" />
          </mat-form-field>
          @if (user()?.isActive) {
            @if (auth.can('user.update', null)) {
              <div class="row">
                <div><b>Reset password</b><p class="sub">Sets a temporary password and signs them out everywhere.</p></div>
                <span class="grow"></span>
                <mat-form-field appearance="outline" subscriptSizing="dynamic"><mat-label>New temporary password</mat-label><input matInput #tmp /></mat-form-field>
                <button mat-stroked-button (click)="resetPassword(tmp.value)" [disabled]="busy()">Reset</button>
              </div>
            }
            @if (auth.can('user.deactivate', null)) {
              <div class="row">
                <div><b>Deactivate account</b><p class="sub">Signs them out immediately. Their history stays in the audit trail.</p></div>
                <span class="grow"></span>
                <button mat-stroked-button class="warn" (click)="setActive(false)" [disabled]="busy()">Deactivate</button>
              </div>
            }
          } @else if (auth.can('user.deactivate', null)) {
            <div class="row">
              <div><b>Reactivate account</b><p class="sub">They can sign in again with their existing password.</p></div>
              <span class="grow"></span>
              <button mat-stroked-button (click)="setActive(true)" [disabled]="busy()">Reactivate</button>
            </div>
          }
        </section>
      }
    </div>
  `,
  styles: `
    .back { color: #5b6b6e; text-decoration: none; font-size: 0.9rem; } .back::before { content: '‹ '; }
    .box { padding: 20px 24px; max-width: 820px; margin-bottom: 16px; }
    h2 { font-size: 1rem; color: #0e3b43; margin-bottom: 12px; }
    .sub { color: #5b6b6e; margin: 0 0 12px; font-size: 0.9rem; }
    .grant { display: grid; grid-template-columns: 1fr 1fr auto; gap: 12px; align-items: center; margin-top: 8px; }
    .desc { color: #5b6b6e; font-size: 0.85rem; margin: 4px 0 8px; }
    .actions { display: flex; gap: 8px; margin-top: 12px; }
    .row { display: flex; align-items: center; gap: 12px; padding: 12px 0; border-top: 1px solid #edf1f0; flex-wrap: wrap; }
    .row:first-of-type { border-top: 0; }
    .row .sub { margin: 2px 0 0; }
    .grow { flex: 1; }
    .warn { color: #b3261e; }
    .reason-field { width: 100%; margin-bottom: 8px; }
    .error { color: #b3261e; max-width: 820px; }
    @media (max-width: 640px) { .grant { grid-template-columns: 1fr auto; } .grant mat-form-field:nth-child(2) { grid-column: 1; } }
  `,
})
export class UserEditPage implements OnInit {
  id = input<string>();
  auth = inject(AuthService);
  ctx = inject(PropertyContext);
  private http = inject(HttpClient);
  private router = inject(Router);
  private snack = inject(MatSnackBar);

  isNew = computed(() => !this.id());
  isSelf = computed(() => this.id() === this.auth.me()?.userId);
  canUpdate = computed(() => this.auth.can('user.update', null));
  canEditRoles = computed(() => !this.isSelf() && (this.isNew() ? this.auth.can('user.create', null) : this.canUpdate()));

  user = signal<User | null>(null);
  roles = signal<Role[]>([]);
  grants = signal<RoleGrant[]>([]);
  busy = signal(false);
  error = signal('');
  err = signal<Record<string, string>>({});
  /** Sent as X-Change-Reason so the audit trail records why (brief §58). */
  reason = signal('');
  private get reasonHeaders() {
    const r = this.reason().trim();
    return r ? { headers: { 'X-Change-Reason': encodeURIComponent(r) } } : {};
  }

  form = inject(FormBuilder).nonNullable.group({
    fullName: ['', Validators.required],
    phone: [''],
    email: ['', [Validators.required, Validators.email]],
    temporaryPassword: ['', [Validators.required, Validators.minLength(10)]],
  });

  ngOnInit() {
    if (this.auth.canAnywhere('role.read'))
      this.http.get<Role[]>('/api/v1/roles').subscribe(r =>
        this.roles.set([...r].sort((a, b) => ROLE_ORDER.indexOf(a.code) - ROLE_ORDER.indexOf(b.code))));
    if (this.isNew()) {
      this.grants.set([{ roleCode: 'EMPLOYEE', propertyId: this.ctx.current()?.id ?? null }]);
      return;
    }
    this.form.controls.email.disable();
    this.form.controls.temporaryPassword.disable();
    this.load();
  }

  private load() {
    this.http.get<User>(`/api/v1/users/${this.id()}`).subscribe(u => {
      this.user.set(u);
      this.grants.set(u.roles.map(r => ({ ...r })));
      this.form.patchValue({ fullName: u.fullName, phone: u.phone ?? '' });
      if (!this.canUpdate()) this.form.disable();
    });
  }

  roleDesc(code: string) { return this.roles().find(r => r.code === code)?.description ?? roleName(code); }
  setGrant(i: number, patch: Partial<RoleGrant>) { this.grants.update(g => g.map((x, j) => (j === i ? { ...x, ...patch } : x))); }
  addGrant() { this.grants.update(g => [...g, { roleCode: 'EMPLOYEE', propertyId: this.ctx.current()?.id ?? null }]); }
  removeGrant(i: number) { this.grants.update(g => g.filter((_, j) => j !== i)); }

  create() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.run(this.http.post<User>('/api/v1/users', { ...v, phone: v.phone || null, roles: this.grants() }), u => {
      this.snack.open('Account created', undefined, { duration: 2500 });
      this.router.navigateByUrl(`/users/${u.id}`);
    });
  }

  saveDetails() {
    if (this.isNew() || this.form.controls.fullName.invalid) return;
    const v = this.form.getRawValue();
    this.run(this.http.put<User>(`/api/v1/users/${this.id()}`, { fullName: v.fullName, phone: v.phone || null, version: this.user()!.version }),
      u => { this.user.set(u); this.snack.open('Details saved', undefined, { duration: 2500 }); });
  }

  saveRoles() {
    this.run(this.http.put<User>(`/api/v1/users/${this.id()}/roles`, { roles: this.grants() }, this.reasonHeaders),
      u => { this.user.set(u); this.snack.open('Roles saved', undefined, { duration: 2500 }); });
  }

  setActive(active: boolean) {
    this.run(this.http.post<User>(`/api/v1/users/${this.id()}/${active ? 'activate' : 'deactivate'}`, null, this.reasonHeaders),
      u => { this.user.set(u); this.snack.open(active ? 'Account reactivated' : 'Account deactivated', undefined, { duration: 2500 }); });
  }

  resetPassword(temporaryPassword: string) {
    this.run(this.http.post(`/api/v1/users/${this.id()}/reset-password`, { temporaryPassword }, this.reasonHeaders),
      () => { this.snack.open('Password reset. They’ll set a new one at sign-in.', undefined, { duration: 3500 }); this.load(); });
  }

  private run<T>(obs: import('rxjs').Observable<T>, ok: (v: T) => void) {
    this.busy.set(true); this.error.set(''); this.err.set({});
    obs.subscribe({
      next: v => { this.busy.set(false); ok(v); },
      error: e => {
        const p = toProblem(e);
        this.err.set(fieldErrors(p));
        const fieldMsgs = Object.values(p.errors ?? {}).map(m => m[0]);
        this.error.set(p.code === 'CONCURRENCY_CONFLICT'
          ? 'Someone else changed this account while you were editing. Reload to see their changes.'
          : fieldMsgs.length ? fieldMsgs.join(' ') : p.detail);
        this.busy.set(false);
      },
    });
  }
}
