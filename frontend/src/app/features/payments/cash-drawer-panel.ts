import { Component, computed, effect, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { CashDrawer, CashDrawerSession } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { rupeesExact } from '../settings/rates/rates.api';
import { ApproveVarianceDialog, CloseDrawerDialog, OpenDrawerDialog } from './payment-dialogs';
import { PaymentsApi } from './payments.api';

/**
 * The cash drawer on the front desk (Sprint 10): whether the shift is open, what the drawer should hold now, and the
 * buttons to open and close it. Shifts that closed short or over are listed until a manager approves them.
 */
@Component({
  selector: 'app-cash-drawer-panel',
  imports: [MatButtonModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    @if (drawer(); as d) {
      <section class="drawer" aria-label="Cash drawer" data-testid="drawer">
        <div class="main">
          <mat-icon aria-hidden="true">point_of_sale</mat-icon>
          @if (d.current; as s) {
            <div>
              <b>{{ d.name }} drawer open</b> <span class="chip ok">Open</span>
              <br /><small class="mute">since {{ time(s.openedAt) }}@if (s.openedByName) { · {{ s.openedByName }} } · float {{ money(s.openingFloat) }}
                · {{ s.payments }} cash payment{{ s.payments === 1 ? '' : 's' }}</small>
            </div>
            <div class="expected"><span>Should hold</span><b class="num" data-testid="drawer-expected">{{ money(s.expected) }}</b></div>
            @if (canOperate()) { <button mat-stroked-button (click)="close(s)">Close drawer</button> }
          } @else {
            <div><b>{{ d.name }} drawer closed</b>
              <br /><small class="mute">Open it with the cash counted in it before taking cash.@if (d.recent[0]; as last) { Last shift closed {{ time(last.closedAt!) }}. }</small>
            </div>
            @if (canOperate()) { <button mat-flat-button (click)="open()">Open drawer</button> }
          }
        </div>
        @for (p of d.pendingApproval; track p.id) {
          <div class="pending" [attr.data-session]="p.id">
            <span><b class="bad num">{{ money(abs(p.variance ?? 0)) }} {{ (p.variance ?? 0) < 0 ? 'short' : 'over' }}</b> · shift closed {{ time(p.closedAt!) }}
              @if (p.closedByName) { by {{ p.closedByName }} } · “{{ p.closeNote }}”</span>
            @if (canApprove()) { <button mat-button (click)="approve(p)">Approve</button> } @else { <small class="mute">Waiting for a manager</small> }
          </div>
        }
        @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      </section>
    } @else if (error()) { <p class="err" role="alert">{{ error() }}</p> }
  `,
  styles: `
    .drawer { background: #fff; border: 1px solid #d9e0de; border-radius: 10px; padding: 12px 16px; margin-bottom: 16px; }
    .main { display: flex; align-items: center; gap: 14px; flex-wrap: wrap; }
    .main > mat-icon { color: #0e3b43; }
    .main > div:first-of-type { flex: 1; min-width: 200px; }
    .expected { display: flex; flex-direction: column; align-items: flex-end; }
    .expected span { color: #5b6b6e; font-size: .8rem; }
    .expected b { font-size: 1.3rem; color: #0e3b43; }
    .pending { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; margin-top: 10px; padding: 8px 12px;
               background: #fbefd8; border-radius: 8px; }
    .chip.ok { background: #e1f2e8; color: #1d5b36; }
    .mute { color: #5b6b6e; }
    .bad { color: #b3261e; }
    .err { color: #b3261e; margin: 8px 0 0; }
  `,
})
export class CashDrawerPanel {
  private api = inject(PaymentsApi);
  private props = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);

  drawer = signal<CashDrawer | null>(null);
  error = signal('');
  money = rupeesExact;
  abs = Math.abs;
  canOperate = computed(() => { this.auth.me(); return this.auth.can('cash_drawer.operate', this.props.current()?.id); });
  canApprove = computed(() => { this.auth.me(); return this.auth.can('cash_drawer.approve_variance', this.props.current()?.id); });
  private pid = () => this.props.current()!.id;

  constructor() {
    effect(() => { if (this.props.current()) this.load(); });
  }

  load() {
    this.api.drawer(this.pid()).subscribe({ next: d => { this.drawer.set(d); this.error.set(''); }, error: e => this.error.set(toProblem(e).detail) });
  }

  time(iso: string) {
    return inZone(iso, this.props.current()!.timezone, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false });
  }

  open() {
    this.dialog.open(OpenDrawerDialog, { data: { propertyId: this.pid() }, autoFocus: 'first-tabbable' }).afterClosed()
      .subscribe((s: CashDrawerSession | undefined) => {
        if (!s) return;
        this.snack.open(`Drawer open with ${rupeesExact(s.openingFloat)}`, undefined, { duration: 3000 });
        this.load();
      });
  }

  close(session: CashDrawerSession) {
    this.dialog.open(CloseDrawerDialog, { data: { propertyId: this.pid(), session }, autoFocus: 'first-tabbable' }).afterClosed()
      .subscribe((s: CashDrawerSession | undefined) => {
        if (!s) return;
        this.snack.open(s.status === 'CLOSED' ? 'Drawer closed: the count matches'
          : `Drawer closed ${rupeesExact(Math.abs(s.variance ?? 0))} ${(s.variance ?? 0) < 0 ? 'short' : 'over'}; waiting for a manager to approve`,
          undefined, { duration: 4000 });
        this.load();
      });
  }

  approve(session: CashDrawerSession) {
    this.dialog.open(ApproveVarianceDialog, { data: { propertyId: this.pid(), session } }).afterClosed()
      .subscribe((s: CashDrawerSession | undefined) => {
        if (!s) return;
        this.snack.open('Variance approved; the shift is closed', undefined, { duration: 3000 });
        this.load();
      });
  }
}
