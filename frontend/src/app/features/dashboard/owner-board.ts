import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { toProblem } from '../../core/http';
import { Dashboard, ReportKpis } from '../../core/models';
import { FreshComponent } from '../reports/report-bits';
import { ChartComponent, ChartSpec } from '../reports/chart';
import { ReportsApi, change, pc, points, rs, rsShort } from '../reports/reports.api';

interface Tile { key: string; label: string; value: string; vsMonth: string | null; vsYear: string | null; upMonth: boolean | null; upYear: boolean | null; }

/**
 * The numbers on Today (Sprint 23, brief §50): tonight and the next two weeks for whoever runs the property; the month so
 * far against last month and a year ago, the days of the month, the last twelve months, the booking sources, GST and the
 * budget for whoever may see money. Read from the reporting summary in one call.
 */
@Component({
  selector: 'app-owner-board',
  imports: [RouterLink, MatButtonModule, MatIconModule, ChartComponent, FreshComponent],
  template: `
    @if (d(); as d) {
      <section class="panel board" data-testid="tonight">
        <div class="h"><h2>Tonight</h2><span class="grow"></span><a mat-button routerLink="/reports"><mat-icon>monitoring</mat-icon>Reports</a></div>
        <div class="kpis">
          <div class="kpi hero">
            <small>Occupancy</small>
            <b class="display" data-testid="tonight-occupancy">{{ pc(d.todayStats.occupancyPercent) }}</b>
            <span class="meter" aria-hidden="true"><span [style.width.%]="d.todayStats.occupancyPercent ?? 0"></span></span>
            <small>{{ d.todayStats.unitsSold }} of {{ d.todayStats.unitsAvailable }} rooms and beds</small>
          </div>
          <div class="kpi"><small>Guests in house</small><b class="display">{{ d.todayStats.guestsInHouse }}</b></div>
          <div class="kpi"><small>Arriving</small><b class="display">{{ d.todayStats.arrivals }}</b></div>
          <div class="kpi"><small>Leaving</small><b class="display">{{ d.todayStats.departures }}</b></div>
          <div class="kpi" [class.bad]="d.todayStats.outOfOrder > 0"><small>Out of order</small><b class="display">{{ d.todayStats.outOfOrder }}</b></div>
        </div>
        <h3>The next 14 nights on the books</h3>
        <app-chart [spec]="aheadChart()" [height]="190" label="Occupancy on the books for the next 14 nights" />
        <app-fresh [freshness]="d.freshness" [busy]="refreshing()" (refresh)="refresh()" />
      </section>

      @if (d.access.financial) {
        <section class="panel board" data-testid="month-to-date">
          <div class="h"><h2>{{ monthName(d.monthToDate.from) }} so far</h2><span class="grow"></span>
            <a mat-button routerLink="/reports"><mat-icon>calendar_month</mat-icon>The month at a glance</a></div>
          <div class="kpis four">
            @for (t of tiles(); track t.key) {
              <div class="kpi" [attr.data-kpi]="t.key"><small>{{ t.label }}</small><b class="display">{{ t.value }}</b>
                <small class="vs">
                  <span class="pill" [class]="tone(t.upMonth)">@if (t.upMonth !== null) { <mat-icon inline>{{ arrow(t.vsMonth) }}</mat-icon> }{{ t.vsMonth ?? '—' }}</span> on last month,
                  <span [class]="tone(t.upYear)">{{ t.vsYear ?? '—' }}</span> on a year ago</small></div>
            }
          </div>

          <div class="grid2">
            <div class="cell wide">
              <h3>Revenue each day this month</h3>
              <app-chart [spec]="dayChart()" [height]="210" label="Revenue each day this month" />
            </div>
            <div class="cell">
              <h3>Where the money came from</h3>
              @if (mixHasData()) { <app-chart [spec]="mixChart()" [height]="210" label="Revenue this month by kind" /> }
              @else { <p class="mute">No revenue yet this month.</p> }
            </div>
            <div class="cell wide">
              <h3>The last 12 months</h3>
              <app-chart [spec]="monthChart()" [height]="210" label="Revenue each month for the last 12 months" />
            </div>
            @if (d.channels; as cs) {
              <div class="cell">
                <h3>Booking sources this month</h3>
                @if (cs.length) {
                  <p class="headline"><b class="display">{{ pc(directShare()) }}</b> <span class="mute">of nights booked direct, no commission</span></p>
                  <app-chart [spec]="channelChart()" [height]="Math.max(120, 34 * Math.min(cs.length, 6) + 20)" label="Net room revenue by booking source" />
                } @else { <p class="mute">No nights sold yet.</p> }
              </div>
            }
            <div class="cell">
              @if (d.gst; as g) {
                <h3>GST this month (estimate)</h3>
                <p class="gst">{{ g.credit ? 'Credit' : 'Payable' }} <b class="display">{{ rs(g.credit || g.payable) }}</b></p>
                <p class="mute small">Output {{ rs(g.outputTotal) }} less input {{ rs(g.inputTotal) }}</p>
              }
              @if (d.budget; as b) {
                <h3>Budget this month</h3>
                <p data-testid="budget-card" class="budget"><span><b>{{ rs(b.spent) }}</b> spent of {{ rs(b.budget) }}</span>
                  @if (b.budget) { <span class="meter"><span [class.over]="b.spent > b.budget" [style.width.%]="Math.min(100, 100 * b.spent / b.budget)"></span></span> }
                  @if (!b.payIncluded) { <span class="mute small">Pay not included.</span> }
                  <a routerLink="/budgets">Budget</a></p>
              }
            </div>
          </div>
        </section>
      } @else if (d.budget; as b) {
        <section class="panel board"><h3>Budget this month</h3><p data-testid="budget-card"><b>{{ rs(b.spent) }}</b> spent of {{ rs(b.budget) }}. <a routerLink="/budgets">Budget</a></p></section>
      }
    } @else if (!error()) {
      <section class="panel board skeleton" aria-busy="true" aria-label="Loading the numbers">
        <div class="sk h1"></div><div class="kpis"><div class="sk"></div><div class="sk"></div><div class="sk"></div><div class="sk"></div><div class="sk"></div></div>
        <div class="sk chart"></div>
      </section>
    }
    @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
  `,
  styles: `
    .board { padding: var(--space-5) var(--space-6); margin-bottom: var(--space-4); }
    .h { display: flex; align-items: center; gap: var(--space-2); } .h h2 { margin: 0; font-family: var(--ro-font-display); font-size: 1.05rem; } .grow { flex: 1; }
    h3 { font-size: .9rem; font-weight: 600; margin: var(--space-5) 0 var(--space-2); color: var(--ro-ink-soft); }
    .kpis { display: grid; gap: var(--space-3); margin-top: var(--space-3); grid-template-columns: 1.4fr repeat(4, 1fr); }
    .kpis.four { grid-template-columns: repeat(4, 1fr); }
    .kpi { background: var(--ro-glass); border: 1px solid var(--ro-glass-edge); border-radius: var(--ro-radius); padding: var(--space-3) var(--space-4);
           display: flex; flex-direction: column; gap: 2px; min-width: 0; box-shadow: var(--ro-glass-sheen); transition: transform 200ms ease, box-shadow 200ms ease; }
    .kpi:hover { transform: translateY(-2px); box-shadow: var(--ro-shadow-card); }
    .kpi small { color: var(--ro-mute); font-size: .8rem; }
    .kpi b { font-size: 1.55rem; line-height: 1.2; color: var(--ro-ink); font-weight: 600; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
    .kpi.hero b { font-size: 2rem; color: var(--ro-accent-strong); }
    .kpi.bad b { color: var(--ro-error); }
    .meter { display: block; height: 6px; background: var(--ro-sunk); border-radius: 3px; overflow: hidden; margin: var(--space-1) 0; }
    .meter span { display: block; height: 100%; background: var(--ro-accent-grad); border-radius: 3px; } .meter span.over { background: var(--ro-error); }
    .vs { font-size: .74rem !important; line-height: 1.5; }
    .pill { display: inline-flex; align-items: center; gap: 1px; padding: 0 6px 0 3px; border-radius: 999px; font-weight: 600; }
    .pill.up { background: var(--ro-ok-bg); } .pill.down { background: var(--ro-error-bg); }
    .up { color: var(--ro-ok-mid); } .down { color: var(--ro-error-strong); }
    .grid2 { display: grid; grid-template-columns: minmax(0, 1.6fr) minmax(0, 1fr); gap: 0 var(--space-6); }
    .cell { min-width: 0; }
    .headline { margin: 0 0 var(--space-1); display: flex; align-items: baseline; gap: var(--space-2); flex-wrap: wrap; }
    .headline b { font-size: 1.4rem; color: var(--ro-accent-strong); }
    .mute { color: var(--ro-mute); } .small { font-size: .82rem; } .err { color: var(--ro-error); }
    .gst { display: flex; gap: var(--space-2); align-items: baseline; flex-wrap: wrap; margin: 0; } .gst b { font-size: 1.3rem; }
    .budget { display: flex; flex-direction: column; gap: 2px; margin: 0; } .budget .meter { width: 100%; }
    .skeleton .sk { background: var(--ro-sunk); border-radius: var(--ro-radius); height: 84px; animation: ro-pulse 1.4s ease-in-out infinite; }
    .skeleton .sk.h1 { height: 22px; width: 160px; } .skeleton .sk.chart { height: 180px; margin-top: var(--space-4); }
    @keyframes ro-pulse { 50% { opacity: .5; } }
    @media (max-width: 1100px) { .grid2 { grid-template-columns: minmax(0, 1fr); } }
    @media (max-width: 900px) { .kpis { grid-template-columns: repeat(3, 1fr); } .kpis.four { grid-template-columns: repeat(2, 1fr); } }
    @media (max-width: 640px) {
      .board { padding: var(--space-4); }
      .kpis { grid-template-columns: 1fr 1fr; gap: var(--space-2); } .kpi.hero { grid-column: 1 / -1; }
      .kpi { padding: var(--space-2) var(--space-3); } .kpi b { font-size: 1.3rem; } .kpi.hero b { font-size: 1.75rem; }
    }
  `,
})
export class OwnerBoard {
  propertyId = input.required<string>();
  private api = inject(ReportsApi);
  rs = rs; pc = pc; Math = Math;
  d = signal<Dashboard | null>(null);
  error = signal('');
  refreshing = signal(false);

