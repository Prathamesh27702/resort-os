import { Component, computed, effect, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { PurchaseOrder, PurchaseOrderList, PurchaseRequest, SupplierInvoice, SupplierInvoiceList } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { InventoryApi, qty } from '../inventory/inventory.api';
import { ReasonData, ReasonDialog } from '../restaurant/order-dialogs';
import { dayText } from '../../core/dates';
import { rupees } from '../settings/rates/rates.api';
import { OrderDialog, PaySupplierDialog, RequestDialog } from './purchasing-dialogs';
import { BILL_STATUS_LABEL, ORDER_STATUS_LABEL, PurchasingApi, REQUEST_STATUS_LABEL, todayAt } from './purchasing.api';

type View = 'orders' | 'requests' | 'bills';
type OrderFilter = 'open' | 'waiting' | 'all';

/**
 * Purchases (Sprint 16): purchase orders (waiting for approval, open, all), the stores' purchase requests (each turned into
 * an order in one tap), and supplier bills with what is still to pay and the input GST. An order opens on its own page
 * for approval, goods received, the bill and payment. Works at 360 px: every list is cards.
 */
@Component({
  selector: 'app-purchases',
  imports: [RouterLink, MatButtonModule, MatButtonToggleModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page pur">
      <div class="head">
        <h1>Purchases</h1>
        @if (orders(); as o) {
          <p class="mute">Orders over {{ rupees(o.approvalLimit) }} need a second person's approval@if (o.waiting) { · <b class="wait">{{ o.waiting }} waiting</b> }</p>
        }
      </div>
      <mat-button-toggle-group [value]="view()" (change)="go($event.value)" aria-label="Purchases view" class="views">
        <mat-button-toggle value="orders">Orders</mat-button-toggle>
        <mat-button-toggle value="requests">Requests</mat-button-toggle>
        <mat-button-toggle value="bills">Bills</mat-button-toggle>
      </mat-button-toggle-group>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

      @switch (view()) {
        @case ('orders') {
          <div class="tools">
            <mat-button-toggle-group [value]="filter()" (change)="filter.set($event.value)" aria-label="Which orders">
              <mat-button-toggle value="open">Open</mat-button-toggle>
              <mat-button-toggle value="waiting">Waiting for approval</mat-button-toggle>
              <mat-button-toggle value="all">All</mat-button-toggle>
            </mat-button-toggle-group>
            @if (can('purchase.create')) { <button mat-flat-button (click)="newOrder(null)"><mat-icon>add</mat-icon>Order</button> }
            @if (can('supplier.read')) { <a mat-stroked-button routerLink="/inventory/suppliers"><mat-icon>local_shipping</mat-icon>Suppliers</a> }
          </div>
          <ul class="cards">
            @for (o of shownOrders(); track o.id) {
              <li [attr.data-order]="o.number">
                <a class="nm" [routerLink]="['/inventory/purchases', o.id]">
                  <b>{{ o.number }}</b> {{ o.supplierName }}
                  <small class="mute">{{ dayText(o.orderDate) }} · for {{ o.locationName }}@if (o.expectedDate) { · expected {{ dayText(o.expectedDate) }} }@if (o.createdByName) { · {{ o.createdByName }} }</small>
                  @if (o.status === 'PARTLY_RECEIVED') { <small>{{ o.received }} % received</small> }
                </a>
                <span class="right"><b>{{ rupees(o.total) }}</b><small [class]="'st ' + o.status">{{ statusLabel[o.status] }}</small></span>
              </li>
            } @empty { @if (orders()) { <li class="mute">{{ filter() === 'waiting' ? 'No order is waiting for approval.' : 'No purchase orders here yet.' }}</li> } }
          </ul>
        }

        @case ('requests') {
          <div class="tools">
            @if (can('purchase.create')) { <button mat-flat-button (click)="newRequest()"><mat-icon>add</mat-icon>Ask for stock</button> }
          </div>
          <ul class="cards">
            @for (r of requests(); track r.id) {
              <li [attr.data-request]="r.number" [class.off]="r.status !== 'OPEN'">
                <div class="nm">
                  <b>{{ r.number }}</b> for {{ r.locationName }} <small [class]="'st ' + r.status">{{ requestLabel[r.status] }}</small>
                  <small>{{ items(r) }}</small>
                  <small class="mute">@if (r.byName) { {{ r.byName }} · }{{ when(r.createdAt) }}@if (r.neededBy) { · needed by {{ dayText(r.neededBy) }} }@if (r.note) { · {{ r.note }} }</small>
                  @if (r.status === 'ORDERED') { <small>On <a [routerLink]="['/inventory/purchases', r.purchaseOrderId]">{{ r.purchaseOrderNumber }}</a></small> }
                  @if (r.status === 'CANCELLED') { <small class="mute">Cancelled: {{ r.cancelReason }}</small> }
                </div>
                @if (r.status === 'OPEN' && can('purchase.create')) {
                  <span class="acts">
                    <button mat-flat-button (click)="newOrder(r)" [attr.aria-label]="'Raise an order for ' + r.number">Raise order</button>
                    <button mat-button (click)="cancelRequest(r)" [attr.aria-label]="'Cancel ' + r.number">Cancel</button>
                  </span>
                }
              </li>
            } @empty { @if (requestsLoaded()) { <li class="mute">No purchase requests yet.</li> } }
          </ul>
        }

        @case ('bills') {
          @if (bills(); as b) {
            <div class="tiles">
              <div><small>Still to pay</small><b>{{ rupees(b.outstanding) }}</b></div>
              <div><small>Overdue</small><b [class.late]="b.overdue > 0">{{ rupees(b.overdue) }}</b></div>
              <div><small>Input GST (claimable)</small><b>{{ rupees(b.inputTax) }}</b></div>
            </div>
            <ul class="cards">
              @for (i of b.items; track i.id) {
                <li [attr.data-bill]="i.number">
                  <div class="nm">
                    <b>{{ i.number }}</b> {{ i.supplierName }} <small class="mute">bill {{ i.supplierNumber }}</small>
                    <small class="mute">{{ dayText(i.invoiceDate) }} · due {{ dayText(i.dueDate) }} · on <a [routerLink]="['/inventory/purchases', i.orderId]">{{ i.orderNumber }}</a></small>
                    <small>GST {{ rupees(i.inputTax) }}@if (i.inputTax > 0) { ({{ i.intraState ? 'CGST + SGST' : 'IGST' }}{{ i.itcEligible ? ', claimable' : ', not claimable' }}) }</small>
                  </div>
                  <span class="right"><b>{{ rupees(i.total) }}</b>
                    <small [class]="'st ' + i.status + (i.overdue ? ' late' : '')">{{ i.overdue ? 'Overdue' : billLabel[i.status] }}@if (i.status === 'PART_PAID') { · {{ rupees(i.balance) }} left }</small>
                    @if (i.balance > 0 && can('supplier_invoice.manage')) { <button mat-stroked-button (click)="pay(i)" [attr.aria-label]="'Pay ' + i.number">Pay</button> }
                  </span>
                </li>
              } @empty { <li class="mute">No supplier bills yet. Record a bill on an order once its goods have come in.</li> }
            </ul>
          }
        }
      }
    </div>
  `,
  styles: `
    .pur { max-width: 940px; padding-bottom: 60px; }
    .head { display: flex; justify-content: space-between; align-items: baseline; gap: 8px; flex-wrap: wrap; }
    .mute { color: #5b6b6e; } .err { color: #b3261e; } .wait { color: #8a5a00; } .late { color: #b3261e; }
    .views { margin: 4px 0 10px; flex-wrap: wrap; }
    .tools { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 8px; }
    .tools mat-button-toggle-group { flex-wrap: wrap; }
    ul { list-style: none; margin: 0; padding: 0; }
    .cards li { display: flex; gap: 10px; align-items: flex-start; background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 10px 14px; margin-top: 8px; }
    .cards li.off { opacity: .7; }
    .nm { flex: 1; display: flex; flex-direction: column; gap: 2px; min-width: 0; color: inherit; text-decoration: none; overflow-wrap: anywhere; }
    a.nm { min-height: 40px; }
    .right { display: flex; flex-direction: column; align-items: flex-end; gap: 2px; text-align: right; font-variant-numeric: tabular-nums; }
    .acts { display: flex; flex-direction: column; gap: 4px; }
    .st { font-size: .8rem; } .st.PENDING_APPROVAL, .st.OPEN, .st.PART_PAID { color: #8a5a00; } .st.APPROVED, .st.PARTLY_RECEIVED { color: #1b5e8a; }
    .st.RECEIVED, .st.PAID, .st.ORDERED { color: #2e7d32; } .st.CANCELLED, .st.CLOSED { color: #5b6b6e; } .st.UNPAID { color: #b3261e; }
    .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 8px; }
    .tiles div { background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 10px 12px; display: flex; flex-direction: column; }
    .tiles b { font-size: 1.25rem; font-variant-numeric: tabular-nums; }
    @media (max-width: 640px) { h1 { font-size: 1.35rem; } .cards li { flex-wrap: wrap; } .right { align-items: flex-start; text-align: left; } .acts { flex-direction: row; } }
  `,
})
export class PurchasesPage {
  private api = inject(PurchasingApi);
  private inventoryApi = inject(InventoryApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  private route = inject(ActivatedRoute);
  private router = inject(Router);

  orders = signal<PurchaseOrderList | null>(null);
  requests = signal<PurchaseRequest[]>([]);
  requestsLoaded = signal(false);
  bills = signal<SupplierInvoiceList | null>(null);
  error = signal('');
  filter = signal<OrderFilter>('open');
  rupees = rupees;
  dayText = dayText;
  statusLabel = ORDER_STATUS_LABEL;
  requestLabel = REQUEST_STATUS_LABEL;
  billLabel = BILL_STATUS_LABEL;
  view = signal<View>((this.route.snapshot.queryParamMap.get('view') as View) ?? 'orders');

  private pid = computed(() => this.ctx.current()?.id ?? '');
  can = (p: string) => this.auth.can(p, this.pid());
  shownOrders = computed(() => {
    const list = this.orders()?.items ?? [];
    const f = this.filter();
    if (f === 'waiting') return list.filter(o => o.status === 'PENDING_APPROVAL');
    if (f === 'open') return list.filter(o => !['RECEIVED', 'CLOSED', 'CANCELLED'].includes(o.status));
    return list;
  });

  constructor() {
    this.route.queryParamMap.subscribe(q => {
      this.view.set((q.get('view') as View) ?? 'orders');
      if (q.get('filter')) this.filter.set(q.get('filter') as OrderFilter);
    });
    effect(() => { if (this.pid()) this.load(this.view()); });
  }

  go(v: View) { this.router.navigate([], { queryParams: { view: v }, queryParamsHandling: 'merge' }); }

  load(v: View) {
    const fail = (e: unknown) => this.error.set(toProblem(e).detail);
    if (v === 'orders') this.api.orders(this.pid()).subscribe({ next: o => { this.orders.set(o); this.error.set(''); }, error: fail });
    if (v === 'requests') this.api.requests(this.pid()).subscribe({ next: r => { this.requests.set(r); this.requestsLoaded.set(true); this.error.set(''); }, error: fail });
    if (v === 'bills') this.api.invoices(this.pid()).subscribe({ next: b => { this.bills.set(b); this.error.set(''); }, error: fail });
  }

  items(r: PurchaseRequest) { return r.items.map(i => `${qty(i.quantity, i.unit)} ${i.itemName}`).join(' · '); }
  when(at: string) { return new Date(at).toLocaleString('en-IN', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false }); }

  newRequest() {
    this.inventoryApi.get(this.pid()).subscribe({
      next: inv => this.dialog.open(RequestDialog, { data: { propertyId: this.pid(), inventory: inv }, maxWidth: '100vw' }).afterClosed()
        .subscribe((m?: string) => { if (m) { this.snack.open(m, 'OK', { duration: 5000 }); this.load('requests'); } }),
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  /** A new draft, from a request or from scratch; it opens on its own page once saved. */
  newOrder(request: PurchaseRequest | null) {
    forkJoin([this.inventoryApi.get(this.pid()), this.api.suppliers(this.pid())]).subscribe({
      next: ([inventory, suppliers]) => {
        if (!suppliers.some(s => s.isActive)) { this.error.set('Add a supplier first (Suppliers → Supplier), then raise the order.'); return; }
        this.dialog.open(OrderDialog, {
          data: { propertyId: this.pid(), inventory, suppliers, order: null, request, propertyState: this.ctx.current()?.gstStateCode ?? null },
          maxWidth: '100vw',
        }).afterClosed().subscribe((o?: PurchaseOrder) => { if (o) this.router.navigate(['/inventory/purchases', o.id]); });
      },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  cancelRequest(r: PurchaseRequest) {
    const data: ReasonData<PurchaseRequest> = {
      title: `Cancel ${r.number}`, intro: 'The store asked for this stock. Say why it is not being ordered.', button: 'Cancel request',
      placeholder: 'Bought at the market', run: reason => this.api.cancelRequest(this.pid(), r.id, reason, r.version),
    };
    this.dialog.open(ReasonDialog, { data, maxWidth: '100vw' }).afterClosed().subscribe((x?: PurchaseRequest) => {
      if (x) { this.snack.open(`${x.number} cancelled.`, 'OK', { duration: 4000 }); this.load('requests'); }
    });
  }

  pay(i: SupplierInvoice) {
    this.dialog.open(PaySupplierDialog, { data: { propertyId: this.pid(), invoice: i, today: todayAt(this.ctx.current()!.timezone) }, maxWidth: '100vw' })
      .afterClosed().subscribe((b?: SupplierInvoice) => {
        if (!b) return;
        this.snack.open(b.status === 'PAID' ? `${b.number} is paid in full.` : `${b.number}: ${rupees(b.balance)} left to pay.`, 'OK', { duration: 5000 });
        this.load('bills');
      });
  }
}
