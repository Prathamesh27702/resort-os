import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { toProblem } from '../../core/http';
import { JobRunResult, Jobs, MessageListItem, MessageTemplate, MessagingSetup, Paged, ScheduledJob } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { JobDialog, MessageDialog, TemplateDialog } from './message-dialogs';
import { CHANNEL, CHANNEL_ICON, MessagesApi, STATUS_FILTERS, hhmm, statusClass } from './messages.api';

type Tab = 'messages' | 'templates' | 'jobs';
const PAGE = 25;

/**
 * Messages (Sprint 24, brief §50): everything the resort sent or meant to send. Booking confirmations go the moment a booking
 * is confirmed; the six daily jobs send reminders and alerts. Each line says what became of it in words (sent, delivered,
 * read, or why it failed or wasn't sent), and a failed one can be sent again. The owner changes the templates' words and
 * the jobs' times here; a receptionist can run a job now.
 */
@Component({
  selector: 'app-messages',
  imports: [RouterLink, MatButtonModule, MatButtonToggleModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule,
            MatSnackBarModule],
  template: `
    <div class="page msg">
      <div class="head">
        <h1>Messages</h1>
        <span class="grow"></span>
        <mat-button-toggle-group [value]="tab()" (change)="go($event.value)" aria-label="Messages, templates or jobs" hideSingleSelectionIndicator>
          <mat-button-toggle value="messages">Messages</mat-button-toggle>
          <mat-button-toggle value="templates">Templates</mat-button-toggle>
          <mat-button-toggle value="jobs">Daily jobs</mat-button-toggle>
        </mat-button-toggle-group>
      </div>

      @if (setup(); as su) {
        @if (su.whatsAppMode !== 'LIVE' || su.emailMode !== 'LIVE') {
          <p class="mode" [class.off]="su.whatsAppMode === 'OFF' || su.emailMode === 'OFF'" role="status" data-testid="mode-note">
            <mat-icon inline>{{ su.whatsAppMode === 'OFF' || su.emailMode === 'OFF' ? 'error' : 'science' }}</mat-icon>
            {{ modeText(su) }}</p>
        }
      }

      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

      @switch (tab()) {
        @case ('messages') {
          <div class="filters">
            <div class="chips" role="group" aria-label="Show messages by what became of them">
              @for (f of statusFilters; track f.value) {
                <button type="button" class="fchip" [class.on]="status() === f.value" (click)="pick(f.value)" [attr.aria-pressed]="status() === f.value">
                  {{ f.label }}@if (count(f.value); as c) { <b class="num">{{ c }}</b> }</button>
              }
            </div>
            <mat-form-field appearance="outline" subscriptSizing="dynamic" class="sel">
              <mat-label>Channel</mat-label>
              <mat-select [value]="channelFilter()" (selectionChange)="channelFilter.set($event.value); page.set(1)" [aria-label]="'Channel'">
                <mat-option value="">All</mat-option>
                <mat-option value="WHATSAPP">WhatsApp</mat-option>
                <mat-option value="EMAIL">Email</mat-option>
                <mat-option value="IN_APP">In the app</mat-option>
              </mat-select>
            </mat-form-field>
            <mat-form-field appearance="outline" subscriptSizing="dynamic" class="q">
              <mat-label>Guest or booking</mat-label>
              <input matInput [value]="q()" (keyup.enter)="search($any($event.target).value)" (blur)="search($any($event.target).value)" autocomplete="off" />
              <mat-icon matSuffix>search</mat-icon>
            </mat-form-field>
            @if (setup()?.canSend) {
              <button mat-stroked-button (click)="dispatch()" [disabled]="busy()" title="Send what is waiting now instead of within a few seconds">
                <mat-icon>send</mat-icon>Send waiting now</button>
            }
          </div>

          @if (list(); as l) {
            <div class="rows" data-testid="messages">
              @for (m of l.items; track m.id) {
                <button type="button" class="row" (click)="open(m)" [attr.data-status]="m.status" [attr.data-template]="m.templateCode">
                  <mat-icon class="ch" [attr.aria-label]="channel[m.channel]">{{ icon[m.channel] }}</mat-icon>
                  <span class="main">
                    <span class="top"><b>{{ m.templateName }}</b> <span class="mute">to {{ m.recipientName }}</span>
                      @if (m.reservationReference) { <span class="ref num">{{ m.reservationReference }}</span> }</span>
                    @if (m.reason) { <small class="why" [class.bad]="m.status === 'FAILED'">{{ m.reason }}</small> }
                    @else { <small class="mute num">{{ m.to }}</small> }
                  </span>
                  <span class="side">
                    <span [class]="'chip ' + cls(m.status)">{{ m.statusWords }}</span>
                    <small class="mute">{{ time(m.readAt ?? m.deliveredAt ?? m.sentAt ?? m.failedAt ?? m.createdAt) }}</small>
                  </span>
                </button>
              } @empty {
                <p class="mute empty">{{ status() || channelFilter() || q() ? 'No messages match.' : 'Nothing sent yet. A booking confirmation goes as soon as a booking is confirmed.' }}</p>
              }
            </div>
            @if (l.total > l.pageSize) {
              <div class="pager">
                <button mat-button (click)="page.set(page() - 1)" [disabled]="page() === 1">Newer</button>
                <span class="mute">{{ (page() - 1) * l.pageSize + 1 }}–{{ Math.min(page() * l.pageSize, l.total) }} of {{ l.total }}</span>
                <button mat-button (click)="page.set(page() + 1)" [disabled]="page() * l.pageSize >= l.total">Older</button>
              </div>
            }
          }
        }

        @case ('templates') {
          <p class="mute">What each message says. WhatsApp sends the text of the template approved in WhatsApp Manager with these values filled in,
            so keep the words here the same as the approved ones.</p>
          <div class="tgrid" data-testid="templates">
            @for (g of templateGroups(); track g.code) {
              <section class="panel tcard" [attr.data-code]="g.code">
                <h2>{{ g.name }}</h2>
                <p class="mute small">{{ g.when }}</p>
                @for (t of g.items; track t.id) {
                  <div class="tline" [attr.data-channel]="t.channel">
                    <mat-icon inline>{{ icon[t.channel] }}</mat-icon>
                    <span class="tmain"><b>{{ channel[t.channel] }}</b>
                      @if (!t.isActive) { <span class="chip off">Off</span> }
                      <small class="mute clip">{{ t.subject ? t.subject + ' — ' : '' }}{{ t.body }}</small></span>
                    @if (setup()?.canManage) { <button mat-button (click)="editTemplate(t)">Change</button> }
                  </div>
                }
              </section>
            }
          </div>
        }

        @case ('jobs') {
          <p class="mute">Each job runs once a day at its time (property time) and never sends the same thing twice, even when it is run again by hand.</p>
          @if (jobs(); as js) {
            <div class="jobs" data-testid="jobs">
              @for (j of js.jobs; track j.job) {
                <section class="panel job" [attr.data-job]="j.job">
                  <div class="jhead">
                    <h2>{{ j.name }}</h2>
                    <span [class]="'chip ' + (j.enabled ? 'ok' : 'off')">{{ j.enabled ? 'Daily at ' + hhmm(j.runAt) : 'Off' }}</span>
                  </div>
                  <p class="mute small">{{ j.description }}</p>
                  <p class="small">
                    @if (j.enabled) { Next: {{ time(j.nextRunAt) }}. }
                    @if (j.lastFinishedAt) { Last: {{ time(j.lastFinishedAt) }}@if (j.lastOutcome) { , {{ j.lastOutcome }} } }
                    @else { Not run yet. }</p>
                  @if (j.lastError) { <p class="bad small" role="status">{{ j.lastError }}</p> }
                  @if (result()?.job === j.job) { <p class="result small" role="status" data-testid="run-result">{{ result()!.message }}
                    @for (n of result()!.notes; track $index) { <br /><span class="mute">· {{ n }}</span> }</p> }
                  <div class="jact">
                    @if (setup()?.canSend) { <button mat-stroked-button (click)="run(j)" [disabled]="running() === j.job">{{ running() === j.job ? 'Running…' : 'Run now' }}</button> }
                    @if (setup()?.canManage) { <button mat-button (click)="editJob(j)">Change</button> }
                  </div>
                </section>
              }
            </div>
            <h2 class="sub">Recently done</h2>
            <div class="runs" data-testid="runs">
              @for (r of js.recent; track r.id) {
                <p><span class="mute num">{{ time(r.createdAt) }}</span> <b>{{ r.jobName }}</b>
                  @if (r.reservationReference) { · <a [routerLink]="['/reservations', r.reservationId]" class="num">{{ r.reservationReference }}</a> }
                  · {{ r.outcome }} <small class="mute">({{ r.triggeredBy === 'MANUAL' ? 'Run now' + (r.runByName ? ' by ' + r.runByName : '') : 'on schedule' }})</small></p>
              } @empty { <p class="mute">Nothing done yet.</p> }
            </div>
          }
        }
      }
    </div>
  `,
  styles: `
    .msg { max-width: 1000px; padding-bottom: 60px; }
    .head { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; } .grow { flex: 1; }
    .mute { color: var(--ro-mute); } .small { font-size: .85rem; } .err, .bad { color: var(--ro-error); }
    .mode { background: #fff4d6; color: #6b4e00; border-radius: 10px; padding: var(--space-2) var(--space-3); margin: 10px 0; }
    .mode.off { background: #fbeceb; color: var(--ro-off); }
    .filters { display: flex; gap: var(--space-2); align-items: center; flex-wrap: wrap; margin: var(--space-3) 0; }
    .chips { display: flex; gap: var(--space-1); flex-wrap: wrap; flex-basis: 100%; }
    .fchip { border: 1px solid var(--ro-line); background: var(--ro-paper); border-radius: 999px; padding: var(--space-1) var(--space-3); font: inherit; font-size: .85rem; cursor: pointer; color: var(--ro-ink); }
    .fchip b { margin-left: 6px; color: var(--ro-mute); font-weight: 500; }
    .fchip.on { background: var(--ro-tide); border-color: var(--ro-tide); color: var(--ro-paper); } .fchip.on b { color: #cfe0dd; }
    .sel { width: 150px; } .q { flex: 1; min-width: 200px; }
    .rows { background: var(--ro-paper); border: 1px solid var(--ro-line); border-radius: 14px; }
    .row { display: flex; gap: var(--space-3); align-items: center; width: 100%; padding: 10px 14px; border: 0; border-top: 1px solid var(--ro-sunk); background: none;
           font: inherit; text-align: left; cursor: pointer; color: inherit; }
    .row:first-child { border-top: 0; } .row:hover { background: var(--ro-hover); }
    .ch { color: var(--ro-tide); flex: none; }
    .main { flex: 1; min-width: 0; display: flex; flex-direction: column; }
    .top { display: flex; gap: 6px; flex-wrap: wrap; align-items: baseline; }
    .ref { font-size: .8rem; background: var(--ro-sunk); border-radius: 6px; padding: 0 6px; }
    .why { color: var(--ro-mute); } .why.bad { color: var(--ro-error); }
    .side { display: flex; flex-direction: column; align-items: flex-end; gap: 2px; flex: none; }
    .empty { padding: var(--space-4); margin: 0; }
    .pager { display: flex; justify-content: center; align-items: center; gap: var(--space-3); margin-top: var(--space-2); }
    .tgrid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-3); margin-top: var(--space-2); }
    .tcard, .job { padding: var(--space-3) var(--space-4); min-width: 0; } h2 { font-size: 1rem; color: var(--ro-tide); margin: 0; } .tcard p, .job p { margin: var(--space-1) 0; }
    .tline { display: flex; align-items: center; gap: var(--space-2); border-top: 1px solid var(--ro-sunk); padding: 6px 0; }
    .tmain { flex: 1; min-width: 0; display: flex; flex-direction: column; }
    .clip { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .jobs { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-3); margin-top: var(--space-2); }
    .jhead { display: flex; justify-content: space-between; align-items: center; gap: var(--space-2); }
    .jact { display: flex; gap: 6px; margin-top: 6px; }
    .result { background: var(--ro-ok-bg); border-radius: 8px; padding: 6px 10px; }
    .sub { margin: var(--space-5) 0 6px; }
    .runs { background: var(--ro-paper); border: 1px solid var(--ro-line); border-radius: 14px; padding: 6px 14px; }
    .runs p { margin: 6px 0; display: flex; gap: 6px; flex-wrap: wrap; align-items: baseline; }
    .chip.ok { background: var(--ro-ok-bg); color: var(--ro-ok); } .chip.wait { background: var(--ro-warn-bg); color: var(--ro-warn); }
    .chip.off { background: #eceff0; color: var(--ro-mute); } .chip.bad { background: var(--ro-off-bg); color: var(--ro-off); }
    @media (max-width: 720px) {
      .tgrid, .jobs { grid-template-columns: minmax(0, 1fr); }
      .row { flex-wrap: wrap; } .side { flex-direction: row; align-items: center; width: 100%; box-sizing: border-box; justify-content: space-between; padding-left: 36px; }
      .sel, .q { width: 100%; flex-basis: 100%; }
    }
  `,
})
export class MessagesPage {
  private api = inject(MessagesApi);
  private ctx = inject(PropertyContext);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  statusFilters = STATUS_FILTERS; channel = CHANNEL; icon = CHANNEL_ICON; cls = statusClass; hhmm = hhmm; Math = Math;