  constructor() {
    effect(() => {
      const pid = this.propertyId();
      untracked(() => this.load(pid));
    });
  }

  private load(pid: string) {
    this.d.set(null);
    this.error.set('');
    this.api.dashboard(pid).subscribe({ next: d => this.d.set(d), error: e => this.error.set(toProblem(e).detail) });
  }

  refresh() {
    this.refreshing.set(true);
    this.api.refresh(this.propertyId()).subscribe({
      next: () => { this.refreshing.set(false); this.load(this.propertyId()); },
      error: e => { this.refreshing.set(false); this.error.set(toProblem(e).detail); },
    });
  }

  /** Occupancy for the next 14 nights, weekends shaded (that is how owners plan prices). */
  aheadChart = computed<ChartSpec>(() => {
    const pts = this.d()?.ahead ?? [];
    const cats = pts.map(p => this.dayLabel(p.date));
    return {
      kind: 'area', categories: cats, max: 100, format: v => `${Math.round(v)} %`,
      series: [{ name: 'Occupancy on the books', data: pts.map(p => p.occupancyPercent) }],
      bands: weekendBands(pts.map(p => p.date), cats),
    };
  });
  dayChart = computed<ChartSpec>(() => {
    const d = this.d();
    const pts = d?.days ?? [];
    const today = pts.findIndex(p => p.date === d!.today);
    return {
      kind: 'bar', categories: pts.map(p => String(+p.date.slice(8))), every: 5, format: v => rsShort(v),
      series: [{ name: 'Revenue', data: pts.map(p => p.revenue) }], today: today < 0 ? undefined : today,
    };
  });
  monthChart = computed<ChartSpec>(() => {
    const ms = this.d()?.months ?? [];
    return {
      kind: 'bar', categories: ms.map(m => m.label.slice(0, 3) + ' ' + m.month.slice(2, 4)), format: v => rsShort(v),
      series: [{ name: 'Revenue', data: ms.map(m => m.revenue) }],
    };
  });
  private mix = computed(() => {
    const t = this.d()?.monthToDate;
    return t ? [['Rooms', t.roomRevenue], ['Food and drink', t.fnbRevenue], ['Activities', t.activityRevenue],
      ['Transport', t.transportRevenue], ['Other', t.otherRevenue]] as [string, number | null][] : [];
  });
  mixHasData = computed(() => this.mix().some(([, v]) => (v ?? 0) > 0));
  mixChart = computed<ChartSpec>(() => ({
    kind: 'donut', categories: this.mix().map(m => m[0]), format: v => rsShort(v),
    series: [{ name: 'Revenue', data: this.mix().map(m => m[1]) }],
  }));
  channelChart = computed<ChartSpec>(() => {
    const cs = (this.d()?.channels ?? []).slice(0, 6);
    return { kind: 'hbar', categories: cs.map(c => c.name), format: v => rsShort(v), series: [{ name: 'Net after commission', data: cs.map(c => c.net) }] };
  });
  /** Share of nights sold that came direct (no commission): the number owners want to grow. */
  directShare = computed(() => {
    const cs = this.d()?.channels ?? [];
    const all = cs.reduce((n, c) => n + c.unitsSold, 0);
    return all ? Math.round(1000 * cs.filter(c => c.channel === 'DIRECT').reduce((n, c) => n + c.unitsSold, 0) / all) / 10 : null;
  });

