import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormArray, FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { fieldErrors, showFieldErrors, toProblem } from '../../core/http';
import { AccommodationType, ChecklistTemplate, TaskType } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { RoomsApi } from '../rooms/rooms.api';
import { HousekeepingApi, TASK_TYPES, taskTypeLabel } from './housekeeping.api';

/**
 * Cleaning checklists (Sprint 13): the lines a housekeeper ticks for each kind of cleaning, optionally per room type.
 * A change applies to tasks created from now on; tasks already open keep the lines they started with.
 */
@Component({
  selector: 'app-checklists',
  imports: [ReactiveFormsModule, RouterLink, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule,
    MatSelectModule, MatSlideToggleModule, MatSnackBarModule],
  template: `
    <div class="page cl">
      <a mat-button routerLink="/housekeeping" class="back"><mat-icon>arrow_back</mat-icon>Housekeeping</a>
      <div class="head">
        <h1>Cleaning checklists</h1>
        @if (!editing()) { <button mat-flat-button (click)="edit(null)"><mat-icon>add</mat-icon>New checklist</button> }
      </div>
      <p class="mute">Changes apply to cleaning tasks created from now on. A room type's own checklist is used before the general one.</p>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

      @if (editing()) {
        <form [formGroup]="form" (ngSubmit)="save()" class="edit">
          <mat-form-field appearance="outline">
            <mat-label>Name</mat-label>
            <input matInput formControlName="name" maxlength="80" placeholder="e.g. Departure clean — villas" />
            <mat-error>{{ err('name') || 'Give the checklist a name (2–80 characters).' }}</mat-error>
          </mat-form-field>
          <div class="two">
            <mat-form-field appearance="outline">
              <mat-label>Kind of cleaning</mat-label>
              <mat-select formControlName="taskType">
                @for (t of types; track t) { <mat-option [value]="t">{{ typeLabel(t) }}</mat-option> }
              </mat-select>
              <mat-error>{{ err('taskType') }}</mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Room type</mat-label>
              <mat-select formControlName="accommodationTypeId">
                <mat-option value="">Every room type</mat-option>
                @for (a of roomTypes(); track a.id) { <mat-option [value]="a.id">{{ a.name }}</mat-option> }
              </mat-select>
              <mat-error>{{ err('accommodationTypeId') }}</mat-error>
            </mat-form-field>
          </div>
          <mat-slide-toggle formControlName="isActive">In use</mat-slide-toggle>

          <h2>Lines</h2>
          <div formArrayName="items" class="items">
            @for (g of items.controls; track g; let i = $index) {
              <div [formGroupName]="i" class="item">
                <mat-form-field appearance="outline" class="grow">
                  <mat-label>Line {{ i + 1 }}</mat-label>
                  <input matInput formControlName="label" maxlength="120" />
                  <mat-error>{{ err('items.' + i + '.label') || 'Write the line (2–120 characters).' }}</mat-error>
                </mat-form-field>
                <mat-checkbox formControlName="isRequired">Required</mat-checkbox>
                <button mat-icon-button type="button" (click)="move(i, -1)" [disabled]="i === 0" [attr.aria-label]="'Move line ' + (i + 1) + ' up'"><mat-icon>arrow_upward</mat-icon></button>
                <button mat-icon-button type="button" (click)="remove(i)" [attr.aria-label]="'Remove line ' + (i + 1)"><mat-icon>delete</mat-icon></button>
              </div>
            }
          </div>
          @if (err('items')) { <p class="err" role="alert">{{ err('items') }}</p> }
          <button mat-button type="button" (click)="add()"><mat-icon>add</mat-icon>Add line</button>
          <div class="actions">
            <button mat-button type="button" (click)="editing.set(false)">Cancel</button>
            <button mat-flat-button type="submit" [disabled]="busy()">Save checklist</button>
          </div>
        </form>
      }

      <div class="list">
        @for (t of templates(); track t.id) {
          <article class="tpl" [class.off]="!t.isActive">
            <div class="top">
              <div><b>{{ t.name }}</b><div class="mute">{{ typeLabel(t.taskType) }} · {{ t.accommodationTypeName ?? 'Every room type' }}@if (!t.isActive) { · not in use }</div></div>
              <button mat-button (click)="edit(t)">Edit</button>
            </div>
            <ol>@for (i of t.items; track i.id) { <li>{{ i.label }}@if (i.isRequired) { <span class="req">required</span> }</li> }</ol>
          </article>
        } @empty {
          @if (loaded()) { <p class="mute">No checklists yet: housekeepers get the standard departure clean list until you add one.</p> }
        }
      </div>
    </div>
  `,
  styles: `
    .cl { max-width: 760px; padding-bottom: 40px; }
    .back { margin-left: -8px; }
    .head { display: flex; justify-content: space-between; align-items: center; gap: 8px; flex-wrap: wrap; }
    .mute { color: #5b6b6e; }
    .edit { display: flex; flex-direction: column; background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 14px; margin: 12px 0; }
    .two { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
    h2 { font-size: 1rem; font-weight: 600; margin: 14px 0 6px; }
    .item { display: flex; align-items: center; gap: 4px; flex-wrap: wrap; }
    .grow { flex: 1 1 220px; }
    .actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 8px; }
    .list { display: flex; flex-direction: column; gap: 10px; margin-top: 12px; }
    .tpl { background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 12px 14px; }
    .tpl.off { opacity: .7; }
    .top { display: flex; justify-content: space-between; align-items: flex-start; gap: 8px; }
    ol { margin: 8px 0 0; padding-left: 20px; }
    .req { font-size: .72rem; color: #8a2449; margin-left: 6px; }
    .err { color: #b3261e; }
    @media (max-width: 480px) { .two { grid-template-columns: 1fr; gap: 0; } }
  `,
})
export class ChecklistsPage {
  private api = inject(HousekeepingApi);
  private roomsApi = inject(RoomsApi);
  private ctx = inject(PropertyContext);
  private snack = inject(MatSnackBar);
  private fb = inject(FormBuilder).nonNullable;
  private pid = computed(() => this.ctx.current()?.id ?? '');
  templates = signal<ChecklistTemplate[]>([]);
  roomTypes = signal<AccommodationType[]>([]);
  loaded = signal(false);
  editing = signal(false);
  busy = signal(false);
  error = signal('');
  private serverErrors = signal<Record<string, string>>({});
  private editId: string | null = null;
  types = TASK_TYPES;
  typeLabel = taskTypeLabel;

