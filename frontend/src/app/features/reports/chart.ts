import { Component, DestroyRef, ElementRef, NgZone, afterNextRender, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { ThemeService } from '../../core/theme';

/** One series on a chart: a name for the legend and the tooltip, and a value for each category (null = no value). */
export interface ChartSeries { name: string; data: (number | null)[]; }

/** A shaded stretch on a time chart (weekends, holidays): from and to are category labels. */
export interface ChartBand { from: string; to: string; label?: string; }

/**
 * What to draw. The chart picks every colour, font and line from the theme, so pages only describe the data.
 * - area: change over time (occupancy, revenue by day); several series share one axis, never two scales.
 * - bar: one value per period, or several side by side; `stack` stacks them.
 * - hbar: ranking (booking sources), longest first.
 * - donut: parts of a whole (revenue mix), at most six parts.
 */
export interface ChartSpec {
  kind: 'area' | 'bar' | 'hbar' | 'donut';
  categories: string[];
  series: ChartSeries[];
  /** How a value reads in the tooltip and on the axis ("₹1.2 L", "62 %"). */
  format?: (v: number) => string;
  /** Top of the value axis (100 for a percentage); default fits the data. */
  max?: number;
  stack?: boolean;
  bands?: ChartBand[];
  /** Index of today's category: its label is bold and later categories are faded (bar) or dashed (area). */
  today?: number;
  /** Long labels on the x-axis: show every nth one only. */
  every?: number;
}

type ECharts = import('echarts/core').ECharts;
let loader: Promise<typeof import('echarts/core')> | null = null;

/** ECharts, loaded once and only on screens with a chart (it is not part of the first download). */
function loadECharts() {
  loader ??= Promise.all([
    import('echarts/core'), import('echarts/charts'), import('echarts/components'), import('echarts/renderers'),
  ]).then(([core, charts, comps, renderers]) => {
    core.use([charts.LineChart, charts.BarChart, charts.PieChart, comps.GridComponent, comps.TooltipComponent,
      comps.LegendComponent, comps.MarkAreaComponent, comps.AriaComponent, renderers.SVGRenderer]);
    return core;
  });
  return loader;
}

/**
 * A chart from the reporting data (dashboard and reports), drawn with ECharts in the app's colours. It redraws when the
 * theme changes or the box is resized. The same numbers are always on the page as a table or as text, for screen readers
 * and for printing.
 */
@Component({
  selector: 'app-chart',
  template: `<div #box class="box" role="img" [attr.aria-label]="aria()" [style.height.px]="height()"></div>`,
  styles: `:host { display: block; min-width: 0; } .box { width: 100%; }`,
})
export class ChartComponent {
  spec = input.required<ChartSpec>();
  height = input(220);
  /** What the chart shows, for screen readers ("Occupancy for the next 30 nights"). */
  label = input('');

  private box = viewChild.required<ElementRef<HTMLDivElement>>('box');
  private theme = inject(ThemeService);
  private chart = signal<ECharts | null>(null);

  aria = computed(() => {
    const s = this.spec(); const f = s.format ?? ((v: number) => v.toLocaleString('en-IN'));
    const parts = s.series.map(x => `${x.name}: ` + s.categories.map((c, i) => x.data[i] === null ? null : `${c} ${f(x.data[i]!)}`)
      .filter(Boolean).slice(0, 40).join(', '));
    return `${this.label()}. ${parts.join('. ')}`;
  });

  constructor() {
    const destroy = inject(DestroyRef);
    const zone = inject(NgZone);
    afterNextRender(() => {
      // ECharts listens to the mouse all the time: keep that outside Angular so it never re-renders the page.
      zone.runOutsideAngular(() => loadECharts()).then(core => zone.runOutsideAngular(() => {
        if (destroyed) return;
        const c = core.init(this.box().nativeElement, undefined, { renderer: 'svg' });
        this.chart.set(c);
        const ro = new ResizeObserver(() => c.resize());
        ro.observe(this.box().nativeElement);
        destroy.onDestroy(() => { ro.disconnect(); c.dispose(); });
      }));
    });
    let destroyed = false;
    destroy.onDestroy(() => { destroyed = true; });
    effect(() => {
      const c = this.chart(); const spec = this.spec();
      this.theme.dark(); this.theme.choice();
      if (!c) return;
      // Read the colours after the theme attribute has changed.
      zone.runOutsideAngular(() => requestAnimationFrame(() => c.setOption(this.option(spec), { notMerge: true })));
    });
  }

  private option(s: ChartSpec) {
    const css = getComputedStyle(document.documentElement);
    const v = (n: string) => css.getPropertyValue(n).trim();
    const colours = [1, 2, 3, 4, 5, 6].map(i => v(`--ro-chart-${i}`));
    const ink = v('--ro-ink'), mute = v('--ro-mute'), line = v('--ro-line'), solid = v('--ro-solid'), font = v('--ro-font');
    const f = s.format ?? ((x: number) => x.toLocaleString('en-IN'));
    const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
    const base = {
      animation: !reduce, animationDuration: 600, color: colours,
      textStyle: { fontFamily: font, color: mute },
      aria: { enabled: false },
      tooltip: {
        trigger: s.kind === 'donut' ? 'item' : 'axis', confine: true,
        backgroundColor: solid, borderColor: line, textStyle: { color: ink, fontFamily: font, fontSize: 13 },
        axisPointer: { type: s.kind === 'area' ? 'line' : 'shadow', lineStyle: { color: mute, type: 'dashed' }, shadowStyle: { color: v('--ro-hover') } },
        valueFormatter: (x: unknown) => typeof x === 'number' ? f(x) : '—',
      },
      legend: s.series.length > 1 || s.kind === 'donut'
        ? { bottom: 0, left: 0, icon: 'roundRect', itemWidth: 10, itemHeight: 10, textStyle: { color: mute, fontFamily: font } }
        : undefined,
    };

    if (s.kind === 'donut') {
      const data = s.categories.map((c, i) => ({ name: c, value: s.series[0]?.data[i] ?? 0 })).filter(d => (d.value ?? 0) > 0);
      const narrow = this.box().nativeElement.clientWidth < 420;
      return {
        ...base,
        legend: narrow
          ? { ...base.legend, orient: 'horizontal', left: 0, right: 0, bottom: 0, top: 'auto',
              formatter: (name: string) => { const d = data.find(x => x.name === name); return d ? `${name} ${f(d.value as number)}` : name; } }
          : { ...base.legend, orient: 'vertical', right: 0, left: 'auto', top: 'middle', bottom: 'auto',
          formatter: (name: string) => { const d = data.find(x => x.name === name); return d ? `${name}  ${f(d.value as number)}` : name; } },
        series: [{
          type: 'pie', radius: narrow ? ['45%', '66%'] : ['58%', '84%'], center: narrow ? ['50%', '36%'] : ['30%', '50%'], avoidLabelOverlap: true, padAngle: 2,
          itemStyle: { borderRadius: 6, borderColor: 'transparent' }, label: { show: false },
          emphasis: { scale: true, scaleSize: 4 }, data,
        }],
      };
    }

    const cat = {
      type: 'category', data: s.categories, boundaryGap: s.kind !== 'area',
      axisLine: { lineStyle: { color: line } }, axisTick: { show: false },
      axisLabel: {
        color: mute, fontSize: 11, hideOverlap: true, interval: s.every ? s.every - 1 : 'auto',
        formatter: (val: string, i: number) => i === s.today ? `{b|${val}}` : val, rich: { b: { fontWeight: 700, color: ink } },
      },
    };
    const val = {
      type: 'value', max: s.max, splitNumber: s.kind === 'hbar' ? 2 : 4,
      axisLabel: { color: mute, fontSize: 11, formatter: (x: number) => f(x) },
      splitLine: { lineStyle: { color: line } },
    };
    const horizontal = s.kind === 'hbar';
    const series = s.series.map((x, i) => {
      if (s.kind === 'area') {
        return {
          type: 'line', name: x.name, data: x.data, smooth: 0.3, showSymbol: false, symbolSize: 8, connectNulls: false,
          lineStyle: { width: 2.5, type: i > 0 ? 'dashed' : 'solid' },
          areaStyle: i === 0 ? { opacity: 1, color: { type: 'linear', x: 0, y: 0, x2: 0, y2: 1,
            colorStops: [{ offset: 0, color: colours[0] + '55' }, { offset: 1, color: colours[0] + '05' }] } } : undefined,
          markArea: i === 0 && s.bands?.length ? {
            silent: true, itemStyle: { color: v('--ro-hover') },
            label: { color: mute, fontSize: 10, position: 'insideTop' },
            data: s.bands.map(b => [{ xAxis: b.from, name: b.label ?? '' }, { xAxis: b.to }]),
          } : undefined,
        };
      }
      return {
        type: 'bar', name: x.name, stack: s.stack ? 'all' : undefined, barMaxWidth: horizontal ? 18 : 22, barGap: '20%',
        itemStyle: { borderRadius: horizontal ? [0, 4, 4, 0] : s.stack ? 0 : [4, 4, 0, 0] },
        data: x.data.map((d, j) => ({ value: d, itemStyle: s.today !== undefined && j > s.today ? { opacity: 0.4 } : undefined })),
      };
    });
    return {
      ...base,
      grid: { left: 4, right: 12, top: 16, bottom: base.legend ? 34 : 4, containLabel: true },
      xAxis: horizontal ? val : cat,
      yAxis: horizontal ? { ...cat, inverse: true, axisLabel: { ...cat.axisLabel, color: ink, fontSize: 12 } } : val,
      series,
    };
  }
}
