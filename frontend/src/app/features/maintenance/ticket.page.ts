import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { Observable } from 'rxjs';
import { fieldErrors, toProblem } from '../../core/http';
import { StaffOption, Ticket, TicketAction } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { MAX_PHOTO_BYTES, compressPhoto } from '../housekeeping/housekeeping.api';
import { MaintenanceApi, categoryLabel, priorityLabel, ticketStatusClass, ticketStatusLabel } from './maintenance.api';

/**
 * One ticket (Sprint 13): what is wrong, where, its photos and timeline, and the next step for whoever is looking —
 * take it on, put it on hold, resolve it (the room goes back to cleaning), or close it after checking.
 */
@Component({
  selector: 'app-ticket',
  imports: [FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatSnackBarModule],
  template: `
    <div class="page tk">
      <a mat-button routerLink="/maintenance" class="back"><mat-icon>arrow_back</mat-icon>Maintenance</a>
      @if (t(); as t) {
        <div class="head">
          <h1>{{ t.number }} · {{ t.title }}</h1>
          <div class="tags">
            <span [class]="'st ' + stCls(t.status)">{{ st(t.status) }}</span>
            <span [class]="'pri ' + t.priority.toLowerCase()">{{ pri(t.priority) }}</span>
            <span class="tag">{{ cat(t.category) }}</span>
          </div>
        </div>
        <dl>
          <dt>Where</dt><dd>{{ t.place }}@if (t.assetName && t.unitId) { · {{ t.assetName }} }@if (t.location && t.unitId) { · {{ t.location }} }</dd>
          @if (t.outOfOrder) {
            <dt>Room</dt>
            <dd><b class="ooo">{{ t.blockActive ? 'Out of order' : 'Was out of order' }}</b>@if (t.expectedReadyOn) { · expected back {{ day(t.expectedReadyOn) }} }</dd>
          }
          <dt>Reported</dt><dd>{{ when(t.createdAt) }}@if (t.reportedByName) { by {{ t.reportedByName }} }</dd>
          <dt>With</dt><dd>{{ t.assignedToName ?? 'Nobody yet' }}</dd>
          @if (t.resolution) { <dt>Fixed</dt><dd>{{ t.resolution }}@if (t.resolvedByName) { ({{ t.resolvedByName }}, {{ when(t.resolvedAt!) }}) }</dd> }
        </dl>
        @if (t.description) { <p class="desc">{{ t.description }}</p> }

        @if (t.photos.length || can('photo')) {
          <h2>Photos</h2>
          <div class="photos">
            @for (p of t.photos; track p.id) {
              <a [href]="photoUrls()[p.id]" target="_blank" rel="noopener" [attr.aria-label]="'Open photo ' + p.fileName">
                @if (photoUrls()[p.id]) { <img [src]="photoUrls()[p.id]" [alt]="p.fileName" /> } @else { <span class="ph"></span> }
              </a>
            }
            @if (can('photo') && t.photos.length < 6) {
              <label class="add"><mat-icon>photo_camera</mat-icon><span>{{ uploading() ? 'Sending…' : 'Add photo' }}</span>
                <input type="file" accept="image/*" capture="environment" (change)="addPhoto($event)" data-testid="photo-input" /></label>
            }
          </div>
          @if (errors()['file']) { <p class="err" role="alert">{{ errors()['file'] }}</p> }
        }

        @if (can('assign')) {
          <section>
            <h2>Give it to</h2>
            <div class="row">
              <mat-form-field appearance="outline" class="grow">
                <mat-label>Maintenance staff</mat-label>
                <mat-select [(ngModel)]="assignTo">
                  <mat-option value="">Nobody</mat-option>
                  @for (s of staff(); track s.userId) { <mat-option [value]="s.userId">{{ s.name }} ({{ s.openWork }} open)</mat-option> }
                </mat-select>
                @if (errors()['userId']) { <mat-hint class="err">{{ errors()['userId'] }}</mat-hint> }
              </mat-form-field>
              <button mat-stroked-button (click)="assign()" [disabled]="busy()">Assign</button>
            </div>
          </section>
        }

        @if (can('progress') || can('resolve')) {
          <section>
            <h2>Work on it</h2>
            <mat-form-field appearance="outline" class="full">
              <mat-label>Note</mat-label>
              <textarea matInput [(ngModel)]="workNote" rows="2" maxlength="2000" placeholder="e.g. Gas refilled; waiting for a compressor part"></textarea>
              @if (errors()['note'] || errors()['resolution']) { <mat-hint class="err">{{ errors()['note'] || errors()['resolution'] }}</mat-hint> }
            </mat-form-field>
            <div class="row wrap">
              @if (can('progress')) {
                <button mat-stroked-button (click)="progress('IN_PROGRESS')" [disabled]="busy()">{{ t.status === 'IN_PROGRESS' ? 'Add update' : 'Start work' }}</button>
                @if (t.status !== 'ON_HOLD') { <button mat-stroked-button (click)="progress('ON_HOLD')" [disabled]="busy()">Put on hold</button> }
              }
              @if (can('resolve')) { <button mat-flat-button (click)="resolve()" [disabled]="busy()">Resolved</button> }
            </div>
            @if (can('resolve') && t.blockActive) { <p class="hint">Resolving gives {{ t.unitName }} back: it goes to housekeeping for a clean first.</p> }
          </section>
        }

        @if (can('close')) {
          <section>
            <h2>Check and close</h2>
            <div class="row">
              <mat-form-field appearance="outline" class="grow">
                <mat-label>Note (optional)</mat-label>
                <input matInput [(ngModel)]="closeNote" maxlength="2000" />
              </mat-form-field>
              <button mat-flat-button (click)="close()" [disabled]="busy()">Close ticket</button>
            </div>
          </section>
        }

        <h2>Timeline</h2>
        <ol class="timeline">
          @for (c of t.comments; track c.id) {
            <li [class.status]="c.kind === 'STATUS'">
              <span class="who">{{ c.createdByName ?? 'System' }} · {{ when(c.createdAt) }}</span>
              @if (c.kind === 'STATUS' && c.toStatus) { <b>{{ st(c.toStatus) }}</b> }
              <span>{{ c.body }}</span>
            </li>
          }
        </ol>
        @if (can('comment')) {
          <div class="row">
            <mat-form-field appearance="outline" class="grow">
              <mat-label>Add a comment</mat-label>
              <input matInput [(ngModel)]="comment" maxlength="2000" (keydown.enter)="addComment()" />
              @if (errors()['body']) { <mat-hint class="err">{{ errors()['body'] }}</mat-hint> }
            </mat-form-field>
            <button mat-stroked-button (click)="addComment()" [disabled]="busy()">Add</button>
          </div>
        }
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>
  `,
  styles: `
    .tk { max-width: 760px; padding-bottom: 40px; }
    .back { margin-left: -8px; }
    h1 { font-size: 1.4rem; margin: 4px 0 6px; }
    h2 { font-size: 1rem; font-weight: 600; margin: 18px 0 8px; }
    .tags { display: flex; gap: 6px; flex-wrap: wrap; }
    .st, .pri, .tag { font-size: .78rem; font-weight: 600; padding: 2px 10px; border-radius: 999px; background: #eef2f1; }
    .pri.urgent { background: #b3261e; color: #fff; } .pri.high { background: #fbe4e2; color: #8c1d18; }
    .st.resolved, .st.closed { background: #e1f2e8; color: #1d5b36; } .st.on-hold { background: #fbefd8; color: #8a5a00; }
    .st.in-progress, .st.assigned { background: #e0eff5; color: #134e63; }
    dl { display: grid; grid-template-columns: max-content 1fr; gap: 4px 14px; margin: 14px 0; }
    dt { color: #5b6b6e; } dd { margin: 0; }
    .ooo { color: #5b3a00; }
    .desc { background: #f5f7f6; border-radius: 8px; padding: 10px 12px; white-space: pre-wrap; }
    .photos { display: flex; flex-wrap: wrap; gap: 8px; }
    .photos img, .ph { width: 96px; height: 96px; object-fit: cover; border-radius: 8px; display: block; background: #eef2f1; }
    .add { width: 96px; height: 96px; border: 2px dashed #a9b6b3; border-radius: 8px; display: flex; flex-direction: column;
           align-items: center; justify-content: center; font-size: .8rem; cursor: pointer; position: relative; }
    .add input { position: absolute; width: 1px; height: 1px; opacity: 0; }
    section { border-top: 1px solid #e3e7e6; margin-top: 8px; }
    .row { display: flex; gap: 8px; align-items: flex-start; }
    .row button { min-height: 48px; margin-top: 4px; }
    .wrap { flex-wrap: wrap; }
    .grow { flex: 1; min-width: 0; }
    .full { width: 100%; }
    .hint { color: #5b6b6e; font-size: .85rem; }
    .timeline { list-style: none; padding: 0; margin: 0; }
    .timeline li { display: flex; flex-direction: column; padding: 8px 0 8px 12px; border-left: 2px solid #d9e0de; }
    .timeline li.status { border-left-color: #1f6f8b; }
    .who { color: #5b6b6e; font-size: .8rem; }
    .err { color: #b3261e; }
  `,
})
export class TicketPage {
  id = input.required<string>();
  private api = inject(MaintenanceApi);
  private ctx = inject(PropertyContext);
  private snack = inject(MatSnackBar);
  private pid = computed(() => this.ctx.current()?.id ?? '');
  t = signal<Ticket | null>(null);
  staff = signal<StaffOption[]>([]);
  photoUrls = signal<Record<string, string>>({});
  busy = signal(false);
  uploading = signal(false);
  error = signal('');
  errors = signal<Record<string, string>>({});
  assignTo = '';
  workNote = '';
  closeNote = '';
  comment = '';
  st = ticketStatusLabel;
  stCls = ticketStatusClass;
  pri = priorityLabel;
  cat = categoryLabel;
  private staffLoaded = false;

