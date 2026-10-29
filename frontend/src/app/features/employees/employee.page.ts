import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth.service';
import { dayText } from '../../core/dates';
import { toProblem } from '../../core/http';
import { Department, Employee, EmployeeCompensation, EmployeeDocument, RosterShift } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { todayAt } from '../purchasing/purchasing.api';
import { ReasonData, ReasonDialog } from '../restaurant/order-dialogs';
import { rupees } from '../settings/rates/rates.api';
import { DocumentDialog, EmployeeDialog, LeaveDialog, PayDialog } from './employee-dialogs';
import { DOCUMENT_LABEL, EMPLOYMENT_LABEL, EmployeesApi, dayName, hm, hours, shortDay } from './employees.api';

/**
 * One employee (Sprint 17): the profile, their next shifts, and — only for people allowed to see them — their pay
 * (employee_salary.read) and documents (employee_document.read). A manager without salary access never gets the pay
 * section at all. Opening a document number or scan is recorded in the audit trail. Works at 360 px.
 */
@Component({
  selector: 'app-employee',
  imports: [RouterLink, MatButtonModule, MatDialogModule, MatIconModule, MatSnackBarModule],
  template: `
    <div class="page one">
      <a mat-button routerLink="/employees" class="back"><mat-icon>arrow_back</mat-icon>Employees</a>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      @if (emp(); as e) {
        <div class="head">
          <h1>{{ e.fullName }} <small class="mute">{{ e.code }}</small></h1>
          @if (e.status === 'LEFT') { <span class="status left">Left {{ dayText(e.leftOn!) }}</span> } @else { <span class="status ok">Working</span> }
        </div>
        <p class="mute">{{ e.departmentName }}@if (e.designationName) { · {{ e.designationName }} } · {{ type[e.employmentType] }} · joined {{ dayText(e.joinedOn) }}</p>
        @if (e.status === 'LEFT') { <p class="banner off">Left on {{ dayText(e.leftOn!) }}: {{ e.leavingReason }}</p> }
        <div class="acts">
          @if (can('employee.update')) { <button mat-stroked-button (click)="edit()"><mat-icon>edit</mat-icon>Edit</button> }
          @if (can('employee.update') && e.status === 'ACTIVE') { <button mat-stroked-button (click)="leave()"><mat-icon>logout</mat-icon>Leaves</button> }
        </div>

        <section class="card">
          <h2>Profile</h2>
          <dl>
            <dt>Phone</dt><dd>@if (e.phone) { <a [href]="'tel:' + e.phone">{{ e.phone }}</a> } @else { — }</dd>
            <dt>E-mail</dt><dd>{{ e.email ?? '—' }}</dd>
            <dt>Weekly day off</dt><dd>{{ e.weeklyOff ? dayName(e.weeklyOff) : 'None fixed' }}</dd>
            <dt>Date of birth</dt><dd>{{ e.dateOfBirth ? dayText(e.dateOfBirth) : '—' }}</dd>
            <dt>Staff account</dt><dd>{{ e.userEmail ?? 'Not linked' }}</dd>
            <dt>Emergency contact</dt><dd>{{ e.emergencyContactName ?? '—' }}@if (e.emergencyContactPhone) { · <a [href]="'tel:' + e.emergencyContactPhone">{{ e.emergencyContactPhone }}</a> }</dd>
            <dt>Address</dt><dd>{{ e.address ?? '—' }}</dd>
            @if (e.note) { <dt>Note</dt><dd>{{ e.note }}</dd> }
          </dl>
        </section>

        @if (can('roster.read')) {
          <section class="card">
            <h2>Next shifts</h2>
            <ul class="shifts">
              @for (s of shifts(); track s.id) {
                <li><b>{{ shortDay(s.workDate) }}</b> {{ hm(s.startTime) }}–{{ hm(s.endTime) }} <span class="mute">{{ s.departmentName }}@if (s.templateName) { · {{ s.templateName }} } · {{ hours(s.paidMinutes) }}</span></li>
              } @empty { <li class="mute">Not on the roster from today.</li> }
            </ul>
            <a mat-button routerLink="/roster">Open the roster</a>
          </section>
        }

        @if (can('employee_salary.read')) {
          <section class="card" data-testid="pay">
            <h2>Pay <small class="mute">(salary access only)</small></h2>
            @if (pay(); as p) {
              @if (p.current; as c) {
                <p class="big"><b>{{ rupees(c.gross) }}</b> {{ c.payBasis === 'DAILY' ? 'a day' : 'a month' }}
                  <span class="mute">= basic {{ rupees(c.basic) }} + allowances {{ rupees(c.allowances) }}@if (c.pfApplicable) { · PF }@if (c.esiApplicable) { · ESI } · from {{ dayText(c.effectiveFrom) }}</span></p>
              } @else { <p class="mute">No pay recorded for today yet.</p> }
              @if (p.history.length) {
                <ul class="hist">
                  @for (h of p.history; track h.id) {
                    <li [class.replaced]="h.replaced">{{ dayText(h.effectiveFrom) }}: {{ rupees(h.gross) }} {{ h.payBasis === 'DAILY' ? '/ day' : '/ month' }}
                      @if (h.isCurrent) { <b class="ok">now</b> }@if (h.replaced) { <span class="mute">(corrected)</span> }
                      @if (h.note) { <span class="mute">· {{ h.note }}</span> }<span class="mute"> · {{ h.byName ?? 'set up' }}</span></li>
                  }
                </ul>
              }
              @if (can('employee.update')) { <button mat-stroked-button (click)="recordPay()"><mat-icon>currency_rupee</mat-icon>{{ p.current ? 'Raise or correct' : 'Record pay' }}</button> }
            }
          </section>
        }

        @if (can('employee_document.read')) {
          <section class="card" data-testid="docs">
            <h2>Documents <small class="mute">(opening one is recorded)</small></h2>
            <ul class="docs">
              @for (d of docs(); track d.id) {
                <li [class.removed]="d.status === 'REMOVED'" [attr.data-doc]="d.documentType">
                  <div><b>{{ docLabel[d.documentType] }}</b> {{ d.maskedNumber ?? '' }}
                    @if (d.expiresOn) { <span [class.late]="d.expired"> · {{ d.expired ? 'expired' : 'expires' }} {{ dayText(d.expiresOn) }}</span> }
                    @if (d.note) { <span class="mute"> · {{ d.note }}</span> }
                    @if (d.status === 'REMOVED') { <span class="mute"> · removed: {{ d.removedReason }}</span> }
                    @if (opened()[d.id]; as n) { <div class="num" data-testid="doc-number">{{ n }}</div> }
                  </div>
                  @if (d.status === 'ACTIVE') {
                    <div class="dacts">
                      @if (d.maskedNumber && d.documentType !== 'AADHAAR') { <button mat-button (click)="open(d)">Show number</button> }
                      @if (d.hasFile) { <button mat-button (click)="download(d)">Scan</button> }
                      @if (can('employee.update')) { <button mat-icon-button (click)="remove(d)" [attr.aria-label]="'Remove ' + docLabel[d.documentType]"><mat-icon>delete</mat-icon></button> }
                    </div>
                  }
                </li>
              } @empty { <li class="mute">No documents yet.</li> }
            </ul>
            @if (can('employee.update')) { <button mat-stroked-button (click)="addDoc()"><mat-icon>upload_file</mat-icon>Add a document</button> }
          </section>
        }
      }
    </div>
  `,
  styles: `
    .one { max-width: 820px; padding-bottom: 60px; } .back { margin-left: -12px; }
    .head { display: flex; justify-content: space-between; align-items: baseline; gap: 8px; flex-wrap: wrap; } h1 { margin: 4px 0; }
    .mute { color: #5b6b6e; font-weight: normal; } .err { color: #b3261e; } .late { color: #b3261e; } .ok { color: #1e6b3a; }
    .status { border-radius: 12px; padding: 2px 10px; font-size: .85rem; } .status.ok { background: #e3f3e8; } .status.left { background: #fdf0d9; color: #8a5a00; }
    .banner { border-radius: 10px; padding: 8px 12px; background: #eef3f1; } .banner.off { background: #fdf0d9; }
    .acts { display: flex; gap: 8px; flex-wrap: wrap; margin: 8px 0; }
    .card { background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 10px 16px 14px; margin-top: 12px; } h2 { font-size: 1.1rem; margin: 6px 0 8px; }
    dl { display: grid; grid-template-columns: max-content 1fr; gap: 4px 16px; margin: 0; } dt { color: #5b6b6e; } dd { margin: 0; overflow-wrap: anywhere; }
    ul { list-style: none; margin: 0 0 8px; padding: 0; } li { padding: 4px 0; }
    .big { font-size: 1.05rem; } .hist li.replaced { text-decoration: line-through; opacity: .7; }
    .docs li { display: flex; gap: 8px; align-items: center; justify-content: space-between; border-bottom: 1px solid #eef1f0; flex-wrap: wrap; }
    .docs li.removed { opacity: .6; } .dacts { display: flex; align-items: center; } .num { font-family: monospace; font-size: 1.05rem; margin-top: 2px; }
    @media (max-width: 640px) { h1 { font-size: 1.35rem; } dl { grid-template-columns: 1fr; } dt { margin-top: 6px; } }
  `,
})
export class EmployeePage {
  id = input.required<string>();
  private api = inject(EmployeesApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);

