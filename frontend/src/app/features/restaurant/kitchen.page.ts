import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
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
  imports: [RouterLink, MatButtonModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page kds">
      <div class="head">
        <div>
          <h1>Kitchen</h1>
          <p class="mute">Refreshes every {{ refreshSeconds }} seconds@if (updated()) { · last {{ updated() }} }</p>
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
                <footer class="mute">{{ k.orderNumber }} · sent {{ time(k.sentAt) }}@if (k.sentByName) { by {{ k.sentByName }} }</footer>
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
    .head { display: flex; justify-content: space-between; align-items: flex-start; gap: 8px; }
    .head p { margin: 2px 0 0; }
    .tools { display: flex; align-items: center; gap: 4px; }
    .mute { color: #5b6b6e; } .err { color: #b3261e; }
    .cols { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; margin-top: 12px; }
    .col { background: #eef2f1; border-radius: 12px; padding: 8px; min-height: 160px; }
    .col.READY { background: #e0eff5; }
    h2 { font-size: 1rem; margin: 4px 4px 8px; display: flex; justify-content: space-between; }
    .ticket { background: #fff; border-radius: 10px; border-top: 6px solid #d29a2c; padding: 10px 12px; margin-bottom: 10px; display: flex; flex-direction: column; gap: 6px; }
    .PREPARING .ticket { border-top-color: #1f6f8b; } .READY .ticket { border-top-color: #2e6b3c; }
    .ticket.late { border-top-color: #b3261e; box-shadow: 0 0 0 2px #b3261e inset; }
    .ticket.late .age { color: #b3261e; font-weight: 700; }
    header { display: flex; align-items: baseline; gap: 8px; }
    .num { font-size: 1.2rem; } .where { font-weight: 600; } .age { margin-left: auto; font-variant-numeric: tabular-nums; }
    ul { list-style: none; margin: 0; padding: 0; font-size: 1.05rem; }
    li { padding: 4px 0; border-bottom: 1px dashed #e3e7e6; }
    li small { display: block; font-size: .85rem; color: #33474a; }
    li.voided { text-decoration: line-through; color: #7a8a8d; }
    .void { color: #b3261e; text-decoration: none; display: inline-block; }
    .note { color: #5b3a00; margin: 0; }
    footer { font-size: .8rem; }
    .go { min-height: 52px; font-size: 1.1rem; border-radius: 10px; }
    .food { display: inline-block; width: 11px; height: 11px; border: 2px solid; border-radius: 2px; margin-right: 4px; vertical-align: middle; }
    .food.VEG { border-color: #2e7d32; background: radial-gradient(#2e7d32 40%, transparent 45%); }
    .food.NON_VEG { border-color: #8c1d18; background: radial-gradient(#8c1d18 40%, transparent 45%); }
    .food.EGG { border-color: #c77700; background: radial-gradient(#c77700 40%, transparent 45%); }
    .empty { margin: 4px; }
    @media (max-width: 900px) { .cols { grid-template-columns: 1fr; } .col { min-height: 0; } h1 { font-size: 1.35rem; } }
  `,
})
export class KitchenPage {
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
