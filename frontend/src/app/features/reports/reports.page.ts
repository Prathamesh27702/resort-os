import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { Observable } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { fieldErrors, toProblem } from '../../core/http';
import {
  ChannelReport, CommissionRate, CostReport, GstReport, MonthReport, OccupancyReport, PnlReport, ReportFreshness, ReportKpis, RevenueReport,
} from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { localToday } from '../maintenance/maintenance.api';
import { FreshComponent } from './report-bits';
import { ChartComponent, ChartSpec } from './chart';
import { CHANNEL, ReportsApi, change, downloadCsv, monthOf, pc, points, rs, rsShort, toCsv } from './reports.api';

type Tab = 'month' | 'occupancy' | 'revenue' | 'channels' | 'gst' | 'costs' | 'pnl';
const TABS: { key: Tab; label: string; financial: boolean }[] = [
  { key: 'month', label: 'Month at a glance', financial: false },
  { key: 'occupancy', label: 'Occupancy', financial: false },
  { key: 'revenue', label: 'Revenue', financial: true },
  { key: 'channels', label: 'Booking sources', financial: true },
  { key: 'gst', label: 'GST', financial: true },
  { key: 'costs', label: 'Food and labour cost', financial: true },
  { key: 'pnl', label: 'Profit and loss', financial: true },
];
/** The tabs that can be shown by day as well as by month. */
const BY_DAY: Tab[] = ['occupancy', 'revenue'];
/** The tabs whose period is in days (the others are in whole months). */
const DAY_PERIOD: Tab[] = ['occupancy', 'revenue', 'channels'];

/**
 * Reports (Sprint 23, brief §50–55). Every figure comes from the reporting summary, one row per property and day, added up
 * again every few minutes, so these pages never scan the bookings and bills themselves. Money is before GST. Revenue, GST
 * and costs need financial access; pay shows only for the owner and HR. Each report downloads as CSV, and the month at a
 * glance prints on one page.
 */
