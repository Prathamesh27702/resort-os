import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { toProblem } from '../../core/http';
import { PropertyContext, inZone } from '../../core/property-context';
import { countryName } from '../guests/guests.api';
import { save } from '../invoices/invoices.api';
import { addDays, shortDate } from '../rooms/rooms.api';
import { FormCList, IndiaApi, deadlineWords, urgency } from './india.api';

type Tab = 'PENDING' | 'SUBMITTED' | 'ALL';

/**
 * Form C (Sprint 30): every foreign guest has to be reported to the FRRO within 24 hours of arriving. A sheet is made at
 * check-in; this list shows the ones still to file with the time left, the ones filed with their FRRO number, and gives
 * the foreign guest register as a spreadsheet for an inspection.
 */
@Component({
  selector: 'app-form-c',
  imports: [FormsModule, RouterLink, MatButtonModule, MatDatepickerModule, MatFormFieldModule, MatIconModule, MatInputModule],
  template: `
    <div class="page wide">
      <div class="page-head">
        <div>
          <a class="back" routerLink="/front-desk">← Front desk</a>
          <h1>Form C</h1>
          <p>Foreign guests are reported to the FRRO within {{ list()?.hoursToFile ?? 24 }} hours of arriving. A sheet is made here when they check in.</p>
        </div>
        @if (list(); as l) {
          <a mat-stroked-button [href]="l.portalUrl" target="_blank" rel="noopener"><mat-icon>open_in_new</mat-icon>Open the FRRO portal</a>
        }
      </div>

      @if (list(); as l) {
        @if (l.overdue) {
          <p class="banner late" role="alert" data-testid="form-c-overdue">
            <mat-icon>warning</mat-icon>{{ l.overdue }} Form C {{ l.overdue === 1 ? 'is' : 'are' }} past the 24 hours. File {{ l.overdue === 1 ? 'it' : 'them' }} on the FRRO portal now.
          </p>
        } @else if (l.pending) {
          <p class="banner soon" data-testid="form-c-pending"><mat-icon>schedule</mat-icon>{{ l.pending }} Form C still to file.</p>
        }
      }

      <nav class="tabs" aria-label="Form C lists">
        @for (t of tabs; track t.id) {
          <button type="button" [class.on]="tab() === t.id" (click)="tab.set(t.id)">{{ t.label }}@if (t.id === 'PENDING' && list()?.pending) { <span class="count">{{ list()!.pending }}</span> }</button>
        }
      </nav>

      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }

      <div class="panel">
        <table class="data">
          <thead><tr><th>Guest</th><th>Stay</th><th>Arrived</th><th>Status</th><th class="right">&nbsp;</th></tr></thead>
          <tbody>
            @for (f of list()?.items ?? []; track f.id) {
              <tr [attr.data-guest]="f.guestName">
                <td><a [routerLink]="['/form-c', f.id]"><b>{{ f.guestName }}</b></a><br /><small class="mute">{{ country(f.nationality) }}</small></td>
                <td class="num"><a [routerLink]="['/reservations', f.reservationId]">{{ f.reference }}</a>@if (f.room) { , {{ f.room }} }<br />
                  <small class="mute">until {{ shortDate(f.checkOut) }}</small></td>
                <td class="num">{{ when(f.arrivedAt) }}</td>
                <td>
                  @switch (f.status) {
                    @case ('PENDING') {
                      <span [class]="'chip due ' + urgency(f)">{{ deadline(f.hoursLeft) }}</span>
                      @if (f.missing) { <br /><small class="mute">{{ f.missing }} field{{ f.missing === 1 ? '' : 's' }} to fill</small> }
                      @else { <br /><small class="ok">Ready to file</small> }
                    }
                    @case ('SUBMITTED') {
                      <span class="chip done">Filed</span>@if (f.frroReference) { <span class="num"> {{ f.frroReference }}</span> }
                      <br /><small class="mute">{{ when(f.submittedAt!) }}@if (f.submittedByName) { by {{ f.submittedByName }} }</small>
                    }
                    @default { <span class="chip off">Not needed</span><br /><small class="mute">{{ f.notRequiredReason }}</small> }
                  }
                </td>
                <td class="right">
                  <a mat-button [routerLink]="['/form-c', f.id]" [attr.aria-label]="'Open the Form C of ' + f.guestName">{{ f.status === 'PENDING' ? 'Fill and file' : 'Open' }}</a>
                </td>
              </tr>
            } @empty {
              <tr><td colspan="5" class="empty">
                @if (loading()) { Loading… }
                @else if (tab() === 'PENDING') { Nothing to file. A sheet appears here when a guest whose nationality isn't Indian checks in. }
                @else if (tab() === 'SUBMITTED') { No Form C has been marked as filed yet. }
                @else { No foreign guest has checked in yet. }
              </td></tr>
            }
          </tbody>
        </table>
      </div>

      <section class="panel register">
        <h2>Foreign guest register</h2>
        <p class="mute">A spreadsheet of every foreign guest who arrived between two dates, with passport and visa details and the FRRO number.
          Keep it for inspections. Downloading it is recorded in the audit trail.</p>
        <div class="range">
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Arrived from</mat-label>
            <input matInput [matDatepicker]="dpFrom" [(ngModel)]="from" name="from" />
            <mat-datepicker-toggle matIconSuffix [for]="dpFrom" /><mat-datepicker #dpFrom />
          </mat-form-field>
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Arrived until</mat-label>
            <input matInput [matDatepicker]="dpTo" [(ngModel)]="to" name="to" />
            <mat-datepicker-toggle matIconSuffix [for]="dpTo" /><mat-datepicker #dpTo />
          </mat-form-field>
          <button mat-stroked-button type="button" (click)="download()" [disabled]="downloading()" data-testid="form-c-register">
            <mat-icon>download</mat-icon>Download register
          </button>
        </div>
        @if (registerError()) { <p class="err" role="alert">{{ registerError() }}</p> }
      </section>
    </div>
  `,
  styles: `
    .wide { max-width: 1100px; }
    .back { color: var(--ro-mute); text-decoration: none; font-size: .85rem; }
    .banner { display: flex; align-items: center; gap: var(--space-2); padding: var(--space-3) var(--space-4); border-radius: var(--ro-radius); margin: 0 0 var(--space-3); font-weight: 500; }
    .banner.late { background: var(--ro-error-bg); color: var(--ro-error-strong); border: 1px solid var(--ro-error-line); }
    .banner.soon { background: var(--ro-warn-bg); color: var(--ro-warn-strong); border: 1px solid var(--ro-warn-line); }
    .tabs { display: flex; gap: var(--space-1); margin-bottom: var(--space-3); padding: 4px; border-radius: 999px; width: fit-content; max-width: 100%;
            box-sizing: border-box; background: var(--ro-glass); border: 1px solid var(--ro-glass-edge); }
    .tabs button { padding: 8px 16px; color: var(--ro-ink-soft); border: 0; background: none; border-radius: 999px; white-space: nowrap; font: inherit; font-weight: 500;
                   min-height: 40px; cursor: pointer; display: inline-flex; align-items: center; gap: 6px; }
    .tabs button:hover { background: var(--ro-hover); }
    .tabs button.on { background: var(--ro-accent-grad); color: var(--ro-on-accent); box-shadow: var(--ro-accent-glow); font-weight: 600; }
    .count { background: var(--ro-error); color: #fff; border-radius: 999px; padding: 0 7px; font-size: .75rem; line-height: 1.5; }
    .chip.due.late { background: var(--ro-error-bg); color: var(--ro-error-strong); font-weight: 600; }
    .chip.due.soon { background: var(--ro-warn-bg); color: var(--ro-warn-strong); font-weight: 600; }
    .chip.due.fine { background: var(--ro-info-bg); color: var(--ro-info-strong); }
    .chip.done { background: var(--ro-ok-bg); color: var(--ro-ok); }
    .ok { color: var(--ro-ok); }
    .mute { color: var(--ro-mute); }
    .err { color: var(--ro-error); }
    .empty { color: var(--ro-mute); padding: var(--space-5) var(--space-4); }
    .register { margin-top: var(--space-4); padding: var(--space-4); }
    .register h2 { margin: 0 0 var(--space-1); font-size: 1.05rem; }
    .register p { margin: 0 0 var(--space-3); }
    .range { display: flex; gap: var(--space-3); align-items: center; flex-wrap: wrap; }
    .range mat-form-field { width: 190px; }
    @media (max-width: 640px) {
      .range mat-form-field { width: 100%; }
      thead { display: none; }
      .data tr { display: grid; grid-template-columns: 1fr auto; gap: 4px var(--space-2); padding: var(--space-3); border-bottom: 1px solid var(--ro-line-soft); }
      .data td { border: 0; padding: 0; }
      .data td:nth-child(3) { display: none; }
    }
  `,
})
export class FormCPage {
  private api = inject(IndiaApi);
  props = inject(PropertyContext);

