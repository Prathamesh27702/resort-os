import { Component, computed, effect, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '../../../core/auth.service';
import { toProblem } from '../../../core/http';
import { AccommodationType } from '../../../core/models';
import { PropertyContext } from '../../../core/property-context';
import { RoomsApi } from '../../rooms/rooms.api';

/** What the property sells: room types (sold per room) and dormitories (sold per bed). */
@Component({
  selector: 'app-room-types',
  imports: [RouterLink, MatButtonModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Room types</h1>
          <p>What you sell at {{ propertyName() }}. Rates and photos are added in later sprints.</p>
        </div>
        @if (canManage()) { <a mat-flat-button routerLink="/settings/room-types/new">Add room type</a> }
      </div>

      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      <section class="panel">
        @if (types().length) {
          <table class="data">
            <thead><tr><th>Type</th><th>Sold</th><th class="num">Guests</th><th class="num">Rooms</th><th class="num hide-sm">Sellable</th><th>Status</th></tr></thead>
            <tbody>
              @for (t of types(); track t.id) {
                <tr class="clickable" tabindex="0" (click)="open(t.id)" (keydown.enter)="open(t.id)">
                  <td><b>{{ t.name }}</b> <span class="mute code">{{ t.code }}</span></td>
                  <td>{{ t.inventoryMode === 'BED' ? 'Per bed' : 'Per room' }}</td>
                  <td class="num">{{ t.inventoryMode === 'BED' ? '1 per bed' : t.baseOccupancy + '–' + t.maxOccupancy }}</td>
                  <td class="num">{{ t.unitCount }}</td>
                  <td class="num hide-sm">{{ t.sellableItems }} {{ t.inventoryMode === 'BED' ? 'beds' : 'rooms' }}</td>
                  <td>@if (t.isActive) { <span class="chip ok">Active</span> } @else { <span class="chip off">Inactive</span> }</td>
                </tr>
              }
            </tbody>
          </table>
        } @else if (loaded()) {
          <div class="empty">
            <p>No room types yet. A typical resort starts with two or three, e.g. <b>Deluxe room</b>, <b>Standard room</b> and <b>Dormitory</b>.</p>
            @if (canManage()) { <a mat-flat-button routerLink="/settings/room-types/new">Add the first one</a> }
          </div>
        }
      </section>
    </div>
  `,
  styles: `
    .err { color: #b3261e; } .mute { color: #5b6b6e; } .code { font-size: .8rem; }
    .empty { padding: 24px; } .empty p { color: #5b6b6e; margin-top: 0; }
    .chip.ok { background: #e3f1e6; color: #1f5130; }
  `,
})
export class RoomTypesPage {
  private api = inject(RoomsApi);
  private auth = inject(AuthService);
  private router = inject(Router);
  ctx = inject(PropertyContext);

  types = signal<AccommodationType[]>([]);
  loaded = signal(false);
  error = signal('');
  private pid = computed(() => this.ctx.current()?.id ?? null);
  propertyName = computed(() => this.ctx.current()?.name ?? 'this property');
  canManage = computed(() => this.auth.can('accommodation.manage', this.pid()));

  constructor() {
    effect(() => {
      const pid = this.pid();
      if (!pid) return;
      this.api.types(pid).subscribe({
        next: t => { this.types.set(t); this.loaded.set(true); },
        error: e => this.error.set(toProblem(e).detail),
      });
    });
  }

  open(id: string) { this.router.navigate(['/settings/room-types', id]); }
}
