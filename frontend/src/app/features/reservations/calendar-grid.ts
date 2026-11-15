import { Component, computed, input, output } from '@angular/core';
import { CdkDrag, CdkDragDrop, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { Calendar, CalendarBlock, CalendarBooking } from '../../core/models';
import { addDays, label, shortDate } from '../rooms/rooms.api';
import { guestsText, statusLabel } from './reservations.api';
import { CalRow, MOVABLE, Placement, blocksByRow, bookingsByRow, buildRows, place } from './calendar.model';

export interface CalendarDrop { booking: CalendarBooking; target: CalRow; }

const DOW = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

/** What the booking has paid so far (Sprint 10): paid in full, part paid, or nothing recorded yet. */
export function paymentText(s: CalendarBooking['paymentStatus']): string {
  return s === 'PAID' ? 'Paid' : s === 'PARTIAL' ? 'Part paid' : 'No payment recorded';
}

/**
 * The calendar grid: one row per room (or dorm bed), one column per day, bookings and blocks as bars. A booking can be
 * dragged up or down to another row (its dates never change). The grid never moves a bar itself: it reports the drop,
 * and the page asks the server; the bar moves only when the reloaded calendar says so.
 */
@Component({
  selector: 'app-calendar-grid',
  imports: [CdkDropListGroup, CdkDropList, CdkDrag],
  template: `
    <div class="scroll">
      <div class="grid" cdkDropListGroup [style.--days]="days().length" [style.--day-w]="dayWidth()" role="grid"
           [attr.aria-label]="'Bookings by room, ' + days().length + ' days'">
        <div class="row head" role="row">
          <div class="label" role="columnheader">Room</div>
          @for (d of header(); track d.iso) {
            <div class="day" role="columnheader" [class.today]="d.iso === cal().today" [class.weekend]="d.weekend">
              <small>{{ d.dow }}</small> <b>{{ d.num }}</b>
            </div>
          }
        </div>
        @for (v of view(); track v.row.key) {
          @if (v.row.first) { <div class="group" role="row"><span role="rowheader">{{ v.row.typeName }}</span></div> }
          <div class="row" role="row" cdkDropList [cdkDropListData]="v.row" cdkDropListSortingDisabled
               (cdkDropListDropped)="drop($event)" [class.inactive]="!v.row.isActive" [attr.data-row]="v.row.label">
            <div class="label" role="rowheader">{{ v.row.label }}@if (!v.row.isActive) { <small>, inactive</small> }</div>
            @for (d of header(); track d.iso) {
              <div class="cell" role="gridcell" [style.grid-column]="$index + 2" [class.today]="d.iso === cal().today" [class.weekend]="d.weekend"></div>
            }
            @for (k of v.blocks; track k.item.id) {
              <div class="block" [style.grid-column]="col(k.p)" [class.cut-start]="k.p.cutStart" [class.cut-end]="k.p.cutEnd"
                   [attr.title]="blockTitle(k.item)">
                <span>{{ label(k.item.blockType) }}: {{ k.item.reason }}</span>
              </div>
            }
            @for (b of v.bars; track b.item.reservationUnitId) {
              <button type="button" class="bar" [class]="'bar s-' + b.item.status.toLowerCase()" cdkDrag [cdkDragData]="b.item"
                      cdkDragLockAxis="y" [cdkDragDisabled]="!canMove() || !movable(b.item)"
                      [style.grid-column]="col(b.p)" [class.cut-start]="b.p.cutStart" [class.cut-end]="b.p.cutEnd"
                      [attr.title]="title(b.item)" [attr.data-ref]="b.item.reference" (click)="opened.emit(b.item)">
                <span class="name">{{ b.item.guestName }}</span> <span class="ref">{{ b.item.reference }}</span>
              </button>
            }
          </div>
        }
      </div>
    </div>
  `,
  styles: `
    :host { display: block; }
    .scroll { overflow-x: auto; border: 1px solid var(--ro-line-mid); border-radius: 10px; background: var(--ro-paper); }
    .grid { --label-w: 150px; min-width: calc(var(--label-w) + var(--days) * var(--day-w)); }
    .row { display: grid; grid-template-columns: var(--label-w) repeat(var(--days), minmax(var(--day-w), 1fr));
           grid-template-rows: 40px; border-top: 1px solid var(--ro-line-soft); position: relative; }
    .row.head { border-top: 0; grid-template-rows: 44px; position: sticky; top: 0; z-index: 3; background: var(--ro-hover); }
    .label { grid-column: 1; grid-row: 1; position: sticky; left: 0; z-index: 2; background: var(--ro-paper); padding: 0 10px;
             display: flex; align-items: center; gap: var(--space-1); font-size: 13px; border-right: 1px solid var(--ro-line-mid); white-space: nowrap;
             overflow: hidden; text-overflow: ellipsis; }
    .head .label { background: var(--ro-hover); font-weight: 600; }
    .day { grid-row: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; font-size: 12px;
           color: var(--ro-mute); border-left: 1px solid var(--ro-line-soft); line-height: 1.1; }
    .day b { color: var(--ro-ink); font-size: 13px; }
    .day.today { background: var(--ro-ok-bg); }
    .day.today b { color: var(--ro-tide-mid); }
    .cell { grid-row: 1; border-left: 1px solid var(--ro-tint); }
    .cell.weekend, .day.weekend { background: var(--ro-hover); }
    .cell.today { background: var(--ro-wash); }
    .group { padding: 6px 10px; font-size: 12px; font-weight: 600; color: var(--ro-ink-soft); background: var(--ro-wash); border-top: 1px solid var(--ro-line-mid);
             position: sticky; left: 0; }
    .inactive .label { color: var(--ro-mute); }
    .inactive .cell { background: repeating-linear-gradient(135deg, var(--ro-hover) 0 6px, var(--ro-sunk) 6px 12px); }
    .bar, .block { grid-row: 1; margin: 5px 2px; border-radius: 6px; font-size: 12px; padding: 0 var(--space-2); min-width: 0; overflow: hidden;
                   white-space: nowrap; text-overflow: ellipsis; display: flex; align-items: center; gap: 6px; z-index: 1; }
    .bar { border: 1px solid transparent; cursor: grab; font: inherit; font-size: 12px; text-align: left; }
    .bar[disabled], .bar.cdk-drag-disabled { cursor: pointer; }
    .bar .name { font-weight: 600; overflow: hidden; text-overflow: ellipsis; }
    .bar .ref { opacity: .75; font-variant-numeric: tabular-nums; }
    .s-confirmed { background: var(--ro-ok-bg); color: var(--ro-ok); border-color: var(--ro-ok-line); }
    .s-pending { background: var(--ro-warn-bg); color: var(--ro-warn-strong); border-color: var(--ro-warn-line); border-style: dashed; }
    .s-checked_in { background: var(--ro-info-bg); color: var(--ro-info-mid); border-color: var(--ro-info-line); }
    .s-checked_out { background: var(--ro-line-soft); color: var(--ro-ink-soft); border-color: var(--ro-on-tide); }
    .block { background: repeating-linear-gradient(135deg, var(--ro-off-bg) 0 6px, var(--ro-off-bg) 6px 12px); color: var(--ro-off); border: 1px solid var(--ro-error-line); }
    .cut-start { border-top-left-radius: 0; border-bottom-left-radius: 0; margin-left: 0; }
    .cut-end { border-top-right-radius: 0; border-bottom-right-radius: 0; margin-right: 0; }
    .cdk-drag-preview { box-shadow: 0 6px 18px var(--ro-shadow-lift); border-radius: 6px; display: flex; align-items: center; padding: 0 var(--space-2);
                        font-size: 12px; }
    .cdk-drag-placeholder { opacity: .35; }
    .row.cdk-drop-list-receiving { background: var(--ro-hover); }
    .row.cdk-drop-list-dragging .label { color: var(--ro-tide-mid); }
  `,
})
export class CalendarGrid {
  cal = input.required<Calendar>();
  days = input.required<string[]>();
  canMove = input(false);
  dropped = output<CalendarDrop>();
  opened = output<CalendarBooking>();

  label = label;

  dayWidth = computed(() => (this.days().length <= 1 ? '360px' : this.days().length <= 7 ? '120px' : '44px'));

  header = computed(() => this.days().map(iso => {
    const dow = new Date(iso + 'T00:00:00Z').getUTCDay();
    return { iso, dow: DOW[dow], num: this.days().length <= 7 ? shortDate(iso) : String(+iso.slice(8, 10)), weekend: dow === 0 || dow === 6 };
  }));

  view = computed(() => {
    const cal = this.cal();
    const days = this.days();
    const from = days[0], to = addDays(days[days.length - 1], 1);
    const rows = buildRows(cal.rooms);
    const bookings = bookingsByRow(cal.bookings);
    const blocks = blocksByRow(rows, cal.blocks);
    const placed = <T extends { startDate: string; endDate: string }>(items: T[] | undefined) =>
      (items ?? []).map(item => ({ item, p: place(item.startDate, item.endDate, from, to) }))
        .filter((x): x is { item: T; p: Placement } => x.p !== null);
    return rows.map(row => ({ row, bars: placed(bookings.get(row.key)), blocks: placed(blocks.get(row.key)) }));
  });

  col(p: Placement) { return `${p.col + 1} / span ${p.span}`; }
  movable(b: CalendarBooking) { return MOVABLE.includes(b.status); }

  title(b: CalendarBooking) {
    return `${b.guestName}, ${b.reference}, ${statusLabel(b.status)}\n${shortDate(b.startDate)} – ${shortDate(b.endDate)}, ` +
      `${guestsText(b.adults, b.children)}\nSource: ${b.sourceName}, ${paymentText(b.paymentStatus)}`;
  }

  blockTitle(k: CalendarBlock) {
    return `${label(k.blockType)}: ${k.reason}\n${shortDate(k.startDate)} – ${shortDate(k.endDate)} (available again)`;
  }

  drop(e: CdkDragDrop<CalRow, CalRow, CalendarBooking>) {
    if (e.container === e.previousContainer) return;
    this.dropped.emit({ booking: e.item.data, target: e.container.data });
  }
}