  tiles = computed<Tile[]>(() => {
    const d = this.d();
    if (!d) return [];
    const t = d.monthToDate, m = d.lastMonth, y = d.lastYear;
    const pct = (f: (k: ReportKpis) => number | null) => (o: ReportKpis | null) => o ? change(f(t), f(o)) : null;
    const pts = (f: (k: ReportKpis) => number | null) => (o: ReportKpis | null) => o ? points(f(t), f(o)) : null;
    const make = (key: string, label: string, value: string, delta: (o: ReportKpis | null) => number | null, unit: string, goodUp = true): Tile => {
      const a = delta(m), b = delta(y);
      return { key, label, value, vsMonth: fmt(a, unit), vsYear: fmt(b, unit), upMonth: a === null || a === 0 ? null : (a > 0) === goodUp,
        upYear: b === null || b === 0 ? null : (b > 0) === goodUp };
    };
    const tiles = [
      make('occupancy', 'Occupancy', pc(t.occupancyPercent), pts(k => k.occupancyPercent), ' pts'),
      make('adr', 'ADR', rs(t.adr), pct(k => k.adr), ' %'),
      make('revpar', 'RevPAR', rs(t.revPar), pct(k => k.revPar), ' %'),
      make('revenue', 'Revenue', rsShort(t.totalRevenue), pct(k => k.totalRevenue), ' %'),
      make('foodCost', 'Food cost', pc(t.foodCostPercent), pts(k => k.foodCostPercent), ' pts', false),
    ];
    if (d.access.pay) tiles.push(make('labour', 'Pay of revenue', pc(t.labourPercent), pts(k => k.labourPercent), ' pts', false));
    tiles.push(make('paid', 'Payments received', rsShort(t.paymentsReceived), pct(k => k.paymentsReceived), ' %'));
    tiles.push(make('arrivals', 'Arrivals', String(t.arrivals), pct(k => k.arrivals), ' %'));
    return tiles;
  });

