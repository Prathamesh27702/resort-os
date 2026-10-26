import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { catchError, concatMap, from, map, of, toArray } from 'rxjs';
import { fieldErrors, showFieldErrors, toProblem } from '../../core/http';
import { MaintenanceAsset, Room, TicketCategory, TicketPriority } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { newKey } from '../reservations/reservations.api';
import { RoomsApi } from '../rooms/rooms.api';
import { MAX_PHOTO_BYTES, compressPhoto } from '../housekeeping/housekeeping.api';
import { CATEGORIES, MaintenanceApi, PRIORITIES, addDays, categoryLabel, localToday, priorityLabel } from './maintenance.api';

interface PickedPhoto { name: string; blob: Blob; url: string; }
const MAX_PHOTOS = 6;

/**
 * Report a problem from a phone (Sprint 13): where, what, how urgent, a few photos (shrunk on the phone first), and —
 * for a room — whether it must come off sale until it is fixed. Opened from a room's housekeeping card with the room filled in.
 */
@Component({
  selector: 'app-report-problem',
  imports: [ReactiveFormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule,
    MatSlideToggleModule, MatSnackBarModule],
  template: `
    <div class="page rp">
      <a mat-button routerLink="/maintenance" class="back"><mat-icon>arrow_back</mat-icon>Maintenance</a>
      <h1>Report a problem</h1>
      <form [formGroup]="form" (ngSubmit)="save()">
        <mat-form-field appearance="outline">
          <mat-label>What is wrong</mat-label>
          <input matInput formControlName="title" maxlength="120" placeholder="e.g. AC not cooling" autocomplete="off" />
          <mat-error>{{ err('title') || 'Say what is wrong in 3–120 characters, e.g. AC not cooling.' }}</mat-error>
        </mat-form-field>

        <div class="two">
          <mat-form-field appearance="outline">
            <mat-label>Kind</mat-label>
            <mat-select formControlName="category">
              @for (c of categories; track c) { <mat-option [value]="c">{{ cat(c) }}</mat-option> }
            </mat-select>
            <mat-error>{{ err('category') }}</mat-error>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>How urgent</mat-label>
            <mat-select formControlName="priority">
              @for (p of priorities; track p) { <mat-option [value]="p">{{ pri(p) }}</mat-option> }
            </mat-select>
            <mat-error>{{ err('priority') }}</mat-error>
          </mat-form-field>
        </div>

        <h2>Where</h2>
        <div class="two">
          <mat-form-field appearance="outline">
            <mat-label>Room</mat-label>
            <mat-select formControlName="unitId">
              <mat-option value="">Not a room</mat-option>
              @for (r of rooms(); track r.id) { <mat-option [value]="r.id">{{ r.code }} · {{ r.accommodationTypeName }}</mat-option> }
            </mat-select>
            <mat-error>{{ err('unitId') }}</mat-error>
          </mat-form-field>
          @if (beds().length) {
            <mat-form-field appearance="outline">
              <mat-label>Bed</mat-label>
              <mat-select formControlName="bedId">
                <mat-option value="">The whole dormitory</mat-option>
                @for (b of beds(); track b.id) { <mat-option [value]="b.id">{{ b.code }}</mat-option> }
              </mat-select>
              <mat-error>{{ err('bedId') }}</mat-error>
            </mat-form-field>
          }
        </div>
        @if (assets().length) {
          <mat-form-field appearance="outline">
            <mat-label>Equipment (optional)</mat-label>
            <mat-select formControlName="assetId">
              <mat-option value="">None</mat-option>
              @for (a of assetChoices(); track a.id) { <mat-option [value]="a.id">{{ a.code }} · {{ a.name }}</mat-option> }
            </mat-select>
            <mat-error>{{ err('assetId') }}</mat-error>
          </mat-form-field>
        }
        <mat-form-field appearance="outline">
          <mat-label>Place {{ form.controls.unitId.value || form.controls.assetId.value ? '(optional)' : '' }}</mat-label>
          <input matInput formControlName="location" maxlength="120" placeholder="e.g. Pool deck, bathroom tap" autocomplete="off" />
          <mat-error>{{ err('location') || 'Say where the problem is: choose a room or equipment, or type the place (e.g. Pool deck).' }}</mat-error>
        </mat-form-field>

        @if (form.controls.unitId.value) {
          <div class="ooo">
            <mat-slide-toggle formControlName="outOfOrder">Take {{ placeName() }} out of order</mat-slide-toggle>
            <p class="hint">It can't be booked or checked into from tonight until the date below, or until the repair is resolved.</p>
            @if (form.controls.outOfOrder.value) {
              <mat-form-field appearance="outline">
                <mat-label>Usable again on</mat-label>
                <input matInput type="date" formControlName="expectedReadyOn" [min]="minDate()" [max]="maxDate()" />
                <mat-error>{{ err('expectedReadyOn') || 'Say when the room should be usable again (the room is blocked until then).' }}</mat-error>
              </mat-form-field>
            }
            @if (err('outOfOrder')) { <p class="err" role="alert">{{ err('outOfOrder') }}</p> }
          </div>
        }

        <mat-form-field appearance="outline">
          <mat-label>Details (optional)</mat-label>
          <textarea matInput formControlName="description" rows="3" maxlength="2000" placeholder="What you saw, since when"></textarea>
          <mat-error>{{ err('description') }}</mat-error>
        </mat-form-field>

        <h2>Photos</h2>
        <div class="photos">
          @for (p of photos(); track p.url; let i = $index) {
            <figure><img [src]="p.url" [alt]="'Photo ' + (i + 1)" /><button type="button" mat-icon-button (click)="removePhoto(i)" [attr.aria-label]="'Remove photo ' + (i + 1)"><mat-icon>close</mat-icon></button></figure>
          }
          @if (photos().length < maxPhotos) {
            <label class="add">
              <mat-icon>photo_camera</mat-icon><span>{{ shrinking() ? 'Shrinking…' : 'Add photo' }}</span>
              <input type="file" accept="image/*" capture="environment" multiple (change)="pick($event)" data-testid="photo-input" />
            </label>
          }
        </div>
        @if (photoError()) { <p class="err" role="alert">{{ photoError() }}</p> }
        @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

        <button mat-flat-button type="submit" class="send" [disabled]="busy() || shrinking()">{{ busy() ? 'Sending…' : 'Send report' }}</button>
      </form>
    </div>
  `,
  styles: `
    .rp { max-width: 640px; padding-bottom: 40px; }
    .back { margin-left: -8px; }
    h1 { margin: 4px 0 12px; }
    h2 { font-size: 1rem; font-weight: 600; margin: 8px 0; }
    form { display: flex; flex-direction: column; }
    .two { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
    .ooo { background: #f5f7f6; border-radius: 10px; padding: 10px 12px; margin-bottom: 12px; }
    .ooo mat-form-field { width: 100%; margin-top: 8px; }
    .hint { color: #5b6b6e; font-size: .85rem; margin: 4px 0 0; }
    .photos { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 8px; }
    figure { position: relative; margin: 0; width: 88px; height: 88px; }
    figure img { width: 100%; height: 100%; object-fit: cover; border-radius: 8px; }
    figure button { position: absolute; top: -6px; right: -6px; background: #fff; transform: scale(.8); }
    .add { width: 88px; height: 88px; border: 2px dashed #a9b6b3; border-radius: 8px; display: flex; flex-direction: column;
           align-items: center; justify-content: center; color: #33474a; font-size: .8rem; cursor: pointer; }
    .add input { position: absolute; width: 1px; height: 1px; opacity: 0; }
    .send { min-height: 52px; font-size: 1.05rem; margin-top: 12px; border-radius: 10px; }
    .err { color: #b3261e; }
    @media (max-width: 480px) { .two { grid-template-columns: 1fr; gap: 0; } }
  `,
})
export class ReportProblemPage {
  private api = inject(MaintenanceApi);
  private roomsApi = inject(RoomsApi);
  private ctx = inject(PropertyContext);
  private router = inject(Router);
  private snack = inject(MatSnackBar);
  private route = inject(ActivatedRoute);
  private key = newKey();
  private pid = computed(() => this.ctx.current()?.id ?? '');
  categories = CATEGORIES;
  priorities = PRIORITIES;
  cat = categoryLabel;
  pri = priorityLabel;
  maxPhotos = MAX_PHOTOS;
  rooms = signal<Room[]>([]);
  assets = signal<MaintenanceAsset[]>([]);
  photos = signal<PickedPhoto[]>([]);
  shrinking = signal(false);
  busy = signal(false);
  error = signal('');
  photoError = signal('');
  private serverErrors = signal<Record<string, string>>({});
  private unitId = signal('');

