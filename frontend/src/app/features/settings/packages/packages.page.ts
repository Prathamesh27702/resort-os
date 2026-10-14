import { Component, computed, effect, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '../../../core/auth.service';
import { toProblem } from '../../../core/http';
import { Package } from '../../../core/models';
import { PropertyContext } from '../../../core/property-context';
import { label } from '../../rooms/rooms.api';
import { RatesApi, rupees } from '../rates/rates.api';

/** Fixed-price packages (room + food/transport/activities) and how their price is split into revenue. */
@Component({
  selector: 'app-packages',
  imports: [RouterLink, MatButtonModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Packages</h1>
          <p>Bundles sold at {{ propertyName() }} for a fixed price, before tax. Each price is split into room, food, transport and activity revenue.</p>
        </div>
        <div class="head-actions">
          <a mat-button routerLink="/settings/rates">Rates</a>
          @if (canManage()) { <a mat-flat-button routerLink="/settings/packages/new">Add package</a> }
        </div>
      </div>

      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      <section class="panel">
        @if (packages().length) {
          <table class="data">
            <thead><tr><th>Package</th><th class="hide-sm">Includes</th><th class="num">Price</th><th>Revenue split</th><th>Status</th></tr></thead>
            <tbody>
              @for (p of packages(); track p.id) {
                <tr class="clickable" tabindex="0" (click)="open(p.id)" (keydown.enter)="open(p.id)">
                  <td><b>{{ p.name }}</b> <span class="mute code">{{ p.code }}</span></td>
                  <td class="hide-sm">{{ p.nights }} night{{ p.nights === 1 ? '' : 's' }} · {{ p.accommodationTypeName }} · {{ p.adults }} adult{{ p.adults === 1 ? '' : 's' }}{{ p.children ? ', ' + p.children + ' child' + (p.children === 1 ? '' : 'ren') : '' }}</td>
                  <td class="num">{{ rupees(p.price) }}</td>
                  <td>
                    @for (c of categories(p); track c.category) {
                      <span class="chip">{{ label(c.category) }} {{ rupees(c.amount) }}</span>
                    }
                  </td>
                  <td>@if (p.isActive) { <span class="chip ok">Active</span> } @else { <span class="chip off">Inactive</span> }</td>
                </tr>
              }
            </tbody>
          </table>
        } @else if (loaded()) {
          <div class="empty">
            <p>No packages yet. Popular ones are a <b>Couple getaway</b> (room, dinner, spa) and <b>Room + breakfast</b>.</p>
            @if (canManage()) { <a mat-flat-button routerLink="/settings/packages/new">Add the first one</a> }
          </div>
        }
      </section>
    </div>
  `,
  styles: `
    .err { color: #b3261e; } .mute { color: #5b6b6e; } .code { font-size: .8rem; }
    .head-actions { display: flex; gap: 8px; align-items: center; }
    .empty { padding: 24px; } .empty p { color: #5b6b6e; margin-top: 0; }
    .chip.ok { background: #e3f1e6; color: #1f5130; }
  `,
})
export class PackagesPage {
  private api = inject(RatesApi);
  private auth = inject(AuthService);
  private router = inject(Router);
  ctx = inject(PropertyContext);

  readonly rupees = rupees;
  readonly label = label;
  packages = signal<Package[]>([]);
  loaded = signal(false);
  error = signal('');
  private pid = computed(() => this.ctx.current()?.id ?? null);
  propertyName = computed(() => this.ctx.current()?.name ?? 'this property');
  canManage = computed(() => this.auth.can('rate.manage', this.pid()));

  constructor() {
    effect(() => {
      const pid = this.pid();
      if (!pid) return;
      this.api.packages(pid).subscribe({
        next: p => { this.packages.set(p); this.loaded.set(true); },
        error: e => this.error.set(toProblem(e).detail),
      });
    });
  }

  categories(p: Package) { return Object.entries(p.revenueByCategory).map(([category, amount]) => ({ category, amount })); }

  open(id: string) { this.router.navigate(['/settings/packages', id]); }
}
