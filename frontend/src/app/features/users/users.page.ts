import { Component, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { CanDirective } from '../../shared/can.directive';
import { PropertyContext, inZone } from '../../core/property-context';
import { User } from '../../core/models';
import { roleName } from './roles';

@Component({
  selector: 'app-users',
  imports: [RouterLink, MatButtonModule, CanDirective],
  template: `
    <div class="page">
      <div class="page-head">
        <div><h1>Staff accounts</h1><p>One sign-in per person. Access comes from roles, given for all properties or just one.</p></div>
        <a mat-flat-button routerLink="/users/new" *appCan="'user.create'; property: null">Add staff</a>
      </div>
      <div class="panel">
        <table class="data">
          <thead><tr><th>Name</th><th>Roles</th><th class="hide-sm">Last sign-in</th><th>Status</th></tr></thead>
          <tbody>
            @for (u of users(); track u.id) {
              <tr class="clickable" (click)="open(u.id)" tabindex="0" (keydown.enter)="open(u.id)">
                <td><b>{{ u.fullName }}</b><br /><small class="mute">{{ u.email }}</small></td>
                <td>
                  @for (r of u.roles; track r.roleCode + r.propertyId) {
                    <span class="chip">{{ roleName(r.roleCode) }}@if (r.propertyId) { · {{ propName(r.propertyId) }} }</span>
                  } @empty { <span class="mute">No roles</span> }
                </td>
                <td class="hide-sm num">{{ u.lastLoginAt ? when(u.lastLoginAt) : 'Never' }}</td>
                <td>
                  @if (!u.isActive) { <span class="chip off">Deactivated</span> }
                  @else if (u.mustChangePassword) { <span class="chip">Awaiting first sign-in</span> }
                  @else { <span class="chip">Active</span> }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="4">{{ users() === null ? 'Loading…' : 'No staff accounts yet. Add your team so everyone signs in as themselves.' }}</td></tr>
            }
          </tbody>
        </table>
      </div>
    </div>
  `,
  styles: `.mute { color: #5b6b6e; }`,
})
export class UsersPage {
  private http = inject(HttpClient);
  private router = inject(Router);
  private ctx = inject(PropertyContext);
  users = signal<User[] | null>(null);
  roleName = roleName;

  constructor() { this.http.get<User[]>('/api/v1/users').subscribe(u => this.users.set(u)); }
  open(id: string) { this.router.navigateByUrl(`/users/${id}`); }
  /** Shown in the current property's timezone, like every other business time. */
  when(iso: string) {
    return inZone(iso, this.ctx.current()?.timezone ?? 'Asia/Kolkata', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false });
  }
  propName(id: string) { return this.ctx.properties().find(p => p.id === id)?.name ?? 'one property'; }
}
