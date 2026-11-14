import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { Menu, MenuItem, Order, OrderAction, OrderItem, OrderKot, OrderLineRequest } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { IndiaApi } from '../india/india.api';
import { KotTicket } from '../india/receipt';
import { ReceiptData, ReceiptDialog } from '../india/receipt-dialog';
import { methodLabel } from '../payments/payments.api';
import { newKey } from '../reservations/reservations.api';
import { rupees, rupeesExact } from '../settings/rates/rates.api';
import { DiscountData, DiscountDialog, DishDialog, PayData, PayDialog, ReasonData, ReasonDialog, RoomData, RoomDialog } from './order-dialogs';
import { FOOD_LABEL, KITCHEN_REFRESH_MS, ORDER_TYPE_LABEL, RestaurantApi, kotLabel, oneTap, searchMenu } from './restaurant.api';

/**
 * One order (Sprint 14), for a waiter's phone. Dishes go on the order as "not sent" lines that can still be changed; Send
 * to kitchen puts them on a kitchen ticket (KOT). When the kitchen marks a ticket ready the waiter serves it, and once
 * everything is served the bill is paid here or charged to the guest's room. Every change sends the order's version: if
 * another waiter changed the order first, the change is refused and the page reloads with their lines. While a ticket is
 * still cooking the page asks for the order every 10 seconds, so Ready appears without a reload.
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
          {{ typeLabel[o.orderType] }}@if (o.guestName) {, {{ o.guestName }} ({{ o.reservationReference }})},
         opened {{ time(o.createdAt) }}@if (o.openedByName) { by {{ o.openedByName }} }
        </p>
        @if (o.status === 'SETTLED') {
          <p class="banner ok" data-testid="settled">
            @if (o.settlement === 'ROOM_CHARGE') { Charged to {{ o.unitName ?? 'the room' }} on folio <a [routerLink]="['/folios', o.folioId]">{{ o.folioNumber }}</a> }
            @else { Paid by {{ method(o.paymentMethod!) }}@if (o.paymentReference) { ({{ o.paymentReference }}) }},
           {{ time(o.settledAt!) }}@if (o.settledByName) { by {{ o.settledByName }} }
          </p>
        }
        @if (o.status === 'VOIDED') { <p class="banner off">Voided: {{ o.voidReason }}@if (o.voidedByName) {, {{ o.voidedByName }} }</p> }

        <section>
          <h2>On the order</h2>
          <ul class="lines">
            @for (l of o.items; track l.id) {
              <li [class.voided]="l.status === 'VOIDED'" [attr.data-line]="l.name">
                <div class="what">
                  <span [class]="'food ' + l.foodType" [attr.aria-label]="food[l.foodType]"></span>
                  <span class="nm"><b>{{ l.quantity }}×</b> {{ l.name }}@if (l.variantName) { ({{ l.variantName }}) }</span>
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
                  <b>{{ k.number }}</b>, {{ k.lines }} line{{ k.lines === 1 ? '' : 's' }}, sent {{ time(k.sentAt) }}
                  <span [class]="'kstate ' + k.status">{{ kot(k.status) }}</span>
                  <button mat-icon-button class="kprint" (click)="printKot(k)" [attr.aria-label]="'Print ticket ' + k.number"><mat-icon>print</mat-icon></button>
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
          @if (printable()) {
            <button mat-stroked-button class="big print" (click)="printBill()" data-testid="print-bill"><mat-icon>receipt_long</mat-icon>Print bill</button>
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
    h1 { margin: var(--space-1) 0 0; } .where { font-weight: 400; color: var(--ro-ink-soft); }
    .status { font-size: .8rem; font-weight: 600; padding: 2px 10px; border-radius: 999px; background: var(--ro-warn-bg); color: var(--ro-warn); }
    .status.SETTLED { background: var(--ro-ok-bg); color: var(--ro-ok); } .status.VOIDED { background: var(--ro-line-mid); color: var(--ro-ink-soft); }
    .mute { color: var(--ro-mute); } .small { font-size: .85rem; } .err { color: var(--ro-error); }
    .banner { padding: 10px var(--space-3); border-radius: 10px; } .banner.ok { background: var(--ro-ok-bg); } .banner.off { background: var(--ro-sunk); }
    section { background: var(--ro-paper); border: 1px solid var(--ro-glass-edge); border-radius: var(--ro-radius-lg); padding: var(--space-1) 14px 14px; margin-top: var(--space-3); box-shadow: var(--ro-shadow-card); }
    h2 { font-size: 1rem; margin: var(--space-3) 0 var(--space-2); }
    ul { list-style: none; margin: 0; padding: 0; }
    .lines li { display: grid; grid-template-columns: 1fr auto; gap: 2px var(--space-2); padding: var(--space-2) 0; border-bottom: 1px solid var(--ro-sunk); }
    .lines li.voided .nm, .lines li.voided .amt { text-decoration: line-through; color: var(--ro-mute); }
    .what small { display: block; }
    .note { color: var(--ro-warn-strong); }
    .amt { text-align: right; font-variant-numeric: tabular-nums; }
    .ctl { grid-column: 1 / -1; display: flex; justify-content: flex-end; gap: var(--space-1); }
    .kstate { font-size: .75rem; font-weight: 600; color: var(--ro-mute); }
    .kstate.NEW, .kstate.unsent { color: var(--ro-warn); } .kstate.READY { color: var(--ro-info); } .kstate.SERVED { color: var(--ro-ok); } .kstate.VOIDED { color: var(--ro-error); }
    .kots li { display: flex; align-items: center; flex-wrap: wrap; gap: 6px; padding: var(--space-2) 0; border-bottom: 1px solid var(--ro-sunk); }
    .kots .kstate { padding: 2px 10px; border-radius: 999px; background: var(--ro-sunk); }
    .kots .kstate.READY { background: var(--ro-info-bg); }
    .serve { min-height: 44px; }
    .kprint { margin-left: auto; }
    .big { min-height: 52px; font-size: 1.05rem; border-radius: 10px; margin-top: var(--space-3); width: 100%; }
    dl { display: grid; grid-template-columns: 1fr auto; gap: var(--space-1) var(--space-3); margin: 0; }
    dd { margin: 0; text-align: right; font-variant-numeric: tabular-nums; }
    .total { font-weight: 700; font-size: 1.15rem; border-top: 1px solid var(--ro-line); padding-top: 6px; }
    .acts { display: flex; flex-wrap: wrap; gap: var(--space-2); margin-top: var(--space-1); }
    .acts .big { flex: 1 1 200px; }
    .danger { color: var(--ro-error); }
    .full { width: 100%; }
    .cats { display: flex; gap: 6px; overflow-x: auto; padding-bottom: 6px; scrollbar-width: none; }
    .cats button { flex: 0 0 auto; border: 1px solid var(--ro-glass-edge); background: var(--ro-paper); border-radius: var(--ro-radius-lg); padding: var(--space-2) 14px; font: inherit; min-height: 40px; cursor: pointer; box-shadow: var(--ro-shadow-card); }
    .cats button.on { background: var(--ro-tide); color: var(--ro-paper); border-color: var(--ro-tide); }
    .dish { all: unset; box-sizing: border-box; display: flex; align-items: center; gap: 10px; width: 100%; min-height: 52px; padding: var(--space-2) var(--space-1);
            border-bottom: 1px solid var(--ro-sunk); cursor: pointer; }
    .dish:focus-visible { outline: 2px solid var(--ro-kokum); }
    .dish[disabled] { opacity: .55; cursor: default; }
    .dish .nm { flex: 1; } .dish .nm small { display: block; }
    .dish .pr { font-variant-numeric: tabular-nums; }
    .dish mat-icon { color: var(--ro-tide); }
    .out { margin-left: 6px; font-size: .75rem; color: var(--ro-error); }
    .food { display: inline-block; flex: 0 0 auto; width: 12px; height: 12px; border: 2px solid; border-radius: 2px; margin-right: var(--space-1); vertical-align: middle; }
    .food.VEG { border-color: var(--ro-ok-mid); background: radial-gradient(var(--ro-ok-mid) 40%, transparent 45%); }
    .food.NON_VEG { border-color: var(--ro-error-strong); background: radial-gradient(var(--ro-error-strong) 40%, transparent 45%); }
    .food.EGG { border-color: var(--ro-warn-mid); background: radial-gradient(var(--ro-warn-mid) 40%, transparent 45%); }
    @media (max-width: 640px) { h1 { font-size: 1.35rem; } section { padding: 2px var(--space-3) var(--space-3); } }
  `,
})
export class OrderPage {
  id = input.required<string>();
  private api = inject(RestaurantApi);
  private ctx = inject(PropertyContext);
  private dialog = inject(MatDialog);
  private india = inject(IndiaApi);
  private auth = inject(AuthService);
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
    // While food is in the kitchen the page follows it (Ready shows up by itself), as the kitchen display does.
    const t = setInterval(() => {
      const o = this.order();
      if (document.visibilityState !== 'visible' || this.busy() || this.dialog.openDialogs.length || o?.status !== 'OPEN') return;
      if (o.kots.some(k => k.status === 'NEW' || k.status === 'PREPARING')) this.load();
    }, KITCHEN_REFRESH_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(t));
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
    return { text: `${l.kotNumber}, ${kotLabel(l.kitchenStatus ?? 'NEW')}`, cls: l.kitchenStatus ?? 'NEW' };
  }

  /** Runs a change and shows the order it returns. A refusal says why; a version conflict reloads the other waiter's order. */
  private run(call: Observable<Order>, done?: string) {
    this.busy.set(true);
    call.subscribe({
      next: o => { this.busy.set(false); this.order.set(o); if (done) this.snack.open(done, undefined, { duration: 2500 }); },
      error: e => this.failed(e),
    });
  }

  /** A refused change: say why, and reload when the order moved on underneath (409) or a rule refused it (422). */
  private failed(e: unknown) {
    this.busy.set(false);
    const p = toProblem(e);
    const fields = Object.values(p.errors ?? {}).flat();
    this.snack.open(fields.length ? fields.join(' ') : p.detail, 'OK', { duration: 8000 });
    if (p.status === 409 || p.status === 422) this.load();
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

  send() {
    const before = new Set(this.order()!.kots.map(k => k.id));
    this.busy.set(true);
    this.api.send(this.pid(), this.order()!, null).subscribe({
      next: o => {
        this.busy.set(false);
        this.order.set(o);
        // The new ticket can go straight to the kitchen printer, for kitchens that work from paper.
        const made = o.kots.find(k => !before.has(k.id));
        const bar = this.snack.open('Sent to the kitchen.', made ? 'Print ticket' : undefined, { duration: 6000 });
        if (made) bar.onAction().subscribe(() => this.printKot(made, false));
      },
      error: e => this.failed(e),
    });
  }

  /** A bill can be printed once there is something on it, open (with the UPI QR) or settled (saying how it was paid). */
  printable = computed(() => {
    const o = this.order();
    return !!o && o.status !== 'VOIDED' && o.items.some(l => l.status !== 'VOIDED');
  });

  printBill() {
    const o = this.order()!;
    const data: ReceiptData = { title: `Bill ${o.number}`, bill: this.india.bill(this.pid(), o.id) };
    this.dialog.open(ReceiptDialog, { data, maxWidth: '100vw' });
  }

  printKot(k: OrderKot, reprint = true) {
    const o = this.order()!;
    const kot: KotTicket = {
      number: k.number, orderNumber: o.number, where: o.where, sentAt: k.sentAt, sentByName: o.openedByName, note: o.note, reprint,
      items: o.items.filter(l => l.kotId === k.id).map(l => ({
        quantity: l.quantity, name: l.variantName ? `${l.name} (${l.variantName})` : l.name,
        modifiers: l.modifiers.length ? l.modifiers.join(', ') : null, note: l.note, voided: l.status === 'VOIDED',
      })),
    };
    const data: ReceiptData = { title: `Kitchen ticket ${k.number}`, kot };
    this.dialog.open(ReceiptDialog, { data, maxWidth: '100vw' });
  }
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
    const data: PayData<Order> = {
      total: this.order()!.total, run: (m, ref) => this.api.pay(this.pid(), this.order()!, m, ref, key),
      upi: { pid: this.pid(), note: `Bill ${this.order()!.number}`, canFix: this.auth.can('property.update', this.pid()) },
    };
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
