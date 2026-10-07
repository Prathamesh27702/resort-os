import { Component, computed, effect, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { AccommodationType, Room, RoomStatus, UnitBlock } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { BlockDialog, BlockDialogData, ReasonDialog, ReasonDialogData } from './block-dialog';
import { RoomsApi, label, shortDate, addDays } from './rooms.api';

interface TypeGroup { type: AccommodationType; rooms: Room[]; }

@Component({
  selector: 'app-rooms',
  imports: [RouterLink, MatButtonModule, MatIconModule, MatTooltipModule, MatDialogModule, MatSnackBarModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Rooms</h1>
          <p>{{ summary() }}</p>
        </div>
        <div class="actions">
          <a mat-stroked-button routerLink="/rooms/status"><mat-icon>grid_view</mat-icon>Room status</a>
          <a mat-stroked-button routerLink="/settings/room-types">Room types</a>
          @if (canBlock()) { <button mat-stroked-button (click)="block()" [disabled]="!rooms().length"><mat-icon>block</mat-icon>Block</button> }
          @if (canManage()) { <a mat-flat-button routerLink="/rooms/new" [class.disabled]="!types().length">Add room</a> }
        </div>
      </div>

      @if (loading()) {
        <p class="mute">Loading…</p>
      } @else if (error()) {
        <p class="err" role="alert">{{ error() }} <button mat-button (click)="load()">Try again</button></p>
      } @else if (!types().length) {
        <section class="panel empty">
          <h2>No room types yet</h2>
          <p>Start with what you sell, e.g. <b>Deluxe room</b> (sold per room) and <b>Dormitory bed</b> (sold per bed). Then add the rooms.</p>
          @if (canManage()) { <a mat-flat-button routerLink="/settings/room-types/new">Add a room type</a> }
        </section>
      } @else {
        @for (g of groups(); track g.type.id) {
          <section class="panel group" [class.inactive]="!g.type.isActive">
            <header>
              <div>
                <h2>{{ g.type.name }} @if (!g.type.isActive) { <span class="chip off">Inactive type</span> }</h2>
                <p class="mute">{{ g.type.inventoryMode === 'BED' ? 'Sold per bed' : 'Sold per room' }} ·
                  {{ g.type.inventoryMode === 'BED' ? g.type.sellableItems + ' beds' : g.type.sellableItems + ' room' + (g.type.sellableItems === 1 ? '' : 's') }} ·
                  {{ g.type.inventoryMode === 'BED' ? '1 guest per bed' : 'up to ' + g.type.maxOccupancy + ' guests per room' }}</p>
              </div>
              <a mat-button [routerLink]="['/settings/room-types', g.type.id]">{{ canManage() ? 'Edit type' : 'View type' }}</a>
            </header>
            @if (g.rooms.length) {
              <table class="data">
                <thead><tr><th>Room</th><th class="hide-sm">Floor</th>@if (g.type.inventoryMode === 'BED') { <th>Beds</th> }<th>Today</th><th></th></tr></thead>
                <tbody>
                  @for (r of g.rooms; track r.id) {
                    <tr class="clickable" (click)="open(r.id)" tabindex="0" (keydown.enter)="open(r.id)">
                      <td><b>{{ r.code }}</b>@if (r.name !== 'Room ' + r.code) { <span class="mute name">{{ r.name }}</span> }</td>
                      <td class="hide-sm">{{ r.floor ?? '—' }}</td>
                      @if (g.type.inventoryMode === 'BED') { <td class="num">{{ activeBeds(r) }}</td> }
                      <td>
                        @switch (statusOf(r.id)?.status) {
                          @case ('BLOCKED') { <span class="chip warn" [matTooltip]="statusOf(r.id)?.block?.reason ?? ''">Blocked</span> }
                          @case ('PARTLY_BLOCKED') { <span class="chip warn">{{ statusOf(r.id)?.blockedBeds }} of {{ statusOf(r.id)?.activeBeds }} beds blocked</span> }
                          @case ('INACTIVE') { <span class="chip off">Inactive</span> }
                          @default { <span class="chip ok">Available</span> }
                        }
                      </td>
                      <td class="row-actions" (click)="$event.stopPropagation()">
                        @if (canBlock() && r.isActive) {
                          <button mat-icon-button (click)="block(r.id)" matTooltip="Block" [attr.aria-label]="'Block ' + r.name"><mat-icon>block</mat-icon></button>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            } @else {
              <p class="mute pad">No rooms of this type yet.</p>
            }
          </section>
        }

        <section class="panel group">
          <header><div><h2>Blocks</h2><p class="mute">Current and upcoming. Released blocks stay in the audit trail.</p></div></header>
          @if (blocks().length) {
            <table class="data">
              <thead><tr><th>Room</th><th>Nights</th><th class="hide-sm">Why</th><th class="hide-sm">By</th><th></th></tr></thead>
              <tbody>
                @for (b of blocks(); track b.id) {
                  <tr>
                    <td><b>{{ b.unitCode }}</b>@if (b.bedCode) { <span class="mute"> · bed {{ b.bedCode }}</span> }</td>
                    <td class="num">{{ shortDate(b.startDate) }} – {{ shortDate(lastNight(b)) }} <span class="mute">({{ b.nights }})</span></td>
                    <td class="hide-sm">{{ label(b.blockType) }} · {{ b.reason }}</td>
                    <td class="hide-sm mute">{{ b.createdByName ?? '—' }}</td>
                    <td class="row-actions">@if (canBlock()) { <button mat-button (click)="release(b)">Release</button> }</td>
                  </tr>
                }
              </tbody>
            </table>
          } @else {
            <p class="mute pad">No rooms are blocked.</p>
          }
        </section>
      }
    </div>
  `,
  styles: `
    .actions { display: flex; gap: 8px; flex-wrap: wrap; }
    .actions mat-icon { margin-right: 4px; }
    a.disabled { pointer-events: none; opacity: .5; }
    .group { margin-bottom: 16px; overflow: hidden; }
    .group header { display: flex; justify-content: space-between; align-items: flex-start; padding: 16px 16px 8px; gap: 12px; }
    .group header p { margin: 2px 0 0; }
    .group.inactive { opacity: .7; }
    .empty { padding: 24px; max-width: 640px; }
    .empty p { color: #5b6b6e; }
    .mute { color: #5b6b6e; }
    .name { margin-left: 8px; }
    .pad { padding: 0 16px 16px; margin: 0; }
    .err { color: #b3261e; }
    .chip.ok { background: #e3f1e6; color: #1f5130; }
    .chip.warn { background: #fdf0d8; color: #7a4a00; }
    .row-actions { text-align: right; white-space: nowrap; width: 1%; }
  `,
})
export class RoomsPage {
  private api = inject(RoomsApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private router = inject(Router);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);

  label = label;
  shortDate = shortDate;
  loading = signal(true);
  error = signal('');
  types = signal<AccommodationType[]>([]);
  rooms = signal<Room[]>([]);
  status = signal<RoomStatus[]>([]);
  blocks = signal<UnitBlock[]>([]);

  private pid = computed(() => this.ctx.current()?.id ?? null);
  canManage = computed(() => this.auth.can('accommodation.manage', this.pid()));
  canBlock = computed(() => this.auth.can('unit_block.manage', this.pid()));

  groups = computed<TypeGroup[]>(() =>
    [...this.types()].sort((a, b) => Number(b.isActive) - Number(a.isActive)) // inactive types last; API order otherwise
      .map(type => ({ type, rooms: this.rooms().filter(r => r.accommodationTypeId === type.id) })));

  summary = computed(() => {
    const rooms = this.rooms().filter(r => r.isActive);
    const unitRooms = rooms.filter(r => r.inventoryMode === 'UNIT').length;
    const beds = rooms.filter(r => r.inventoryMode === 'BED').reduce((n, r) => n + this.activeBeds(r), 0);
    const name = this.ctx.current()?.name ?? '';
    return `${unitRooms} room${unitRooms === 1 ? '' : 's'}${beds ? ` and ${beds} dorm beds` : ''} at ${name}`;
  });

  constructor() {
    effect(() => { if (this.pid()) this.load(); });
  }

  load() {
    const pid = this.pid();
    if (!pid) return;
    this.loading.set(true); this.error.set('');
    forkJoin({ types: this.api.types(pid), rooms: this.api.rooms(pid), status: this.api.status(pid), blocks: this.api.blocks(pid) })
      .subscribe({
        next: r => {
          this.types.set(r.types); this.rooms.set(r.rooms); this.status.set(r.status.rooms); this.blocks.set(r.blocks);
          this.loading.set(false);
        },
        error: e => { this.error.set(toProblem(e).detail); this.loading.set(false); },
      });
  }

  activeBeds(r: Room) { return r.beds.filter(b => b.isActive).length; }
  statusOf(id: string) { return this.status().find(s => s.unitId === id); }
  lastNight(b: UnitBlock) { return addDays(b.endDate, -1); }
  open(id: string) { this.router.navigate(['/rooms', id]); }

  block(roomId?: string) {
    const p = this.ctx.current();
    if (!p) return;
    this.dialog.open<BlockDialog, BlockDialogData>(BlockDialog, {
      data: { propertyId: p.id, today: p.localDate, rooms: this.rooms(), roomId },
    }).afterClosed().subscribe(b => {
      if (!b) return;
      this.snack.open(`${b.unitName}${b.bedCode ? ' bed ' + b.bedCode : ''} blocked for ${b.nights} night${b.nights === 1 ? '' : 's'}`, undefined, { duration: 3000 });
      this.load();
    });
  }

  release(b: UnitBlock) {
    const pid = this.pid();
    if (!pid) return;
    this.dialog.open<ReasonDialog, ReasonDialogData, string>(ReasonDialog, {
      data: { title: `Release block on ${b.unitName}${b.bedCode ? ' bed ' + b.bedCode : ''}?`, message: 'The room becomes sellable again for those nights.', action: 'Release' },
    }).afterClosed().subscribe(reason => {
      if (!reason) return;
      this.api.releaseBlock(pid, b.id, reason).subscribe({
        next: () => { this.snack.open('Block released', undefined, { duration: 2500 }); this.load(); },
        error: e => this.snack.open(toProblem(e).detail, 'OK', { duration: 5000 }),
      });
    });
  }
}
