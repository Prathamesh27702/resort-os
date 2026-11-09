import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { Observable } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { CostCentre, Expense, ExpenseCategory, ExpenseList, ExpenseReceipt, ExpenseSetup } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { MAX_PHOTO_BYTES, compressPhoto } from '../housekeeping/housekeeping.api';
import { short } from '../leave/leave.api';
import { localToday } from '../maintenance/maintenance.api';
import { ReasonData, ReasonDialog } from '../restaurant/order-dialogs';
import { ExpenseDialog, ExpenseSaved, ExpenseSetupDialog } from './expense-dialogs';
import { EXPENSE_STATUS, ExpenseFilter, ExpensesApi, PAYMENT_METHOD, inr, yearOf } from './expenses.api';

type Show = 'waiting' | 'all' | 'mine';
type Period = 'month' | 'last' | 'year' | 'any';

/**
 * Expenses (Sprint 22, brief §40). Anyone who records expenses adds one from a phone with a photo of the receipt; the list
 * shows what waits for approval first. Someone who approves expenses opens one, looks at the receipt and the GST, and
 * approves or rejects it (with the reason); only approved expenses count in the budget. The owner keeps the categories
 * and departments.
 */
@Component({
  selector: 'app-expenses',
  imports: [RouterLink, MatButtonModule, MatButtonToggleModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatSelectModule, MatSnackBarModule],
  template: `
    <div class="page exp">
      <div class="head">
        <h1>Expenses</h1>
        <span class="grow"></span>
        @if (canBudget()) { <a mat-button routerLink="/budgets"><mat-icon>insights</mat-icon>Budget</a> }
        @if (canCreate()) { <button mat-flat-button (click)="record()" [disabled]="!setup()"><mat-icon>add_a_photo</mat-icon>Record expense</button> }
      </div>

      @if (view() === 'list') {
        <div class="bar">
          <mat-button-toggle-group [value]="show()" (change)="show.set($event.value)" aria-label="Show">
            <mat-button-toggle value="waiting">Waiting@if (list()?.waiting) { ({{ list()!.waiting }}) }</mat-button-toggle>
            <mat-button-toggle value="all">All</mat-button-toggle>
            <mat-button-toggle value="mine">Mine</mat-button-toggle>
          </mat-button-toggle-group>
          <mat-form-field appearance="outline" subscriptSizing="dynamic" class="sel">
            <mat-label>When</mat-label>
            <mat-select [value]="period()" (selectionChange)="period.set($event.value)" [aria-label]="'When'">
              <mat-option value="month">This month</mat-option>
              <mat-option value="last">Last month</mat-option>
              <mat-option value="year">This financial year</mat-option>
              <mat-option value="any">Any time</mat-option>
            </mat-select>
          </mat-form-field>
          <mat-form-field appearance="outline" subscriptSizing="dynamic" class="sel">
            <mat-label>Department</mat-label>
            <mat-select [value]="centre()" (selectionChange)="centre.set($event.value)" [aria-label]="'Department'">
              <mat-option value="">All departments</mat-option>
              @for (c of setup()?.costCentres ?? []; track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <span class="grow"></span>
          @if (setup()?.canManage) { <button mat-button (click)="view.set('setup')"><mat-icon>tune</mat-icon>Categories and departments</button> }
        </div>

        @if (list(); as l) {
          <p class="sum" data-testid="totals">{{ l.items.length }} {{ l.items.length === 1 ? 'expense' : 'expenses' }} · {{ inr(l.total) }} paid
            @if (l.costTotal !== l.total) { · {{ inr(l.costTotal) }} cost after GST credit }
            @if (l.waiting && show() !== 'waiting') { · <b>{{ l.waiting }} waiting for approval ({{ inr(l.waitingAmount) }})</b> }</p>
        }
        <ul class="items" data-testid="expenses">
          @for (e of list()?.items ?? []; track e.id) {
            <li [attr.data-expense]="e.code" [class.open]="openId() === e.id" [class.off]="e.status === 'REJECTED' || e.status === 'CANCELLED'">
              <button class="row" type="button" (click)="toggle(e)" [attr.aria-expanded]="openId() === e.id">
                <span class="day">{{ short(e.expenseDate) }}</span>
                <span class="what"><b>{{ e.description }}</b>
                  <small class="mute">{{ e.payee }} · {{ e.costCentreName }} · {{ e.categoryName }}</small></span>
                <span class="amt"><b>{{ inr(e.amount) }}</b>
                  <small class="chip" [class]="e.status.toLowerCase()">{{ statusText[e.status] }}</small>
                  @if (e.needsReceipt) { <small class="need"><mat-icon inline>receipt_long</mat-icon> needs receipt</small> }</span>
              </button>
              @if (openId() === e.id) {
                <div class="detail">
                  <dl>
                    <dt>Number</dt><dd>{{ e.code }}</dd>
                    <dt>Paid</dt><dd>{{ inr(e.amount) }} by {{ method[e.paymentMethod] }}@if (e.paymentReference) { ({{ e.paymentReference }}) }</dd>
                    @if (e.vendorGstin) {
                      <dt>GST bill</dt><dd>{{ e.billNumber }} · GSTIN {{ e.vendorGstin }}</dd>
                      <dt>GST</dt><dd>@if (e.igst) { IGST {{ inr(e.igst) }} } @else { CGST {{ inr(e.cgst) }} + SGST {{ inr(e.sgst) }} } on {{ inr(e.taxableValue) }}
                        · {{ e.itcEligible ? 'claimable as input credit' : 'not claimable' }}</dd>
                    }
                    <dt>Counts in the budget</dt><dd>{{ inr(e.costAmount) }}{{ e.status === 'APPROVED' ? '' : ' once approved' }}</dd>
                    <dt>Recorded</dt><dd>by {{ e.recordedBy ?? 'someone' }}</dd>
                    @if (e.decidedAt) { <dt>{{ statusText[e.status] }}</dt><dd>by {{ e.decidedBy }}@if (e.decisionReason) { : {{ e.decisionReason }} }</dd> }
                    @if (e.note) { <dt>Note</dt><dd>{{ e.note }}</dd> }
                  </dl>
                  <div class="receipts" data-testid="receipts">
                    @for (r of e.receipts; track r.id) {
                      <button type="button" class="thumb" (click)="openReceipt(e, r)" [attr.aria-label]="'Open ' + r.fileName">
                        @if (thumbs()[r.id]; as url) { <img [src]="url" [alt]="r.fileName" /> }
                        @else { <mat-icon>{{ r.contentType === 'application/pdf' ? 'picture_as_pdf' : 'image' }}</mat-icon><small>{{ r.fileName }}</small> }
                      </button>
                    } @empty { <span class="mute">No receipt.</span> }
                    @if (e.actions.includes('receipt')) {
                      <label class="add">
                        <mat-icon>add_a_photo</mat-icon><span>Add receipt</span>
                        <input type="file" accept="image/*,application/pdf" capture="environment" (change)="addReceipt(e, $event)" data-testid="add-receipt" />
                      </label>
                    }
                  </div>
                  @if (e.blocked['approve']) { <p class="why" role="note">{{ e.blocked['approve'] }}</p> }
                  <div class="actions">
                    @if (e.actions.includes('approve')) { <button mat-flat-button (click)="approve(e)"><mat-icon>check</mat-icon>Approve</button> }
                    @if (e.actions.includes('reject')) { <button mat-stroked-button color="warn" (click)="reject(e)">Reject</button> }
                    @if (e.actions.includes('edit')) { <button mat-stroked-button (click)="edit(e)">Change</button> }
                    @if (e.actions.includes('cancel')) { <button mat-button (click)="cancel(e)">Cancel it</button> }
                  </div>
                </div>
              }
            </li>
          } @empty {
            <li class="mute empty">{{ show() === 'waiting' ? 'Nothing waits for approval.' : 'No expenses for this choice.' }}</li>
          }
        </ul>
      } @else {
        <div class="bar"><button mat-button (click)="view.set('list')"><mat-icon>arrow_back</mat-icon>Expenses</button></div>
        <h2>Departments</h2>
        <p class="mute">Each department has its own budget. Expenses are recorded against one.</p>
        <div class="bar"><span class="grow"></span><button mat-flat-button (click)="editItem('centre', null)"><mat-icon>add</mat-icon>New department</button></div>
        <ul class="list" data-testid="centres">
          @for (c of setup()?.costCentres ?? []; track c.id) {
            <li [class.off]="!c.isActive"><div class="main"><b>{{ c.name }}</b> <small class="mute">{{ c.code }}{{ c.isActive ? '' : ' · not in use' }}</small></div>
              <button mat-button (click)="editItem('centre', c)">Change</button></li>
          }
        </ul>
        <h2>Categories</h2>
        <ul class="list" data-testid="categories">
          @for (c of setup()?.categories ?? []; track c.id) {
            <li [class.off]="!c.isActive"><div class="main"><b>{{ c.name }}</b>
              <small class="mute">{{ c.code }}{{ c.gstClaimable ? '' : ' · GST not claimable' }}{{ c.isActive ? '' : ' · not in use' }}</small></div>
              <button mat-button (click)="editItem('category', c)">Change</button></li>
          }
        </ul>
        <div class="bar"><span class="grow"></span><button mat-flat-button (click)="editItem('category', null)"><mat-icon>add</mat-icon>New category</button></div>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>
  `,
  styles: `
    .exp { max-width: 900px; padding-bottom: 60px; }
    .head, .bar { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; } .bar { margin-top: var(--space-2); }
    .grow { flex: 1; } .mute { color: var(--ro-mute); } .err { color: var(--ro-error); } h2 { margin: 18px 0 0; font-size: 1.1rem; }
    .sel { width: 180px; } .sum { margin: 10px 0 var(--space-1); color: var(--ro-ink-soft); }
    .items, .list { list-style: none; margin: var(--space-2) 0 0; padding: 0; }
    .items > li { background: var(--ro-paper); border: 1px solid var(--ro-line); border-radius: 14px; margin-bottom: var(--space-2); overflow: hidden; }
    .items > li.off { opacity: .7; } .items > li.empty { padding: 14px; }
    .row { display: flex; gap: var(--space-3); align-items: center; width: 100%; padding: 10px 14px; background: none; border: 0; text-align: left; cursor: pointer;
           font: inherit; color: inherit; } .row:hover { background: #f4f8f7; }
    .day { min-width: 76px; font-weight: 600; color: var(--ro-tide); font-size: .9rem; }
    .what { flex: 1; display: flex; flex-direction: column; min-width: 0; }
    .what b { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .amt { display: flex; flex-direction: column; align-items: flex-end; gap: 2px; }
    .chip { border-radius: 10px; padding: 1px var(--space-2); font-size: .75rem; background: #fff4d6; color: #6b4e00; }
    .chip.approved { background: #e2f3e8; color: #1b6e3a; } .chip.rejected, .chip.cancelled { background: var(--ro-off-bg); color: #8a1c1c; }
    .need { color: var(--ro-warn); font-size: .75rem; }
    .detail { border-top: 1px solid var(--ro-sunk); padding: var(--space-2) 14px var(--space-3); }
    dl { display: grid; grid-template-columns: max-content 1fr; gap: 2px var(--space-3); margin: var(--space-1) 0 var(--space-2); } dt { color: var(--ro-mute); } dd { margin: 0; }
    .receipts { display: flex; gap: var(--space-2); flex-wrap: wrap; align-items: center; margin: 6px 0; }
    .thumb { width: 84px; height: 84px; border: 1px solid var(--ro-line); border-radius: 8px; background: var(--ro-paper); padding: 0; overflow: hidden; cursor: pointer;
             display: flex; flex-direction: column; align-items: center; justify-content: center; color: var(--ro-ink-soft); }
    .thumb img { width: 100%; height: 100%; object-fit: cover; } .thumb small { font-size: .7rem; max-width: 76px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .add { position: relative; width: 84px; height: 84px; border: 2px dashed var(--ro-outline); border-radius: 8px; display: flex; flex-direction: column;
           align-items: center; justify-content: center; color: var(--ro-ink-soft); font-size: .8rem; cursor: pointer; text-align: center; }
    .add input { position: absolute; width: 1px; height: 1px; opacity: 0; }
    .why { background: #fff4d6; color: #6b4e00; border-radius: 8px; padding: 6px 10px; margin: 6px 0; }
    .actions { display: flex; gap: var(--space-2); flex-wrap: wrap; margin-top: 6px; } .actions button { min-height: 44px; }
    .list { background: var(--ro-paper); border: 1px solid var(--ro-line); border-radius: 14px; }
    .list li { display: flex; gap: var(--space-2); align-items: center; padding: 10px 14px; border-top: 1px solid var(--ro-sunk); } .list li:first-child { border-top: 0; }
    .list li.off { opacity: .6; } .main { flex: 1; display: flex; flex-direction: column; }
    @media (max-width: 560px) { .sel { width: calc(50% - 4px); } .day { min-width: 54px; font-size: .8rem; } }
  `,
})
export class ExpensesPage {
  private api = inject(ExpensesApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  inr = inr; short = short; statusText = EXPENSE_STATUS; method = PAYMENT_METHOD;

  private pid = computed(() => this.ctx.current()?.id ?? '');
  canCreate = computed(() => this.auth.can('expense.create', this.pid()));
  canBudget = computed(() => this.auth.can('budget.read', this.pid()));
  private canApprove = computed(() => this.auth.can('expense.approve', this.pid()));
  private today = computed(() => localToday(this.ctx.current()?.timezone ?? 'Asia/Kolkata'));
  view = signal<'list' | 'setup'>('list');
  /** An approver starts on what waits for them; everyone else on everything. */
  show = signal<Show>('all');
  period = signal<Period>('year');
  centre = signal('');
  setup = signal<ExpenseSetup | null>(null);
  list = signal<ExpenseList | null>(null);
  openId = signal<string | null>(null);
  thumbs = signal<Record<string, string>>({});
  error = signal('');

  constructor() {
    effect(() => {
      if (!this.pid()) return;
      untracked(() => {
        if (this.canApprove()) this.show.set('waiting');
        this.api.setup(this.pid()).subscribe({ next: s => this.setup.set(s), error: e => this.error.set(toProblem(e).detail) });
      });
    });
    effect(() => {
      const f = this.filter();
      if (this.pid()) untracked(() => this.load(f));
    });
  }

  private filter = computed<ExpenseFilter>(() => {
    const today = this.today();
    const [y, m] = today.split('-').map(Number);
    const first = (yy: number, mm: number) => `${yy}-${String(mm).padStart(2, '0')}-01`;
    const range = this.period() === 'month' ? { from: first(y, m) }
      : this.period() === 'last' ? { from: m === 1 ? first(y - 1, 12) : first(y, m - 1), to: (() => { const d = new Date(Date.UTC(y, m - 1, 0)); return d.toISOString().slice(0, 10); })() }
      : this.period() === 'year' ? { from: `${yearOf(today)}-04-01` }
      : {};
    return { ...range, status: this.show() === 'waiting' ? 'WAITING' : undefined, mine: this.show() === 'mine', costCentreId: this.centre() || undefined };
  });

  load(f: ExpenseFilter = this.filter()) {
    this.error.set('');
    this.api.list(this.pid(), f).subscribe({
      next: l => {
        this.list.set(l);
        const open = l.items.find(e => e.id === this.openId());
        if (open) this.loadThumbs(open); else this.openId.set(null);
      },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  toggle(e: Expense) {
    if (this.openId() === e.id) { this.openId.set(null); return; }
    this.openId.set(e.id);
    this.loadThumbs(e);
  }

  /** Photos show as small pictures (fetched with the sign-in, so not by a plain link); PDFs show as an icon. */
  private loadThumbs(e: Expense) {
    for (const r of e.receipts.filter(x => x.contentType.startsWith('image/') && !this.thumbs()[x.id]))
      this.api.receipt(this.pid(), e.id, r.id).subscribe({ next: b => this.thumbs.update(t => ({ ...t, [r.id]: URL.createObjectURL(b) })) });
  }

  openReceipt(e: Expense, r: ExpenseReceipt) {
    this.api.receipt(this.pid(), e.id, r.id).subscribe({
      next: b => { const url = URL.createObjectURL(b); window.open(url, '_blank'); setTimeout(() => URL.revokeObjectURL(url), 60_000); },
      error: err => this.error.set(toProblem(err).detail),
    });
  }

  record() { this.openForm(null); }
  edit(e: Expense) { this.openForm(e); }

  private openForm(e: Expense | null) {
    const setup = this.setup();
    if (!setup) return;
    this.dialog.open(ExpenseDialog, { data: { propertyId: this.pid(), setup, today: this.today(), expense: e }, maxWidth: '100vw' })
      .afterClosed().subscribe((r?: ExpenseSaved) => {
        if (!r) return;
        const x = r.expense;
        const msg = r.failed.length ? `${x.code} saved, but a receipt wasn't added. ${r.failed[0]}`
          : e ? `${x.code} saved.`
          : `${x.code} recorded: ${inr(x.amount)} to ${x.payee}. ${x.needsReceipt ? 'Add the receipt before it can be approved.' : 'It waits for approval.'}`;
        this.snack.open(msg, 'OK', { duration: r.failed.length ? 10000 : 5000 });
        this.openId.set(x.id);
        this.load();
      });
  }

  async addReceipt(e: Expense, ev: Event) {
    const input = ev.target as HTMLInputElement;
    const f = input.files?.[0];
    input.value = '';
    if (!f) return;
    this.error.set('');
    const pdf = f.type === 'application/pdf';
    if (!pdf && !f.type.startsWith('image/')) { this.error.set(`${f.name} isn't a photo or a PDF. Choose a JPEG, PNG or WebP photo, or a PDF bill.`); return; }
    const blob = pdf ? f : await compressPhoto(f);
    if (blob.size > MAX_PHOTO_BYTES) { this.error.set(`${f.name} is ${(blob.size / 1024 / 1024).toFixed(1)} MB; at most 2 MB.`); return; }
    this.run(this.api.addReceipt(this.pid(), e.id, blob, pdf ? f.name : f.name.replace(/\.[^.]+$/, '') + '.jpg'), () => `Receipt added to ${e.code}.`);
  }

  approve(e: Expense) {
    this.run(this.api.approve(this.pid(), e), x => `${x.code} approved: ${inr(x.costAmount)} counts in ${x.costCentreName}'s budget.`);
  }

  reject(e: Expense) {
    const data: ReasonData<Expense> = {
      title: `Reject ${e.code}?`, button: 'Reject', placeholder: 'No bill, or paid from the owner\'s account',
      intro: `${inr(e.amount)} to ${e.payee}. It won't count in the budget; ${e.recordedBy ?? 'the person who recorded it'} sees the reason.`,
      run: reason => this.api.reject(this.pid(), e, reason),
    };
    this.dialog.open(ReasonDialog, { data, maxWidth: '100vw' }).afterClosed().subscribe(x => {
      if (!x) return;
      this.snack.open(`${e.code} rejected.`, 'OK', { duration: 4000 });
      this.load();
    });
  }

  cancel(e: Expense) {
    const data: ReasonData<Expense> = {
      title: `Cancel ${e.code}?`, button: 'Cancel it', placeholder: 'Entered twice', intro: `${inr(e.amount)} to ${e.payee} is withdrawn; it won't be approved.`,
      run: reason => this.api.cancel(this.pid(), e, reason),
    };
    this.dialog.open(ReasonDialog, { data, maxWidth: '100vw' }).afterClosed().subscribe(x => {
      if (!x) return;
      this.snack.open(`${e.code} cancelled.`, 'OK', { duration: 4000 });
      this.load();
    });
  }

  editItem(kind: 'category' | 'centre', item: ExpenseCategory | CostCentre | null) {
    this.dialog.open(ExpenseSetupDialog, { data: { propertyId: this.pid(), kind, item }, maxWidth: '100vw' }).afterClosed()
      .subscribe((x?: ExpenseCategory | CostCentre) => {
        if (!x) return;
        this.snack.open(`${x.name} saved.`, 'OK', { duration: 3000 });
        this.api.setup(this.pid()).subscribe({ next: s => this.setup.set(s) });
      });
  }

  private run<T>(call: Observable<T>, done: (x: T) => string) {
    this.error.set('');
    call.subscribe({
      next: x => { this.snack.open(done(x), 'OK', { duration: 5000 }); this.load(); },
      error: err => { this.error.set(toProblem(err).detail); this.load(); },
    });
  }
}
