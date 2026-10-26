import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { Floor, FloorTable, InHouseRoom, Order, OrderType } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { newKey } from '../reservations/reservations.api';
import { rupees } from '../settings/rates/rates.api';
import { RoomData, RoomDialog } from './order-dialogs';
import { RestaurantApi } from './restaurant.api';

/**
 * The restaurant floor (Sprint 14), for a waiter's phone: one tile per table — free, or its open order with what is still
 * to send, cooking or ready to serve — plus room-service and takeaway orders. Tap a free table to open an order there.
 */
@Component({
  selector: 'app-restaurant-floor',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page floor">
      <div class="head">
        <div>
          <h1>Restaurant</h1>
          <p class="mute">Tap a free table to take an order.</p>
        </div>
        <div class="tools">
          <a mat-button routerLink="/restaurant/orders">Today's orders</a>
          @if (canKitchen()) { <a mat-button routerLink="/restaurant/kitchen">Kitchen</a> }
          @if (canMenu()) { <a mat-button routerLink="/restaurant/menu">Menu</a> }
          <button mat-icon-button (click)="load()" aria-label="Refresh"><mat-icon>refresh</mat-icon></button>
        </div>
      </div>

      @if (canCreate()) {
        <div class="new">
          <button mat-stroked-button (click)="roomService()" [disabled]="busy()"><mat-icon>room_service</mat-icon>Room service</button>
          <button mat-stroked-button (click)="open('TAKEAWAY', null, null)" [disabled]="busy()"><mat-icon>takeout_dining</mat-icon>Takeaway</button>
        </div>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

      @for (area of areas(); track area.name) {
        <h2>{{ area.name }}</h2>
        <div class="tables">
          @for (t of area.tables; track t.id) {
            <button class="table" [class.busy]="!!t.order" [class.ready]="(t.order?.ready ?? 0) > 0" [attr.data-table]="t.code"
                    (click)="tap(t)" [disabled]="busy() || (!t.order && !canCreate())">
              <span class="code">{{ t.code }}</span>
              <span class="seats">{{ t.seats }} seats</span>
              @if (t.order; as o) {
                <span class="num">{{ o.number }} · {{ rupees(o.total) }}</span>
                <span class="chips">
                  @if (o.unsent) { <i class="unsent">{{ o.unsent }} not sent</i> }
                  @if (o.inKitchen) { <i class="cooking">{{ o.inKitchen }} cooking</i> }
                  @if (o.ready) { <i class="ok">{{ o.ready }} ready</i> }
                </span>
              } @else { <span class="free">Free</span> }
            </button>
          }
        </div>
      } @empty {
        @if (floor()) { <p class="mute">No tables yet. @if (canMenu()) { Add them on the <a routerLink="/restaurant/menu">menu page</a>. } </p> }
        @else if (!error()) { <p class="mute">Loading…</p> }
      }

      @if (floor()?.otherOrders?.length) {
        <h2>Room service and takeaway</h2>
        <div class="others">
          @for (o of floor()!.otherOrders; track o.id) {
            <a class="other" [routerLink]="['/restaurant/orders', o.id]" [attr.data-order]="o.number">
              <b>{{ o.number }}</b> {{ o.where }}@if (o.guestName) { · {{ o.guestName }} }
              <span class="right">{{ rupees(o.total) }}</span>
              <span class="chips">
                @if (o.unsent) { <i class="unsent">{{ o.unsent }} not sent</i> }
                @if (o.inKitchen) { <i class="cooking">{{ o.inKitchen }} cooking</i> }
                @if (o.ready) { <i class="ok">{{ o.ready }} ready</i> }
              </span>
            </a>
          }
        </div>
      }
    </div>
  `,
  styles: `
    .floor { max-width: 1000px; padding-bottom: 60px; }
    .head { display: flex; justify-content: space-between; align-items: flex-start; gap: 8px; flex-wrap: wrap; }
    .head p { margin: 2px 0 0; }
    .tools { display: flex; align-items: center; gap: 2px; flex-wrap: wrap; }
    .mute { color: #5b6b6e; }
    .err { color: #b3261e; }
    .new { display: flex; gap: 8px; margin: 12px 0 4px; flex-wrap: wrap; }
    .new button { min-height: 48px; }
    h2 { font-size: 1rem; margin: 18px 0 8px; color: #33474a; }
    .tables { display: grid; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); gap: 10px; }
    .table { display: flex; flex-direction: column; align-items: flex-start; gap: 2px; min-height: 104px; padding: 12px; text-align: left;
             background: #fff; border: 1px solid #d9e0de; border-left: 6px solid #2e6b3c; border-radius: 12px; font: inherit; color: inherit; cursor: pointer; }
    .table.busy { border-left-color: #d29a2c; background: #fffaf0; }
    .table.ready { border-left-color: #1f6f8b; box-shadow: 0 0 0 2px #1f6f8b inset; }
    .table:disabled { cursor: default; opacity: .7; }
    .code { font-size: 1.5rem; font-weight: 700; }
    .seats { font-size: .8rem; color: #5b6b6e; }
    .free { color: #2e6b3c; font-weight: 600; margin-top: auto; }
    .num { font-weight: 600; margin-top: 4px; }
    .chips { display: flex; flex-wrap: wrap; gap: 4px; }
    .chips i { font-style: normal; font-size: .75rem; padding: 1px 8px; border-radius: 999px; }
    .unsent { background: #fbefd8; color: #8a5a00; } .cooking { background: #eef2f1; color: #33474a; } .ok { background: #e0eff5; color: #134e63; }
    .others { display: flex; flex-direction: column; gap: 8px; }
    .other { display: flex; flex-wrap: wrap; gap: 4px 8px; align-items: center; background: #fff; border: 1px solid #d9e0de; border-radius: 12px;
             padding: 12px; color: inherit; text-decoration: none; min-height: 48px; }
    .other .right { margin-left: auto; font-weight: 600; }
    .other .chips { flex-basis: 100%; }
    @media (max-width: 640px) { .tables { grid-template-columns: repeat(2, 1fr); } h1 { font-size: 1.35rem; } }
  `,
})
export class FloorPage {
  private api = inject(RestaurantApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private router = inject(Router);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);

  floor = signal<Floor | null>(null);
  error = signal('');
  busy = signal(false);
  rupees = rupees;
  private pid = computed(() => this.ctx.current()?.id ?? '');
  canCreate = computed(() => this.auth.can('pos_order.create', this.pid()));
  canKitchen = computed(() => this.auth.can('kot.read', this.pid()));
  canMenu = computed(() => this.auth.can('menu.update', this.pid()));
  areas = computed(() => {
    const out: { name: string; tables: FloorTable[] }[] = [];
    for (const t of this.floor()?.tables ?? []) {
      const name = t.area ?? 'Tables';
      (out.find(a => a.name === name) ?? out[out.push({ name, tables: [] }) - 1]).tables.push(t);
    }
    return out;
  });

  constructor() {
    effect(() => { if (this.pid()) this.load(); });
    // The floor keeps up with the kitchen (a table turns blue when its food is ready).
    const t = setInterval(() => { if (document.visibilityState === 'visible' && !this.busy()) this.load(); }, 20_000);
    inject(DestroyRef).onDestroy(() => clearInterval(t));
  }

  load() {
    this.api.floor(this.pid()).subscribe({
      next: f => { this.floor.set(f); this.error.set(''); },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  tap(t: FloorTable) {
    if (t.order) this.router.navigate(['/restaurant/orders', t.order.id]);
    else this.open('DINE_IN', t.id, null);
  }

  open(type: OrderType, tableId: string | null, unitId: string | null) {
    this.busy.set(true);
    this.api.create(this.pid(), { orderType: type, tableId, unitId, covers: null, note: null, items: null }, newKey()).subscribe({
      next: o => this.router.navigate(['/restaurant/orders', o.id]),
      error: e => { this.busy.set(false); this.snack.open(toProblem(e).detail, 'OK', { duration: 8000 }); this.load(); },
    });
  }

  roomService() {
    this.api.rooms(this.pid()).subscribe({
      next: rooms => {
        const data: RoomData<Order> = {
          title: 'Room service', intro: 'Which room is the order for?', button: 'Open order', rooms, selected: null, field: 'unitId',
          run: (r: InHouseRoom) => this.api.create(this.pid(), { orderType: 'ROOM_SERVICE', tableId: null, unitId: r.unitId, covers: null, note: null, items: null }, newKey()),
        };
        this.dialog.open(RoomDialog<Order>, { data, maxWidth: '100vw' }).afterClosed()
          .subscribe((o?: Order) => { if (o) this.router.navigate(['/restaurant/orders', o.id]); });
      },
      error: e => this.snack.open(toProblem(e).detail, 'OK', { duration: 8000 }),
    });
  }
}