  emp = signal<Employee | null>(null);
  pay = signal<EmployeeCompensation | null>(null);
  docs = signal<EmployeeDocument[]>([]);
  shifts = signal<RosterShift[]>([]);
  opened = signal<Record<string, string>>({});
  private departments = signal<Department[]>([]);
  error = signal('');
  type = EMPLOYMENT_LABEL;
  docLabel = DOCUMENT_LABEL;
  dayText = dayText; dayName = dayName; hm = hm; hours = hours; shortDay = shortDay; rupees = rupees;
  private pid = computed(() => this.ctx.current()?.id ?? '');
  private today = computed(() => todayAt(this.ctx.current()?.timezone ?? 'Asia/Kolkata'));

  constructor() {
    effect(() => { if (this.pid() && this.id()) this.load(); });
  }

  can(perm: string) { return this.auth.can(perm, this.pid()); }

  load() {
    const pid = this.pid(), id = this.id();
    this.api.employee(pid, id).subscribe({ next: e => { this.emp.set(e); this.error.set(''); }, error: e => this.error.set(toProblem(e).detail) });
    if (this.can('roster.read')) this.api.upcoming(pid, id).subscribe({ next: s => this.shifts.set(s), error: () => this.shifts.set([]) });
    if (this.can('employee_salary.read')) this.api.pay(pid, id).subscribe({ next: p => this.pay.set(p), error: e => this.error.set(toProblem(e).detail) });
    if (this.can('employee_document.read')) this.api.documents(pid, id).subscribe({ next: d => this.docs.set(d), error: e => this.error.set(toProblem(e).detail) });
    if (this.can('employee.update')) this.api.departments(pid).subscribe({ next: d => this.departments.set(d) });
  }