  tone(up: boolean | null) { return up === null ? '' : up ? 'up' : 'down'; }
  arrow(text: string | null) { return text?.startsWith('−') ? 'south_east' : 'north_east'; }
  dayLabel(iso: string) { return new Date(iso + 'T12:00:00Z').toLocaleDateString('en-IN', { weekday: 'narrow', day: 'numeric', timeZone: 'UTC' }); }
  monthName(iso: string) { return new Date(iso + 'T12:00:00Z').toLocaleDateString('en-IN', { month: 'long', timeZone: 'UTC' }); }
}

/** Saturday and Sunday as shaded bands on a day chart. */
export function weekendBands(dates: string[], labels: string[]) {
  const bands: { from: string; to: string; label?: string }[] = [];
  dates.forEach((iso, i) => {
    const wd = new Date(iso + 'T12:00:00Z').getUTCDay();
    if (wd === 6) bands.push({ from: labels[i], to: labels[Math.min(i + 1, labels.length - 1)], label: 'Weekend' });
    else if (wd === 0 && i === 0) bands.push({ from: labels[0], to: labels[0] });
  });
  return bands;
}

function fmt(n: number | null, unit: string) { return n === null ? null : `${n > 0 ? '+' : n < 0 ? '−' : ''}${Math.abs(n).toLocaleString('en-IN')}${unit}`; }
