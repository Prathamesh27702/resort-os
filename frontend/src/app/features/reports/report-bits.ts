import { Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { ReportFreshness } from '../../core/models';
import { freshText } from './reports.api';

export interface Bar { key: string; label: string; value: number | null; title: string; mute?: boolean; mark?: boolean; }

/**
 * A plain bar chart (one series): a bar per point, the label under it. The values are also given as a table in a
 * collapsed "as a table" block by the pages, for screen readers and printing.
 */
@Component({
  selector: 'app-bars',
  template: `
    <div class="chart" role="img" [attr.aria-label]="aria()" [style.height.px]="height()"
         [style.grid-template-columns]="'repeat(' + bars().length + ', minmax(0, 1fr))'">
      @for (b of bars(); track b.key; let i = $index) {
        <div class="col" [class.mute]="b.mute" [class.mark]="b.mark" [title]="b.title">
          <div class="bar"><span [style.height.%]="pct(b.value)" [style.background]="color()"></span></div>
          <small [class.hide]="!showLabel(i)">{{ b.label }}</small>
        </div>
      }
    </div>
  `,
  styles: `
    .chart { display: grid; gap: 2px; border-bottom: 1px solid var(--ro-line); padding-bottom: 2px; }
    .col { display: flex; flex-direction: column; align-items: center; gap: 3px; min-width: 0; }
    .col.mute { opacity: .45; } .col.mark small { font-weight: 700; color: var(--ro-tide); }
    .bar { flex: 1; width: 100%; display: flex; align-items: flex-end; justify-content: center; }
    .bar span { width: 70%; max-width: 22px; border-radius: 3px 3px 0 0; min-height: 1px; }
    small { color: var(--ro-mute); font-size: .68rem; white-space: nowrap; } small.hide { visibility: hidden; }
  `,
})
export class BarsComponent {
  bars = input.required<Bar[]>();
  /** The top of the scale; default the largest value. 100 for a percentage. */
  max = input<number | null>(null);
  height = input(150);
  color = input('var(--ro-sea-bright)');
  label = input('');
  /** Label every nth bar (1: all). */
  every = input(1);
  private top = computed(() => this.max() ?? Math.max(1, ...this.bars().map(b => b.value ?? 0)));
  aria = computed(() => `${this.label()}: ` + this.bars().map(b => b.title).join('; '));
  pct(v: number | null) { return v === null ? 0 : Math.max(0, Math.min(100, Math.round(1000 * v / this.top()) / 10)); }
  showLabel(i: number) { return this.every() <= 1 || i % this.every() === 0; }
}

/** "Updated 4 min ago (every 15 min) · Refresh now". */
@Component({
  selector: 'app-fresh',
  imports: [MatButtonModule, MatIconModule],
  template: `
    <p class="fresh no-print" data-testid="freshness">
      <mat-icon inline>update</mat-icon> {{ text() }}
      @if (canRefresh()) { <button mat-button type="button" (click)="refresh.emit()" [disabled]="busy()">{{ busy() ? 'Adding up…' : 'Refresh now' }}</button> }
    </p>
  `,
  styles: `.fresh { color: var(--ro-mute); font-size: .85rem; display: flex; align-items: center; gap: var(--space-1); margin: 2px 0; flex-wrap: wrap; }`,
})
export class FreshComponent {
  freshness = input<ReportFreshness | null | undefined>(null);
  canRefresh = input(true);
  busy = input(false);
  refresh = output<void>();
  text = computed(() => freshText(this.freshness()));
}