  edit() {
    this.dialog.open(EmployeeDialog, { data: { propertyId: this.pid(), employee: this.emp(), departments: this.departments() }, maxWidth: '100vw' })
      .afterClosed().subscribe((e?: Employee) => { if (e) { this.snack.open(`${e.fullName} saved.`, 'OK', { duration: 4000 }); this.load(); } });
  }

  leave() {
    this.dialog.open(LeaveDialog, { data: { propertyId: this.pid(), employee: this.emp(), today: this.today() }, maxWidth: '100vw' })
      .afterClosed().subscribe((e?: Employee) => { if (e) { this.snack.open(`${e.fullName} leaves after ${dayText(e.leftOn!)}.`, 'OK', { duration: 5000 }); this.load(); } });
  }

  recordPay() {
    this.dialog.open(PayDialog, { data: { propertyId: this.pid(), employee: this.emp(), current: this.pay()?.current ?? null, today: this.today() }, maxWidth: '100vw' })
      .afterClosed().subscribe((p?: EmployeeCompensation) => { if (p) { this.pay.set(p); this.snack.open('Pay recorded.', 'OK', { duration: 4000 }); } });
  }

  addDoc() {
    this.dialog.open(DocumentDialog, { data: { propertyId: this.pid(), employee: this.emp() }, maxWidth: '100vw' })
      .afterClosed().subscribe((d?: EmployeeDocument) => { if (d) { this.snack.open(`${DOCUMENT_LABEL[d.documentType]} saved.`, 'OK', { duration: 4000 }); this.load(); } });
  }

  open(d: EmployeeDocument) {
    this.api.openDocument(this.pid(), this.id(), d.id).subscribe({
      next: x => this.opened.update(o => ({ ...o, [d.id]: x.number ?? '' })),
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  download(d: EmployeeDocument) {
    this.api.documentFile(this.pid(), this.id(), d.id).subscribe({
      next: blob => { const url = URL.createObjectURL(blob); window.open(url, '_blank'); setTimeout(() => URL.revokeObjectURL(url), 60_000); },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  remove(d: EmployeeDocument) {
    const data: ReasonData<boolean> = {
      title: `Remove ${DOCUMENT_LABEL[d.documentType]}`, intro: 'The number and the scan are wiped. The entry stays with the reason.', button: 'Remove',
      placeholder: 'Uploaded the wrong file', run: r => this.api.removeDocument(this.pid(), this.id(), d.id, r).pipe(map(() => true)),
    };
    this.dialog.open(ReasonDialog<boolean>, { data, maxWidth: '100vw' }).afterClosed().subscribe(ok => { if (ok === true) this.load(); });
  }
}
