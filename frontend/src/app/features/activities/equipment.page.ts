import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { EquipmentAssignment, EquipmentBoard, EquipmentItem, EquipmentType } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { ActivitiesApi, EQUIPMENT_STATUS } from './activities.api';
import { EquipmentStatusDialog, EquipmentTypeDialog, IssueEquipmentDialog, ReturnEquipmentDialog } from './equipment-dialogs';

type View = 'out' | 'items';

/**
 * Equipment (Sprint 20, brief §49). What is out now (overdue first) with Return, Hand over and Release; every item with
 * its status, filtered by kind; issuing to someone by name. Managers add kinds and items and move damaged ones through
 * maintenance back into use.
 */
@Component({
  selector: 'app-equipment',
  imports: [MatButtonModule, MatButtonToggleModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page eq">
      <div class="head">
        <h1>Equipment</h1>
        <mat-button-toggle-group [value]="view()" (change)="view.set($event.value)" aria-label="Show">
          <mat-button-toggle value="out">Out now ({{ board()?.out?.length ?? 0 }})</mat-button-toggle>
          <mat-button-toggle value="items">Items</mat-button-toggle>
        </mat-button-toggle-group>
      </div>

      <section class="kinds" data-testid="kinds">
        @for (t of board()?.types ?? []; track t.id) {
          <button type="button" class="kind" [class.sel]="kind() === t.id" (click)="pick(t)" [attr.data-kind]="t.code">
            <b>{{ t.available }}<small>/{{ t.total }}</small></b><span>{{ t.name }}</span>
            @if (outOfUse(t)) { <small class="bad">{{ outOfUse(t) }} out of use</small> }
          </button>
        }
        @if (canManage()) { <button mat-stroked-button class="addkind" (click)="editType(null)"><mat-icon>add</mat-icon>Kind</button> }
      </section>

      <div class="bar">
        @if (canIssue()) { <button mat-flat-button (click)="issue()"><mat-icon>outbox</mat-icon>Issue</button> }
        @if (canManage() && selectedType(); as t) { <button mat-stroked-button (click)="editType(t)">Change {{ t.name.toLowerCase() }} / add items</button> }
      </div>

      @if (view() === 'out') {
        <ul class="list" data-testid="out">
          @for (x of out(); track x.id) {
            <li [attr.data-tag]="x.tag" [class.late]="x.overdue">
              <div class="main"><span><b>{{ x.tag }}</b> {{ x.typeName }}, <b>{{ x.issuedTo }}</b></span>
                <small class="mute">{{ x.status === 'RESERVED' ? 'Kept aside' : 'Out since ' + time(x.issuedAt) }}@if (x.bookingCode) {, {{ x.bookingCode }} }
                  @if (x.reservationReference) {, {{ x.reservationReference }} }@if (x.dueBackAt) {, due back {{ time(x.dueBackAt) }} }</small>
                @if (x.overdue) { <small class="bad">Overdue</small> }</div>
              @if (canIssue()) {
                <div class="btns">
                  @if (x.status === 'ISSUED') { <button mat-flat-button (click)="giveBack(x)">Return</button> }
                  @if (x.status === 'RESERVED') {
                    <button mat-flat-button (click)="handOver(x)">Hand over</button>
                    <button mat-button (click)="release(x)">Release</button>
                  }
                </div>
              }
            </li>
          } @empty { <li class="mute">Nothing is out{{ selectedType() ? ' of this kind' : '' }}.</li> }
        </ul>
      } @else {
        <ul class="list items" data-testid="items">
          @for (i of items(); track i.id) {
            <li [attr.data-tag]="i.tag" [class]="'s-' + i.status">
              <div class="main"><span><b>{{ i.tag }}</b> {{ i.typeName }}</span>
                @if (i.with) { <small class="mute">{{ i.status === 'RESERVED' ? 'Kept aside for' : 'With' }} {{ i.with }}</small> }
                @if (i.statusNote) { <small class="mute">{{ i.statusNote }}</small> }</div>
              <span class="status">{{ label[i.status] }}</span>
              @if (canManage() && i.moves.length && i.status !== 'RESERVED' && i.status !== 'ISSUED') {
                <button mat-button (click)="setStatus(i)">Change</button>
              }
            </li>
          } @empty { <li class="mute">No items{{ selectedType() ? ' of this kind' : '' }} yet.</li> }
        </ul>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>
  `,
  styles: `
    .eq { max-width: 900px; padding-bottom: 60px; }
    .head, .bar { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; } .head { justify-content: space-between; } .bar { margin: var(--space-2) 0; }
    .mute { color: var(--ro-mute); } .err, .bad { color: var(--ro-error); }
    .kinds { display: flex; gap: var(--space-2); flex-wrap: wrap; margin: var(--space-2) 0; align-items: stretch; }
    .kind { background: var(--ro-paper); border: 1px solid var(--ro-glass-edge); border-radius: var(--ro-radius-lg); padding: var(--space-2) var(--space-3); min-width: 110px; display: flex; flex-direction: column;
            text-align: left; cursor: pointer; font: inherit; color: inherit; box-shadow: var(--ro-shadow-card); }
    .kind.sel { border-color: var(--ro-sea); box-shadow: 0 0 0 1px var(--ro-sea) inset; } .kind b { font-size: 1.25rem; } .kind b small { font-size: .85rem; color: var(--ro-mute); }
    .list { list-style: none; margin: 0; padding: 0; background: var(--ro-paper); border: 1px solid var(--ro-glass-edge); border-radius: var(--ro-radius-lg); box-shadow: var(--ro-shadow-card); }
    .list li { display: flex; gap: var(--space-2); align-items: center; flex-wrap: wrap; padding: 10px 14px; border-top: 1px solid var(--ro-sunk); }
    .list li:first-child { border-top: 0; } .list li.late { background: var(--ro-error-bg); }
    .main { flex: 1 1 240px; display: flex; flex-direction: column; min-width: 0; } .btns { display: flex; gap: 6px; }
    .status { font-size: .85rem; border-radius: 10px; padding: 0 var(--space-2); background: var(--ro-sunk); white-space: nowrap; }
    .s-AVAILABLE .status { background: var(--ro-ok-bg); color: var(--ro-ok-mid); } .s-ISSUED .status, .s-RESERVED .status { background: var(--ro-info-bg); color: var(--ro-tide); }
    .s-DAMAGED .status, .s-LOST .status { background: var(--ro-error-bg); color: var(--ro-error-strong); } .s-MAINTENANCE .status { background: var(--ro-warn-wash); color: var(--ro-warn-strong); }
    .s-RETIRED { opacity: .6; }
  `,
})
export class EquipmentPage {
  private api = inject(ActivitiesApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  label = EQUIPMENT_STATUS;

  private pid = computed(() => this.ctx.current()?.id ?? '');
  private tz = computed(() => this.ctx.current()?.timezone ?? 'Asia/Kolkata');
  canManage = computed(() => this.auth.can('equipment.manage', this.pid()));
  canIssue = computed(() => this.auth.can('equipment.issue', this.pid()));
  view = signal<View>('out');
  board = signal<EquipmentBoard | null>(null);
  kind = signal<string | null>(null);
  selectedType = computed(() => this.board()?.types.find(t => t.id === this.kind()) ?? null);
  out = computed(() => {
    const t = this.selectedType();
    return (this.board()?.out ?? []).filter(x => !t || x.typeName === t.name);
  });
  items = computed(() => (this.board()?.items ?? []).filter(i => !this.kind() || i.equipmentTypeId === this.kind()));
  error = signal('');

  constructor() {
    effect(() => { if (this.pid()) untracked(() => this.load()); });
  }

  load() {
    this.error.set('');
    this.api.board(this.pid()).subscribe({ next: b => this.board.set(b), error: e => this.error.set(toProblem(e).detail) });
  }

  pick(t: EquipmentType) { this.kind.set(this.kind() === t.id ? null : t.id); }
  outOfUse(t: EquipmentType) { return (t.byStatus.DAMAGED ?? 0) + (t.byStatus.MAINTENANCE ?? 0) + (t.byStatus.LOST ?? 0); }
  time(iso: string | null) { return iso ? inZone(iso, this.tz(), { weekday: 'short', hour: '2-digit', minute: '2-digit', hour12: false }) : ''; }

  issue() {
    const types = (this.board()?.types ?? []).filter(t => t.isActive);
    this.dialog.open(IssueEquipmentDialog, { data: { propertyId: this.pid(), types, typeId: this.kind() }, maxWidth: '100vw' }).afterClosed()
      .subscribe((x?: EquipmentAssignment[]) => {
        if (!x?.length) return;
        this.snack.open(`${x.map(i => i.tag).join(', ')} ${x[0].status === 'RESERVED' ? 'kept aside' : 'issued'} for ${x[0].issuedTo}.`, 'OK', { duration: 5000 });
        this.view.set('out');
        this.load();
      });
  }

  giveBack(x: EquipmentAssignment) {
    this.dialog.open(ReturnEquipmentDialog, { data: { propertyId: this.pid(), assignment: x }, maxWidth: '100vw' }).afterClosed()
      .subscribe((r?: EquipmentAssignment) => {
        if (!r) return;
        this.snack.open(r.returnCondition === 'GOOD' ? `${r.tag} is back and available.` : r.returnCondition === 'DAMAGED'
          ? `${r.tag} is back damaged and out of use: ${r.returnNote}` : `${r.tag} recorded as lost: ${r.returnNote}`, 'OK', { duration: 5000 });
        this.load();
      });
  }

  handOver(x: EquipmentAssignment) {
    this.api.handOver(this.pid(), x).subscribe({
      next: r => { this.snack.open(`${r.tag} handed to ${r.issuedTo}.`, 'OK', { duration: 4000 }); this.load(); },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  release(x: EquipmentAssignment) {
    this.api.release(this.pid(), x).subscribe({
      next: r => { this.snack.open(`${r.tag} is back on the shelf.`, 'OK', { duration: 4000 }); this.load(); },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  setStatus(i: EquipmentItem) {
    this.dialog.open(EquipmentStatusDialog, { data: { propertyId: this.pid(), item: i }, maxWidth: '100vw' }).afterClosed().subscribe((x?: EquipmentItem) => {
      if (!x) return;
      this.snack.open(`${x.tag} is ${this.label[x.status].toLowerCase()}.`, 'OK', { duration: 4000 });
      this.load();
    });
  }

  editType(t: EquipmentType | null) {
    this.dialog.open(EquipmentTypeDialog, { data: { propertyId: this.pid(), type: t }, maxWidth: '100vw' }).afterClosed().subscribe((msg?: string) => {
      if (!msg) return;
      this.snack.open(msg.includes('added') ? msg + '.' : `${msg} saved.`, 'OK', { duration: 5000 });
      this.load();
    });
  }
}