  form = inject(FormBuilder).nonNullable.group({
    title: ['', [Validators.required, Validators.minLength(3), Validators.maxLength(120)]],
    category: ['OTHER' as TicketCategory],
    priority: ['NORMAL' as TicketPriority],
    unitId: [''], bedId: [''], assetId: [''], location: [''],
    outOfOrder: [false], expectedReadyOn: [''],
    description: [''],
  });

  beds = computed(() => this.rooms().find(r => r.id === this.unitId())?.beds.filter(b => b.isActive) ?? []);
  /** Equipment in the chosen room, or anywhere when no room is chosen. */
  assetChoices = computed(() => this.assets().filter(a => a.isActive && (!this.unitId() || !a.unitId || a.unitId === this.unitId())));
  minDate = computed(() => addDays(localToday(this.ctx.current()?.timezone ?? 'Asia/Kolkata'), 1));
  maxDate = computed(() => addDays(localToday(this.ctx.current()?.timezone ?? 'Asia/Kolkata'), 90));
  placeName = computed(() => this.rooms().find(r => r.id === this.unitId())?.code ?? 'the room');

  constructor() {
    const q = this.route.snapshot.queryParamMap;
    const pid = this.pid();
    this.roomsApi.rooms(pid).subscribe({
      next: r => {
        this.rooms.set(r.filter(x => x.isActive));
        if (q.get('unitId')) this.form.patchValue({ unitId: q.get('unitId')!, bedId: q.get('bedId') ?? '' });
      },
    });
    this.api.assets(pid).subscribe({ next: a => this.assets.set(a), error: () => this.assets.set([]) });
    this.form.controls.unitId.valueChanges.subscribe(v => {
      this.unitId.set(v);
      this.form.patchValue({ bedId: '' }, { emitEvent: false });
      if (!v) this.form.patchValue({ outOfOrder: false }, { emitEvent: false });
    });
    this.form.controls.outOfOrder.valueChanges.subscribe(on => {
      if (on && !this.form.controls.expectedReadyOn.value) this.form.patchValue({ expectedReadyOn: addDays(this.minDate(), 1) });
    });
  }

