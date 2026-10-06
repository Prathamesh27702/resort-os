import { Component, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Permission, Role } from '../../core/models';
import { ROLE_ORDER } from './roles';

/** Read-only permission matrix: rows are permissions grouped by module, columns are roles. */
@Component({
  selector: 'app-roles',
  imports: [MatIconModule, MatTooltipModule],
  template: `
    <div class="page wide">
      <div class="page-head">
        <div>
          <h1>Roles &amp; permissions</h1>
          <p>What each built-in role can do. Permissions marked with a shield are sensitive (money, salaries, ID documents, audit).</p>
        </div>
      </div>
      <div class="panel scroll">
        <table class="matrix">
          <thead>
            <tr>
              <th class="perm">Permission</th>
              @for (r of roles(); track r.code) { <th class="role"><span [matTooltip]="r.description ?? ''">{{ r.name }}</span></th> }
            </tr>
          </thead>
          <tbody>
            @for (m of modules(); track m.module) {
              <tr class="mod"><td [attr.colspan]="roles().length + 1">{{ m.label }}</td></tr>
              @for (p of m.perms; track p.code) {
                <tr>
                  <td class="perm">
                    {{ p.description }}
                    @if (p.isSensitive) { <mat-icon class="sens" matTooltip="Sensitive">shield</mat-icon> }
                    <code>{{ p.code }}</code>
                  </td>
                  @for (r of roles(); track r.code) {
                    <td class="cell">@if (has(r, p.code)) { <span class="dot" [attr.aria-label]="r.name + ' has ' + p.code"></span> }</td>
                  }
                </tr>
              }
            }
          </tbody>
        </table>
      </div>
    </div>
  `,
  styles: `
    .wide { max-width: none; }
    .scroll { overflow: auto; max-height: calc(100vh - 190px); }
    .matrix { border-collapse: separate; border-spacing: 0; font-size: 0.85rem; }
    .matrix th, .matrix td { border-bottom: 1px solid #edf1f0; padding: 6px 8px; }
    thead th { position: sticky; top: 0; background: #fff; z-index: 1; border-bottom: 1px solid #d9e0de; }
    th.role { writing-mode: vertical-rl; transform: rotate(180deg); text-align: left; font-weight: 500; color: #1b2a2e; height: 150px; white-space: nowrap; }
    .perm { position: sticky; left: 0; background: #fff; min-width: 300px; text-align: left; }
    .perm code { display: block; color: #8a9a9c; font-size: 0.75rem; }
    .sens { font-size: 14px; width: 14px; height: 14px; color: #8a2449; vertical-align: -2px; }
    tr.mod td { background: #f5f7f6; font-weight: 600; color: #0e3b43; position: sticky; left: 0; }
    .cell { text-align: center; }
    .dot { display: inline-block; width: 10px; height: 10px; border-radius: 50%; background: #0e3b43; }
    tbody tr:not(.mod):hover td { background: #f8faf9; }
  `,
})
export class RolesPage {
  private http = inject(HttpClient);
  roles = signal<Role[]>([]);
  perms = signal<Permission[]>([]);

  constructor() {
    this.http.get<Role[]>('/api/v1/roles').subscribe(r =>
      this.roles.set([...r].sort((a, b) => ROLE_ORDER.indexOf(a.code) - ROLE_ORDER.indexOf(b.code))));
    this.http.get<Permission[]>('/api/v1/permissions').subscribe(p => this.perms.set(p));
  }

  modules = computed(() => {
    const groups = new Map<string, Permission[]>();
    for (const p of this.perms()) groups.set(p.module, [...(groups.get(p.module) ?? []), p]);
    return [...groups.entries()].map(([module, perms]) => ({
      module, perms, label: module === 'gst' ? 'GST & invoicing' : module === 'pos' ? 'Restaurant POS' : module.charAt(0).toUpperCase() + module.slice(1),
    }));
  });

  has(r: Role, code: string) { return r.permissions.includes(code); }
}
