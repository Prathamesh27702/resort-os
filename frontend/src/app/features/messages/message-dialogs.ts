import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { AbstractControl, FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { RouterLink } from '@angular/router';
import { toProblem } from '../../core/http';
import { Message, MessageTemplate, ScheduledJob } from '../../core/models';
import { inZone } from '../../core/property-context';
import { showError } from '../leave/leave.api';
import { CHANNEL, CHANNEL_ICON, MessagesApi, hhmm, placeholders, preview, statusClass, unknownPlaceholders } from './messages.api';

const server = (c: AbstractControl | null) => (c?.errors?.['server'] as string | undefined) ?? '';
const text = (v: string | null | undefined) => (v ?? '').trim() || null;

// ------------------------------------------------------------------ one message: what was sent, to whom, and what became of it

export interface MessageData { propertyId: string; timezone: string; message: Message; canSend: boolean; }

/**
 * One message: the words as the guest got them, who it went to (masked), each step WhatsApp or the email provider reported,
 * and, for one that failed, why and Send again. With the built-in test WhatsApp the receptionist can play WhatsApp's part
 * (delivered, read, failed) to see the page follow.
 */
@Component({
  selector: 'app-message-dialog',
  imports: [RouterLink, MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    @let m = msg();
    @let s = m.summary;
    <h2 mat-dialog-title><mat-icon inline>{{ icon[s.channel] }}</mat-icon> {{ s.templateName }} · {{ channel[s.channel] }}</h2>
    <div mat-dialog-content class="body" data-testid="message-detail">
      <p class="who"><b>{{ s.recipientName }}</b> <span class="mute num">{{ s.to }}</span>
        <span [class]="'chip ' + cls(s.status)" data-testid="detail-status">{{ s.statusWords }}</span></p>
      @if (s.reservationReference) { <p class="mute small">Booking <a [routerLink]="['/reservations', s.reservationId]" mat-dialog-close>{{ s.reservationReference }}</a>
        @if (m.jobName) { · sent by the {{ m.jobName }} job } @else if (s.source === 'EVENT') { · sent when it happened } </p> }
      @if (s.reason) { <p class="reason" [class.bad]="s.status === 'FAILED'" role="status" data-testid="detail-reason">{{ s.reason }}</p> }
      @if (m.subject) { <p class="subject"><small class="mute">Subject</small><br />{{ m.subject }}</p> }
      <pre class="text" data-testid="detail-body">{{ m.body }}</pre>
      @if (m.providerTemplate) {
        <p class="mute small">WhatsApp template <span class="num">{{ m.providerTemplate }}</span> ({{ m.language }})@if (m.pricingCategory) { · {{ m.pricingCategory }} conversation }</p>
      }
      <h3>What happened</h3>
      <ol class="steps" data-testid="steps">
        @for (st of m.steps; track $index) {
          <li><span [class]="'dot ' + cls(st.status)"></span><span><b>{{ st.words }}</b><br /><small class="mute">{{ time(st.at) }}</small></span></li>
        }
      </ol>
      @if (s.status === 'QUEUED' && s.attempts > 0) { <p class="mute small">Tried {{ s.attempts }} {{ s.attempts === 1 ? 'time' : 'times' }}; next try {{ time(m.nextAttemptAt) }}.</p> }
      @if (m.canFakeStatus && data.canSend) {
        <div class="fake" data-testid="fake-status">
          <small>Test WhatsApp (no keys on this server): play WhatsApp's part.</small>
          <span>
            <button mat-stroked-button (click)="fake('DELIVERED')" [disabled]="busy()">Delivered</button>
            <button mat-stroked-button (click)="fake('READ')" [disabled]="busy()">Read</button>
            <button mat-stroked-button (click)="fake('FAILED')" [disabled]="busy()">Failed</button>
          </span>
        </div>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </div>
    <div mat-dialog-actions align="end">
      <button mat-button type="button" (click)="close()">Close</button>
      @if (s.canRetry && data.canSend) {
        <button mat-flat-button type="button" (click)="retry()" [disabled]="busy()" data-testid="retry">{{ busy() ? 'Sending…' : 'Send again' }}</button>
      }
    </div>
  `,
  styles: `
    .body { width: min(560px, 86vw); }
    .who { display: flex; gap: var(--space-2); align-items: center; flex-wrap: wrap; margin: 0 0 var(--space-1); }
    .mute { color: var(--ro-mute); } .small { font-size: .85rem; } .err, .bad { color: var(--ro-error); }
    .reason { background: var(--ro-wash); border-radius: 8px; padding: var(--space-2) 10px; margin: var(--space-2) 0; } .reason.bad { background: #fbeceb; }
    .subject { margin: var(--space-2) 0 var(--space-1); }
    .text { white-space: pre-wrap; font: inherit; background: #e7f6ee; border-radius: 10px; padding: 10px var(--space-3); margin: 6px 0; }
    h3 { font-size: .95rem; margin: 14px 0 var(--space-1); }
    .steps { list-style: none; margin: 0; padding: 0; }
    .steps li { display: flex; gap: 10px; align-items: flex-start; padding: var(--space-1) 0; }
    .dot { width: 10px; height: 10px; border-radius: 50%; margin-top: 6px; background: #b7791f; flex: none; }
    .dot.ok { background: #1d7a45; } .dot.bad { background: var(--ro-error); } .dot.off { background: #8b9a9c; }
    .fake { display: flex; flex-direction: column; gap: 6px; border: 1px dashed #b7791f; border-radius: 10px; padding: var(--space-2) 10px; margin-top: var(--space-3); }
    .fake span { display: flex; gap: 6px; flex-wrap: wrap; }
    .chip.ok { background: var(--ro-ok-bg); color: var(--ro-ok); } .chip.wait { background: var(--ro-warn-bg); color: var(--ro-warn); }
    .chip.off { background: #eceff0; color: var(--ro-mute); } .chip.bad { background: var(--ro-off-bg); color: var(--ro-off); }
  `,
})
export class MessageDialog {
  data = inject<MessageData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<MessageDialog, boolean>);
  private api = inject(MessagesApi);
  msg = signal(this.data.message);
  busy = signal(false);
  error = signal('');
  private changed = false;
  channel = CHANNEL; icon = CHANNEL_ICON; cls = statusClass;

  time(iso: string) { return inZone(iso, this.data.timezone, { day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit', second: '2-digit' }); }

  retry() { this.act(this.api.retry(this.data.propertyId, this.msg().summary.id)); }
  fake(status: 'DELIVERED' | 'READ' | 'FAILED') { this.act(this.api.fakeStatus(this.data.propertyId, this.msg().summary.id, status)); }
  close() { this.ref.close(this.changed); }

  private act(call: ReturnType<MessagesApi['get']>) {
    this.busy.set(true); this.error.set('');
    call.subscribe({
      next: m => { this.msg.set(m); this.changed = true; this.busy.set(false); },
      error: e => { this.error.set(toProblem(e).detail); this.busy.set(false); },
    });
  }
}

// ------------------------------------------------------------------ the words of a template

/** The field messages before the server has answered (kept here: a literal {{…}} can't go in an Angular template). */
const SAY = {
  providerTemplate: 'Give the name of the template approved in WhatsApp Manager, lower-case letters, digits and _ only, e.g. booking_confirmation.',
  language: "The language is WhatsApp's code for it, e.g. en, en_US, hi or mr.",
  subject: 'Give it a subject, 2–150 characters, e.g. Your booking {{reference}} is confirmed.',
};

export interface TemplateData { propertyId: string; template: MessageTemplate; }

/**
 * Changes what a message says. The variables it can use are listed with a sample, and the preview fills them in as a guest
 * would read it. For WhatsApp the words must match the template approved in WhatsApp Manager (WhatsApp sends its approved
 * text with these values; ours is the record of what was sent). An unknown {{variable}} is shown under the text before saving.
 */
@Component({
  selector: 'app-template-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSlideToggleModule],
  template: `
    <h2 mat-dialog-title>{{ t.name }} · {{ channel[t.channel] }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="template-form">
      <p class="mute small">{{ t.when }}</p>
      <mat-slide-toggle formControlName="isActive">Send this message</mat-slide-toggle>
      @if (!form.value.isActive) { <p class="hint">Turned off: nothing is sent, and each message that would have gone says so on the Messages page.</p> }
      @if (t.channel === 'WHATSAPP') {
        <div class="two">
          <mat-form-field appearance="outline"><mat-label>Template name in WhatsApp Manager</mat-label>
            <input matInput formControlName="providerTemplate" maxlength="512" autocomplete="off" class="num" />
            <mat-error>{{ server(form.get('providerTemplate')) || say.providerTemplate }}</mat-error></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Language</mat-label>
            <input matInput formControlName="language" maxlength="10" autocomplete="off" />
            <mat-error>{{ server(form.get('language')) || say.language }}</mat-error></mat-form-field>
        </div>
      }
      @if (t.channel !== 'WHATSAPP') {
        <mat-form-field appearance="outline"><mat-label>{{ t.channel === 'EMAIL' ? 'Subject' : 'Title' }}</mat-label>
          <input matInput formControlName="subject" maxlength="150" autocomplete="off" />
          <mat-error>{{ server(form.get('subject')) || say.subject }}</mat-error></mat-form-field>
      }
      <mat-form-field appearance="outline" subscriptSizing="dynamic"><mat-label>Text</mat-label>
        <textarea matInput formControlName="body" rows="6" [maxlength]="t.channel === 'WHATSAPP' ? 1024 : 4000" data-testid="template-body"></textarea>
        <mat-hint align="end">{{ length() }} / {{ t.channel === 'WHATSAPP' ? '1,024' : '4,000' }}</mat-hint>
        <mat-error>{{ server(form.get('body')) || bodyError() }}</mat-error></mat-form-field>
      @if (unknown().length && !server(form.get('body'))) {
        <p class="err small" role="alert" data-testid="unknown-vars">{{ unknownText() }}</p>
      }
      <h3>It can fill in</h3>
      <div class="vars">
        @for (v of t.variables; track v.name) {
          <button type="button" class="var" (click)="insert(v.name)" [class.used]="used().includes(v.name)" [title]="v.description + ' (e.g. ' + v.sample + ')'">
            {{ brace(v.name) }}</button>
        }
      </div>
      <h3>Preview</h3>
      @if (form.value.subject && t.channel !== 'WHATSAPP') { <p class="psub">{{ previewText(form.value.subject) }}</p> }
      <pre class="text" data-testid="template-preview">{{ previewText(form.value.body ?? '') }}</pre>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="template-form" [disabled]="busy()">{{ busy() ? 'Saving…' : 'Save' }}</button>
    </div>
  `,
  styles: `
    form { display: flex; flex-direction: column; gap: 2px; width: min(600px, 86vw); }
    .two { display: grid; grid-template-columns: 2fr 1fr; gap: var(--space-2); margin-top: var(--space-2); }
    .mute { color: var(--ro-mute); } .small { font-size: .85rem; } .err { color: var(--ro-error); } .hint { color: var(--ro-warn); font-size: .85rem; margin: var(--space-1) 0; }
    mat-slide-toggle { margin: var(--space-1) 0 10px; }
    h3 { font-size: .95rem; margin: var(--space-2) 0 var(--space-1); }
    .vars { display: flex; flex-wrap: wrap; gap: var(--space-1); }
    .var { border: 1px solid var(--ro-line); background: var(--ro-paper); border-radius: 999px; padding: 2px 10px; font: inherit; font-size: .8rem; cursor: pointer; color: var(--ro-tide); }
    .var.used { background: var(--ro-ok-bg); border-color: #b9dcc8; }
    .psub { font-weight: 600; margin: 0 0 var(--space-1); }
    .text { white-space: pre-wrap; font: inherit; background: #e7f6ee; border-radius: 10px; padding: 10px var(--space-3); margin: 0; }
    @media (max-width: 560px) { .two { grid-template-columns: 1fr; } }
  `,
})
export class TemplateDialog {
  data = inject<TemplateData>(MAT_DIALOG_DATA);
  t = this.data.template;
  private ref = inject(MatDialogRef<TemplateDialog, MessageTemplate>);
  private api = inject(MessagesApi);
  private fb = inject(FormBuilder).nonNullable;
  form = this.fb.group({
    isActive: [this.t.isActive],
    providerTemplate: [this.t.providerTemplate ?? '', this.t.channel === 'WHATSAPP' ? [Validators.required, Validators.pattern(/^[a-z0-9_]+$/)] : []],
    language: [this.t.language, [Validators.required, Validators.pattern(/^[a-z]{2,3}(_[A-Z]{2})?$/)]],
    subject: [this.t.subject ?? '', this.t.channel === 'WHATSAPP' ? [] : [Validators.required, Validators.minLength(2), Validators.maxLength(150)]],
    body: [this.t.body, [Validators.required, Validators.minLength(3)]],
  });
  busy = signal(false);
  error = signal('');
  server = server; channel = CHANNEL; say = SAY;
  brace = (name: string) => `{{${name}}}`;
  private body = toSignal(this.form.controls.body.valueChanges, { initialValue: this.t.body });
  length = computed(() => (this.body() ?? '').length);
  used = computed(() => placeholders(this.body() ?? ''));
  unknown = computed(() => unknownPlaceholders(this.body() ?? '', this.t.variables));
  unknownText = computed(() => {
    const u = this.unknown().map(x => `{{${x}}}`).join(', ');
    return `${u} ${this.unknown().length === 1 ? "isn't something" : "aren't things"} this message can fill in. It can use the ones listed below.`;
  });

  bodyError() {
    const c = this.form.controls.body;
    return c.hasError('required') || c.hasError('minlength') ? 'Write what the message says, at least 3 characters.' : '';
  }

  previewText(s: string) { return preview(s, this.t.variables); }

  insert(name: string) {
    const c = this.form.controls.body;
    c.setValue(`${c.value}${c.value.endsWith(' ') || !c.value ? '' : ' '}{{${name}}}`);
    c.markAsDirty();
  }

  save() {
    this.error.set('');
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.api.saveTemplate(this.data.propertyId, this.t.id, {
      providerTemplate: this.t.channel === 'WHATSAPP' ? text(v.providerTemplate) : null, language: text(v.language),
      subject: this.t.channel === 'WHATSAPP' ? null : text(v.subject), body: v.body, isActive: v.isActive, version: this.t.version,
    }).subscribe({
      next: t => this.ref.close(t),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ a scheduled job: on or off, and when

export interface JobData { propertyId: string; job: ScheduledJob; }

/** Turns a scheduled job on or off and sets the time of day it runs (the property's time). */
@Component({
  selector: 'app-job-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSlideToggleModule],
  template: `
    <h2 mat-dialog-title>{{ data.job.name }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="job-form">
      <p class="mute">{{ data.job.description }}</p>
      <mat-slide-toggle formControlName="enabled">Run every day</mat-slide-toggle>
      <mat-form-field appearance="outline"><mat-label>At (property time)</mat-label>
        <input matInput type="time" formControlName="runAt" step="60" />
        <mat-error>{{ server(form.get('runAt')) || 'Give the time in hours and minutes, e.g. 10:00.' }}</mat-error></mat-form-field>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="job-form" [disabled]="busy()">{{ busy() ? 'Saving…' : 'Save' }}</button>
    </div>
  `,
  styles: `
    form { display: flex; flex-direction: column; gap: var(--space-2); width: min(420px, 86vw); }
    .mute { color: var(--ro-mute); margin: 0; } .err { color: var(--ro-error); }
  `,
})
export class JobDialog {
  data = inject<JobData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<JobDialog, ScheduledJob>);
  private api = inject(MessagesApi);
  form = inject(FormBuilder).nonNullable.group({
    enabled: [this.data.job.enabled],
    runAt: [hhmm(this.data.job.runAt), [Validators.required, Validators.pattern(/^\d{2}:\d{2}$/)]],
  });
  busy = signal(false);
  error = signal('');
  server = server;

  save() {
    this.error.set('');
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.api.saveJob(this.data.propertyId, this.data.job, v.enabled, v.runAt).subscribe({
      next: j => this.ref.close(j),
      error: e => { this.busy.set(false); showError(this.form, e, m => this.error.set(m)); },
    });
  }
}