@Component({
  selector: 'app-reports',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule,
    MatSnackBarModule, MatDatepickerModule, ChartComponent, FreshComponent],
  template: `
    <div class="page rep">
      <div class="head no-print">
        <h1>Reports</h1>
        <span class="grow"></span>
        @if (tab() === 'month') { <button mat-button (click)="print()"><mat-icon>print</mat-icon>Print</button> }
        <button mat-stroked-button (click)="csv()" [disabled]="!loaded()" data-testid="csv"><mat-icon>download</mat-icon>CSV</button>
      </div>
      <nav class="tabs no-print" aria-label="Reports">
        @for (t of tabs(); track t.key) {
          <button type="button" class="tab" [class.on]="tab() === t.key" [attr.aria-current]="tab() === t.key ? 'page' : null" (click)="go(t.key)">{{ t.label }}</button>
        }
      </nav>

      <div class="bar no-print">
        @if (tab() === 'month') {
          <mat-form-field appearance="outline" subscriptSizing="dynamic" class="sel">
            <mat-label>Month</mat-label>
            <mat-select [value]="month() ?? monthOf(today())" (selectionChange)="month.set($event.value)" [aria-label]="'Month'">
              @for (m of monthChoices(); track m.value) { <mat-option [value]="m.value">{{ m.label }}</mat-option> }
            </mat-select>
            @if (err()['month']; as e) { <mat-hint class="bad">{{ e }}</mat-hint> }
          </mat-form-field>
        } @else {
          <mat-form-field appearance="outline" subscriptSizing="dynamic" class="sel">
            <mat-label>{{ dayPeriod() ? 'From' : 'From the month of' }}</mat-label>
            <input matInput [matDatepicker]="dpFrom" [ngModel]="from() ?? shown().from" (dateChange)="setFrom($event.value)" aria-label="From" data-testid="from" />
            <mat-datepicker-toggle matIconSuffix [for]="dpFrom" /><mat-datepicker #dpFrom />
            @if (err()['from']; as e) { <mat-hint class="bad">{{ e }}</mat-hint> }
          </mat-form-field>
          <mat-form-field appearance="outline" subscriptSizing="dynamic" class="sel">
            <mat-label>{{ dayPeriod() ? 'To' : 'To the month of' }}</mat-label>
            <input matInput [matDatepicker]="dpTo" [ngModel]="to() ?? shown().to" (dateChange)="setTo($event.value)" aria-label="To" data-testid="to" />
            <mat-datepicker-toggle matIconSuffix [for]="dpTo" /><mat-datepicker #dpTo />
            @if (err()['to']; as e) { <mat-hint class="bad">{{ e }}</mat-hint> }
          </mat-form-field>
          @if (byDay()) {
            <mat-button-toggle-group [value]="by()" (change)="setBy($event.value)" aria-label="Show by">
              <mat-button-toggle value="day">By day</mat-button-toggle>
              <mat-button-toggle value="month">By month</mat-button-toggle>
            </mat-button-toggle-group>
            @if (err()['by']; as e) { <span class="bad">{{ e }}</span> }
          }
          @if (tab() === 'channels' && canCommission()) { <button mat-button (click)="commission()"><mat-icon>percent</mat-icon>Commission rates</button> }
        }
      </div>
      @if (tab() !== 'month') {
        <div class="chips no-print" role="group" aria-label="Quick periods">
          @for (q of quick(); track q.label) {
            <button type="button" class="qchip" [class.on]="isShown(q)" [attr.aria-pressed]="isShown(q)" (click)="pick(q)">{{ q.label }}</button>
          }
        </div>
      }
      <app-fresh [freshness]="freshness()" [busy]="refreshing()" (refresh)="refresh()" />
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

      <!-- ------------------------------------------------------------ month at a glance -->
      @if (tab() === 'month' && monthR(); as m) {
        <section class="print-page" data-testid="month-report">
          <h2 class="title">{{ propName() }}: {{ labelOf(m.month) }}@if (m.this.to !== monthEndOf(m.month)) { <small> (to {{ dayShort(m.this.to) }})</small> }</h2>
          <div class="mkpis">
            @for (k of headlineRows(); track k.key) {
              <div class="mkpi"><small>{{ k.label }}</small><b class="display">{{ k.value }}</b>
                <small [class]="k.tone">{{ k.vs }} on {{ labelOf(m.previous.from) }}</small></div>
            }
          </div>
          @if (m.access.financial) {
            <div class="charts2 no-print-break">
              <div class="card"><h3>Where the money came from</h3><app-chart [spec]="monthMixSpec()" [height]="220" label="Revenue this month by kind" /></div>
              @if (m.channels?.length) { <div class="card"><h3>Booking sources, net of commission</h3>
                <app-chart [spec]="monthChannelsSpec()" [height]="Math.max(140, 34 * Math.min(m.channels!.length, 6) + 30)" label="Net room revenue by booking source" /></div> }
            </div>
          }
          <table class="rt cmp" aria-label="The month beside the month before and a year ago">
            <thead><tr><th></th><th>{{ labelOf(m.month) }}</th><th>{{ labelOf(m.previous.from) }}</th><th>Change</th><th>{{ labelOf(m.lastYear.from) }}</th><th>Change</th></tr></thead>
            <tbody>
              @for (k of kpiRows(m.access.financial, m.access.pay); track k.label) {
                <tr [attr.data-kpi]="k.key">
                  <th>{{ k.label }}</th><td>{{ k.show(m.this) }}</td><td>{{ k.show(m.previous) }}</td><td [class]="tone(k.delta(m.this, m.previous), k.goodUp)">{{ k.deltaText(m.this, m.previous) }}</td>
                  <td>{{ k.show(m.lastYear) }}</td><td [class]="tone(k.delta(m.this, m.lastYear), k.goodUp)">{{ k.deltaText(m.this, m.lastYear) }}</td>
                </tr>
              }
            </tbody>
          </table>
          @if (m.pnl; as p) {
            <h3>Profit and loss</h3>
            <table class="rt pnl" aria-label="Profit and loss for the month">
              <tbody>@for (l of p.lines; track l.key) { <tr [class]="l.kind.toLowerCase()" [attr.data-line]="l.key"><th>{{ l.label }}</th><td>{{ rs(l.total) }}</td></tr> }</tbody>
            </table>
            @if (!p.payIncluded) { <p class="mute small">Pay is left out: only the owner and HR see pay.</p> }
          }
          <div class="two">
            @if (m.channels; as cs) {
              <div><h3>Booking sources</h3>
                <table class="rt" aria-label="Booking sources"><thead><tr><th>Source</th><th>Nights</th><th>Room revenue</th><th>Net of commission</th></tr></thead>
                  <tbody>@for (c of cs; track c.sourceId) { <tr><th>{{ c.name }}</th><td>{{ c.unitsSold }}</td><td>{{ rs(c.roomRevenue) }}</td><td>{{ rs(c.net) }}</td></tr> }
                    @empty { <tr><td colspan="4" class="mute">No nights sold.</td></tr> }</tbody></table></div>
            }
            @if (m.gst; as g) {
              <div><h3>GST (estimate)</h3>
                <table class="rt" aria-label="GST"><tbody>
                  <tr><th>Output on invoices</th><td>{{ rs(g.outputTotal - g.posUninvoiced) }}</td></tr>
                  <tr><th>Restaurant bills paid at the table</th><td>{{ rs(g.posUninvoiced) }}</td></tr>
                  <tr><th>Input that can be claimed</th><td>{{ rs(g.inputTotal) }}</td></tr>
                  <tr class="total"><th>{{ g.credit ? 'Credit to carry' : 'Payable in cash' }}</th><td>{{ rs(g.credit || g.payable) }}</td></tr>
                  <tr><th class="mute">GST on the guests' bills</th><td class="mute">{{ rs(g.chargedOnBills) }}</td></tr>
                </tbody></table></div>
            }
            @if (m.departments; as ds) {
              <div><h3>Spending by department</h3>
                <table class="rt" aria-label="Spending by department"><thead><tr><th>Department</th><th>Expenses</th><th>Bills</th>@if (m.access.pay) { <th>Pay</th> }<th>Total</th></tr></thead>
                  <tbody>@for (d of ds; track d.code) { <tr><th>{{ d.name }}</th><td>{{ rs(d.expenses) }}</td><td>{{ rs(d.supplierBills) }}</td>@if (m.access.pay) { <td>{{ rs(d.labour) }}</td> }<td>{{ rs(d.total) }}</td></tr> }
                    @empty { <tr><td colspan="5" class="mute">Nothing spent.</td></tr> }</tbody></table></div>
            }
            @if (m.budget; as b) {
              <div><h3>Budget</h3>
                <p>{{ rs(b.spent) }} spent of {{ rs(b.budget) }} planned for {{ labelOf(b.month) }}@if (!b.payIncluded) { (pay not included) }.</p></div>
            }
          </div>
          <p class="mute small">Money is before GST. Occupancy counts a dorm bed as one unit, like a room; ADR is room revenue per unit sold, RevPAR room revenue per unit available.</p>
        </section>
      }

      <!-- ------------------------------------------------------------ occupancy -->
      @if (tab() === 'occupancy' && occ(); as o) {
        <div class="card"><app-chart [spec]="occChart()" [height]="240" label="Occupancy" /></div>
        <div class="scroll"><table class="rt wide" aria-label="Occupancy" data-testid="occupancy">
          <thead><tr><th>{{ o.by === 'day' ? 'Day' : 'Month' }}</th><th>Available</th><th>Sold</th><th>Occupancy</th><th>Out of order</th><th>Guest nights</th>
            <th>Arrivals</th><th>Departures</th>@if (financial()) { <th>Room revenue</th><th>ADR</th><th>RevPAR</th> }</tr></thead>
          <tbody>
            @for (r of o.rows; track r.period) {
              <tr><th>{{ r.label }}</th><td>{{ r.unitsAvailable }}</td><td>{{ r.unitsSold }}</td><td>{{ pc(r.occupancyPercent) }}</td><td>{{ r.outOfOrder }}</td>
                <td>{{ r.guestNights }}</td><td>{{ r.arrivals }}</td><td>{{ r.departures }}</td>
                @if (financial()) { <td>{{ rs(r.roomRevenue) }}</td><td>{{ rs(r.adr) }}</td><td>{{ rs(r.revPar) }}</td> }</tr>
            }
            <tr class="total" data-testid="total"><th>Total</th><td>{{ o.total.unitsAvailable }}</td><td>{{ o.total.unitsSold }}</td><td>{{ pc(o.total.occupancyPercent) }}</td>
              <td>{{ o.total.outOfOrder }}</td><td>{{ o.total.guestNights }}</td><td>{{ o.total.arrivals }}</td><td>{{ o.total.departures }}</td>
              @if (financial()) { <td>{{ rs(o.total.roomRevenue) }}</td><td>{{ rs(o.total.adr) }}</td><td>{{ rs(o.total.revPar) }}</td> }</tr>
          </tbody>
        </table></div>
        <p class="mute small">A unit is a room or a dorm bed. Available leaves out units out of order or under maintenance.
          Rooms {{ o.total.roomsSold }} of {{ o.total.roomsAvailable }}, dorm beds {{ o.total.bedsSold }} of {{ o.total.bedsAvailable }}.</p>
      }

      <!-- ------------------------------------------------------------ revenue -->
      @if (tab() === 'revenue' && rev(); as v) {
        <div class="card"><app-chart [spec]="revChart()" [height]="260" label="Revenue by kind" /></div>
        <div class="scroll"><table class="rt wide" aria-label="Revenue" data-testid="revenue">
          <thead><tr><th>{{ v.by === 'day' ? 'Day' : 'Month' }}</th><th>Rooms</th><th>Food and drink</th><th>Activities</th><th>Transport</th><th>Other</th>
            <th>Total</th><th>GST charged</th><th>Payments received</th><th>Refunds paid</th></tr></thead>
          <tbody>
            @for (r of v.rows; track r.period) {
              <tr><th>{{ r.label }}</th><td>{{ rs(r.room) }}</td><td>{{ rs(r.fnb) }}</td><td>{{ rs(r.activity) }}</td><td>{{ rs(r.transport) }}</td><td>{{ rs(r.other) }}</td>
                <td><b>{{ rs(r.total) }}</b></td><td>{{ rs(r.tax) }}</td><td>{{ rs(r.paymentsReceived) }}</td><td>{{ rs(r.refundsPaid) }}</td></tr>
            }
            <tr class="total" data-testid="total"><th>Total</th><td>{{ rs(v.total.room) }}</td><td>{{ rs(v.total.fnb) }}</td><td>{{ rs(v.total.activity) }}</td>
              <td>{{ rs(v.total.transport) }}</td><td>{{ rs(v.total.other) }}</td><td>{{ rs(v.total.total) }}</td><td>{{ rs(v.total.tax) }}</td>
              <td>{{ rs(v.total.paymentsReceived) }}</td><td>{{ rs(v.total.refundsPaid) }}</td></tr>
          </tbody>
        </table></div>
        <p class="mute small">Revenue is what was charged on the guests' bills for that day of service (and restaurant bills paid at the table), before GST,
          less discounts and corrections.</p>
      }

      <!-- ------------------------------------------------------------ booking sources -->
      @if (tab() === 'channels' && ch(); as c) {
        @if (c.rows.length) {
          <div class="charts2">
            <div class="card"><h3>Net room revenue by source</h3>
              <app-chart [spec]="chChart()" [height]="Math.max(160, 32 * Math.min(c.rows.length, 8) + 30)" label="Net room revenue by booking source" /></div>
            <div class="card"><h3>Nights by kind of source</h3>
              <p class="headline"><b class="display">{{ pc(directShareOf(c)) }}</b> <span class="mute">of nights booked direct</span></p>
              <app-chart [spec]="chGroups()" [height]="200" label="Nights sold by kind of source" /></div>
          </div>
        }
        <div class="scroll"><table class="rt wide" aria-label="Booking sources" data-testid="channels">
          <thead><tr><th>Source</th><th>Kind</th><th>Nights sold</th><th>Share</th><th>Room revenue</th><th>ADR</th><th>Commission</th><th>Net</th></tr></thead>
          <tbody>
            @for (r of c.rows; track r.sourceId) {
              <tr [attr.data-source]="r.code"><th>{{ r.name }}</th><td>{{ channel(r.channel) }}</td><td>{{ r.unitsSold }}</td><td>{{ pc(r.sharePercent) }}</td>
                <td>{{ rs(r.roomRevenue) }}</td><td>{{ rs(r.adr) }}</td><td>{{ rs(r.commission) }} <small class="mute">{{ r.commissionPercent }} %</small></td><td><b>{{ rs(r.net) }}</b></td></tr>
            } @empty { <tr><td colspan="8" class="mute">No nights sold in this period.</td></tr> }
          </tbody>
        </table></div>
        <h3>By kind of source</h3>
        <table class="rt" aria-label="By kind of source" data-testid="groups">
          <thead><tr><th>Kind</th><th>Nights</th><th>Room revenue</th><th>Commission</th><th>Net</th></tr></thead>
          <tbody>
            @for (g of c.groups; track g.channel) { <tr><th>{{ channel(g.channel) }}</th><td>{{ g.unitsSold }}</td><td>{{ rs(g.roomRevenue) }}</td><td>{{ rs(g.commission) }}</td><td>{{ rs(g.net) }}</td></tr> }
            <tr class="total"><th>Total</th><td>{{ c.total.unitsSold }}</td><td>{{ rs(c.total.roomRevenue) }}</td><td>{{ rs(c.total.commission) }}</td><td>{{ rs(c.total.net) }}</td></tr>
          </tbody>
        </table>
        <p class="mute small">Commission is worked out at the source's rate on the room revenue, until the OTAs' own statements come in (Sprint 24).
          The rates are the ones set here, else 18 % for an online travel agent and 10 % for a travel agent.</p>
      }

      <!-- ------------------------------------------------------------ GST -->
      @if (tab() === 'gst' && gst(); as g) {
        <div class="card"><h3>Output and input GST by month</h3><app-chart [spec]="gstChart()" [height]="240" label="Output and input GST by month" /></div>
        <div class="scroll"><table class="rt wide" aria-label="GST by month" data-testid="gst">
          <thead><tr><th>Month</th><th>Taxable (invoices)</th><th>CGST</th><th>SGST</th><th>IGST</th><th>Cess</th><th>At the table</th><th>Output</th>
            <th>Input</th><th>Payable</th><th>Credit</th><th>On the bills</th></tr></thead>
          <tbody>
            @for (r of withTotal(g.rows, g.total); track r.month + r.label) {
              <tr [class.total]="r === g.total"><th>{{ r === g.total ? 'Total' : r.label }}</th><td>{{ rs(r.outputTaxable) }}</td><td>{{ rs(r.outputCgst) }}</td><td>{{ rs(r.outputSgst) }}</td>
                <td>{{ rs(r.outputIgst) }}</td><td>{{ rs(r.outputCess) }}</td><td>{{ rs(r.posUninvoiced) }}</td><td><b>{{ rs(r.outputTotal) }}</b></td>
                <td>{{ rs(r.inputTotal) }}</td><td><b>{{ rs(r.payable) }}</b></td><td>{{ rs(r.credit) }}</td><td class="mute">{{ rs(r.chargedOnBills) }}</td></tr>
            }
          </tbody>
        </table></div>
        <p class="mute small">An estimate for the accountant to check, not a return. Output is GST on invoices and debit notes less credit notes, by their date,
          plus restaurant bills paid at the table (no GST invoice yet). Input is the GST that can be claimed on supplier bills and approved expenses.
          "On the bills" is the GST charged on the guests' bills by the day of service: an invoice is made at check-out, so the two differ until then.</p>
      }

      <!-- ------------------------------------------------------------ costs -->
      @if (tab() === 'costs' && cost(); as c) {
        <div class="card"><h3>Food cost{{ c.payIncluded ? ' and pay' : '' }} as a share</h3><app-chart [spec]="costChart()" [height]="240" label="Food cost percent and pay percent by month" /></div>
        <div class="scroll"><table class="rt wide" aria-label="Food and labour cost by month" data-testid="costs">
          <thead><tr><th>Month</th><th>Revenue</th><th>Restaurant sales</th><th>Food cost</th><th>Food cost %</th><th>Written off</th>
            @if (c.payIncluded) { <th>Pay</th><th>Pay % of revenue</th> }<th>Expenses</th><th>Supplier bills</th></tr></thead>
          <tbody>
            @for (r of withTotal(c.rows, c.total); track r.month + r.label) {
              <tr [class.total]="r === c.total"><th>{{ r === c.total ? 'Total' : r.label }}</th><td>{{ rs(r.revenue) }}</td><td>{{ rs(r.restaurantSales) }}</td><td>{{ rs(r.foodCost) }}</td>
                <td><b>{{ pc(r.foodCostPercent) }}</b></td><td>{{ rs(r.stockWrittenOff) }}</td>
                @if (c.payIncluded) { <td>{{ rs(r.labour) }}</td><td><b>{{ pc(r.labourPercent) }}</b></td> }<td>{{ rs(r.expenses) }}</td><td>{{ rs(r.supplierBills) }}</td></tr>
            }
          </tbody>
        </table></div>
        <h3>By department</h3>
        <table class="rt" aria-label="Spending by department" data-testid="departments">
          <thead><tr><th>Department</th><th>Expenses</th><th>Supplier bills</th>@if (c.payIncluded) { <th>Pay</th> }<th>Total</th></tr></thead>
          <tbody>@for (d of c.departments; track d.code) { <tr [attr.data-centre]="d.code"><th>{{ d.name }}</th><td>{{ rs(d.expenses) }}</td><td>{{ rs(d.supplierBills) }}</td>
            @if (c.payIncluded) { <td>{{ rs(d.labour) }}</td> }<td>{{ rs(d.total) }}</td></tr> }
            @empty { <tr><td colspan="5" class="mute">Nothing spent.</td></tr> }</tbody>
        </table>
        <p class="mute small">Food cost is the stock the restaurant's dishes used, at its cost; food cost % is that of restaurant sales.
          @if (c.payIncluded) { Pay is an estimate: monthly pay spread over the days employed, daily pay for each day worked, before PF and ESI. }
          @else { Pay is left out: only the owner and HR see pay. }</p>
      }

      <!-- ------------------------------------------------------------ P&L -->
      @if (tab() === 'pnl' && pnl(); as p) {
        <div class="card"><h3>Operating profit by month</h3><app-chart [spec]="pnlChart()" [height]="240" label="Operating profit by month" /></div>
        <div class="scroll"><table class="rt wide pnl" aria-label="Profit and loss by month" data-testid="pnl">
          <thead><tr><th></th>@for (l of p.labels; track $index) { <th>{{ l }}</th> }<th>Total</th></tr></thead>
          <tbody>@for (l of p.lines; track l.key) {
            <tr [class]="l.kind.toLowerCase()" [attr.data-line]="l.key"><th>{{ l.label }}</th>@for (v of l.months; track $index) { <td>{{ rs(v) }}</td> }<td>{{ rs(l.total) }}</td></tr>
          }</tbody>
        </table></div>
        <p class="mute small">Management figures before GST. Supplier bills are not a cost here: what they bought counts when the kitchen uses it (food cost)
          or it is written off. @if (!p.payIncluded) { Pay is left out: only the owner and HR see pay. }</p>
      }
    </div>
  `,
  styles: `
    .rep { max-width: 1200px; padding-bottom: 60px; }
    .head, .bar { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; } .grow { flex: 1; } .sel { width: 180px; }
    .tabs { display: flex; gap: var(--space-1); overflow-x: auto; margin: var(--space-3) 0 var(--space-4); padding: 4px; border-radius: 999px;
            background: var(--ro-glass); border: 1px solid var(--ro-glass-edge); width: fit-content; max-width: 100%; box-sizing: border-box; scrollbar-width: none; }
    .tab { background: none; border: 0; border-radius: 999px; padding: var(--space-2) var(--space-4); font: inherit; font-weight: 500; color: var(--ro-ink-soft); cursor: pointer; white-space: nowrap; min-height: 40px; }
    .tab:hover { background: var(--ro-hover); }
    .tab.on { background: var(--ro-accent-grad); color: var(--ro-on-accent); font-weight: 600; box-shadow: var(--ro-accent-glow); }
    .chips { display: flex; gap: var(--space-2); flex-wrap: wrap; margin: var(--space-2) 0; }
    .qchip { border: 1px solid var(--ro-line-strong); background: var(--ro-glass-strong); color: var(--ro-ink-soft); border-radius: 999px; padding: 6px 14px; font: inherit; font-size: .85rem; min-height: 36px; }
    .qchip:hover { border-color: var(--ro-accent); }
    .qchip.on { background: var(--ro-accent-soft); border-color: var(--ro-accent); color: var(--ro-accent-strong); font-weight: 600; }
    .card { background: var(--ro-paper); border: 1px solid var(--ro-glass-edge); border-radius: var(--ro-radius-lg); box-shadow: var(--ro-shadow-card); padding: var(--space-4) var(--space-5); margin: var(--space-3) 0; min-width: 0; }
    .card h3 { margin-top: 0; }
    .charts2 { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 0 var(--space-4); }
    .headline { margin: 0 0 var(--space-1); display: flex; gap: var(--space-2); align-items: baseline; flex-wrap: wrap; } .headline b { font-size: 1.4rem; color: var(--ro-accent-strong); }
    .mkpis { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: var(--space-3); margin: var(--space-3) 0; }
    .mkpi { background: var(--ro-paper); border: 1px solid var(--ro-glass-edge); border-radius: var(--ro-radius); box-shadow: var(--ro-shadow-card); padding: var(--space-3) var(--space-4); display: flex; flex-direction: column; min-width: 0; }
    .mkpi small { color: var(--ro-mute); font-size: .8rem; } .mkpi b { font-size: 1.5rem; font-weight: 600; } .mkpi small.up { color: var(--ro-ok-mid); } .mkpi small.down { color: var(--ro-error-strong); }
    .scroll { background: var(--ro-paper); border: 1px solid var(--ro-glass-edge); border-radius: var(--ro-radius-lg); box-shadow: var(--ro-shadow-card); padding: var(--space-2); }
    .scroll table.rt { background: transparent; }
    .mute { color: var(--ro-mute); } .small { font-size: .85rem; } .err, .bad { color: var(--ro-error); }
    h2.title { margin: var(--space-2) 0; } h3 { font-size: 1rem; margin: 18px 0 var(--space-1); }
    .scroll { overflow-x: auto; margin-top: 10px; }
    table.rt { border-collapse: collapse; background: var(--ro-paper); font-variant-numeric: tabular-nums; }
    table.rt th, table.rt td { padding: 5px 10px; border-bottom: 1px solid var(--ro-sunk); text-align: right; white-space: nowrap; }
    table.rt th:first-child { text-align: left; font-weight: 500; }
    table.rt thead th { color: var(--ro-mute); font-weight: 500; font-size: .85rem; }
    table.wide { width: 100%; }
    tr.total th, tr.total td { font-weight: 700; border-top: 2px solid var(--ro-line); }
    tr.profit th, tr.profit td { font-weight: 700; color: var(--ro-tide); background: var(--ro-tint); }
    tr.cost th:first-child, tr.revenue th:first-child { padding-left: 22px; }
    td.up { color: var(--ro-ok-mid); } td.down { color: var(--ro-error-strong); }
    .two { display: grid; grid-template-columns: 1fr 1fr; gap: 0 var(--space-6); }
    @media (max-width: 900px) { .charts2 { grid-template-columns: minmax(0, 1fr); } .mkpis { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
    @media (max-width: 720px) { .two { grid-template-columns: 1fr; } .cmp { font-size: .85rem; } .sel { width: 46%; } .card { padding: var(--space-3); } .mkpi b { font-size: 1.2rem; } }
    @media print {
      .no-print { display: none !important; }
      .rep { max-width: none; padding: 0; }
      .card, .mkpi, .scroll { box-shadow: none; border-color: var(--ro-line); background: transparent; }
      table.rt th, table.rt td { padding: 2px 6px; }
    }
  `,
})
export class ReportsPage {
  private api = inject(ReportsApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  rs = rs; pc = pc; monthOf = monthOf;

  private pid = computed(() => this.ctx.current()?.id ?? '');
  propName = computed(() => this.ctx.current()?.name ?? '');
  today = computed(() => localToday(this.ctx.current()?.timezone ?? 'Asia/Kolkata'));
  financial = computed(() => this.auth.can('financial_report.read', this.pid()));
  canRefresh = computed(() => this.auth.can('report.operational.read', this.pid()));
  canCommission = computed(() => this.auth.can('property_settings.update', this.pid()));
  tabs = computed(() => TABS.filter(t => !t.financial || this.financial()));

  tab = signal<Tab>('month');
  from = signal<string | null>(null);
  to = signal<string | null>(null);
  by = signal<'day' | 'month'>('day');
  month = signal<string | null>(null);
  byDay = computed(() => BY_DAY.includes(this.tab()));
  dayPeriod = computed(() => DAY_PERIOD.includes(this.tab()) && (!this.byDay() || this.by() === 'day'));

  monthR = signal<MonthReport | null>(null);
  occ = signal<OccupancyReport | null>(null);
  rev = signal<RevenueReport | null>(null);
  ch = signal<ChannelReport | null>(null);
  gst = signal<GstReport | null>(null);
  cost = signal<CostReport | null>(null);
  pnl = signal<PnlReport | null>(null);
  error = signal('');
  err = signal<Record<string, string>>({});
  refreshing = signal(false);
  /** The period the report on screen covers (the server's default until one is chosen). */
  shown = signal<{ from: string | null; to: string | null }>({ from: null, to: null });

  freshness = computed<ReportFreshness | null>(() => {
    switch (this.tab()) {
      case 'month': return this.monthR()?.freshness ?? null;
      case 'occupancy': return this.occ()?.freshness ?? null;
      case 'revenue': return this.rev()?.freshness ?? null;
      case 'channels': return this.ch()?.freshness ?? null;
      case 'gst': return this.gst()?.freshness ?? null;
      case 'costs': return this.cost()?.freshness ?? null;
      default: return this.pnl()?.freshness ?? null;
    }
  });
  loaded = computed(() => !!this.freshness());

  monthChoices = computed(() => {
    const t = monthOf(this.today());
    return Array.from({ length: 25 }, (_, i) => { const v = monthOf(t, -i); return { value: v, label: this.labelOf(v) }; });
  });

  Math = Math;
  private isDayTab = computed(() => this.dayPeriod());
  private day = (iso: string) => new Date(iso + 'T12:00:00Z').toLocaleDateString('en-IN', { day: 'numeric', month: 'short', timeZone: 'UTC' });
  private catOf = (period: string, label: string, by: 'day' | 'month') => by === 'day' ? this.day(period) : label.slice(0, 3) + ' ' + period.slice(2, 4);

  occChart = computed<ChartSpec>(() => {
    const o = this.occ();
    const rows = o?.rows ?? [];
    const today = rows.findIndex(r => r.period === this.today());
    return {
      kind: o?.by === 'day' ? 'area' : 'bar', max: 100, format: v => `${Math.round(v)} %`,
      categories: rows.map(r => this.catOf(r.period, r.label, o!.by)), today: today < 0 ? undefined : today,
      series: [{ name: 'Occupancy', data: rows.map(r => r.occupancyPercent) }],
    };
  });
  revChart = computed<ChartSpec>(() => {
    const v = this.rev();
    const rows = v?.rows ?? [];
    return {
      kind: 'bar', stack: true, format: x => rsShort(x), categories: rows.map(r => this.catOf(r.period, r.label, v!.by)),
      series: [
        { name: 'Rooms', data: rows.map(r => r.room) }, { name: 'Food and drink', data: rows.map(r => r.fnb) },
        { name: 'Activities', data: rows.map(r => r.activity) }, { name: 'Transport', data: rows.map(r => r.transport) },
        { name: 'Other', data: rows.map(r => r.other) },
      ],
    };
  });
  chChart = computed<ChartSpec>(() => {
    const rows = (this.ch()?.rows ?? []).filter(r => r.unitsSold > 0).slice(0, 8);
    return { kind: 'hbar', format: x => rsShort(x), categories: rows.map(r => r.name), series: [{ name: 'Net after commission', data: rows.map(r => r.net) }] };
  });
  chGroups = computed<ChartSpec>(() => {
    const g = (this.ch()?.groups ?? []).filter(x => x.unitsSold > 0);
    return { kind: 'donut', format: x => `${x.toLocaleString('en-IN')} nights`, categories: g.map(x => this.channel(x.channel)),
      series: [{ name: 'Nights', data: g.map(x => x.unitsSold) }] };
  });
  directShareOf(c: ChannelReport) {
    const all = c.total.unitsSold;
    return all ? Math.round(1000 * (c.groups.find(g => g.channel === 'DIRECT')?.unitsSold ?? 0) / all) / 10 : null;
  }
  gstChart = computed<ChartSpec>(() => {
    const rows = this.gst()?.rows ?? [];
    return { kind: 'bar', format: x => rsShort(x), categories: rows.map(r => r.label.slice(0, 3) + ' ' + r.month.slice(2, 4)),
      series: [{ name: 'Output GST', data: rows.map(r => r.outputTotal) }, { name: 'Input GST to claim', data: rows.map(r => r.inputTotal) }] };
  });
  costChart = computed<ChartSpec>(() => {
    const c = this.cost();
    const rows = c?.rows ?? [];
    const series = [{ name: 'Food cost % of restaurant sales', data: rows.map(r => r.foodCostPercent) }];
    if (c?.payIncluded) series.push({ name: 'Pay % of revenue', data: rows.map(r => r.labourPercent) });
    return { kind: 'area', format: x => `${Math.round(x)} %`, categories: rows.map(r => r.label.slice(0, 3) + ' ' + r.month.slice(2, 4)), series };
  });
  pnlChart = computed<ChartSpec>(() => {
    const p = this.pnl();
    const profit = p?.lines.find(l => l.kind === 'PROFIT');
    return { kind: 'bar', format: x => rsShort(x), categories: (p?.labels ?? []).map((l, i) => l.slice(0, 3) + ' ' + (p!.months[i] ?? '').slice(2, 4)),
      series: [{ name: profit?.label ?? 'Operating profit', data: profit?.months ?? [] }] };
  });
  monthMixSpec = computed(() => { const m = this.monthR(); return m ? this.monthMix(m) : { kind: 'donut' as const, categories: [], series: [] }; });
  monthChannelsSpec = computed(() => { const m = this.monthR(); return m ? this.monthChannels(m) : { kind: 'hbar' as const, categories: [], series: [] }; });
  headlineRows = computed(() => { const m = this.monthR(); return m ? this.headline(m) : []; });
  monthMix(m: MonthReport): ChartSpec {
    const t = m.this;
    return { kind: 'donut', format: x => rsShort(x), categories: ['Rooms', 'Food and drink', 'Activities', 'Transport', 'Other'],
      series: [{ name: 'Revenue', data: [t.roomRevenue, t.fnbRevenue, t.activityRevenue, t.transportRevenue, t.otherRevenue] }] };
  }
  monthChannels(m: MonthReport): ChartSpec {
    const cs = (m.channels ?? []).slice(0, 6);
    return { kind: 'hbar', format: x => rsShort(x), categories: cs.map(c => c.name), series: [{ name: 'Net after commission', data: cs.map(c => c.net) }] };
  }
  /** Four headline numbers for the month at a glance, against the month before. */
  headline(m: MonthReport) {
    const rows = this.kpiRows(m.access.financial, m.access.pay);
    const keys = m.access.financial ? ['occupancy', 'adr', 'revpar', 'total'] : ['occupancy', 'sold', 'guestNights', 'arrivals'];
    return keys.map(k => rows.find(r => r.key === k)!).filter(Boolean).map(r => ({
      key: r.key, label: r.label, value: r.show(m.this), vs: r.deltaText(m.this, m.previous), tone: this.tone(r.delta(m.this, m.previous), r.goodUp),
    }));
  }

  /** Quick periods. Indian businesses think in financial years (April to March) and GST months. */
  quick = computed<{ label: string; from: string; to: string }[]>(() => {
    const t = this.today();
    const [y, mo] = t.split('-').map(Number);
    const fy = mo >= 4 ? y : y - 1;
    const iso = (yy: number, mm: number, dd: number) => new Date(Date.UTC(yy, mm - 1, dd)).toISOString().slice(0, 10);
    const fyLabel = (a: number) => `FY ${a}-${String((a + 1) % 100).padStart(2, '0')}`;
    const days = [
      { label: 'This month', from: iso(y, mo, 1), to: t },
      { label: 'Last month', from: iso(y, mo - 1, 1), to: iso(y, mo, 0) },
      { label: 'Last 30 days', from: iso(y, mo, +t.slice(8) - 29), to: t },
    ];
    const year = [
      { label: `This financial year (${fyLabel(fy)})`, from: iso(fy, 4, 1), to: t },
      { label: `Last financial year (${fyLabel(fy - 1)})`, from: iso(fy - 1, 4, 1), to: iso(fy, 3, 31) },
      { label: 'Last 12 months', from: iso(y - 1, mo + 1, 1), to: t },
    ];
    return this.isDayTab() ? [...days, ...year.slice(0, 1)] : year;
  });
  isShown(q: { from: string; to: string }) { const s = this.shown(); return s.from === q.from && s.to === q.to; }
  pick(q: { from: string; to: string }) {
    // A long period by day is hard to read: show it by month instead.
    if (this.byDay() && this.by() === 'day' && q.from < monthOf(this.today(), -2)) this.by.set('month');
    this.from.set(q.from); this.to.set(q.to);
  }

  constructor() {
    this.route.queryParamMap.subscribe(q => {
      const t = q.get('tab') as Tab | null;
      const next = t && TABS.some(x => x.key === t) ? t : 'month';
      if (next !== this.tab()) { this.from.set(null); this.to.set(null); this.err.set({}); }
      this.tab.set(next);
    });
    effect(() => {
      const pid = this.pid();
      const tab = this.tab();
      const p = { from: this.from(), to: this.to(), by: this.by() };
      const month = this.month();
      if (pid) untracked(() => this.load(pid, tab, p, month));
    });
  }

  go(t: Tab) { this.router.navigate([], { queryParams: { tab: t === 'month' ? null : t }, replaceUrl: true }); }
  setFrom(v: string) { this.from.set(v || null); }
  setTo(v: string) { this.to.set(v || null); }
  setBy(v: 'day' | 'month') { this.from.set(null); this.to.set(null); this.by.set(v); }

  private load(pid: string, tab: Tab, p: { from: string | null; to: string | null; by: 'day' | 'month' }, month: string | null) {
    this.error.set('');
    const done = <T>(set: (v: T) => void) => ({
      next: (v: T) => { this.err.set({}); set(v); },
      error: (e: unknown) => {
        const prob = toProblem(e);
        const f = fieldErrors(prob);
        this.err.set(f);
        if (!Object.keys(f).length) this.error.set(prob.detail);
      },
    });
    const keep = (r: { from: string; to: string }) => this.shown.set({ from: r.from, to: r.to });
    switch (tab) {
      case 'month': this.api.month(pid, month).subscribe(done<MonthReport>(r => this.monthR.set(r))); break;
      case 'occupancy': this.api.occupancy(pid, p).subscribe(done<OccupancyReport>(r => { this.occ.set(r); keep(r); })); break;
      case 'revenue': this.api.revenue(pid, p).subscribe(done<RevenueReport>(r => { this.rev.set(r); keep(r); })); break;
      case 'channels': this.api.channels(pid, p).subscribe(done<ChannelReport>(r => { this.ch.set(r); keep(r); })); break;
      case 'gst': this.api.gst(pid, p).subscribe(done<GstReport>(r => { this.gst.set(r); keep(r); })); break;
      case 'costs': this.api.costs(pid, p).subscribe(done<CostReport>(r => { this.cost.set(r); keep(r); })); break;
      case 'pnl': this.api.pnl(pid, p).subscribe(done<PnlReport>(r => { this.pnl.set(r); keep(r); })); break;
    }
  }

  refresh() {
    this.refreshing.set(true);
    this.api.refresh(this.pid()).subscribe({
      next: f => {
        this.refreshing.set(false);
        this.snack.open(`Added up again in ${((f.durationMs ?? 0) / 1000).toLocaleString('en-IN', { maximumFractionDigits: 1 })} s.`, 'OK', { duration: 3000 });
        this.load(this.pid(), this.tab(), { from: this.from(), to: this.to(), by: this.by() }, this.month());
      },
      error: e => { this.refreshing.set(false); this.error.set(toProblem(e).detail); },
    });
  }

  commission() {
    this.dialog.open(CommissionDialog, { data: { propertyId: this.pid() }, maxWidth: '100vw' }).afterClosed().subscribe(saved => {
      if (!saved) return;
      this.snack.open('Commission rates saved. The booking sources are added up again with them.', 'OK', { duration: 4000 });
      this.load(this.pid(), this.tab(), { from: this.from(), to: this.to(), by: this.by() }, this.month());
    });
  }

  print() { window.print(); }
  withTotal<T>(rows: T[], total: T): T[] { return [...rows, total]; }

  channel(c: string) { return CHANNEL[c] ?? c; }
  labelOf(iso: string) { return new Date(iso + 'T12:00:00Z').toLocaleDateString('en-IN', { month: 'long', year: 'numeric', timeZone: 'UTC' }); }
  dayShort(iso: string) { return new Date(iso + 'T12:00:00Z').toLocaleDateString('en-IN', { day: 'numeric', month: 'short', timeZone: 'UTC' }); }
  monthEndOf(iso: string) { const [y, m] = iso.split('-').map(Number); return new Date(Date.UTC(y, m, 0)).toISOString().slice(0, 10); }
  tone(delta: number | null, goodUp: boolean) { return delta === null || delta === 0 ? '' : (delta > 0) === goodUp ? 'up' : 'down'; }

  /** The rows of the month comparison. */
  kpiRows(financial: boolean, pay: boolean) {
    const money = (key: string, label: string, f: (k: ReportKpis) => number | null, goodUp = true): K => ({
      key, label, goodUp, show: k => rsShort(f(k)), delta: (a, b) => change(f(a), f(b)), deltaText: (a, b) => signed(change(f(a), f(b)), ' %'),
    });
    const count = (key: string, label: string, f: (k: ReportKpis) => number): K => ({
      key, label, goodUp: true, show: k => f(k).toLocaleString('en-IN'), delta: (a, b) => change(f(a), f(b)), deltaText: (a, b) => signed(change(f(a), f(b)), ' %'),
    });
    const pts = (key: string, label: string, f: (k: ReportKpis) => number | null, goodUp = true): K => ({
      key, label, goodUp, show: k => pc(f(k)), delta: (a, b) => points(f(a), f(b)), deltaText: (a, b) => signed(points(f(a), f(b)), ' pts'),
    });
    const rows: K[] = [
      pts('occupancy', 'Occupancy', k => k.occupancyPercent),
      count('sold', 'Nights sold (rooms and beds)', k => k.unitsSold),
      count('guestNights', 'Guest nights', k => k.guestNights),
      count('arrivals', 'Arrivals', k => k.arrivals),
    ];
    if (financial) rows.push(
      money('adr', 'ADR', k => k.adr), money('revpar', 'RevPAR', k => k.revPar),
      money('room', 'Room revenue', k => k.roomRevenue), money('fnb', 'Food and drink', k => k.fnbRevenue),
      money('activity', 'Activities', k => k.activityRevenue), money('transport', 'Transport', k => k.transportRevenue),
      money('other', 'Other income', k => k.otherRevenue), money('total', 'Total revenue', k => k.totalRevenue),
      money('paid', 'Payments received', k => k.paymentsReceived),
      pts('foodCost', 'Food cost % of restaurant sales', k => k.foodCostPercent, false),
    );
    if (financial && pay) rows.push(money('labour', 'Pay (estimate)', k => k.labour, false), pts('labourPct', 'Pay % of revenue', k => k.labourPercent, false));
    return rows;
  }

  /** The report on screen as a CSV file. */
  csv() {
    const pid = this.ctx.current()?.code ?? 'resort';
    const name = (what: string, a: string, b: string) => `${pid}-${what}-${a}-to-${b}.csv`.toLowerCase();
    switch (this.tab()) {
      case 'month': {
        const m = this.monthR(); if (!m) return;
        const rows = this.kpiRows(m.access.financial, m.access.pay).map(k => [k.label, k.show(m.this), k.show(m.previous), k.show(m.lastYear)]);
        const pnl = (m.pnl?.lines ?? []).map(l => [l.label, l.total]);
        downloadCsv(`${pid}-month-${m.month.slice(0, 7)}.csv`.toLowerCase(),
          toCsv(['', m.label, this.labelOf(m.previous.from), this.labelOf(m.lastYear.from)], rows) + (pnl.length ? '\r\n' + toCsv(['Profit and loss', m.label], pnl) : ''));
        break;
      }
      case 'occupancy': {
        const o = this.occ(); if (!o) return;
        const f = this.financial();
        downloadCsv(name('occupancy', o.from, o.to), toCsv(
          ['Period', 'Rooms available', 'Rooms sold', 'Beds available', 'Beds sold', 'Units available', 'Units sold', 'Out of order', 'Occupancy %', 'Guest nights',
            'Arrivals', 'Departures', ...(f ? ['Room revenue', 'ADR', 'RevPAR'] : [])],
          [...o.rows, o.total].map(r => [r === o.total ? 'Total' : r.period, r.roomsAvailable, r.roomsSold, r.bedsAvailable, r.bedsSold, r.unitsAvailable, r.unitsSold,
            r.outOfOrder, r.occupancyPercent, r.guestNights, r.arrivals, r.departures, ...(f ? [r.roomRevenue, r.adr, r.revPar] : [])])));
        break;
      }
      case 'revenue': {
        const v = this.rev(); if (!v) return;
        downloadCsv(name('revenue', v.from, v.to), toCsv(
          ['Period', 'Rooms', 'Food and drink', 'Activities', 'Transport', 'Other', 'Total', 'GST charged', 'Payments received', 'Refunds paid'],
          [...v.rows, v.total].map(r => [r === v.total ? 'Total' : r.period, r.room, r.fnb, r.activity, r.transport, r.other, r.total, r.tax, r.paymentsReceived, r.refundsPaid])));
        break;
      }
      case 'channels': {
        const c = this.ch(); if (!c) return;
        downloadCsv(name('booking-sources', c.from, c.to), toCsv(
          ['Code', 'Source', 'Kind', 'Nights sold', 'Share %', 'Room revenue', 'ADR', 'Commission %', 'Commission', 'Net'],
          c.rows.map(r => [r.code, r.name, r.channel, r.unitsSold, r.sharePercent, r.roomRevenue, r.adr, r.commissionPercent, r.commission, r.net])));
        break;
      }
      case 'gst': {
        const g = this.gst(); if (!g) return;
        downloadCsv(name('gst', g.from, g.to), toCsv(
          ['Month', 'Output taxable', 'Output CGST', 'Output SGST', 'Output IGST', 'Output cess', 'At the table (no invoice)', 'Output total',
            'Input CGST', 'Input SGST', 'Input IGST', 'Input total', 'Payable', 'Credit', 'GST on the bills'],
          [...g.rows, g.total].map(r => [r === g.total ? 'Total' : r.month, r.outputTaxable, r.outputCgst, r.outputSgst, r.outputIgst, r.outputCess, r.posUninvoiced,
            r.outputTotal, r.inputCgst, r.inputSgst, r.inputIgst, r.inputTotal, r.payable, r.credit, r.chargedOnBills])));
        break;
      }
      case 'costs': {
        const c = this.cost(); if (!c) return;
        const p = c.payIncluded;
        downloadCsv(name('costs', c.from, c.to), toCsv(
          ['Month', 'Revenue', 'Restaurant sales', 'Food cost', 'Food cost %', 'Written off', ...(p ? ['Pay', 'Pay %'] : []), 'Expenses', 'Supplier bills'],
          [...c.rows, c.total].map(r => [r === c.total ? 'Total' : r.month, r.revenue, r.restaurantSales, r.foodCost, r.foodCostPercent, r.stockWrittenOff,
            ...(p ? [r.labour, r.labourPercent] : []), r.expenses, r.supplierBills])));
        break;
      }
      case 'pnl': {
        const p = this.pnl(); if (!p) return;
        downloadCsv(name('profit-and-loss', p.from, p.to), toCsv(['Line', ...p.months, 'Total'], p.lines.map(l => [l.label, ...l.months, l.total])));
        break;
      }
    }
  }
}

interface K {
  key: string; label: string; goodUp: boolean; show: (k: ReportKpis) => string; delta: (a: ReportKpis, b: ReportKpis) => number | null;
  deltaText: (a: ReportKpis, b: ReportKpis) => string;
}

function signed(n: number | null, unit: string) { return n === null ? '—' : `${n > 0 ? '+' : n < 0 ? '−' : ''}${Math.abs(n).toLocaleString('en-IN')}${unit}`; }

// ------------------------------------------------------------------ commission rates

/** Sets each booking source's commission for this property; empty puts it back to its default. */
@Component({
  selector: 'app-commission-dialog',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Commission rates</h2>
    <div mat-dialog-content>
      <p class="mute">The share each source keeps of the room revenue. Leave a rate empty for the default.</p>
      @for (r of rates(); track r.sourceId; let i = $index) {
        <div class="row" [attr.data-source]="r.code">
          <span class="name">{{ r.name }}<small>{{ kind(r.channel) }}</small></span>
          <mat-form-field appearance="outline" subscriptSizing="dynamic" class="pct">
            <mat-label>%</mat-label>
            <input matInput type="number" min="0" max="50" step="0.5" [(ngModel)]="values[r.sourceId]" [placeholder]="'' + r.defaultPercent"
                   [attr.aria-label]="r.name + ' commission %'" />
            @if (errOf(i); as e) { <mat-hint class="bad">{{ e }}</mat-hint> }
            @else { <mat-hint>default {{ r.defaultPercent }} %</mat-hint> }
          </mat-form-field>
        </div>
      }
      @if (error()) { <p class="bad" role="alert">{{ error() }}</p> }
    </div>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="busy() || !rates().length">Save</button>
    </div>
  `,
  styles: `
    .row { display: flex; align-items: center; gap: var(--space-3); padding: var(--space-1) 0; min-width: min(420px, 84vw); }
    .name { flex: 1; display: flex; flex-direction: column; } .name small, .mute { color: var(--ro-mute); } .pct { width: 120px; } .bad { color: var(--ro-error); }
  `,
})
export class CommissionDialog {
  private api = inject(ReportsApi);
  rates = signal<CommissionRate[]>([]);
  values: Record<string, number | null> = {};
  busy = signal(false);
  error = signal('');
  errs = signal<Record<string, string>>({});

  private data = inject<{ propertyId: string }>(MAT_DIALOG_DATA);
  private ref = inject<MatDialogRef<CommissionDialog, boolean>>(MatDialogRef);

  constructor() {
    this.api.commission(this.data.propertyId).subscribe({
      next: r => { this.values = Object.fromEntries(r.map(x => [x.sourceId, x.isSet ? x.percent : null])); this.rates.set(r); },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  kind(c: string) { return CHANNEL[c] ?? c; }
  errOf(i: number): string | undefined { const e = this.errs(); return e[`rates[${i}].percent`] ?? e[`rates.${i}.percent`]; }

  save() {
    this.busy.set(true);
    this.error.set('');
    const rates = this.rates().map(r => {
      const v = this.values[r.sourceId];
      return { sourceId: r.sourceId, percent: v === null || v === undefined || (v as unknown) === '' ? null : Number(v) };
    });
    const run: Observable<CommissionRate[]> = this.api.saveCommission(this.data.propertyId, rates);
    run.subscribe({
      next: () => this.ref.close(true),
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const f: Record<string, string> = {};
        for (const [k, v] of Object.entries(p.errors ?? {})) f[k.toLowerCase()] = v[0];
        this.errs.set(f);
        if (!Object.keys(f).length) this.error.set(p.detail);
      },
    });
  }
}
