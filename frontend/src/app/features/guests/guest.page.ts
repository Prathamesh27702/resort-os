import { Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { toProblem } from '../../core/http';
import { CONSENT_PURPOSES, Guest, GuestDocument, GuestDocumentDetail, GuestStay } from '../../core/models';
import { PropertyContext, inZone } from '../../core/property-context';
import { CanDirective } from '../../shared/can.directive';
import { ReasonDialog } from '../rooms/block-dialog';
import { label } from '../rooms/rooms.api';
import { DocumentDialog, MergeDialog } from './guest-dialogs';
import { GuestsApi, countryName, formatPhone } from './guests.api';

const CONSENT_LABEL: Record<string, string> = {
  MARKETING_WHATSAPP: 'Offers on WhatsApp', MARKETING_EMAIL: 'Offers by email', DATA_PROCESSING: 'Keep profile for future stays',
};

/** A guest's profile: contact, address, preferences, consents, stays and (with permission) ID documents. */
@Component({
  selector: 'app-guest',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatIconModule, MatMenuModule, MatSnackBarModule, CanDirective],
  template: `
    <div class="page">
      @if (guest(); as g) {
        <div class="page-head">
          <div>
            <a routerLink="/guests" class="back">Guests</a>
            <h1>{{ g.fullName }}
              @if (g.isVip) { <span class="chip vip">VIP</span> }
              @if (g.status === 'MERGED') { <span class="chip off">Merged</span> }
              @if (g.status === 'ANONYMISED') { <span class="chip off">Anonymised</span> }
            </h1>
            <p>Guest since {{ day(g.createdAt) }}@if (g.companyName) { · {{ g.companyName }} }</p>
          </div>
          @if (g.status === 'ACTIVE') {
            <div class="head-actions">
              <a mat-flat-button [routerLink]="'/guests/' + g.id + '/edit'" *appCan="'guest.update'">Edit</a>
              @if (canMerge() || canAnonymise()) {
                <button mat-stroked-button [matMenuTriggerFor]="more" aria-label="More actions">More <mat-icon iconPositionEnd>expand_more</mat-icon></button>
                <mat-menu #more="matMenu">
                  @if (canMerge()) { <button mat-menu-item (click)="merge()"><mat-icon>call_merge</mat-icon>Merge a duplicate into this profile</button> }
                  @if (canAnonymise()) { <button mat-menu-item (click)="anonymise()"><mat-icon>person_off</mat-icon>Anonymise (forget this guest)</button> }
                </mat-menu>
              }
            </div>
          }
        </div>

        @if (g.status === 'MERGED') {
          <p class="banner">This profile was merged into <a [routerLink]="'/guests/' + g.mergedIntoId">another profile</a>. Use that one.</p>
        }
        @if (g.status === 'ANONYMISED') {
          <p class="banner">Personal data was removed on {{ day(g.anonymisedAt!) }}. The profile is kept only so past stays and invoices still add up.</p>
        }

        <div class="grid">
          <section class="panel card">
            <h2>Contact</h2>
            <dl>
              <dt>Phone</dt><dd class="num">{{ phone(g.phone) || '—' }}</dd>
              <dt>Email</dt><dd>{{ g.email || '—' }}</dd>
              <dt>Nationality</dt><dd>{{ country(g.nationality) || '—' }}@if (g.nationality && g.nationality !== 'IN') { <span class="chip">Form-C needed</span> }</dd>
              @if (g.dateOfBirth) { <dt>Date of birth</dt><dd>{{ g.dateOfBirth }}</dd> }
              @if (g.gstin) { <dt>GSTIN</dt><dd class="num">{{ g.gstin }}</dd> }
            </dl>
          </section>

          <section class="panel card">
            <h2>Address</h2>
            @if (g.address; as a) {
              <p class="addr">
                @if (a.line1) { {{ a.line1 }}<br /> }
                @if (a.line2) { {{ a.line2 }}<br /> }
                {{ cityLine(a) }}<br />
                {{ country(a.countryCode) }}
              </p>
            } @else { <p class="mute">No address.</p> }
          </section>

          <section class="panel card">
            <h2>Preferences and notes</h2>
            @for (p of g.preferences; track p.category + p.value) {
              <span class="chip">{{ label(p.category) }}: {{ p.value }}</span>
            } @empty { <p class="mute">No preferences recorded.</p> }
            @if (g.notes) { <p class="notes">{{ g.notes }}</p> }
          </section>

          <section class="panel card">
            <h2>Consent</h2>
            <table class="plain">
              @for (purpose of purposes; track purpose) {
                <tr>
                  <td>{{ consentLabel[purpose] }}</td>
                  <td>
                    @if (consent(purpose); as c) {
                      <span class="chip" [class.off]="!c.granted">{{ c.granted ? 'Yes' : 'No' }}</span>
                      <small class="mute when">{{ day(c.recordedAt) }}@if (c.recordedByName) { · {{ c.recordedByName }} }</small>
                    } @else { <span class="mute">Not asked</span> }
                  </td>
                  <td class="right">
                    @if (g.status === 'ACTIVE') {
                      <ng-container *appCan="'guest.update'">
                        <button mat-button (click)="setConsent(purpose, true)" [disabled]="consent(purpose)?.granted === true">Yes</button>
                        <button mat-button (click)="setConsent(purpose, false)" [disabled]="consent(purpose)?.granted === false">No</button>
                      </ng-container>
                    }
                  </td>
                </tr>
              }
            </table>
            <p class="hint">Record what the guest said. Every answer is kept with the time and who recorded it.</p>
          </section>
        </div>

        <section class="panel card wide">
          <h2>Stays</h2>
          @for (s of stays(); track s.reservationId) {
            <p>{{ s.propertyName }} · {{ s.checkIn }} – {{ s.checkOut }} · {{ label(s.status) }}</p>
          } @empty {
            <p class="mute">No stays yet. Bookings appear here once reservations are built (Sprint 05).</p>
          }
        </section>

        @if (g.documentCount !== null) {
          <section class="panel card wide">
            <div class="row">
              <h2>ID documents</h2>
              @if (g.status === 'ACTIVE') { <button mat-stroked-button (click)="addDocument()"><mat-icon>add</mat-icon>Add document</button> }
            </div>
            <p class="hint">Encrypted. Opening a number or a scan is recorded in the audit trail.</p>
            <table class="data">
              <thead><tr><th>Document</th><th>Number</th><th class="hide-sm">Expires</th><th class="hide-sm">Added</th><th></th></tr></thead>
              <tbody>
                @for (d of documents(); track d.id) {
                  <tr>
                    <td>{{ label(d.documentType) }}@if (d.issuingCountry) { <small class="mute"> · {{ d.issuingCountry }}</small> }
                      @if (d.status === 'PURGED') { <span class="chip off">Purged</span> }</td>
                    <td class="num">
                      @if (revealed()[d.id]; as r) { <b>{{ r.number ?? d.maskedNumber }}</b>@if (r.details?.['visaNumber']) { <br /><small>Visa {{ r.details!['visaNumber'] }}</small> } }
                      @else { {{ d.maskedNumber ?? '—' }} }
                    </td>
                    <td class="hide-sm">{{ d.expiresOn ?? '—' }}</td>
                    <td class="hide-sm">{{ day(d.createdAt) }}@if (d.createdByName) { <br /><small class="mute">{{ d.createdByName }}</small> }</td>
                    <td class="right">
                      @if (d.status === 'ACTIVE') {
                        @if (d.maskedNumber && !revealed()[d.id] && d.documentType !== 'AADHAAR') { <button mat-button (click)="reveal(d)">Show number</button> }
                        @if (d.hasFile) { <button mat-button (click)="viewScan(d)">View scan</button> }
                      }
                    </td>
                  </tr>
                } @empty {
                  <tr><td colspan="5" class="mute">No ID documents. Add one at check-in if the law requires it (always for foreign nationals).</td></tr>
                }
              </tbody>
            </table>
          </section>
        }
        @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      } @else if (error()) {
        <p class="err" role="alert">{{ error() }}</p>
      } @else {
        <p class="mute">Loading…</p>
      }
    </div>
  `,
  styles: `
    .back { color: #5b6b6e; text-decoration: none; font-size: 0.9rem; } .back::before { content: '‹ '; }
    h1 .chip { font-size: .8rem; vertical-align: middle; margin-left: 8px; }
    .vip { background: #fbefd8; color: #8a5a00; }
    .head-actions { display: flex; gap: 8px; }
    .banner { background: #eef2f1; border-left: 4px solid #5b6b6e; padding: 10px 14px; border-radius: 6px; }
    .grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
    .card { padding: 16px 20px; }
    .wide { margin-top: 16px; }
    h2 { font-size: 1rem; color: #0e3b43; margin: 0 0 12px; }
    dl { display: grid; grid-template-columns: 120px 1fr; gap: 6px 12px; margin: 0; }
    dt { color: #5b6b6e; } dd { margin: 0; }
    .addr { margin: 0; line-height: 1.5; }
    .notes { white-space: pre-wrap; background: #f5f7f6; padding: 8px 12px; border-radius: 6px; }
    .mute { color: #5b6b6e; }
    .hint { color: #5b6b6e; font-size: .85rem; margin: 8px 0 0; }
    .plain { width: 100%; border-collapse: collapse; } .plain td { padding: 4px 0; vertical-align: middle; }
    .right { text-align: right; white-space: nowrap; }
    .when { display: block; font-size: .75rem; }
    .row { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
    .row h2 { margin: 0; }
    .err { color: #b3261e; }
    @media (max-width: 900px) { .grid { grid-template-columns: 1fr; } }
    @media (max-width: 600px) {
      .plain tr { display: grid; grid-template-columns: 1fr auto; align-items: center; padding: 6px 0; border-bottom: 1px solid #edf1f0; }
      .plain td.right { grid-column: 1 / -1; text-align: left; }
      dl { grid-template-columns: 96px 1fr; }
    }
  `,
})
export class GuestPage implements OnInit, OnDestroy {
  id = input.required<string>();
  private api = inject(GuestsApi);
  private auth = inject(AuthService);
  private ctx = inject(PropertyContext);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);

  label = label;
  phone = formatPhone;
  country = countryName;
  purposes = CONSENT_PURPOSES;
  consentLabel = CONSENT_LABEL;

  guest = signal<Guest | null>(null);
  stays = signal<GuestStay[]>([]);
  documents = signal<GuestDocument[]>([]);
  revealed = signal<Record<string, GuestDocumentDetail>>({});
  error = signal('');
  private objectUrls: string[] = [];

  canMerge = computed(() => this.auth.canAnywhere('guest.update'));
  /** Forgetting a guest is an organisation-wide decision: needs guest.delete for all properties. */
  canAnonymise = computed(() => this.auth.can('guest.delete', null));

  async ngOnInit() { await this.load(); }
  ngOnDestroy() { this.objectUrls.forEach(u => URL.revokeObjectURL(u)); }

  private async load() {
    try {
      const g = await firstValueFrom(this.api.get(this.id()));
      this.guest.set(g);
      this.revealed.set({});
      this.stays.set(await firstValueFrom(this.api.stays(g.id)));
      if (g.documentCount !== null) this.documents.set(await firstValueFrom(this.api.documents(g.id)));
    } catch (e) {
      this.error.set(toProblem(e).status === 404 ? 'This guest doesn’t exist, or belongs to another organisation.' : toProblem(e).detail);
    }
  }

  day(iso: string) {
    return inZone(iso, this.ctx.current()?.timezone ?? 'Asia/Kolkata', { day: 'numeric', month: 'short', year: 'numeric' });
  }

  cityLine(a: { city: string | null; state: string | null; postalCode: string | null }) {
    return [a.city, a.state, a.postalCode].filter(Boolean).join(', ');
  }

  consent(purpose: string) { return this.guest()?.consents.find(c => c.purpose === purpose) ?? null; }

  setConsent(purpose: string, granted: boolean) {
    this.api.recordConsent(this.guest()!.id, purpose, granted, 'FRONT_DESK').subscribe({
      next: g => { this.guest.set(g); this.snack.open('Consent recorded', undefined, { duration: 2500 }); },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  addDocument() {
    this.dialog.open(DocumentDialog, { data: { guest: this.guest()! } }).afterClosed().subscribe(d => {
      if (!d) return;
      this.snack.open('Document saved', undefined, { duration: 2500 });
      this.load();
    });
  }

  reveal(d: GuestDocument) {
    this.api.document(this.guest()!.id, d.id).subscribe({
      next: r => this.revealed.set({ ...this.revealed(), [d.id]: r }),
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  viewScan(d: GuestDocument) {
    // Opened from a blob in this tab's memory: the scan never gets a URL that could be shared or cached.
    const win = window.open('', '_blank');
    this.api.documentFile(this.guest()!.id, d.id).subscribe({
      next: blob => {
        const url = URL.createObjectURL(blob);
        this.objectUrls.push(url);
        if (win) win.location.href = url; else window.location.href = url;
      },
      error: e => { win?.close(); this.error.set(toProblem(e).detail); },
    });
  }

  merge() {
    this.dialog.open(MergeDialog, { data: { survivor: this.guest()! } }).afterClosed().subscribe(g => {
      if (!g) return;
      this.snack.open('Profiles merged', undefined, { duration: 2500 });
      this.load();
    });
  }

  anonymise() {
    const g = this.guest()!;
    this.dialog.open(ReasonDialog, { data: {
      title: `Anonymise ${g.fullName}?`,
      message: 'Name, contact details, address, preferences and ID documents are deleted for good. Past stays and invoices keep an anonymous entry. Use this when a guest asks to be forgotten.',
      action: 'Anonymise',
    } }).afterClosed().subscribe((reason?: string) => {
      if (!reason) return;
      this.api.anonymise(g.id, reason).subscribe({
        next: a => { this.guest.set(a); this.documents.set([]); this.snack.open('Guest anonymised', undefined, { duration: 2500 }); this.load(); },
        error: e => this.error.set(toProblem(e).detail),
      });
    });
  }

}