  private pid = computed(() => this.ctx.current()?.id ?? '');
  private tz = computed(() => this.ctx.current()?.timezone ?? 'Asia/Kolkata');
  tab = signal<Tab>(this.tabOf(this.route.snapshot.queryParamMap.get('tab')));
  status = signal(this.route.snapshot.queryParamMap.get('status') ?? '');
  channelFilter = signal('');
  q = signal('');
  page = signal(1);
  setup = signal<MessagingSetup | null>(null);
  list = signal<Paged<MessageListItem> | null>(null);
  templates = signal<MessageTemplate[]>([]);
  jobs = signal<Jobs | null>(null);
  result = signal<JobRunResult | null>(null);
  running = signal<string | null>(null);
  busy = signal(false);
  error = signal('');

  templateGroups = computed(() => {
    const out: { code: string; name: string; when: string; items: MessageTemplate[] }[] = [];
    for (const t of this.templates()) {
      let g = out.find(x => x.code === t.code);
      if (!g) out.push(g = { code: t.code, name: t.name, when: t.when, items: [] });
      g.items.push(t);
    }
    return out;
  });

  constructor() {
    effect(() => {
      const pid = this.pid();
      if (pid) untracked(() => this.loadSetup());
    });
    effect(() => {
      const [pid, tab] = [this.pid(), this.tab()];
      const f = { status: this.status(), channel: this.channelFilter(), q: this.q(), page: this.page() };
      if (!pid) return;
      untracked(() => {
        if (tab === 'messages') this.loadList(f);
        else if (tab === 'templates') this.loadTemplates();
        else this.loadJobs();
      });
    });
  }

