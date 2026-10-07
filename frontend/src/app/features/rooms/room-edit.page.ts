import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { FormBuilder, FormsModule, ReactiveFormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { fieldErrors, toProblem } from '../../core/http';
import { AccommodationType, BED_TYPES, Bed, Room } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { RoomsApi, label } from './rooms.api';
import { roomForm } from './rooms.forms';

@Component({
  selector: 'app-room-edit',
  imports: [ReactiveFormsModule, FormsModule, DecimalPipe, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule,
    MatSlideToggleModule, MatSnackBarModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <a routerLink="/rooms" class="back">Rooms</a>
          <h1>{{ isNew() ? 'Add room' : (room()?.name ?? '') }}</h1>
          @if (!isNew() && !canManage()) { <p>You can view this room but not change it.</p> }
        </div>
      </div>

      @if (isNew() || room()) {
      <form class="panel box" [formGroup]="form" (ngSubmit)="save()">
        <div class="form-grid">
          <mat-form-field appearance="outline">
            <mat-label>Room number / code</mat-label>
            <input matInput formControlName="code" placeholder="103 or DORM" />
            <mat-hint>{{ isNew() ? 'Can’t be changed later.' : 'Fixed after creation.' }}</mat-hint>
            @if (err()['code']) { <mat-error>{{ err()['code'] }}</mat-error> } @else { <mat-error>Up to 20 letters, digits, - or _ (no spaces).</mat-error> }
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Name shown to staff</mat-label>
            <input matInput formControlName="name" placeholder="Room 103" />
            <mat-error>Required (max 100 characters).</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Room type</mat-label>
            <mat-select formControlName="accommodationTypeId">
              @for (t of selectableTypes(); track t.id) {
                <mat-option [value]="t.id">{{ t.name }} · {{ t.inventoryMode === 'BED' ? 'sold per bed' : 'sold per room' }}</mat-option>
              }
            </mat-select>
            <mat-error>Choose a room type.</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Floor</mat-label>
            <input matInput formControlName="floor" placeholder="Ground" />
          </mat-form-field>
          <mat-form-field appearance="outline" class="full">
            <mat-label>Notes for staff</mat-label>
            <textarea matInput formControlName="notes" rows="2"></textarea>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Display order</mat-label>
            <input matInput type="number" formControlName="sortOrder" min="0" />
          </mat-form-field>
          @if (!isNew()) {
            <mat-slide-toggle formControlName="isActive" class="toggle">Room is in use</mat-slide-toggle>
          }
        </div>
        @if (!isNew() && !form.controls.isActive.value) {
          <p class="note">Inactive rooms can't be sold or blocked. Nothing is deleted; reactivate any time.</p>
        }
        @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
        @if (canManage()) {
          <div class="actions">
            <button mat-flat-button type="submit" [disabled]="busy()">{{ isNew() ? 'Add room' : 'Save changes' }}</button>
            <a mat-button routerLink="/rooms">Cancel</a>
          </div>
        }
      </form>
      } @else {
        <p class="loading">Loading…</p>
      }

      @if (!isNew() && room(); as r) {
        @if (r.inventoryMode === 'BED') {
          <section class="panel box">
            <h2>Beds</h2>
            <p class="sub">Each active bed is sold separately. Deactivate a broken bed instead of deleting it.</p>
            @if (r.beds.length) {
              <table class="data">
                <thead><tr><th>Code</th><th>Name</th><th>Type</th><th>In use</th>@if (canManage()) { <th></th> }</tr></thead>
                <tbody>
                  @for (b of beds(); track b.id) {
                    <tr>
                      <td class="num"><b>{{ b.code }}</b></td>
                      <td>@if (canManage()) { <input class="cell" [(ngModel)]="b.name" [name]="'n' + b.id" maxlength="100" /> } @else { {{ b.name }} }</td>
                      <td>
                        @if (canManage()) {
                          <select class="cell" [(ngModel)]="b.bedType" [name]="'t' + b.id">
                            @for (t of bedTypes; track t) { <option [value]="t">{{ label(t) }}</option> }
                          </select>
                        } @else { {{ label(b.bedType) }} }
                      </td>
                      <td>@if (canManage()) { <input type="checkbox" [(ngModel)]="b.isActive" [name]="'a' + b.id" [attr.aria-label]="'Bed ' + b.code + ' in use'" /> } @else { {{ b.isActive ? 'Yes' : 'No' }} }</td>
                      @if (canManage()) { <td><button mat-button type="button" (click)="saveBed(b)">Save</button></td> }
                    </tr>
                  }
                </tbody>
              </table>
            } @else {
              <p class="mute">No beds yet.</p>
            }
            @if (canManage()) {
              <div class="add-beds">
                <mat-form-field appearance="outline" subscriptSizing="dynamic">
                  <mat-label>Add how many beds</mat-label>
                  <input matInput type="number" min="1" max="50" [(ngModel)]="addCount" name="addCount" />
                </mat-form-field>
                <mat-form-field appearance="outline" subscriptSizing="dynamic">
                  <mat-label>Bed type</mat-label>
                  <mat-select [(ngModel)]="addType" name="addType">
                    <mat-option value="BUNKS">Bunk pairs (lower / upper)</mat-option>
                    @for (t of bedTypes; track t) { <mat-option [value]="t">{{ label(t) }}</mat-option> }
                  </mat-select>
                </mat-form-field>
                <button mat-stroked-button type="button" (click)="addBeds()" [disabled]="busy() || addCount < 1 || addCount > 50">Add beds</button>
              </div>
              <p class="mute small">New beds are numbered after the last one (B{{ nextNumber() | number: '2.0-0' }}…).</p>
            }
          </section>
        }
      }
    </div>
  `,
  styles: `
    .loading { color: #5b6b6e; }
    .back { color: #5b6b6e; text-decoration: none; font-size: 0.9rem; } .back::before { content: '‹ '; }
    .box { padding: 20px 24px; max-width: 820px; margin-bottom: 16px; }
    h2 { font-size: 1rem; color: #0e3b43; margin-bottom: 4px; }
    .sub, .mute { color: #5b6b6e; } .small { font-size: .85rem; }
    .actions { display: flex; gap: 8px; margin-top: 8px; }
    .toggle { margin: 12px 0; }
    .note { color: #7a4a00; background: #fdf0d8; padding: 8px 12px; border-radius: 6px; }
    .err { color: #b3261e; }
    .cell { width: 100%; padding: 6px 8px; border: 1px solid #d9e0de; border-radius: 6px; font: inherit; background: #fff; }
    .add-beds { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; margin-top: 16px; }
  `,
})
export class RoomEditPage implements OnInit {
  id = input<string>();
  /** ?type= preselects the room type (used after adding a new type). */
  type = input<string>();
  private api = inject(RoomsApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private router = inject(Router);
  private snack = inject(MatSnackBar);

  label = label;
  bedTypes = BED_TYPES;
  isNew = computed(() => !this.id());
  private pid = computed(() => this.ctx.current()?.id ?? '');
  canManage = computed(() => this.auth.can('accommodation.manage', this.pid()));

  room = signal<Room | null>(null);
  beds = signal<Bed[]>([]);
  types = signal<AccommodationType[]>([]);
  busy = signal(false);
  error = signal('');
  err = signal<Record<string, string>>({});
  addCount = 10;
  addType = 'BUNKS';

  form = roomForm(inject(FormBuilder));
  selectableTypes = computed(() => this.types().filter(t => t.isActive || t.id === this.room()?.accommodationTypeId));
  nextNumber = computed(() => this.beds().reduce((n, b) => Math.max(n, Number(/\d+/.exec(b.code)?.[0] ?? 0)), 0) + 1);

  async ngOnInit() {
    this.types.set(await firstValueFrom(this.api.types(this.pid())));
    if (this.isNew()) {
      const first = this.types().find(t => t.isActive && t.id === this.type()) ?? this.types().find(t => t.isActive);
      if (first) this.form.controls.accommodationTypeId.setValue(first.id);
      return;
    }
    this.form.controls.code.disable();
    this.show(await firstValueFrom(this.api.room(this.pid(), this.id()!)));
    if (!this.canManage()) this.form.disable();
  }

  private show(r: Room) {
    this.room.set(r);
    this.beds.set(r.beds.map(b => ({ ...b })));
    this.form.patchValue({ ...r, floor: r.floor ?? '', notes: r.notes ?? '' });
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true); this.error.set(''); this.err.set({});
    const v = this.form.getRawValue();
    const body = { ...v, floor: v.floor || null, notes: v.notes || null, sortOrder: Number(v.sortOrder) || 0 };
    const req = this.isNew()
      ? this.api.createRoom(this.pid(), body)
      : this.api.updateRoom(this.pid(), this.id()!, { ...body, version: this.room()!.version });
    req.subscribe({
      next: r => {
        this.busy.set(false);
        if (this.isNew()) {
          const bedBased = r.inventoryMode === 'BED';
          this.snack.open(bedBased ? 'Dormitory added. Now add its beds.' : 'Room added', undefined, { duration: 3000 });
          this.router.navigate(bedBased ? ['/rooms', r.id] : ['/rooms']);
        } else {
          this.show(r);
          this.snack.open('Changes saved', undefined, { duration: 2500 });
        }
      },
      error: e => this.fail(e),
    });
  }

  saveBed(b: Bed) {
    this.api.updateBed(this.pid(), b.id, { name: b.name, bedType: b.bedType, sortOrder: b.sortOrder, isActive: b.isActive, version: b.version })
      .subscribe({
        next: saved => { this.beds.update(list => list.map(x => (x.id === saved.id ? { ...saved } : x))); this.snack.open(`Bed ${saved.code} saved`, undefined, { duration: 2000 }); },
        error: e => this.snack.open(toProblem(e).detail, 'OK', { duration: 5000 }),
      });
  }

  /** Adds N beds one by one (each is its own audited insert), numbering after the highest existing bed. */
  async addBeds() {
    this.busy.set(true);
    const start = this.nextNumber();
    let added = 0;
    try {
      for (let i = 0; i < this.addCount; i++) {
        const n = start + i;
        const bedType = this.addType === 'BUNKS' ? (i % 2 === 0 ? 'BUNK_LOWER' : 'BUNK_UPPER') : this.addType;
        await firstValueFrom(this.api.createBed(this.pid(), this.id()!, {
          code: `B${String(n).padStart(2, '0')}`, name: `Bed ${n}`, bedType, sortOrder: n,
        }));
        added++;
      }
      this.snack.open(`${added} bed${added === 1 ? '' : 's'} added`, undefined, { duration: 2500 });
    } catch (e) {
      this.snack.open(`${added} added, then: ${toProblem(e).detail}`, 'OK', { duration: 6000 });
    } finally {
      this.show(await firstValueFrom(this.api.room(this.pid(), this.id()!)));
      this.busy.set(false);
    }
  }

  private fail(e: unknown) {
    const p = toProblem(e);
    this.err.set(fieldErrors(p));
    this.error.set(p.code === 'CONCURRENCY_CONFLICT'
      ? 'Someone else changed this room while you were editing. Reload the page to see their changes.'
      : p.detail);
    this.busy.set(false);
  }
}