  constructor() {
    effect(() => { if (this.pid() && this.id()) this.load(); });
    inject(DestroyRef).onDestroy(() => Object.values(this.photoUrls()).forEach(u => URL.revokeObjectURL(u)));
  }

  can(a: TicketAction) { return !!this.t()?.nextActions.includes(a); }
  when(iso: string) { return inZone(iso, this.ctx.current()?.timezone ?? 'Asia/Kolkata', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false }); }
  day(iso: string) { return inZone(iso + 'T00:00:00Z', 'UTC', { weekday: 'short', day: 'numeric', month: 'short' }); }

  load() {
    this.api.get(this.pid(), this.id()).subscribe({
      next: t => this.show(t),
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  private show(t: Ticket) {
    this.t.set(t);
    this.assignTo = t.assignedTo ?? '';
    if (t.nextActions.includes('assign') && !this.staffLoaded) {
      this.staffLoaded = true;
      this.api.staff(this.pid()).subscribe({ next: s => this.staff.set(s) });
    }
    for (const p of t.photos) {
      if (this.photoUrls()[p.id]) continue;
      this.api.photo(this.pid(), t.id, p.id).subscribe({ next: b => this.photoUrls.update(m => ({ ...m, [p.id]: URL.createObjectURL(b) })) });
    }
  }

  assign() { this.run(this.api.assign(this.pid(), this.t()!, this.assignTo || null, null), 'Assigned.'); }

  progress(status: 'IN_PROGRESS' | 'ON_HOLD') {
    const note = this.workNote.trim();
    if (note.length < 3) {
      this.errors.set({ note: status === 'ON_HOLD' ? 'Say why it is on hold (at least 3 characters), e.g. Waiting for a part.' : 'Say what was done (at least 3 characters).' });
      return;
    }
    this.run(this.api.progress(this.pid(), this.t()!, status, note), status === 'ON_HOLD' ? 'Put on hold.' : 'Update added.', () => this.workNote = '');
  }

  resolve() {
    const r = this.workNote.trim();
    if (r.length < 3) { this.errors.set({ resolution: 'Say what was fixed (at least 3 characters), e.g. Replaced the tap washer.' }); return; }
    this.run(this.api.resolve(this.pid(), this.t()!, r), 'Resolved.', () => this.workNote = '');
  }

  close() { this.run(this.api.close(this.pid(), this.t()!, this.closeNote.trim() || null), 'Closed.'); }

  addComment() {
    const body = this.comment.trim();
    if (!body) { this.errors.set({ body: 'Write the comment first.' }); return; }
    this.run(this.api.comment(this.pid(), this.t()!.id, body), '', () => this.comment = '');
  }

  async addPhoto(e: Event) {
    const input = e.target as HTMLInputElement;
    const f = input.files?.[0];
    input.value = '';
    if (!f) return;
    this.errors.set({});
    this.uploading.set(true);
    const blob = await compressPhoto(f);
    if (blob.size > MAX_PHOTO_BYTES) {
      this.uploading.set(false);
      this.errors.set({ file: `${f.name} is ${(blob.size / 1024 / 1024).toFixed(1)} MB even after shrinking; at most 2 MB. Take it again closer up.` });
      return;
    }
    this.api.addPhoto(this.pid(), this.t()!.id, blob, f.name.replace(/\.[^.]+$/, '') + '.jpg').subscribe({
      next: () => { this.uploading.set(false); this.load(); },
      error: err => {
        this.uploading.set(false);
        const p = toProblem(err);
        this.errors.set({ file: fieldErrors(p)['file'] ?? p.detail });
      },
    });
  }

  /** Runs a step; a field problem shows under its field, anything else above the page's foot. */
  private run(call: Observable<Ticket>, done: string, after?: () => void) {
    this.busy.set(true);
    this.error.set('');
    this.errors.set({});
    call.subscribe({
      next: t => { this.busy.set(false); this.show(t); after?.(); if (done) this.snack.open(done, undefined, { duration: 2000 }); },
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const f = fieldErrors(p);
        this.errors.set(f);
        if (!Object.keys(f).length) { this.error.set(p.detail); this.snack.open(p.detail, 'OK', { duration: 8000 }); }
      },
    });
  }
}
