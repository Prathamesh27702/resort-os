import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { toProblem } from '../../core/http';
import { Dashboard, ReportKpis } from '../../core/models';
import { Bar, BarsComponent, FreshComponent } from '../reports/report-bits';
import { ReportsApi, change, pc, points, rs, rsShort } from '../reports/reports.api';

interface Tile { key: string; label: string; value: string; vsMonth: string | null; vsYear: string | null; upMonth: boolean | null; upYear: boolean | null; }

/**
 * The numbers on Today (Sprint 23, brief §50): tonight and the next two weeks for whoever runs the property; the month so
 * far against last month and a year ago, the days of the month, the last twelve months, the booking sources, GST and the
 * budget for whoever may see money. Read from the reporting summary in one call.
 */
@Component({
  selector: 'app-owner-board',
  imports: [RouterLink, MatButtonModule, MatIconModule, BarsComponent, FreshComponent],
  template: `
    @if (d(); as d) {
      <section class="panel board" data-testid="tonight">
        <div class="h"><h2>Tonight</h2><span class="grow"></span><a mat-button routerLink="/reports"><mat-icon>monitoring</mat-icon>Reports</a></div>
        <div class="tiles five">
          <div class="tile"><small>Occupancy</small><b data-testid="tonight-occupancy">{{ pc(d.todayStats.occupancyPercent) }}</b>
            <small>{{ d.todayStats.unitsSold }} of {{ d.todayStats.unitsAvailable }} rooms and beds</small></div>
          <div class="tile"><small>Guests in house</small><b>{{ d.todayStats.guestsInHouse }}</b></div>
          <div class="tile"><small>Arriving</small><b>{{ d.todayStats.arrivals }}</b></div>
          <div class="tile"><small>Leaving</small><b>{{ d.todayStats.departures }}</b></div>
          <div class="tile"><small>Out of order</small><b>{{ d.todayStats.outOfOrder }}</b></div>
        </div>
        <h3>The next 14 nights on the books</h3>
        <app-bars [bars]="aheadBars()" [max]="100" label="Occupancy on the books" [height]="90" color="#b7791f" />
        <app-fresh [freshness]="d.freshness" [busy]="refreshing()" (refresh)="refresh()" />
      </section>

      @if (d.access.financial) {
        <section class="panel board" data-testid="month-to-date">
          <div class="h"><h2>{{ monthName(d.monthToDate.from) }} so far</h2><span class="grow"></span>
            <a mat-button routerLink="/reports"><mat-icon>calendar_month</mat-icon>The month at a glance</a></div>
          <div class="tiles four">
            @for (t of tiles(); track t.key) {
              <div class="tile" [attr.data-kpi]="t.key"><small>{{ t.label }}</small><b>{{ t.value }}</b>
                <small class="vs"><span [class]="tone(t.upMonth)">{{ t.vsMonth ?? '—' }}</span> on last month ·
                  <span [class]="tone(t.upYear)">{{ t.vsYear ?? '—' }}</span> on a year ago</small></div>
            }
          </div>
          <h3>Revenue each day this month</h3>
          <app-bars [bars]="dayBars()" label="Revenue each day" [height]="110" [every]="5" />
          <h3>The last 12 months</h3>
          <app-bars [bars]="monthBars()" label="Revenue each month" [height]="120" color="#0e3b43" />
          <div class="two">
            @if (d.channels; as cs) {
              <div><h3>Booking sources this month</h3>
                <ul class="list">@for (c of cs.slice(0, 6); track c.sourceId) {
                  <li><span>{{ c.name }}</span><span class="mute">{{ c.unitsSold }} nights · {{ pc(c.sharePercent) }}</span><b>{{ rs(c.net) }}</b></li>
                } @empty { <li class="mute">No nights sold yet.</li> }</ul></div>
            }
            <div>
              @if (d.gst; as g) {
                <h3>GST this month (estimate)</h3>
                <p class="gst">{{ g.credit ? 'Credit' : 'Payable' }} <b>{{ rs(g.credit || g.payable) }}</b>
                  <span class="mute">output {{ rs(g.outputTotal) }} less input {{ rs(g.inputTotal) }}</span></p>
              }
              @if (d.budget; as b) {
                <h3>Budget this month</h3>
                <p data-testid="budget-card"><b>{{ rs(b.spent) }}</b> spent of {{ rs(b.budget) }}
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
    }
    @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
  `,
  styles: `
    .board { padding: 16px 20px; max-width: 980px; margin-bottom: 12px; }
    .h { display: flex; align-items: center; gap: 8px; } .h h2 { margin: 0; } .grow { flex: 1; }
    h3 { font-size: .95rem; margin: 14px 0 6px; color: #33474a; }
    .tiles { display: grid; gap: 8px; margin-top: 8px; } .five { grid-template-columns: repeat(5, 1fr); } .four { grid-template-columns: repeat(4, 1fr); }
    .tile { background: #f7faf9; border: 1px solid #e3e9e7; border-radius: 12px; padding: 8px 12px; display: flex; flex-direction: column; min-width: 0; }
    .tile small { color: #5b6b6e; font-size: .8rem; } .tile b { font-size: 1.3rem; color: #0e3b43; font-variant-numeric: tabular-nums; }
    .vs { font-size: .74rem !important; } .up { color: #1b6e3a; } .down { color: #8a1c1c; }
    .two { display: grid; grid-template-columns: 1fr 1fr; gap: 0 24px; }
    .list { list-style: none; margin: 0; padding: 0; } .list li { display: grid; grid-template-columns: 1fr auto 90px; gap: 8px; padding: 3px 0; border-bottom: 1px solid #eef2f1; }
    .list b { text-align: right; font-variant-numeric: tabular-nums; }
    .mute { color: #5b6b6e; } .small { font-size: .82rem; } .err { color: #b3261e; }
    .gst { display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap; margin: 0; }
    .meter { display: inline-block; vertical-align: middle; width: 120px; height: 6px; background: #eef2f1; border-radius: 3px; overflow: hidden; margin: 0 6px; }
    .meter span { display: block; height: 100%; background: #0090a0; } .meter span.over { background: #b3261e; }
    @media (max-width: 720px) { .five, .four { grid-template-columns: 1fr 1fr; } .two { grid-template-columns: 1fr; } }
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

  aheadBars = computed<Bar[]>(() => (this.d()?.ahead ?? []).map(p => ({
    key: p.date, label: this.dayLabel(p.date), value: p.occupancyPercent, title: `${this.dayLabel(p.date)}: ${pc(p.occupancyPercent)} (${p.unitsSold} of ${p.unitsAvailable})`,
  })));
  dayBars = computed<Bar[]>(() => {
    const d = this.d();
    return (d?.days ?? []).map(p => ({ key: p.date, label: p.date.slice(8), value: p.revenue, title: `${p.date}: ${rs(p.revenue)}, ${pc(p.occupancyPercent)}`,
      mute: p.date > d!.today, mark: p.date === d!.today }));
  });
  monthBars = computed<Bar[]>(() => (this.d()?.months ?? []).map(m => ({
    key: m.month, label: m.label.slice(0, 3), value: m.revenue, title: `${m.label}: ${rs(m.revenue)}, occupancy ${pc(m.occupancyPercent)}, ADR ${rs(m.adr)}`,
  })));

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
  dayLabel(iso: string) { return new Date(iso + 'T12:00:00Z').toLocaleDateString('en-IN', { weekday: 'narrow', day: 'numeric', timeZone: 'UTC' }); }
  monthName(iso: string) { return new Date(iso + 'T12:00:00Z').toLocaleDateString('en-IN', { month: 'long', timeZone: 'UTC' }); }
}

function fmt(n: number | null, unit: string) { return n === null ? null : `${n > 0 ? '+' : n < 0 ? '−' : ''}${Math.abs(n).toLocaleString('en-IN')}${unit}`; }