  err(field: string) { return this.serverErrors()[field] ?? ''; }

  async pick(e: Event) {
    const input = e.target as HTMLInputElement;
    const files = [...(input.files ?? [])];
    input.value = '';
    this.photoError.set('');
    const room = MAX_PHOTOS - this.photos().length;
    if (files.length > room) this.photoError.set(`At most ${MAX_PHOTOS} photos per report; only the first ${room} were added.`);
    this.shrinking.set(true);
    for (const f of files.slice(0, room)) {
      if (!f.type.startsWith('image/')) { this.photoError.set(`${f.name} isn't a photo. Choose a JPEG, PNG or WebP picture.`); continue; }
      const blob = await compressPhoto(f);
      if (blob.size > MAX_PHOTO_BYTES) {
        this.photoError.set(`${f.name} is ${(blob.size / 1024 / 1024).toFixed(1)} MB even after shrinking; at most 2 MB. Take it again closer up.`);
        continue;
      }
      this.photos.update(p => [...p, { name: f.name.replace(/\.[^.]+$/, '') + '.jpg', blob, url: URL.createObjectURL(blob) }]);
    }
    this.shrinking.set(false);
  }

  removePhoto(i: number) {
    URL.revokeObjectURL(this.photos()[i].url);
    this.photos.update(p => p.filter((_, j) => j !== i));
  }

  save() {
    this.error.set('');
    this.serverErrors.set({});
    const v = this.form.getRawValue();
    if (!v.unitId && !v.assetId && !v.location.trim()) this.form.controls.location.setErrors({ where: true });
    if (v.outOfOrder && !v.expectedReadyOn) this.form.controls.expectedReadyOn.setErrors({ required: true });
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    const pid = this.pid();
    this.api.create(pid, {
      title: v.title.trim(), description: v.description.trim() || null, category: v.category, priority: v.priority,
      unitId: v.unitId || null, bedId: v.bedId || null, assetId: v.assetId || null, location: v.location.trim() || null,
      outOfOrder: !!v.unitId && v.outOfOrder, expectedReadyOn: v.unitId && v.outOfOrder ? v.expectedReadyOn : null,
    }, this.key).subscribe({
      next: t => {
        // Photos go one by one after the ticket exists; one that fails doesn't lose the report.
        from(this.photos()).pipe(
          concatMap(p => this.api.addPhoto(pid, t.id, p.blob, p.name).pipe(map(() => null), catchError(e => of(`${p.name}: ${toProblem(e).detail}`)))),
          toArray(),
        ).subscribe(problems => {
          const failed = problems.filter(x => x);
          this.snack.open(failed.length ? `${t.number} reported, but a photo wasn't added. ${failed[0]}` : `${t.number} reported.`,
            'OK', { duration: failed.length ? 10000 : 3000 });
          this.photos().forEach(p => URL.revokeObjectURL(p.url));
          this.router.navigate(['/maintenance', t.id]);
        });
      },
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const errs = fieldErrors(p);
        this.serverErrors.set(errs);
        showFieldErrors(this.form, p);
        if (!Object.keys(errs).length) this.error.set(p.detail);
      },
    });
  }
}