  form = this.fb.group({
    name: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(80)]],
    taskType: ['DEPARTURE' as TaskType],
    accommodationTypeId: [''],
    isActive: [true],
    items: this.fb.array<FormGroup>([]),
  });
  get items() { return this.form.controls.items as FormArray<FormGroup>; }

  constructor() {
    effect(() => {
      if (!this.pid()) return;
      this.load();
      this.roomsApi.types(this.pid()).subscribe({ next: t => this.roomTypes.set(t), error: () => this.roomTypes.set([]) });
    });
  }

  load() {
    this.api.checklists(this.pid()).subscribe({
      next: t => { this.templates.set(t); this.loaded.set(true); },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  err(field: string) { return this.serverErrors()[field] ?? ''; }

  private line(label = '', isRequired = true) {
    return this.fb.group({ label: [label, [Validators.required, Validators.minLength(2), Validators.maxLength(120)]], isRequired: [isRequired] });
  }

  edit(t: ChecklistTemplate | null) {
    this.editId = t?.id ?? null;
    this.serverErrors.set({});
    this.error.set('');
    this.items.clear();
    this.form.reset({ name: t?.name ?? '', taskType: t?.taskType ?? 'DEPARTURE', accommodationTypeId: t?.accommodationTypeId ?? '', isActive: t?.isActive ?? true });
    for (const i of t?.items ?? [{ label: '', isRequired: true }]) this.items.push(this.line(i.label, i.isRequired));
    this.editing.set(true);
  }

  add() { this.items.push(this.line()); }
  remove(i: number) { this.items.removeAt(i); }
  move(i: number, by: number) {
    const c = this.items.at(i);
    this.items.removeAt(i);
    this.items.insert(i + by, c);
  }

  save() {
    this.serverErrors.set({});
    if (!this.items.length) { this.serverErrors.set({ items: 'Add at least one line to tick.' }); return; }
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.api.saveChecklist(this.pid(), this.editId, {
      name: v.name.trim(), taskType: v.taskType, accommodationTypeId: v.accommodationTypeId || null, isActive: v.isActive,
      items: v.items.map(i => ({ label: String(i['label']).trim(), isRequired: !!i['isRequired'] })),
    }).subscribe({
      next: t => { this.busy.set(false); this.editing.set(false); this.snack.open(`${t.name} saved.`, undefined, { duration: 2500 }); this.load(); },
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        const errs = fieldErrors(p);
        // "Items[0].Label" → "items.0.label", matching the err() keys in the template.
        const byPath: Record<string, string> = {};
        for (const [k, m] of Object.entries(errs)) byPath[k.replace(/\[(\d+)\]\.(\w)/g, (_, n, c) => `.${n}.${c.toLowerCase()}`)] = m;
        this.serverErrors.set(byPath);
        showFieldErrors(this.form, p);
        if (!Object.keys(errs).length) this.error.set(p.detail);
      },
    });
  }
}