  private tabOf(v: string | null): Tab { return v === 'templates' || v === 'jobs' ? v : 'messages'; }

  go(t: Tab) {
    this.tab.set(t);
    this.error.set('');
    this.router.navigate([], { queryParams: { tab: t === 'messages' ? null : t }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  pick(s: string) { this.status.set(s); this.page.set(1); }
  search(v: string) { if (v.trim() !== this.q()) { this.q.set(v.trim()); this.page.set(1); } }

  count(s: string): number {
    const c = this.setup()?.counts ?? {};
    if (s === '') return 0;
    if (s === 'PROBLEM') return (c.FAILED ?? 0) + (c.SKIPPED ?? 0);
    return c[s as keyof typeof c] ?? 0;
  }

  modeText(s: MessagingSetup): string {
    const say = (name: string, m: string) => m === 'FAKE'
      ? `${name} ${name.includes(' and ') ? 'use' : 'uses'} the built-in test service (no keys set), so nothing reaches a real phone or inbox.`
      : m === 'OFF' ? `${name} ${name.includes(' and ') ? "aren't" : "isn't"} connected on this server, so those messages fail with the reason until the keys are set (docs/WHATSAPP.md).` : '';
    if (s.whatsAppMode === s.emailMode) return say('WhatsApp and email', s.whatsAppMode);
    return [say('WhatsApp', s.whatsAppMode), say('Email', s.emailMode)].filter(x => x).join(' ');
  }

  time(iso: string) { return inZone(iso, this.tz(), { day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit' }); }

  loadSetup() { this.api.setup(this.pid()).subscribe({ next: s => this.setup.set(s), error: e => this.error.set(toProblem(e).detail) }); }

  private loadList(f: { status: string; channel: string; q: string; page: number }) {
    this.api.list(this.pid(), { ...f, pageSize: PAGE }).subscribe({
      next: l => { this.list.set(l); this.error.set(''); },
      error: e => this.error.set(toProblem(e).detail),
    });
  }
  private loadTemplates() { this.api.templates(this.pid()).subscribe({ next: t => this.templates.set(t), error: e => this.error.set(toProblem(e).detail) }); }
  private loadJobs() { this.api.jobs(this.pid()).subscribe({ next: j => this.jobs.set(j), error: e => this.error.set(toProblem(e).detail) }); }

  private refresh() {
    this.loadSetup();
    if (this.tab() === 'messages') this.loadList({ status: this.status(), channel: this.channelFilter(), q: this.q(), page: this.page() });
    if (this.tab() === 'jobs') this.loadJobs();
  }

  open(m: MessageListItem) {
    this.api.get(this.pid(), m.id).subscribe({
      next: full => this.dialog.open(MessageDialog, {
        data: { propertyId: this.pid(), timezone: this.tz(), message: full, canSend: !!this.setup()?.canSend }, maxWidth: '100vw', autoFocus: false,
      }).afterClosed().subscribe(changed => { if (changed) this.refresh(); }),
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  dispatch() {
    this.busy.set(true);
    this.api.dispatch(this.pid()).subscribe({
      next: r => {
        this.busy.set(false);
        this.snack.open(r.handled ? `${r.handled} ${r.handled === 1 ? 'message' : 'messages'} handled.` : 'Nothing was waiting.', 'OK', { duration: 4000 });
        this.refresh();
      },
      error: e => { this.busy.set(false); this.error.set(toProblem(e).detail); },
    });
  }

  editTemplate(t: MessageTemplate) {
    this.dialog.open(TemplateDialog, { data: { propertyId: this.pid(), template: t }, maxWidth: '100vw' })
      .afterClosed().subscribe((x?: MessageTemplate) => {
        if (!x) return;
        this.snack.open(`${x.name} (${CHANNEL[x.channel]}) saved${x.isActive ? '' : ', and turned off'}.`, 'OK', { duration: 4000 });
        this.loadTemplates();
      });
  }

  editJob(j: ScheduledJob) {
    this.dialog.open(JobDialog, { data: { propertyId: this.pid(), job: j }, maxWidth: '100vw' })
      .afterClosed().subscribe((x?: ScheduledJob) => {
        if (!x) return;
        this.snack.open(x.enabled ? `${x.name}: daily at ${hhmm(x.runAt)}, next ${this.time(x.nextRunAt)}.` : `${x.name} is off.`, 'OK', { duration: 5000 });
        this.loadJobs();
      });
  }

  run(j: ScheduledJob) {
    this.running.set(j.job);
    this.result.set(null);
    this.api.runJob(this.pid(), j.job).subscribe({
      next: r => { this.running.set(null); this.result.set(r); this.loadJobs(); this.loadSetup(); },
      error: e => { this.running.set(null); this.error.set(toProblem(e).detail); },
    });
  }
}
