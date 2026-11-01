import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { dayText } from '../../core/dates';
import { toProblem } from '../../core/http';
import { EmployeeLeaveBalances, Holiday, LeaveRequest, LeaveType, MyLeave } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { ReasonData, ReasonDialog } from '../restaurant/order-dialogs';
import { ACCRUAL_LABEL, LEAVE_STATUS, LeaveApi, days, range, short } from './leave.api';
import { ApproveLeaveDialog, AskData, AskLeaveDialog, HolidayDialog, LeaveTypeDialog, LedgerDialog } from './leave-dialogs';

type View = 'mine' | 'team' | 'balances' | 'holidays' | 'types';

/**
 * Leave (Sprint 19, brief §45). Everyone sees their balances and asks for leave (the days that count, the balance after
 * and what it does to the roster are shown before sending). Managers see what is waiting with the cover warnings and
 * approve or refuse; approving takes the days off the balance and the person off the roster. HR sees everyone's balances
 * and their ledger, adjusts them with a reason, keeps the holidays and the kinds of leave.
 */
@Component({
  selector: 'app-leave',
  imports: [NgTemplateOutlet, MatButtonModule, MatButtonToggleModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page leave">
      <div class="head">
        <h1>Leave</h1>
        @if (views().length > 1) {
          <mat-button-toggle-group [value]="view()" (change)="show($event.value)" aria-label="Show">
            @for (v of views(); track v) { <mat-button-toggle [value]="v">{{ viewLabel[v] }}@if (v === 'team' && pendingCount()) { ({{ pendingCount() }}) }</mat-button-toggle> }
          </mat-button-toggle-group>
        }
      </div>

      @switch (view()) {
        @case ('mine') {
          @if (mine(); as m) {
            <section class="cards" data-testid="my-balances">
              @for (b of m.balances; track b.leaveTypeId) {
                @if (b.needsBalance) {
                  <div class="card" [attr.data-type]="b.code"><b>{{ days(b.balance) }}</b><span>{{ b.name }}</span>
                    @if (b.pending) { <small class="mute">{{ days(b.pending) }} waiting · {{ days(b.available) }} free</small> }
                  </div>
                }
              }
            </section>
            <div class="actions">
              <button mat-flat-button (click)="ask()"><mat-icon>add</mat-icon>Ask for leave</button>
              <button mat-stroked-button (click)="ledger(m.employeeId, false)">History</button>
            </div>
            <h2>My leave</h2>
            <ul class="reqs" data-testid="my-requests">
              @for (r of m.requests; track r.id) { <ng-container *ngTemplateOutlet="row; context: { $implicit: r, mine: true }" /> }
              @empty { <li class="mute">You haven't asked for leave yet.</li> }
            </ul>
            @if (m.holidays.length) {
              <h2>Coming holidays</h2>
              <ul class="hols">@for (h of m.holidays.slice(0, 6); track h.id) { <li><b>{{ short(h.date) }}</b> {{ h.name }}</li> }</ul>
            }
          } @else if (mineError()) { <p class="mute notlinked" data-testid="not-linked"><mat-icon inline>info</mat-icon> {{ mineError() }}</p> }
        }

        @case ('team') {
          @if (canApprove()) { <div class="actions"><button mat-stroked-button (click)="askFor()"><mat-icon>person_add</mat-icon>Leave for someone</button></div> }
          <h2>Waiting for approval</h2>
          <ul class="reqs" data-testid="pending">
            @for (r of pending(); track r.id) { <ng-container *ngTemplateOutlet="row; context: { $implicit: r, mine: false }" /> }
            @empty { <li class="mute">Nothing is waiting.</li> }
          </ul>
          <h2>Who is off</h2>
          <ul class="reqs" data-testid="upcoming">
            @for (r of upcoming(); track r.id) { <ng-container *ngTemplateOutlet="row; context: { $implicit: r, mine: false }" /> }
            @empty { <li class="mute">Nobody has approved leave in the next six months.</li> }
          </ul>
        }

        @case ('balances') {
          @if (canManage()) {
            <div class="actions"><button mat-stroked-button (click)="accrue()"><mat-icon>update</mat-icon>Add this month's leave</button>
              <span class="mute small">It is added by itself on the 1st; this does it now. Running it twice adds nothing.</span></div>
          }
          <div class="scroll">
            <table class="bal" data-testid="balances">
              <thead><tr><th>Employee</th>@for (t of balanceTypes(); track t.id) { <th class="n">{{ t.code }}</th> }</tr></thead>
              <tbody>
                @for (e of balances(); track e.employeeId) {
                  <tr [attr.data-emp]="e.employeeCode" (click)="ledger(e.employeeId, canManage())" tabindex="0" (keydown.enter)="ledger(e.employeeId, canManage())">
                    <td><b>{{ e.employeeName }}</b> <small class="mute">{{ e.employeeCode }} · {{ e.departmentName }}</small></td>
                    @for (t of balanceTypes(); track t.id) { <td class="n">{{ bal(e, t.id) }}</td> }
                  </tr>
                } @empty { <tr><td class="mute">Nobody works here yet.</td></tr> }
              </tbody>
            </table>
          </div>
        }

        @case ('holidays') {
          <div class="actions">
            <button mat-icon-button (click)="year.set(year() - 1)" aria-label="Previous year"><mat-icon>chevron_left</mat-icon></button>
            <b>{{ year() }}</b>
            <button mat-icon-button (click)="year.set(year() + 1)" aria-label="Next year"><mat-icon>chevron_right</mat-icon></button>
            @if (canManage()) { <button mat-flat-button (click)="holiday(null)"><mat-icon>add</mat-icon>Add a holiday</button> }
          </div>
          <ul class="hols full" data-testid="holidays">
            @for (h of holidays(); track h.id) {
              <li [class.off]="!h.isActive"><b>{{ dayText(h.date) }}</b> {{ h.name }}@if (!h.isActive) { <small class="mute"> · not a holiday any more</small> }
                @if (canManage()) { <button mat-button (click)="holiday(h)">Change</button> }</li>
            } @empty { <li class="mute">No holidays in {{ year() }} yet.</li> }
          </ul>
        }

        @case ('types') {
          @if (canManage()) { <div class="actions"><button mat-flat-button (click)="editType(null)"><mat-icon>add</mat-icon>New kind of leave</button></div> }
          <ul class="types" data-testid="types">
            @for (t of types(); track t.id) {
              <li [class.off]="!t.isActive">
                <div><b>{{ t.name }}</b> <small class="mute">{{ t.code }}</small>
                  <small>{{ t.isPaid ? 'Paid' : 'Unpaid' }}{{ t.needsBalance ? '' : ', no balance' }}{{ t.allowHalfDay ? ', half days' : ', whole days' }}{{ t.isActive ? '' : ' · switched off' }}</small>
                  <small class="mute">{{ policy(t) }}</small></div>
                @if (canManage()) { <button mat-button (click)="editType(t)">Change</button> }
              </li>
            }
          </ul>
        }
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>

    <ng-template #row let-r let-mine="mine">
      <li [attr.data-req]="r.code" [class]="'s-' + r.status">
        <div class="main">
          <div class="top"><b>{{ mine ? r.leaveTypeName : r.employeeName }}</b>
            <span class="status">{{ statusText(r.status) }}</span></div>
          <span>{{ range(r) }} · <b>{{ days(r.days) }}</b>@if (!mine) { · {{ r.leaveTypeName }} · {{ r.departmentName }} }</span>
          <small class="mute">{{ r.code }}@if (r.reason) { · {{ r.reason }} }@if (r.decidedBy) { · {{ r.status === 'REJECTED' ? 'refused' : 'approved' }} by {{ r.decidedBy }} }
            @if (r.decisionNote) { : {{ r.decisionNote }} }@if (r.cancelReason) { · cancelled: {{ r.cancelReason }} }
            @if (r.shiftsCancelled) { · {{ r.shiftsCancelled }} {{ r.shiftsCancelled === 1 ? 'shift' : 'shifts' }} taken off the roster }</small>
          @for (w of r.warnings; track w.message) { <small class="w" [class]="'k-' + w.kind"><mat-icon inline>{{ w.kind === 'ROSTERED' ? 'event_busy' : 'warning' }}</mat-icon> {{ w.message }}</small> }
        </div>
        <div class="btns">
          @if (r.actions.includes('approve')) { <button mat-flat-button (click)="approve(r)">Approve</button> }
          @if (r.actions.includes('reject')) { <button mat-stroked-button (click)="reject(r)">Refuse</button> }
          @if (r.actions.includes('cancel') && (mine || r.status === 'APPROVED')) { <button mat-button (click)="cancel(r)">Cancel</button> }
        </div>
      </li>
    </ng-template>
  `,
  styles: `
    .leave { max-width: 960px; padding-bottom: 60px; }
    .head { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 8px; }
    mat-button-toggle-group { flex-wrap: wrap; }
    .mute { color: #5b6b6e; } .small { font-size: .85rem; } .err { color: #b3261e; }
    .cards { display: flex; gap: 10px; flex-wrap: wrap; margin: 8px 0; }
    .card { background: #fff; border: 1px solid #d9e0de; border-radius: 14px; padding: 10px 14px; min-width: 120px; display: flex; flex-direction: column; }
    .card b { font-size: 1.3rem; }
    .actions { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin: 8px 0 4px; }
    h2 { margin: 18px 0 6px; font-size: 1.1rem; }
    .reqs, .hols, .types { list-style: none; margin: 0; padding: 0; background: #fff; border: 1px solid #d9e0de; border-radius: 14px; }
    .reqs li, .types li { display: flex; gap: 10px; align-items: center; padding: 10px 14px; border-top: 1px solid #eef2f1; flex-wrap: wrap; }
    .reqs li:first-child, .types li:first-child, .hols li:first-child { border-top: 0; }
    .main { flex: 1 1 260px; display: flex; flex-direction: column; gap: 2px; min-width: 0; }
    .top { display: flex; justify-content: space-between; gap: 8px; }
    .status { font-size: .85rem; border-radius: 10px; padding: 0 8px; background: #eef2f1; white-space: nowrap; }
    .s-PENDING .status { background: #fdf6e6; color: #6b4a00; } .s-APPROVED .status { background: #e3f3ea; color: #1e6b3a; }
    .s-REJECTED .status, .s-CANCELLED .status { background: #f3eeee; color: #6b2a2a; }
    .w { color: #5b4a00; } .k-ROSTERED { color: #0e3b43; }
    .btns { display: flex; gap: 6px; flex-wrap: wrap; }
    .hols li { padding: 8px 14px; border-top: 1px solid #eef2f1; } .hols.full li { display: flex; align-items: center; gap: 8px; } .hols b { min-width: 110px; display: inline-block; }
    .off { opacity: .6; }
    .types li > div { flex: 1; display: flex; flex-direction: column; }
    .scroll { overflow-x: auto; background: #fff; border: 1px solid #d9e0de; border-radius: 14px; }
    .bal { width: 100%; border-collapse: collapse; } .bal th, .bal td { padding: 8px 10px; border-bottom: 1px solid #eef2f1; text-align: left; }
    .bal tbody tr { cursor: pointer; } .bal tbody tr:hover { background: #f4f8f7; } .bal small { display: block; } .n { text-align: right; }
    .notlinked { background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 12px 16px; }
  `,
})
export class LeavePage {
  private api = inject(LeaveApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  days = days; range = range; short = short; dayText = dayText;
  viewLabel: Record<View, string> = { mine: 'Mine', team: 'Team', balances: 'Balances', holidays: 'Holidays', types: 'Kinds of leave' };

  private pid = computed(() => this.ctx.current()?.id ?? '');
  canRead = computed(() => this.auth.can('leave.read', this.pid()));
  canApprove = computed(() => this.auth.can('leave.approve', this.pid()));
  canManage = computed(() => this.auth.can('leave.manage_balances', this.pid()));
  views = computed<View[]>(() => ['mine', ...(this.canRead() ? ['team', 'balances'] as View[] : []), 'holidays', ...(this.canManage() ? ['types'] as View[] : [])]);
  view = signal<View>('mine');

  mine = signal<MyLeave | null>(null);
  mineError = signal('');
  pending = signal<LeaveRequest[]>([]);
  upcoming = signal<LeaveRequest[]>([]);
  pendingCount = computed(() => this.pending().length);
  balances = signal<EmployeeLeaveBalances[]>([]);
  types = signal<LeaveType[]>([]);
  holidays = signal<Holiday[]>([]);
  year = signal(new Date().getFullYear());
  error = signal('');
  balanceTypes = computed(() => this.types().filter(t => t.needsBalance && t.isActive));

  constructor() {
    const q = this.route.snapshot.queryParamMap.get('view') as View | null;
    effect(() => {
      if (!this.pid()) return;
      untracked(() => {
        if (q && this.views().includes(q)) this.view.set(q);
        else if (this.canApprove() && !q) this.view.set('team');
        this.load();
      });
    });
    effect(() => { const y = this.year(); if (this.pid()) untracked(() => this.loadHolidays(y)); });
  }

  show(v: View) {
    this.view.set(v);
    this.router.navigate([], { queryParams: { view: v }, replaceUrl: true });
  }

  load() {
    const pid = this.pid();
    this.error.set('');
    this.api.types(pid).subscribe({ next: t => this.types.set(t), error: e => this.error.set(toProblem(e).detail) });
    this.api.mine(pid).subscribe({ next: m => { this.mine.set(m); this.mineError.set(''); }, error: e => { this.mine.set(null); this.mineError.set(toProblem(e).detail); } });
    if (this.canRead()) {
      this.api.requests(pid, { status: 'PENDING' }).subscribe({ next: l => this.pending.set(l.requests), error: e => this.error.set(toProblem(e).detail) });
      const today = new Date().toISOString().slice(0, 10);
      this.api.requests(pid, { status: 'APPROVED', from: today }).subscribe({ next: l => this.upcoming.set(l.requests) });
      this.api.balances(pid).subscribe({ next: b => this.balances.set(b) });
    }
  }

  private loadHolidays(y: number) {
    this.api.holidays(this.pid(), y).subscribe({ next: h => this.holidays.set(h), error: e => this.error.set(toProblem(e).detail) });
  }

  statusText(s: string) { return LEAVE_STATUS[s as keyof typeof LEAVE_STATUS] ?? s; }

  bal(e: EmployeeLeaveBalances, typeId: string) {
    const b = e.balances.find(x => x.leaveTypeId === typeId);
    return b ? (b.pending ? `${b.balance} (${b.pending} waiting)` : `${b.balance}`) : '–';
  }

  policy(t: LeaveType) {
    const p = t.policies.find(x => x.employmentType === null);
    if (!p || !t.needsBalance) return 'Not taken from a balance';
    return `${p.daysPerYear} a year · ${ACCRUAL_LABEL[p.accrual].toLowerCase()}${p.maxBalance !== null ? ` · at most ${p.maxBalance}` : ''}`;
  }

  ask() {
    const m = this.mine();
    if (!m) return;
    const data: AskData = { propertyId: this.pid(), types: m.types, today: m.today };
    this.dialog.open(AskLeaveDialog, { data, maxWidth: '100vw' }).afterClosed().subscribe((r?: LeaveRequest) => {
      if (!r) return;
      this.snack.open(`${r.code}: ${days(r.days)} of ${r.leaveTypeName} asked for. Your manager will approve it.`, 'OK', { duration: 5000 });
      this.load();
    });
  }

  askFor() {
    const data: AskData = { propertyId: this.pid(), types: this.types().filter(t => t.isActive), today: this.mine()?.today ?? new Date().toISOString().slice(0, 10),
      employees: this.balances().map(e => ({ id: e.employeeId, name: `${e.employeeName} · ${e.departmentName}` })) };
    this.dialog.open(AskLeaveDialog, { data, maxWidth: '100vw' }).afterClosed().subscribe((r?: LeaveRequest) => {
      if (!r) return;
      this.snack.open(`${r.code} for ${r.employeeName}: ${days(r.days)} of ${r.leaveTypeName}, waiting for approval.`, 'OK', { duration: 5000 });
      this.load();
    });
  }

  approve(r: LeaveRequest) {
    this.dialog.open(ApproveLeaveDialog, { data: { propertyId: this.pid(), request: r }, maxWidth: '100vw' }).afterClosed().subscribe((x?: LeaveRequest) => {
      if (!x) return;
      this.snack.open(`${x.code} approved: ${days(x.days)} off ${x.employeeName}'s ${x.leaveTypeName}` +
        `${x.shiftsCancelled ? `, ${x.shiftsCancelled} ${x.shiftsCancelled === 1 ? 'shift' : 'shifts'} taken off the roster` : ''}.`, 'OK', { duration: 6000 });
      this.load();
    });
  }

  reject(r: LeaveRequest) {
    const data: ReasonData<LeaveRequest> = {
      title: `Refuse ${r.code}?`, intro: `${r.employeeName} sees the reason.`, button: 'Refuse', placeholder: 'Two cooks are already off that week',
      run: reason => this.api.reject(this.pid(), r, reason),
    };
    this.dialog.open(ReasonDialog, { data, maxWidth: '100vw' }).afterClosed().subscribe(x => {
      if (!x) return;
      this.snack.open(`${r.code} refused.`, 'OK', { duration: 4000 });
      this.load();
    });
  }

  cancel(r: LeaveRequest) {
    const data: ReasonData<LeaveRequest> = {
      title: `Cancel ${r.code}?`, button: 'Cancel the leave', placeholder: 'Trip postponed',
      intro: r.status === 'APPROVED' ? `The ${days(r.days)} go back to the balance. Shifts taken off the roster for it are not put back.` : 'It is taken off the approval list.',
      run: reason => this.api.cancel(this.pid(), r, reason),
    };
    this.dialog.open(ReasonDialog, { data, maxWidth: '100vw' }).afterClosed().subscribe(x => {
      if (!x) return;
      this.snack.open(`${r.code} cancelled.`, 'OK', { duration: 4000 });
      this.load();
    });
  }

  ledger(employeeId: string, canAdjust: boolean) {
    this.dialog.open(LedgerDialog, { data: { propertyId: this.pid(), employeeId, canAdjust, types: this.types() }, maxWidth: '100vw' })
      .afterClosed().subscribe(changed => { if (changed) this.load(); });
  }

  accrue() {
    const today = this.mine()?.today ?? new Date().toISOString().slice(0, 10);
    this.api.runAccrual(this.pid(), today.slice(0, 7)).subscribe({
      next: x => {
        this.snack.open(x.posted ? `${x.posted} entries, ${x.days} days added for ${x.month}.${x.notes.length ? ' ' + x.notes.join(' ') : ''}`
          : `Nothing to add: this month's leave was already there (${x.alreadyPosted} entries).`, 'OK', { duration: 6000 });
        this.load();
      },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  holiday(h: Holiday | null) {
    this.dialog.open(HolidayDialog, { data: { propertyId: this.pid(), holiday: h }, maxWidth: '100vw' }).afterClosed().subscribe((x?: Holiday) => {
      if (!x) return;
      this.snack.open(`${x.name} on ${dayText(x.date)} saved.`, 'OK', { duration: 4000 });
      const y = Number(x.date.slice(0, 4));
      if (y !== this.year()) this.year.set(y); else this.loadHolidays(y);
    });
  }

  editType(t: LeaveType | null) {
    this.dialog.open(LeaveTypeDialog, { data: { propertyId: this.pid(), type: t }, maxWidth: '100vw' }).afterClosed().subscribe((x?: LeaveType) => {
      if (!x) return;
      this.snack.open(`${x.name} saved.`, 'OK', { duration: 4000 });
      this.load();
    });
  }
}
