import { Component, inject, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { AuditLog, Paged } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';

const ACTIONS: Partial<Record<string, string>> = {
  INSERT: 'Created', UPDATE: 'Changed', DELETE: 'Deleted', SOFT_DELETE: 'Removed',
  REFRESH_TOKEN_REUSE: 'Stolen session blocked', PASSWORD_CHANGED: 'Password changed',
  SENSITIVE_READ: 'Opened (sensitive)',
};
const ENTITIES: Partial<Record<string, string>> = {
  Property: 'Property', PropertySetting: 'Property setting', User: 'Staff account', UserRole: 'Role grant',
  Organization: 'Organisation', Role: 'Role', RefreshToken: 'Session',
  AccommodationType: 'Room type', AccommodationUnit: 'Room', Bed: 'Bed', UnitBlock: 'Block',
  Guest: 'Guest', GuestAddress: 'Guest address', GuestPreference: 'Guest preference', GuestConsent: 'Guest consent',
  GuestDocument: 'ID document',
  RatePlan: 'Rate plan', RatePlanPrice: 'Rate price', Package: 'Package', PackageComponent: 'Package part',
};

@Component({
  selector: 'app-audit',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatSelectModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div><h1>Audit trail</h1><p>Every change, who made it and when. Entries can’t be edited or deleted.</p></div>
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Show</mat-label>
          <mat-select [(ngModel)]="entity" (selectionChange)="load(1)">
            <mat-option [value]="''">Everything</mat-option>
            @for (e of entityKeys; track e) { <mat-option [value]="e">{{ entities[e] }}</mat-option> }
          </mat-select>
        </mat-form-field>
      </div>
      <div class="panel">
        <table class="data">
          <thead><tr><th>When ({{ tzLabel() }})</th><th>Who</th><th>What</th><th class="hide-sm">Details</th></tr></thead>
          <tbody>
            @for (a of page()?.items ?? []; track a.id) {
              <tr>
                <td class="num nowrap">{{ when(a.occurredAt) }}</td>
                <td>{{ a.userName ?? 'System' }}</td>
                <td>{{ actions[a.action] ?? a.action }} · {{ entities[a.entityType] ?? a.entityType }}
                  @if (a.reason) { <div class="reason">“{{ a.reason }}”</div> }
                  @if (a.ipAddress) { <div class="meta">{{ a.ipAddress }}</div> }
                </td>
                <td class="hide-sm">
                  @if (a.changes) {
                    <ul class="changes">
                      @for (c of fields(a); track c.k) {
                        <li><b>{{ c.k }}</b>
                          @if (a.action === 'UPDATE') { {{ show(c.v.old) }} → {{ show(c.v.new) }} } @else { {{ show(c.v.new ?? c.v.old) }} }
                        </li>
                      }
                    </ul>
                  }
                </td>
              </tr>
            } @empty { <tr><td colspan="4">Nothing recorded yet.</td></tr> }
          </tbody>
        </table>
      </div>
      @if (page(); as p) {
        <div class="pager">
          <span class="num">{{ (p.page - 1) * p.pageSize + 1 }}–{{ (p.page - 1) * p.pageSize + p.items.length }} of {{ p.total }}</span>
          <button mat-button [disabled]="p.page <= 1" (click)="load(p.page - 1)">Newer</button>
          <button mat-button [disabled]="p.page * p.pageSize >= p.total" (click)="load(p.page + 1)">Older</button>
        </div>
      }
    </div>
  `,
  styles: `
    .nowrap { white-space: nowrap; }
    .reason { color: #1b2a2e; font-size: 0.85rem; margin-top: 2px; }
    .meta { color: #8a9a9c; font-size: 0.78rem; }
    .changes { margin: 0; padding: 0; list-style: none; font-size: 0.85rem; color: #5b6b6e; }
    .changes b { color: #1b2a2e; font-weight: 500; margin-right: 4px; }
    .pager { display: flex; align-items: center; gap: 8px; justify-content: flex-end; margin-top: 12px; color: #5b6b6e; }
  `,
})
export class AuditPage {
  private http = inject(HttpClient);
  private ctx = inject(PropertyContext);
  page = signal<Paged<AuditLog> | null>(null);
  entity = '';
  actions = ACTIONS;
  entities = ENTITIES;
  entityKeys = Object.keys(ENTITIES);

  constructor() { this.load(1); }

  load(n: number) {
    let params = new HttpParams().set('page', n).set('pageSize', 25);
    if (this.entity) params = params.set('entityType', this.entity);
    this.http.get<Paged<AuditLog>>('/api/v1/audit-logs', { params }).subscribe(p => this.page.set(p));
  }

  tz() { return this.ctx.current()?.timezone ?? 'Asia/Kolkata'; }
  tzLabel() { return this.tz() === 'Asia/Kolkata' ? 'IST' : this.tz(); }
  when(iso: string) { return inZone(iso, this.tz(), { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false }); }
  fields(a: AuditLog) { return Object.entries(a.changes ?? {}).filter(([k]) => k !== 'Id').slice(0, 6).map(([k, v]) => ({ k, v })); }
  show(v: unknown) { return v === null || v === undefined || v === '' ? '—' : typeof v === 'object' ? JSON.stringify(v) : String(v); }
}
