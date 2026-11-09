import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { Budget, BudgetReport, BudgetStanding, BudgetSummary, BudgetVarianceRow } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { short } from '../leave/leave.api';
import { localToday } from '../maintenance/maintenance.api';
import { BudgetRowDialog, NewBudgetDialog } from './expense-dialogs';
import { ExpensesApi, STANDING, inr, monthShort, varianceText, yearLabel, yearOf } from './expenses.api';

const ICON: Record<BudgetStanding, string> = { ON_TRACK: 'check_circle', OVER: 'error', HEADING_OVER: 'trending_up', NO_BUDGET: 'help' };

/**
 * Budget against actual (Sprint 22, brief §41). For the financial year chosen, from April to the month chosen: each
 * department's budget so far, what it actually spent (approved expenses only, their cost after GST credit), the variance and
 * where the year is heading at this rate (Sprint 23: supplier bills and, for the owner and HR, pay count too), with what still waits for approval beside it. The owner plans each department's
 * months here. The months chart puts the plan and the spending side by side.
 */
@Component({
  selector: 'app-budgets',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatSelectModule, MatSnackBarModule],
  template: `
    <div class="page bud">
      <div class="head">
        <h1>Budget</h1>
        <span class="grow"></span>
        <a mat-button routerLink="/expenses"><mat-icon>receipt_long</mat-icon>Expenses</a>
      </div>
      <div class="bar">
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="sel">
          <mat-label>Financial year</mat-label>
          <mat-select [value]="year()" (selectionChange)="pickYear($event.value)" [aria-label]="'Financial year'">
            @for (y of years(); track y) { <mat-option [value]="y">{{ label(y) }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="sel">
          <mat-label>Up to the end of</mat-label>
          <mat-select [value]="through()" (selectionChange)="through.set($event.value)" [aria-label]="'Up to the end of'">
            @for (m of report()?.months ?? []; track m.month) { <mat-option [value]="m.month">{{ monthShort(m.month, true) }}</mat-option> }
          </mat-select>
        </mat-form-field>
      </div>

      @if (report(); as r) {
        @if (!r.budgetId) {
          <div class="none" role="status">
            <p><b>No budget for {{ r.label }} yet.</b> The spending below is compared with nothing.</p>
            @if (canManage()) { <button mat-flat-button (click)="make()"><mat-icon>add</mat-icon>Make the budget</button> }
          </div>
        }
        <div class="tiles" data-testid="tiles">
          <div class="tile"><small>Budget, April to {{ monthShort(r.through) }}</small><b>{{ inr(r.total.budgetToDate) }}</b></div>
          <div class="tile"><small>Spent</small><b>{{ inr(r.total.actualToDate) }}</b></div>
          <div class="tile" [class.bad]="r.total.variance < 0"><small>Variance</small><b>{{ varianceText(r.total.variance) }}</b></div>
          <div class="tile"><small>Year at this rate</small><b>{{ r.total.forecast === null ? '—' : inr(r.total.forecast) }}</b>
            <small>of {{ inr(r.total.annualBudget) }} planned</small></div>
        </div>
        @if (r.total.waitingCount) {
          <p class="wait"><mat-icon inline>hourglass_top</mat-icon> {{ r.total.waitingCount }} {{ r.total.waitingCount === 1 ? 'expense' : 'expenses' }}
            ({{ inr(r.total.waiting) }}) wait for approval and are not counted yet. <a routerLink="/expenses">See them</a></p>
        }

        <div class="table" role="table" aria-label="Budget against actual by department" data-testid="departments">
          <div class="tr th" role="row">
            <span role="columnheader">Department</span><span role="columnheader">Budget</span><span role="columnheader">Spent</span>
            <span role="columnheader">Variance</span><span role="columnheader">Year at this rate</span><span role="columnheader"></span>
          </div>
          @for (d of r.rows; track d.costCentreId) {
            <div class="tr" role="row" [attr.data-centre]="d.costCentreCode" [class.open]="openId() === d.costCentreId">
              <span role="cell" class="name">
                <button type="button" class="link" (click)="toggle(d)" [attr.aria-expanded]="openId() === d.costCentreId">{{ d.costCentreName }}</button>
                <small class="stand" [class]="d.standing.toLowerCase()"><mat-icon inline>{{ icon[d.standing] }}</mat-icon> {{ standing[d.standing] }}</small>
              </span>
              <span role="cell" class="num" data-label="Budget">{{ inr(d.budgetToDate) }}</span>
              <span role="cell" class="num" data-label="Spent">{{ inr(d.actualToDate) }}
                <span class="meter" [attr.aria-hidden]="true"><span [class.over]="d.actualToDate > d.budgetToDate"
                  [style.width.%]="d.budgetToDate ? Math.min(100, 100 * d.actualToDate / d.budgetToDate) : (d.actualToDate ? 100 : 0)"></span></span>
              </span>
              <span role="cell" class="num" data-label="Variance" [class.bad]="d.variance < 0">{{ varianceText(d.variance) }}
                @if (d.variancePercent !== null && d.variance !== 0) { <small>{{ Math.abs(d.variancePercent) }} %</small> }</span>
              <span role="cell" class="num" data-label="Year at this rate">{{ d.forecast === null ? '—' : inr(d.forecast) }}<small>of {{ inr(d.annualBudget) }}</small></span>
              <span role="cell" class="act">
                @if (canManage() && budget() && d.costCentreCode !== 'NONE') { <button mat-stroked-button (click)="plan(d)">Plan</button> }
              </span>
              @if (openId() === d.costCentreId) {
                <div class="more" role="cell">
                  <p>{{ monthShort(r.through, true) }}: {{ inr(d.monthActual) }} spent of {{ inr(d.monthBudget) }} planned.
                    @if (d.waitingCount) { {{ inr(d.waiting) }} waits for approval. }</p>
                  <p class="split" data-testid="split">This year: expenses {{ inr(d.expenses) }} · supplier bills {{ inr(d.supplierBills) }}@if (r.payIncluded) { · pay {{ inr(d.pay ?? 0) }} }</p>
                  @if (d.categories.length) {
                    <ul class="cats">@for (c of d.categories; track c.categoryId) { <li><span>{{ c.name }}</span><b>{{ inr(c.actual) }}</b></li> }</ul>
                  } @else { <p class="mute">Nothing approved yet this year.</p> }
                </div>
              }
            </div>
          }
        </div>

        <h2>Month by month</h2>
        <div class="legend"><span class="key plan"></span>Budget <span class="key spent"></span>Spent</div>
        <div class="chart" role="img" [attr.aria-label]="chartLabel()" data-testid="months">
          @for (m of r.months; track m.month) {
            <div class="col" [class.after]="m.month > r.through" [title]="monthShort(m.month, true) + ': budget ' + inr(m.budget) + ', spent ' + inr(m.actual)">
              <div class="bars">
                <span class="b plan" [style.height.%]="pct(m.budget)"></span>
                <span class="b spent" [style.height.%]="pct(m.actual)"></span>
              </div>
              <small>{{ monthShort(m.month) }}</small>
            </div>
          }
        </div>
        <details class="tablev"><summary>The months as a table</summary>
          <table><thead><tr><th>Month</th><th>Budget</th><th>Spent</th></tr></thead>
            <tbody>@for (m of r.months; track m.month) { <tr><td>{{ monthShort(m.month, true) }}</td><td>{{ inr(m.budget) }}</td><td>{{ inr(m.actual) }}</td></tr> }</tbody>
          </table>
        </details>
        <p class="mute small" data-testid="footnote">Spent is approved expenses, supplier bills (by the bill's date, in the department chosen on the bill)
          @if (r.payIncluded) { and pay (by each person's department) } at their cost: when the GST on a bill is claimed back as input credit, only the taxable value.
          @if (!r.payIncluded) { Pay is left out: only the owner and HR see pay. }
          "No department" is bills and pay that are not set to a department yet.
          "Year at this rate" is the spending so far spread over the whole year (up to {{ short(r.asOf) }}).</p>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>
  `,
  styles: `
    .bud { max-width: 1000px; padding-bottom: 60px; }
    .head, .bar { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; } .grow { flex: 1; } .sel { width: 190px; }
    .mute { color: var(--ro-mute); } .small { font-size: .85rem; } .err { color: var(--ro-error); } h2 { font-size: 1.1rem; margin: 20px 0 4px; }
    .none { background: #fff4d6; border-radius: 12px; padding: 10px 14px; margin: 10px 0; display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
    .none p { margin: 0; flex: 1; }
    .tiles { display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px; margin: 12px 0 6px; }
    .tile { background: var(--ro-paper); border: 1px solid var(--ro-line); border-radius: 12px; padding: 10px 12px; display: flex; flex-direction: column; }
    .tile small { color: var(--ro-mute); } .tile b { font-size: 1.25rem; color: var(--ro-tide); } .tile.bad b { color: #8a1c1c; }
    .wait { color: #6b4e00; margin: 4px 0; }
    .table { background: var(--ro-paper); border: 1px solid var(--ro-line); border-radius: 14px; margin-top: 10px; }
    .tr { display: grid; grid-template-columns: 2fr 1fr 1.2fr 1.2fr 1.3fr 80px; gap: 8px; align-items: center; padding: 10px 14px; border-top: 1px solid var(--ro-sunk); }
    .tr:first-child { border-top: 0; } .th { color: var(--ro-mute); font-size: .85rem; }
    .name { display: flex; flex-direction: column; align-items: flex-start; }
    .link { background: none; border: 0; padding: 0; font: inherit; font-weight: 600; color: var(--ro-tide); cursor: pointer; text-align: left; }
    .stand { font-size: .78rem; display: inline-flex; align-items: center; gap: 2px; color: #1b6e3a; }
    .stand.over { color: #8a1c1c; } .stand.heading_over { color: var(--ro-warn); } .stand.no_budget { color: var(--ro-mute); }
    .num { display: flex; flex-direction: column; font-variant-numeric: tabular-nums; } .num small { color: var(--ro-mute); font-size: .78rem; }
    .num.bad { color: #8a1c1c; }
    .meter { width: 100%; max-width: 110px; height: 5px; background: var(--ro-sunk); border-radius: 3px; margin-top: 3px; overflow: hidden; }
    .meter span { display: block; height: 100%; background: #0090a0; border-radius: 3px; } .meter span.over { background: var(--ro-error); }
    .act { text-align: right; }
    .more { grid-column: 1 / -1; background: var(--ro-wash); border-radius: 10px; padding: 8px 12px; } .more p { margin: 0 0 6px; } .split { color: var(--ro-ink-soft); font-size: .88rem; }
    .cats { list-style: none; margin: 0; padding: 0; columns: 2; } .cats li { display: flex; justify-content: space-between; gap: 8px; padding: 2px 0; break-inside: avoid; }
    .legend { display: flex; gap: 6px; align-items: center; color: var(--ro-ink-soft); font-size: .85rem; }
    .key { display: inline-block; width: 12px; height: 12px; border-radius: 3px; margin-left: 8px; }
    .key.plan, .b.plan { background: #b7791f; } .key.spent, .b.spent { background: #0090a0; }
    .chart { display: grid; grid-template-columns: repeat(12, 1fr); gap: 4px; height: 160px; margin-top: 8px; padding-bottom: 4px;
             border-bottom: 1px solid var(--ro-line); }
    .col { display: flex; flex-direction: column; align-items: center; gap: 4px; min-width: 0; } .col.after { opacity: .55; }
    .bars { flex: 1; width: 100%; display: flex; align-items: flex-end; justify-content: center; gap: 2px; }
    .b { width: 40%; max-width: 14px; border-radius: 4px 4px 0 0; min-height: 0; }
    .col small { color: var(--ro-mute); font-size: .72rem; }
    .tablev { margin: 8px 0; } .tablev table { border-collapse: collapse; } .tablev td, .tablev th { padding: 2px 12px 2px 0; text-align: left; }
    @media (max-width: 720px) {
      .tiles { grid-template-columns: 1fr 1fr; }
      .th { display: none; }
      .tr { grid-template-columns: 1fr 1fr; }
      .name { grid-column: 1 / -1; flex-direction: row; justify-content: space-between; align-items: center; width: 100%; }
      .num::before { content: attr(data-label); color: var(--ro-mute); font-size: .75rem; }
      .act { grid-column: 1 / -1; text-align: left; } .cats { columns: 1; }
    }
  `,
})
export class BudgetsPage {
  private api = inject(ExpensesApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  inr = inr; label = yearLabel; monthShort = monthShort; varianceText = varianceText; standing = STANDING; icon = ICON; Math = Math; short = short;

  private pid = computed(() => this.ctx.current()?.id ?? '');
  canManage = computed(() => this.auth.can('budget.manage', this.pid()));
  private thisYear = computed(() => yearOf(localToday(this.ctx.current()?.timezone ?? 'Asia/Kolkata')));
  years = computed(() => [this.thisYear() + 1, this.thisYear(), this.thisYear() - 1, ...this.budgets().map(b => b.year)]
    .filter((y, i, a) => a.indexOf(y) === i).sort((a, b) => b - a));
  year = signal<number | null>(null);
  through = signal<string | null>(null);
  report = signal<BudgetReport | null>(null);
  budget = signal<Budget | null>(null);
  budgets = signal<BudgetSummary[]>([]);
  openId = signal<string | null>(null);
  error = signal('');

  private top = computed(() => Math.max(1, ...(this.report()?.months ?? []).flatMap(m => [m.budget, m.actual])));
  chartLabel = computed(() => (this.report()?.months ?? []).map(m => `${monthShort(m.month)} budget ${inr(m.budget)} spent ${inr(m.actual)}`).join('; '));

  constructor() {
    effect(() => {
      const y = this.year();
      const t = this.through();
      if (this.pid()) untracked(() => this.load(y, t));
    });
  }

  load(y: number | null = this.year(), t: string | null = this.through()) {
    this.error.set('');
    this.api.budgets(this.pid()).subscribe({ next: b => this.budgets.set(b) });
    this.api.report(this.pid(), y, t).subscribe({
      next: r => {
        this.report.set(r);
        if (this.year() === null) this.year.set(r.year);
        if (this.through() === null) this.through.set(r.through);
        if (r.budgetId) this.api.budget(this.pid(), r.budgetId).subscribe({ next: b => this.budget.set(b) });
        else this.budget.set(null);
      },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  pickYear(y: number) {
    this.through.set(null);
    this.year.set(y);
  }

  pct(v: number) { return Math.round(1000 * v / this.top()) / 10; }

  toggle(d: BudgetVarianceRow) { this.openId.set(this.openId() === d.costCentreId ? null : d.costCentreId); }

  make() {
    const r = this.report();
    if (!r) return;
    this.dialog.open(NewBudgetDialog, { data: { propertyId: this.pid(), year: r.year, budgets: this.budgets() }, maxWidth: '100vw' })
      .afterClosed().subscribe((b?: Budget) => {
        if (!b) return;
        this.snack.open(`${b.name} made${b.total ? ` (${inr(b.total)} for the year)` : ''}. Plan each department with its Plan button.`, 'OK', { duration: 6000 });
        this.load();
      });
  }

  plan(d: BudgetVarianceRow) {
    const b = this.budget();
    if (!b) return;
    this.dialog.open(BudgetRowDialog, { data: { propertyId: this.pid(), budget: b, costCentreId: d.costCentreId }, maxWidth: '100vw' })
      .afterClosed().subscribe((x?: Budget) => {
        if (!x) return;
        const row = x.rows.find(r => r.costCentreId === d.costCentreId);
        this.snack.open(`${d.costCentreName}: ${inr(row?.total ?? 0)} for ${x.label}.`, 'OK', { duration: 4000 });
        this.load();
      });
  }
}