  tabs: { id: Tab; label: string }[] = [{ id: 'PENDING', label: 'To file' }, { id: 'SUBMITTED', label: 'Filed' }, { id: 'ALL', label: 'All' }];
  tab = signal<Tab>('PENDING');
  list = signal<FormCList | null>(null);
  loading = signal(true);
  error = signal('');
  from = '';
  to = '';
  downloading = signal(false);
  registerError = signal('');

  private tz = computed(() => this.props.current()?.timezone ?? 'Asia/Kolkata');
  shortDate = shortDate;
  urgency = urgency;
  deadline = deadlineWords;

  constructor() {
    const today = inZone(new Date(), this.tz(), { year: 'numeric', month: '2-digit', day: '2-digit' }).split('/').reverse().join('-');
    this.to = today;
    this.from = addDays(today, -30);
    effect(() => {
      const p = this.props.current(); const status = this.tab();
      if (!p) return;
      this.loading.set(true); this.error.set('');
      this.api.formCList(p.id, status).subscribe({
        next: l => { this.list.set(l); this.loading.set(false); },
        error: e => { this.error.set(toProblem(e).detail); this.loading.set(false); },
      });
    });
  }

  country(code: string | null) { return code ? countryName(code) : 'Nationality not recorded'; }
  when(iso: string) { return inZone(iso, this.tz(), { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false }); }

  download() {
    const p = this.props.current();
    this.registerError.set('');
    if (!this.from || !this.to) { this.registerError.set('Choose both dates: the first and the last arrival day the register should cover.'); return; }
    if (this.from > this.to) { this.registerError.set('“Arrived from” is after “Arrived until”. Swap the two dates.'); return; }
    this.downloading.set(true);
    this.api.register(p.id, this.from, this.to).subscribe({
      next: r => {
        const name = /filename="?([^";]+)"?/.exec(r.headers.get('content-disposition') ?? '')?.[1] ?? `form-c-register-${this.from}-to-${this.to}.csv`;
        save(r.body!, name);
        this.downloading.set(false);
      },
      error: async e => {
        // The error body arrives as a blob because the request asked for one.
        let detail = toProblem(e).detail;
        try { const b = JSON.parse(await e.error.text()); if (b.message) detail = b.message; } catch { /* keep the general reason */ }
        this.registerError.set(detail); this.downloading.set(false);
      },
    });
  }
}
