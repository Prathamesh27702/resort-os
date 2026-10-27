import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { Supplier } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { rupees } from '../settings/rates/rates.api';
import { SupplierDialog } from './purchasing-dialogs';
import { PurchasingApi } from './purchasing.api';

/**
 * Suppliers (Sprint 16): who the property buys from, with their GSTIN (or "unregistered: no GST"), the state they supply
 * from, payment terms, and what is still owed to them. Works at 360 px: each supplier is a card.
 */
@Component({
  selector: 'app-suppliers',
  imports: [FormsModule, RouterLink, MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSnackBarModule],
  template: `
    <div class="page sup">
      <div class="head">
        <h1>Suppliers</h1>
        @if (owed() > 0) { <p class="mute">Owed {{ rupees(owed()) }}@if (overdue() > 0) { · <b class="late">{{ rupees(overdue()) }} overdue</b> }</p> }
      </div>
      <div class="tools">
        <mat-form-field appearance="outline" class="search" subscriptSizing="dynamic"><mat-label>Find a supplier</mat-label>
          <input matInput [ngModel]="search()" (ngModelChange)="search.set($event)" placeholder="Name, code or GSTIN" /></mat-form-field>
        @if (canManage()) { <button mat-flat-button (click)="edit(null)"><mat-icon>add</mat-icon>Supplier</button> }
        <a mat-stroked-button routerLink="/inventory/purchases"><mat-icon>shopping_cart</mat-icon>Purchases</a>
      </div>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      <ul class="cards">
        @for (s of shown(); track s.id) {
          <li [class.off]="!s.isActive" [attr.data-supplier]="s.code">
            <div class="nm">
              <b>{{ s.name }}</b> <small class="mute">{{ s.code }}</small>
              <small>@if (s.gstin) { GSTIN {{ s.gstin }} · {{ s.stateName }} } @else { <span class="unreg">Unregistered: no GST</span> · {{ s.stateName }} }</small>
              <small class="mute">@if (s.contactName) { {{ s.contactName }} · }@if (s.phone) { <a [href]="'tel:' + s.phone">{{ s.phone }}</a> · }pays {{ s.paymentTermsDays ? 'after ' + s.paymentTermsDays + ' days' : 'on delivery' }}</small>
              @if (s.note) { <small class="note">{{ s.note }}</small> }
              @if (!s.isActive) { <small class="mute">Not buying from them now</small> }
            </div>
            <div class="owed">
              @if (s.outstanding > 0) { <b>{{ rupees(s.outstanding) }}</b><small>owed</small> } @else { <small class="mute">Nothing owed</small> }
              @if (s.overdue > 0) { <small class="late">{{ rupees(s.overdue) }} overdue</small> }
            </div>
            @if (canManage()) { <button mat-icon-button (click)="edit(s)" [attr.aria-label]="'Edit ' + s.name"><mat-icon>edit</mat-icon></button> }
          </li>
        } @empty { @if (loaded()) { <li class="mute">No suppliers yet.@if (canManage()) { Add the first one. }</li> } }
      </ul>
    </div>
  `,
  styles: `
    .sup { max-width: 900px; padding-bottom: 60px; }
    .head { display: flex; justify-content: space-between; align-items: baseline; gap: 8px; flex-wrap: wrap; }
    .mute { color: #5b6b6e; } .err { color: #b3261e; } .late { color: #b3261e; } .unreg { color: #8a5a00; }
    .tools { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 8px; } .search { flex: 1 1 220px; }
    ul { list-style: none; margin: 0; padding: 0; }
    .cards li { display: flex; gap: 10px; align-items: flex-start; background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 10px 14px; margin-top: 8px; }
    .cards li.off { opacity: .6; }
    .nm { flex: 1; display: flex; flex-direction: column; gap: 2px; min-width: 0; overflow-wrap: anywhere; }
    .note { color: #34484b; }
    .owed { display: flex; flex-direction: column; align-items: flex-end; text-align: right; font-variant-numeric: tabular-nums; min-width: 96px; }
    @media (max-width: 640px) { h1 { font-size: 1.35rem; } .cards li { flex-wrap: wrap; } .owed { align-items: flex-start; text-align: left; } }
  `,
})
export class SuppliersPage {
  private api = inject(PurchasingApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);

  suppliers = signal<Supplier[]>([]);
  loaded = signal(false);
  error = signal('');
  search = signal('');
  rupees = rupees;
  private pid = computed(() => this.ctx.current()?.id ?? '');
  canManage = computed(() => this.auth.can('supplier.manage', this.pid()));
  shown = computed(() => {
    const s = this.search().trim().toLowerCase();
    return this.suppliers().filter(x => !s || x.name.toLowerCase().includes(s) || x.code.toLowerCase().includes(s) || (x.gstin ?? '').toLowerCase().includes(s));
  });
  owed = computed(() => this.suppliers().reduce((t, s) => t + s.outstanding, 0));
  overdue = computed(() => this.suppliers().reduce((t, s) => t + s.overdue, 0));

  constructor() {
    effect(() => { if (this.pid()) this.load(); });
  }

  load() {
    this.api.suppliers(this.pid()).subscribe({
      next: s => { this.suppliers.set(s); this.loaded.set(true); this.error.set(''); },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  edit(s: Supplier | null) {
    this.dialog.open(SupplierDialog, { data: { propertyId: this.pid(), supplier: s }, maxWidth: '100vw' }).afterClosed().subscribe((m?: string) => {
      if (!m) return;
      this.snack.open(m, 'OK', { duration: 5000 });
      this.load();
    });
  }
}
