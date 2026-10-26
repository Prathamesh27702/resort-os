import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { toProblem } from '../../core/http';
import { Menu, MenuItem, Order, OrderAction, OrderItem, OrderKot, OrderLineRequest } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { methodLabel } from '../payments/payments.api';
import { newKey } from '../reservations/reservations.api';
import { rupees, rupeesExact } from '../settings/rates/rates.api';
import { DiscountData, DiscountDialog, DishDialog, PayData, PayDialog, ReasonData, ReasonDialog, RoomData, RoomDialog } from './order-dialogs';
import { FOOD_LABEL, ORDER_TYPE_LABEL, RestaurantApi, kotLabel, oneTap, searchMenu } from './restaurant.api';

/**
 * One order (Sprint 14), for a waiter's phone. Dishes go on the order as "not sent" lines that can still be changed; Send
 * to kitchen puts them on a kitchen ticket (KOT). When the kitchen marks a ticket ready the waiter serves it, and once
 * everything is served the bill is paid here or charged to the guest's room. Every change sends the order's version: if
 * another waiter changed the order first, the change is refused and the page reloads with their lines.
 */
@Component({
  selector: 'app-restaurant-order',
  imports: [FormsModule, RouterLink, MatButtonModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSnackBarModule],
  template: `
    <div class="page order">
      <a mat-button routerLink="/restaurant" class="back"><mat-icon>arrow_back</mat-icon>Floor</a>
      @if (order(); as o) {
        <div class="head">
          <h1>{{ o.number }} <span class="where">{{ o.where }}</span></h1>
          <span [class]="'status ' + o.status">{{ o.status === 'OPEN' ? 'Open' : o.status === 'SETTLED' ? 'Settled' : 'Voided' }}</span>
        </div>
        <p class="mute">
          {{ typeLabel[o.orderType] }}@if (o.guestName) { · {{ o.guestName }} ({{ o.reservationReference }}) }
          · opened {{ time(o.createdAt) }}@if (o.openedByName) { by {{ o.openedByName }} }
        </p>
        @if (o.status === 'SETTLED') {
          <p class="banner ok" data-testid="settled">
            @if (o.settlement === 'ROOM_CHARGE') { Charged to {{ o.unitName ?? 'the room' }} on folio <a [routerLink]="['/folios', o.folioId]">{{ o.folioNumber }}</a> }
            @else { Paid by {{ method(o.paymentMethod!) }}@if (o.paymentReference) { ({{ o.paymentReference }}) } }
            · {{ time(o.settledAt!) }}@if (o.settledByName) { by {{ o.settledByName }} }
          </p>
        }
        @if (o.status === 'VOIDED') { <p class="banner off">Voided: {{ o.voidReason }}@if (o.voidedByName) { · {{ o.voidedByName }} }</p> }

        <section>
          <h2>On the order</h2>
          <ul class="lines">
            @for (l of o.items; track l.id) {
              <li [class.voided]="l.status === 'VOIDED'" [attr.data-line]="l.name">
                <div class="what">
                  <span [class]="'food ' + l.foodType" [attr.aria-label]="food[l.foodType]"></span>
                  <b>{{ l.quantity }}×</b> {{ l.name }}@if (l.variantName) { ({{ l.variantName }}) }
                  @if (l.modifiers.length) { <small class="mute">{{ l.modifiers.join(', ') }}</small> }
                  @if (l.note) { <small class="note">“{{ l.note }}”</small> }
                  <small [class]="'kstate ' + lineState(l).cls">{{ lineState(l).text }}</small>
                </div>
                <div class="amt">{{ rupeesExact(l.amount) }}</div>
                @if (l.status === 'NEW' && can('edit')) {
                  <div class="ctl">
                    <button mat-icon-button (click)="setQty(l, l.quantity - 1)" [disabled]="busy()" [attr.aria-label]="'One less ' + l.name"><mat-icon>remove</mat-icon></button>
                    <button mat-icon-button (click)="setQty(l, l.quantity + 1)" [disabled]="busy() || l.quantity >= 99" [attr.aria-label]="'One more ' + l.name"><mat-icon>add</mat-icon></button>
                  </div>
                } @else if (l.status === 'SENT' && can('void')) {
                  <div class="ctl"><button mat-button (click)="voidLine(l)" [disabled]="busy()" [attr.aria-label]="'Void ' + l.name">Void</button></div>
                }
              </li>
            } @empty { <li class="mute">Nothing yet. Add dishes below.</li> }
          </ul>
          @if (can('send')) {
            <button mat-flat-button class="big" (click)="send()" [disabled]="busy()" data-testid="send">
              <mat-icon>send</mat-icon>Send to kitchen ({{ unsent() }})</button>
          }
        </section>

        @if (o.kots.length) {
          <section>
            <h2>Kitchen tickets</h2>
            <ul class="kots">
              @for (k of o.kots; track k.id) {
                <li [attr.data-kot]="k.number">
                  <b>{{ k.number }}</b> · {{ k.lines }} line{{ k.lines === 1 ? '' : 's' }} · sent {{ time(k.sentAt) }}
                  <span [class]="'kstate ' + k.status">{{ kot(k.status) }}</span>
                  @if (k.status === 'READY' && can('serve')) {
                    <button mat-flat-button class="serve" (click)="serve(k)" [disabled]="busy()">Served</button>
                  }
                </li>
              }
            </ul>
          </section>
        }

        <section class="bill">
          <h2>Bill</h2>
          <dl>
            <dt>Food and drinks</dt><dd>{{ rupeesExact(o.subtotal) }}</dd>
            @if (o.discountAmount) {
              <dt>Discount{{ o.discountKind === 'PERCENT' ? ' ' + o.discountValue + '%' : '' }} <small class="mute">{{ o.discountReason }}</small></dt>
              <dd>−{{ rupeesExact(o.discountAmount) }}</dd>
            }
            @for (t of o.taxes; track t.code) { <dt>{{ t.code }} {{ t.rate }}%</dt><dd>{{ rupeesExact(t.amount) }}</dd> }
            <dt class="total">Total</dt><dd class="total" data-testid="total">{{ rupeesExact(o.total) }}</dd>
          </dl>
          @if (o.taxNote) { <p class="mute small">{{ o.taxNote }}</p> }
          @if (o.status === 'OPEN' && o.items.length && !can('pay') && !can('charge')) {
            <p class="mute small">{{ settleHint() }}</p>
          }
          <div class="acts">
            @if (can('charge')) { <button mat-flat-button class="big" (click)="charge()" [disabled]="busy()" data-testid="charge"><mat-icon>bed</mat-icon>Charge to room</button> }
            @if (can('pay')) { <button mat-flat-button class="big" (click)="pay()" [disabled]="busy()" data-testid="pay"><mat-icon>payments</mat-icon>Paid</button> }
            @if (can('discount')) { <button mat-stroked-button (click)="discount()" [disabled]="busy()">Discount</button> }
            @if (can('void')) { <button mat-button class="danger" (click)="voidOrder()" [disabled]="busy()">Void order</button> }
          </div>
        </section>

        @if (can('add')) {
          <section class="add">
            <h2>Add dishes</h2>
            <mat-form-field appearance="outline" class="full search">
              <mat-label>Search the menu</mat-label>
              <mat-icon matPrefix>search</mat-icon>
              <input matInput [ngModel]="q()" (ngModelChange)="q.set($event)" placeholder="e.g. naan" />
            </mat-form-field>
            @if (!q()) {
              <div class="cats" role="tablist" aria-label="Menu sections">
                @for (c of menu()?.categories ?? []; track c.id) {
                  <button role="tab" [class.on]="cat() === c.id" [attr.aria-selected]="cat() === c.id" (click)="cat.set(c.id)">{{ c.name }}</button>
                }
              </div>
            }
            <ul class="dishes">
              @for (i of dishes(); track i.id) {
                <li>
                  <button class="dish" (click)="pick(i)" [disabled]="busy() || !i.isAvailable" [attr.data-dish]="i.code">
                    <span [class]="'food ' + i.foodType" [attr.aria-label]="food[i.foodType]"></span>
                    <span class="nm">{{ i.name }}@if (!i.isAvailable) { <b class="out">Sold out</b> }
                      @if (i.variants.length) { <small class="mute">{{ portions(i) }}</small> }</span>
                    <span class="pr">{{ fromPrice(i) }}</span>
                    <mat-icon>{{ quick(i) ? 'add_circle' : 'tune' }}</mat-icon>
                  </button>
                </li>
              } @empty { <li class="mute">{{ menu() ? 'No dish matches.' : 'Loading the menu…' }}</li> }
            </ul>
          </section>
        }
      } @else if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      @else { <p class="mute">Loading…</p> }
    </div>
  `,
  styles: `
    .order { max-width: 760px; padding-bottom: 80px; }
    .back { margin-left: -8px; }
    .head { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    h1 { margin: 4px 0 0; } .where { font-weight: 400; color: #33474a; }
    .status { font-size: .8rem; font-weight: 600; padding: 2px 10px; border-radius: 999px; background: #fbefd8; color: #8a5a00; }
    .status.SETTLED { background: #e1f2e8; color: #1d5b36; } .status.VOIDED { background: #e3e7e6; color: #33474a; }
    .mute { color: #5b6b6e; } .small { font-size: .85rem; } .err { color: #b3261e; }
    .banner { padding: 10px 12px; border-radius: 10px; } .banner.ok { background: #e1f2e8; } .banner.off { background: #eef2f1; }
    section { background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 4px 14px 14px; margin-top: 12px; }
    h2 { font-size: 1rem; margin: 12px 0 8px; }
    ul { list-style: none; margin: 0; padding: 0; }
    .lines li { display: grid; grid-template-columns: 1fr auto; gap: 2px 8px; padding: 8px 0; border-bottom: 1px solid #eef2f1; }
    .lines li.voided .what, .lines li.voided .amt { text-decoration: line-through; color: #7a8a8d; }
    .what small { display: block; }
    .note { color: #5b3a00; }
    .amt { text-align: right; font-variant-numeric: tabular-nums; }
    .ctl { grid-column: 1 / -1; display: flex; justify-content: flex-end; gap: 4px; }
    .kstate { font-size: .75rem; font-weight: 600; color: #5b6b6e; }
    .kstate.NEW, .kstate.unsent { color: #8a5a00; } .kstate.READY { color: #134e63; } .kstate.SERVED { color: #1d5b36; } .kstate.VOIDED { color: #b3261e; }
    .lines .kstate { text-decoration: none; display: inline-block; }
    .kots li { display: flex; align-items: center; flex-wrap: wrap; gap: 6px; padding: 8px 0; border-bottom: 1px solid #eef2f1; }
    .kots .kstate { padding: 2px 10px; border-radius: 999px; background: #eef2f1; }
    .kots .kstate.READY { background: #e0eff5; }
    .serve { margin-left: auto; min-height: 44px; }
    .big { min-height: 52px; font-size: 1.05rem; border-radius: 10px; margin-top: 12px; width: 100%; }
    dl { display: grid; grid-template-columns: 1fr auto; gap: 4px 12px; margin: 0; }
    dd { margin: 0; text-align: right; font-variant-numeric: tabular-nums; }
    .total { font-weight: 700; font-size: 1.15rem; border-top: 1px solid #d9e0de; padding-top: 6px; }
    .acts { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 4px; }
    .acts .big { flex: 1 1 200px; }
    .danger { color: #b3261e; }
    .full { width: 100%; }
    .cats { display: flex; gap: 6px; overflow-x: auto; padding-bottom: 6px; scrollbar-width: none; }
    .cats button { flex: 0 0 auto; border: 1px solid #d9e0de; background: #fff; border-radius: 999px; padding: 8px 14px; font: inherit; min-height: 40px; cursor: pointer; }
    .cats button.on { background: #0e3b43; color: #fff; border-color: #0e3b43; }
    .dish { all: unset; box-sizing: border-box; display: flex; align-items: center; gap: 10px; width: 100%; min-height: 52px; padding: 8px 4px;
            border-bottom: 1px solid #eef2f1; cursor: pointer; }
    .dish:focus-visible { outline: 2px solid #8a2449; }
    .dish[disabled] { opacity: .55; cursor: default; }
    .dish .nm { flex: 1; } .dish .nm small { display: block; }
    .dish .pr { font-variant-numeric: tabular-nums; }
    .dish mat-icon { color: #0e3b43; }
    .out { margin-left: 6px; font-size: .75rem; color: #b3261e; }
    .food { display: inline-block; flex: 0 0 auto; width: 12px; height: 12px; border: 2px solid; border-radius: 2px; margin-right: 4px; vertical-align: middle; }
    .food.VEG { border-color: #2e7d32; background: radial-gradient(#2e7d32 40%, transparent 45%); }
    .food.NON_VEG { border-color: #8c1d18; background: radial-gradient(#8c1d18 40%, transparent 45%); }
    .food.EGG { border-color: #c77700; background: radial-gradient(#c77700 40%, transparent 45%); }
    @media (max-width: 640px) { h1 { font-size: 1.35rem; } section { padding: 2px 12px 12px; } }
  `,
})
export class OrderPage {
  id = input.required<string>();
  private api = inject(RestaurantApi);
  private ctx = inject(PropertyContext);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);

  order = signal<Order | null>(null);
  menu = signal<Menu | null>(null);
  error = signal('');
  busy = signal(false);
  q = signal('');
  cat = signal<string | null>(null);
  food = FOOD_LABEL;
  typeLabel = ORDER_TYPE_LABEL;
  kot = kotLabel;
  method = methodLabel;
  rupeesExact = rupeesExact;
  quick = oneTap;
  private pid = computed(() => this.ctx.current()?.id ?? '');
  unsent = computed(() => this.order()?.items.filter(l => l.status === 'NEW').reduce((n, l) => n + l.quantity, 0) ?? 0);
  dishes = computed(() => {
    const q = this.q();
    if (q) return searchMenu(this.menu(), q);
    const c = this.menu()?.categories.find(x => x.id === this.cat()) ?? this.menu()?.categories[0];
    return c?.items.filter(i => i.isActive) ?? [];
  });
  /** Why the bill can't be settled yet, in words. */
  settleHint = computed(() => {
    const o = this.order()!;
    if (o.items.some(l => l.status === 'NEW')) return 'Send every dish to the kitchen before settling the bill.';
    if (o.kots.some(k => ['NEW', 'PREPARING', 'READY'].includes(k.status))) return 'The bill can be settled once every kitchen ticket is served.';
    return '';
  });

  constructor() {
    effect(() => {
      if (!this.pid()) return;
      this.load();
      this.api.menu(this.pid()).subscribe({ next: m => { this.menu.set(m); if (!this.cat()) this.cat.set(m.categories[0]?.id ?? null); } });
    });
  }

  load() {
    this.api.order(this.pid(), this.id()).subscribe({
      next: o => { this.order.set(o); this.error.set(''); },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  can(a: OrderAction) { return this.order()?.nextActions.includes(a) ?? false; }
  time(iso: string) { return inZone(iso, this.ctx.current()?.timezone ?? 'Asia/Kolkata', { hour: '2-digit', minute: '2-digit', hour12: false }); }
  portions(i: MenuItem) { return i.variants.map(v => v.name).join(' / '); }
  fromPrice(i: MenuItem) { return i.variants.length ? rupees(Math.min(...i.variants.map(v => v.price))) + '+' : rupees(i.price); }

  lineState(l: OrderItem): { text: string; cls: string } {
    if (l.status === 'VOIDED') return { text: `Voided: ${l.voidReason}`, cls: 'VOIDED' };
    if (l.status === 'NEW') return { text: 'Not sent yet', cls: 'unsent' };
    return { text: `${l.kotNumber} · ${kotLabel(l.kitchenStatus ?? 'NEW')}`, cls: l.kitchenStatus ?? 'NEW' };
  }

  /** Runs a change and shows the order it returns. A refusal says why; a version conflict reloads the other waiter's order. */
  private run(call: Observable<Order>, done?: string) {
    this.busy.set(true);
    call.subscribe({
      next: o => { this.busy.set(false); this.order.set(o); if (done) this.snack.open(done, undefined, { duration: 2500 }); },
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const fields = Object.values(p.errors ?? {}).flat();
        this.snack.open(fields.length ? fields.join(' ') : p.detail, 'OK', { duration: 8000 });
        if (p.status === 409 || p.status === 422) this.load();
      },
    });
  }

  pick(i: MenuItem) {
    if (oneTap(i)) { this.add({ menuItemId: i.id, variantId: null, modifierIds: [], quantity: 1, note: null }, i.name); return; }
    this.dialog.open(DishDialog, { data: { item: i }, maxWidth: '100vw', autoFocus: false }).afterClosed()
      .subscribe((line?: OrderLineRequest) => { if (line) this.add(line, i.name); });
  }

  private add(line: OrderLineRequest, name: string) {
    this.run(this.api.addItems(this.pid(), this.order()!, [line]), `${line.quantity}× ${name} added.`);
  }

  setQty(l: OrderItem, qty: number) {
    const o = this.order()!;
    this.run(qty < 1 ? this.api.removeItem(this.pid(), o, l.id) : this.api.updateItem(this.pid(), o, l.id, qty, l.note));
  }

  send() { this.run(this.api.send(this.pid(), this.order()!, null), 'Sent to the kitchen.'); }
  serve(k: OrderKot) { this.run(this.api.serve(this.pid(), this.order()!, k.id), `${k.number} served.`); }

  voidLine(l: OrderItem) {
    const data: ReasonData<Order> = {
      title: `Void ${l.quantity}× ${l.name}`, intro: 'It is struck off the kitchen ticket and the bill. The reason is kept in the audit trail.',
      button: 'Void', placeholder: 'Guest changed their mind', run: r => this.api.voidItem(this.pid(), this.order()!, l.id, r),
    };
    this.openThen(ReasonDialog<Order>, data, 'Voided.');
  }

  voidOrder() {
    const o = this.order()!;
    const data: ReasonData<Order> = {
      title: `Void ${o.number}`, intro: 'Every dish is struck off, the kitchen stops on anything not ready, and nothing is billed.',
      button: 'Void order', placeholder: 'Guest left before ordering', run: r => this.api.voidOrder(this.pid(), this.order()!, r),
    };
    this.openThen(ReasonDialog<Order>, data, `${o.number} voided.`);
  }

  discount() {
    const o = this.order()!;
    const data: DiscountData<Order> = {
      subtotal: o.subtotal, kind: o.discountKind, value: o.discountValue, reason: o.discountReason,
      run: (k, v, r) => this.api.discount(this.pid(), this.order()!, k, v, r),
    };
    this.openThen(DiscountDialog<Order>, data);
  }

  pay() {
    const key = newKey();
    const data: PayData<Order> = { total: this.order()!.total, run: (m, ref) => this.api.pay(this.pid(), this.order()!, m, ref, key) };
    this.openThen(PayDialog<Order>, data, 'Bill settled.');
  }

  charge() {
    const o = this.order()!;
    const key = newKey();
    this.api.rooms(this.pid()).subscribe({
      next: rooms => {
        const data: RoomData<Order> = {
          title: `Charge ${rupeesExact(o.total)} to a room`, intro: 'The bill goes on the guest’s folio as one line, with its GST, and is paid at check-out.',
          button: 'Charge to room', rooms, selected: o.reservationId, field: 'reservationId',
          run: r => this.api.chargeToRoom(this.pid(), this.order()!, r.reservationId, key),
        };
        this.openThen(RoomDialog<Order>, data, 'Charged to the room.');
      },
      error: e => this.snack.open(toProblem(e).detail, 'OK', { duration: 8000 }),
    });
  }

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  private openThen(component: any, data: unknown, done?: string) {
    this.dialog.open(component, { data, maxWidth: '100vw' }).afterClosed().subscribe((o?: Order) => {
      if (!o) { this.load(); return; }
      this.order.set(o);
      if (done) this.snack.open(done, undefined, { duration: 2500 });
    });
  }
}
