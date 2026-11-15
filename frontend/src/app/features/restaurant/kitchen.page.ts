import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { KotTicket } from '../india/receipt';
import { ReceiptData, ReceiptDialog } from '../india/receipt-dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { Kitchen, Kot } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { FOOD_LABEL, KITCHEN_REFRESH_MS, LATE_MINUTES, RestaurantApi, ageMinutes, kitchenColumns } from './restaurant.api';

/**
 * The kitchen display (Sprint 14): every ticket still to cook, in three columns — New, Preparing, Ready — oldest first. It
 * asks the server for tickets every 10 seconds while the screen is showing (no websockets yet). A ticket waiting longer
 * than 15 minutes turns red. Voided dishes are struck through so the cook stops on them. Start / Ready are one tap.
 */
@Component({
  selector: 'app-kitchen',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page kds">
      <div class="head">
        <div>
          <h1>Kitchen</h1>
          <p class="mute">Refreshes every {{ refreshSeconds }} seconds@if (updated()) {, last {{ updated() }} }</p>
        </div>
        <div class="tools">
          @if (canOrders()) { <a mat-button routerLink="/restaurant">Floor</a> }
          <button mat-icon-button (click)="load()" aria-label="Refresh"><mat-icon>refresh</mat-icon></button>
        </div>
      </div>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

      <div class="cols">
        @for (col of columns(); track col.status) {
          <section [class]="'col ' + col.status" [attr.aria-label]="col.label">
            <h2>{{ col.label }} <b>{{ col.tickets.length }}</b></h2>
            @for (k of col.tickets; track k.id) {
              <article class="ticket" [class.late]="late(k)" [attr.data-kot]="k.number">
                <header>
                  <b class="num">{{ k.number }}</b>
                  <span class="where">{{ k.where }}</span>
                  <span class="age">{{ age(k) }} min</span>
                  <button mat-icon-button class="kprint" (click)="print(k)" [attr.aria-label]="'Print ticket ' + k.number"><mat-icon>print</mat-icon></button>
                </header>
                <ul>
                  @for (i of k.items; track i.id) {
                    <li [class.voided]="i.voided">
                      <span [class]="'food ' + i.foodType" [attr.aria-label]="food[i.foodType]"></span>
                      <b>{{ i.quantity }}×</b> {{ i.name }}
                      @if (i.modifiers) { <small>{{ i.modifiers }}</small> }
                      @if (i.note) { <small class="note">“{{ i.note }}”</small> }
                      @if (i.voided) { <small class="void">Voided: {{ i.voidReason }}</small> }
                    </li>
                  }
                </ul>
                @if (k.note) { <p class="note">{{ k.note }}</p> }
                <footer class="mute">{{ k.orderNumber }}, sent {{ time(k.sentAt) }}@if (k.sentByName) { by {{ k.sentByName }} }</footer>
                @if (canUpdate() && k.status !== 'READY') {
                  <button mat-flat-button class="go" (click)="step(k, k.status === 'NEW' ? 'start' : 'ready')" [disabled]="busy() === k.id">
                    {{ k.status === 'NEW' ? 'Start' : 'Ready' }}</button>
                }
                @if (k.status === 'NEW' && canUpdate()) {
                  <button mat-button class="skip" (click)="step(k, 'ready')" [disabled]="busy() === k.id">Ready now</button>
                }
              </article>
            } @empty { <p class="mute empty">Nothing here.</p> }
          </section>
        }
      </div>
    </div>
  `,
  styles: `
    .kds { max-width: 1400px; padding-bottom: 40px; }
    .head { display: flex; justify-content: space-between; align-items: flex-start; gap: var(--space-2); }
    .head p { margin: 2px 0 0; }
    .tools { display: flex; align-items: center; gap: var(--space-1); }
    .mute { color: var(--ro-mute); } .err { color: var(--ro-error); }
    .cols { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: var(--space-3); margin-top: var(--space-3); }
    .col { background: var(--ro-sunk); border-radius: 12px; padding: var(--space-2); min-height: 160px; }
    .col.READY { background: var(--ro-info-bg); }
    h2 { font-size: 1rem; margin: var(--space-1) var(--space-1) var(--space-2); display: flex; justify-content: space-between; }
    .ticket { background: var(--ro-paper); border-radius: 10px; border-top: 6px solid var(--ro-warn-bright); padding: 10px var(--space-3); margin-bottom: 10px; display: flex; flex-direction: column; gap: 6px; }
    .PREPARING .ticket { border-top-color: var(--ro-info-bright); } .READY .ticket { border-top-color: var(--ro-ok-mid); }
    .ticket.late { border-top-color: var(--ro-error); box-shadow: 0 0 0 2px var(--ro-error) inset; }
    .ticket.late .age { color: var(--ro-error); font-weight: 700; }
    header { display: flex; align-items: center; gap: var(--space-2); } .kprint { margin: -8px -8px -8px 0; }
    .num { font-size: 1.2rem; } .where { font-weight: 600; } .age { margin-left: auto; font-variant-numeric: tabular-nums; }
    ul { list-style: none; margin: 0; padding: 0; font-size: 1.05rem; }
    li { padding: var(--space-1) 0; border-bottom: 1px dashed var(--ro-line-mid); }
    li small { display: block; font-size: .85rem; color: var(--ro-ink-soft); }
    li.voided { text-decoration: line-through; color: var(--ro-mute); }
    .void { color: var(--ro-error); text-decoration: none; display: inline-block; }
    .note { color: var(--ro-warn-strong); margin: 0; }
    footer { font-size: .8rem; }
    .go { min-height: 52px; font-size: 1.1rem; border-radius: 10px; }
    .food { display: inline-block; width: 11px; height: 11px; border: 2px solid; border-radius: 2px; margin-right: var(--space-1); vertical-align: middle; }
    .food.VEG { border-color: var(--ro-ok-mid); background: radial-gradient(var(--ro-ok-mid) 40%, transparent 45%); }
    .food.NON_VEG { border-color: var(--ro-error-strong); background: radial-gradient(var(--ro-error-strong) 40%, transparent 45%); }
    .food.EGG { border-color: var(--ro-warn-mid); background: radial-gradient(var(--ro-warn-mid) 40%, transparent 45%); }
    .empty { margin: var(--space-1); }
    @media (max-width: 900px) { .cols { grid-template-columns: 1fr; } .col { min-height: 0; } h1 { font-size: 1.35rem; } }
  `,
})
export class KitchenPage {
  private receiptDialog = inject(MatDialog);

  /** The ticket on paper, for kitchens that work from a printer next to the pass. */
  print(k: Kot) {
    const kot: KotTicket = {
      number: k.number, orderNumber: k.orderNumber, where: k.where, sentAt: k.sentAt, sentByName: k.sentByName, note: k.note, reprint: true,
      items: k.items.map(i => ({ quantity: i.quantity, name: i.name, modifiers: i.modifiers, note: i.note, voided: i.voided })),
    };
    const data: ReceiptData = { title: `Kitchen ticket ${k.number}`, kot };
    this.receiptDialog.open(ReceiptDialog, { data, maxWidth: '100vw' });
  }

  private api = inject(RestaurantApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private snack = inject(MatSnackBar);

  kitchen = signal<Kitchen | null>(null);
  error = signal('');
  busy = signal<string | null>(null);
  updated = signal('');
  food = FOOD_LABEL;
  refreshSeconds = KITCHEN_REFRESH_MS / 1000;
  private now = signal(new Date());
  private pid = computed(() => this.ctx.current()?.id ?? '');
  canUpdate = computed(() => this.auth.can('kot.update', this.pid()));
  canOrders = computed(() => this.auth.can('pos_order.read', this.pid()));
  columns = computed(() => kitchenColumns(this.kitchen()?.tickets ?? []));

  constructor() {
    effect(() => { if (this.pid()) this.load(); });
    const t = setInterval(() => { if (document.visibilityState === 'visible' && !this.busy()) this.load(); }, KITCHEN_REFRESH_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(t));
  }

  load() {
    this.api.kitchen(this.pid()).subscribe({
      next: k => {
        this.kitchen.set(k);
        this.now.set(new Date(k.now));
        this.updated.set(this.time(k.now, true));
        this.error.set('');
      },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  age(k: Kot) { return ageMinutes(k.sentAt, this.now()); }
  late(k: Kot) { return this.age(k) >= LATE_MINUTES; }
  time(iso: string, seconds = false) {
    return inZone(iso, this.ctx.current()?.timezone ?? 'Asia/Kolkata', { hour: '2-digit', minute: '2-digit', ...(seconds ? { second: '2-digit' } : {}), hour12: false });
  }

  step(k: Kot, step: 'start' | 'ready') {
    this.busy.set(k.id);
    this.api.kotStep(this.pid(), k, step).subscribe({
      next: () => { this.busy.set(null); this.snack.open(`${k.number} ${step === 'start' ? 'started' : 'ready'}.`, undefined, { duration: 2000 }); this.load(); },
      error: e => { this.busy.set(null); this.snack.open(toProblem(e).detail, 'OK', { duration: 8000 }); this.load(); },
    });
  }
}
