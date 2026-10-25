import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { fieldErrors, showFieldErrors, toProblem } from '../../core/http';
import { HousekeepingRoom, HousekeepingTask, StaffOption, TaskType } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { housekeepingLabel } from '../front-desk/front-desk.api';
import { HousekeepingApi, TASK_TYPES, statusClass, taskStatusLabel, taskTypeLabel } from './housekeeping.api';

export interface TaskSheetData { propertyId: string; room: HousekeepingRoom; canAssign: boolean; }

/**
 * A room's card opened up: its checklist to tick, who has it, what happened so far, and the less common steps —
 * assign it, start an inspection, send it back with what to redo, report a problem. Closes with true when anything changed.
 */
@Component({
  selector: 'app-task-sheet',
  imports: [FormsModule, ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule,
    MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ room.place }} <span [class]="'state ' + cls(room.status)">{{ hk(room.status) }}</span></h2>
    <div mat-dialog-content>
      <p class="mute">{{ room.typeName }}@if (room.occupied) { · guest in room }@if (room.arrivalToday) { · guest arrives today }</p>

      @if (task(); as t) {
        <p><b>{{ typeLabel(t.taskType) }}</b> · {{ statusLabel(t.status) }}@if (t.reservationReference) { · after {{ t.reservationReference }} }</p>
        @if (t.note) { <p class="note">{{ t.note }}</p> }
        @if (t.inspectionNote && t.failedInspections && t.status !== 'DONE') { <p class="back">Sent back: {{ t.inspectionNote }}</p> }
        <ul class="trail">
          @if (t.startedAt) { <li>Started {{ time(t.startedAt) }} by {{ t.startedByName }}</li> }
          @if (t.finishedAt) { <li>Cleaned {{ time(t.finishedAt) }} by {{ t.finishedByName }}</li> }
        </ul>

        @if (t.checklist.length) {
          <h3>Checklist</h3>
          <div class="checklist" role="group" aria-label="Checklist">
            @for (l of t.checklist; track l.id) {
              <mat-checkbox [checked]="ticked().has(l.id)" (change)="tick(l.id, $event.checked)" [disabled]="!canFinish()">
                {{ l.label }}@if (l.required) { <b class="req">required</b> }
              </mat-checkbox>
            }
          </div>
        }

        @if (canFinish()) {
          <mat-form-field appearance="outline" class="full">
            <mat-label>Note (optional)</mat-label>
            <input matInput [(ngModel)]="finishNote" maxlength="500" placeholder="e.g. Minibar restocked" />
          </mat-form-field>
          @if (checkedError()) { <p class="err" role="alert">{{ checkedError() }}</p> }
        }

        @if (can('assign')) {
          <mat-form-field appearance="outline" class="full">
            <mat-label>Assigned to</mat-label>
            <mat-select [value]="t.assignedTo ?? ''" (selectionChange)="assign($event.value || null)" [disabled]="busy()">
              <mat-option value="">Nobody yet</mat-option>
              @for (s of staff(); track s.userId) { <mat-option [value]="s.userId">{{ s.name }} ({{ s.openWork }} open)</mat-option> }
            </mat-select>
            <mat-error>{{ assignError() }}</mat-error>
          </mat-form-field>
          @if (assignError()) { <p class="err" role="alert">{{ assignError() }}</p> }
        }

        @if (can('inspect')) {
          <form [formGroup]="inspectForm" (ngSubmit)="sendBack()" id="send-back">
            <mat-form-field appearance="outline" class="full">
              <mat-label>What needs redoing (to send it back)</mat-label>
              <input matInput formControlName="note" maxlength="500" placeholder="e.g. Bathroom mirror smeared" />
              <mat-error>{{ serverErrors()['note'] || 'Say what needs redoing (at least 3 characters).' }}</mat-error>
            </mat-form-field>
          </form>
        }
      } @else {
        <p>No cleaning task for this room right now.@if (room.status === 'OUT_OF_ORDER') { It gets one when its repair is resolved. }</p>
      }
      @if (room.repair; as rep) {
        <p class="repair"><mat-icon>build</mat-icon>{{ rep.number }}: {{ rep.title }}@if (rep.outOfOrder) { (out of order) }</p>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>
    <div mat-dialog-actions class="actions">
      <button mat-button type="button" (click)="report()"><mat-icon>build</mat-icon>Report a problem</button>
      <span class="grow"></span>
      @if (can('start')) { <button mat-flat-button (click)="start()" [disabled]="busy()">Start</button> }
      @if (canFinish()) { <button mat-flat-button (click)="finish()" [disabled]="busy()">Done</button> }
      @if (can('inspect')) {
        @if (room.status === 'CLEAN') { <button mat-button (click)="inspect('START')" [disabled]="busy()">Start inspection</button> }
        <button mat-stroked-button type="submit" form="send-back" [disabled]="busy()">Send back</button>
        <button mat-flat-button (click)="inspect('PASS')" [disabled]="busy()">Mark ready</button>
      }
      <button mat-button mat-dialog-close type="button">Close</button>
    </div>
  `,
  styles: `
    h2 { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    .state { font-size: .8rem; font-weight: 600; padding: 2px 10px; border-radius: 999px; background: #eef2f1; }
    .state.dirty { background: #fbe4e2; color: #8c1d18; } .state.cleaning { background: #fbefd8; color: #8a5a00; }
    .state.clean, .state.inspection { background: #e0eff5; color: #134e63; } .state.ready { background: #e1f2e8; color: #1d5b36; }
    .mute { color: #5b6b6e; margin: 0 0 8px; }
    h3 { font-size: 1rem; font-weight: 500; margin: 12px 0 4px; }
    .checklist { display: flex; flex-direction: column; }
    .checklist mat-checkbox { min-height: 44px; }
    .req { font-size: .72rem; color: #8a2449; margin-left: 6px; font-weight: 500; }
    .note { background: #eef4f3; padding: 8px 10px; border-radius: 8px; }
    .back { color: #b3261e; }
    .trail { margin: 4px 0; padding-left: 18px; color: #5b6b6e; font-size: .9rem; }
    .full { width: 100%; margin-top: 8px; }
    .repair { display: flex; align-items: center; gap: 6px; color: #5b3a00; }
    .err { color: #b3261e; }
    .actions { flex-wrap: wrap; gap: 6px; }
    .actions button { min-height: 44px; }
    .grow { flex: 1; }
  `,
})
export class TaskSheet {
  private data = inject<TaskSheetData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<TaskSheet, boolean>);
  private api = inject(HousekeepingApi);
  private ctx = inject(PropertyContext);
  private router = inject(Router);
  room = this.data.room;
  task = signal<HousekeepingTask | null>(this.room.task);
  ticked = signal(new Set((this.room.task?.checklist ?? []).filter(l => l.done || this.room.task?.status === 'IN_PROGRESS').map(l => l.id)));
  staff = signal<StaffOption[]>([]);
  busy = signal(false);
  error = signal('');
  checkedError = signal('');
  assignError = signal('');
  serverErrors = signal<Record<string, string>>({});
  finishNote = '';
  private changed = false;
  hk = housekeepingLabel;
  cls = statusClass;
  typeLabel = taskTypeLabel;
  statusLabel = taskStatusLabel;
  inspectForm = inject(FormBuilder).nonNullable.group({ note: ['', [Validators.maxLength(500)]] });
  canFinish = computed(() => this.can('finish'));

  constructor() {
    if (this.data.canAssign && this.task()?.nextActions.includes('assign'))
      this.api.staff(this.data.propertyId).subscribe({ next: s => this.staff.set(s) });
  }

  can(a: string) { return !!this.task()?.nextActions.includes(a as never); }
  time(iso: string) { return inZone(iso, this.ctx.current()?.timezone ?? 'Asia/Kolkata', { hour: '2-digit', minute: '2-digit', hour12: false }); }
  tick(id: string, on: boolean) {
    const s = new Set(this.ticked());
    if (on) s.add(id); else s.delete(id);
    this.ticked.set(s);
  }

  start() { this.run(this.api.start(this.data.propertyId, this.task()!), t => this.ticked.set(new Set(t.checklist.map(l => l.id)))); }

  /** Done from the sheet sends exactly what is ticked (the card's Done ticks everything). */
  finish() {
    this.checkedError.set('');
    this.run(this.api.finish(this.data.propertyId, this.task()!, [...this.ticked()], this.finishNote.trim() || null), () => this.ref.close(true),
      p => { const f = fieldErrors(p); if (f['checked']) { this.checkedError.set(f['checked']); return true; } return false; });
  }

  inspect(result: 'PASS' | 'START') {
    this.run(this.api.inspect(this.data.propertyId, this.task()!, result, null), () => { if (result === 'PASS') this.ref.close(true); });
  }

  sendBack() {
    const note = this.inspectForm.getRawValue().note.trim();
    if (note.length < 3) {
      this.inspectForm.controls.note.setErrors({ server: 'Say what needs redoing (at least 3 characters).' });
      this.serverErrors.set({ note: 'Say what needs redoing (at least 3 characters), e.g. Bathroom mirror smeared.' });
      this.inspectForm.controls.note.markAsTouched();
      return;
    }
    this.run(this.api.inspect(this.data.propertyId, this.task()!, 'FAIL', note), () => this.ref.close(true),
      p => { this.serverErrors.set(showFieldErrors(this.inspectForm, p)); return Object.keys(fieldErrors(p)).length > 0; });
  }

  assign(userId: string | null) {
    this.assignError.set('');
    this.run(this.api.assign(this.data.propertyId, this.task()!, userId), () => undefined,
      p => { const f = fieldErrors(p); if (f['userId']) { this.assignError.set(f['userId']); return true; } return false; });
  }

  report() {
    this.ref.close(this.changed);
    this.router.navigate(['/maintenance/new'], { queryParams: { unitId: this.room.unitId, bedId: this.room.bedId ?? undefined } });
  }

  /** Runs a step; on success keeps the new task; a field problem is shown under its field (onProblem returns true). */
  private run(call: ReturnType<HousekeepingApi['start']>, then: (t: HousekeepingTask) => void, onProblem?: (p: ReturnType<typeof toProblem>) => boolean) {
    this.busy.set(true);
    this.error.set('');
    call.subscribe({
      next: t => { this.busy.set(false); this.task.set(t); this.changed = true; then(t); },
      error: e => {
        this.busy.set(false);
        const p = toProblem(e);
        if (!onProblem?.(p)) this.error.set(p.detail);
      },
    });
  }

  close() { this.ref.close(this.changed); }
}

// ------------------------------------------------------------------ a task by hand (supervisor)

export interface NewTaskData { propertyId: string; rooms: HousekeepingRoom[]; }

/** A cleaning task made by hand: a stay-over clean, a deep clean. The room becomes dirty until it is done. */
@Component({
  selector: 'app-new-task-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>New cleaning task</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="new-task">
      <mat-form-field appearance="outline">
        <mat-label>Room</mat-label>
        <mat-select formControlName="place">
          @for (r of choices; track r.place) { <mat-option [value]="r.place">{{ r.place }} ({{ hk(r.status) }})</mat-option> }
        </mat-select>
        <mat-error>{{ err('unitId') || err('bedId') || 'Choose the room to clean.' }}</mat-error>
      </mat-form-field>
      <mat-form-field appearance="outline">
        <mat-label>Kind of cleaning</mat-label>
        <mat-select formControlName="taskType">
          @for (t of types; track t) { <mat-option [value]="t">{{ typeLabel(t) }}</mat-option> }
        </mat-select>
        <mat-error>{{ err('taskType') }}</mat-error>
      </mat-form-field>
      <mat-form-field appearance="outline">
        <mat-label>Give it to</mat-label>
        <mat-select formControlName="assignTo">
          <mat-option value="">Anyone (first to start)</mat-option>
          @for (s of staff(); track s.userId) { <mat-option [value]="s.userId">{{ s.name }} ({{ s.openWork }} open)</mat-option> }
        </mat-select>
        <mat-error>{{ err('assignTo') }}</mat-error>
      </mat-form-field>
      <mat-form-field appearance="outline">
        <mat-label>Note (optional)</mat-label>
        <input matInput formControlName="note" maxlength="500" placeholder="e.g. Guest asked for fresh towels" />
        <mat-error>{{ err('note') }}</mat-error>
      </mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="new-task" [disabled]="busy()">Add task</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 4px; min-width: min(400px, 84vw); } .err { color: #b3261e; }`,
})
export class NewTaskDialog {
  private data = inject<NewTaskData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<NewTaskDialog, HousekeepingTask>);
  private api = inject(HousekeepingApi);
  hk = housekeepingLabel;
  typeLabel = taskTypeLabel;
  types: TaskType[] = TASK_TYPES.filter(t => t !== 'AFTER_REPAIR');
  /** Rooms without an open task that aren't out of order. */
  choices = this.data.rooms.filter(r => !r.task && r.status !== 'OUT_OF_ORDER');
  staff = signal<StaffOption[]>([]);
  busy = signal(false);
  error = signal('');
  private serverErrors = signal<Record<string, string>>({});
  form = inject(FormBuilder).nonNullable.group({
    place: ['', Validators.required], taskType: ['STAYOVER' as TaskType], assignTo: [''], note: [''],
  });

  constructor() { this.api.staff(this.data.propertyId).subscribe({ next: s => this.staff.set(s) }); }

  err(field: string) { return this.serverErrors()[field] ?? ''; }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    const room = this.choices.find(r => r.place === v.place)!;
    this.busy.set(true);
    this.error.set('');
    this.api.create(this.data.propertyId, { unitId: room.unitId, bedId: room.bedId, taskType: v.taskType, note: v.note.trim() || null, assignTo: v.assignTo || null })
      .subscribe({
        next: t => this.ref.close(t),
        error: e => {
          this.busy.set(false);
          const p = toProblem(e);
          const errs = fieldErrors(p);
          this.serverErrors.set(errs);
          if (errs['unitId'] || errs['bedId']) this.form.controls.place.setErrors({ server: true });
          const shown = showFieldErrors(this.form, p);
          if (!Object.keys(errs).length || (!Object.keys(shown).length && !errs['unitId'] && !errs['bedId'])) this.error.set(p.detail);
        },
      });
  }
}
