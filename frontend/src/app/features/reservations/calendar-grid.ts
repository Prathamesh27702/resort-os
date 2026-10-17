import { Component, computed, input, output } from '@angular/core';
import { CdkDrag, CdkDragDrop, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { Calendar, CalendarBlock, CalendarBooking } from '../../core/models';
import { addDays, label, shortDate } from '../rooms/rooms.api';
import { guestsText, statusLabel } from './reservations.api';
import { CalRow, MOVABLE, Placement, blocksByRow, bookingsByRow, buildRows, place } from './calendar.model';

export interface CalendarDrop { booking: CalendarBooking; target: CalRow; }

const DOW = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

/** "No payment recorded" until payments arrive (Sprint 10). */
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
            <div class="label" role="rowheader">{{ v.row.label }}@if (!v.row.isActive) { <small> · inactive</small> }</div>
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
    .scroll { overflow-x: auto; border: 1px solid #dfe6e4; border-radius: 10px; background: #fff; }
    .grid { --label-w: 150px; min-width: calc(var(--label-w) + var(--days) * var(--day-w)); }
    .row { display: grid; grid-template-columns: var(--label-w) repeat(var(--days), minmax(var(--day-w), 1fr));
           grid-template-rows: 40px; border-top: 1px solid #edf1f0; position: relative; }
    .row.head { border-top: 0; grid-template-rows: 44px; position: sticky; top: 0; z-index: 3; background: #f7faf9; }
    .label { grid-column: 1; grid-row: 1; position: sticky; left: 0; z-index: 2; background: #fff; padding: 0 10px;
             display: flex; align-items: center; gap: 4px; font-size: 13px; border-right: 1px solid #dfe6e4; white-space: nowrap;
             overflow: hidden; text-overflow: ellipsis; }
    .head .label { background: #f7faf9; font-weight: 600; }
    .day { grid-row: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; font-size: 12px;
           color: #5b6b6e; border-left: 1px solid #edf1f0; line-height: 1.1; }
    .day b { color: #1b2a2e; font-size: 13px; }
    .day.today { background: #e3f1ef; }
    .day.today b { color: #00695c; }
    .cell { grid-row: 1; border-left: 1px solid #f1f4f3; }
    .cell.weekend, .day.weekend { background: #fafbfb; }
    .cell.today { background: #f0f8f6; }
    .group { padding: 6px 10px; font-size: 12px; font-weight: 600; color: #3d5054; background: #f3f6f5; border-top: 1px solid #dfe6e4;
             text-transform: uppercase; letter-spacing: .03em; position: sticky; left: 0; }
    .inactive .label { color: #8a979a; }
    .inactive .cell { background: repeating-linear-gradient(135deg, #fafafa 0 6px, #f2f2f2 6px 12px); }
    .bar, .block { grid-row: 1; margin: 5px 2px; border-radius: 6px; font-size: 12px; padding: 0 8px; min-width: 0; overflow: hidden;
                   white-space: nowrap; text-overflow: ellipsis; display: flex; align-items: center; gap: 6px; z-index: 1; }
    .bar { border: 1px solid transparent; cursor: grab; font: inherit; font-size: 12px; text-align: left; }
    .bar[disabled], .bar.cdk-drag-disabled { cursor: pointer; }
    .bar .name { font-weight: 600; overflow: hidden; text-overflow: ellipsis; }
    .bar .ref { opacity: .75; font-variant-numeric: tabular-nums; }
    .s-confirmed { background: #d7efe0; color: #14532d; border-color: #9fd4b4; }
    .s-pending { background: #fbefd8; color: #7a4f00; border-color: #efcd8a; border-style: dashed; }
    .s-checked_in { background: #dbe8fb; color: #163e7a; border-color: #a7c3ef; }
    .s-checked_out { background: #eceff0; color: #4d5b5e; border-color: #d3d9da; }
    .block { background: repeating-linear-gradient(135deg, #fde3e1 0 6px, #f9d0cc 6px 12px); color: #7f1d1d; border: 1px solid #f2b8b2; }
    .cut-start { border-top-left-radius: 0; border-bottom-left-radius: 0; margin-left: 0; }
    .cut-end { border-top-right-radius: 0; border-bottom-right-radius: 0; margin-right: 0; }
    .cdk-drag-preview { box-shadow: 0 6px 18px rgba(0,0,0,.18); border-radius: 6px; display: flex; align-items: center; padding: 0 8px;
                        font-size: 12px; }
    .cdk-drag-placeholder { opacity: .35; }
    .row.cdk-drop-list-receiving { background: #f7fbfa; }
    .row.cdk-drop-list-dragging .label { color: #00695c; }
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
    return `${b.guestName} · ${b.reference} · ${statusLabel(b.status)}\n${shortDate(b.startDate)} – ${shortDate(b.endDate)} · ` +
      `${guestsText(b.adults, b.children)}\nSource: ${b.sourceName} · ${paymentText(b.paymentStatus)}`;
  }

  blockTitle(k: CalendarBlock) {
    return `${label(k.blockType)}: ${k.reason}\n${shortDate(k.startDate)} – ${shortDate(k.endDate)} (available again)`;
  }

  drop(e: CdkDragDrop<CalRow, CalRow, CalendarBooking>) {
    if (e.container === e.previousContainer) return;
    this.dropped.emit({ booking: e.item.data, target: e.container.data });
  }
}
