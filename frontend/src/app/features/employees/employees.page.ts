import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { dayText } from '../../core/dates';
import { toProblem } from '../../core/http';
import { Department, Employee } from '../../core/models';
import { PropertyContext } from '../../core/property-context';
import { DepartmentsDialog, EmployeeDialog } from './employee-dialogs';
import { EMPLOYMENT_LABEL, EmployeesApi, dayName } from './employees.api';

/**
 * Employees (Sprint 17): everyone who works at the property, by department, with their job title, phone and weekly day
 * off. Pay is never shown here (it is on the profile, for people with salary access). Works at 360 px: each person is a
 * card.
 */
@Component({
  selector: 'app-employees',
  imports: [FormsModule, RouterLink, MatButtonModule, MatButtonToggleModule, MatDialogModule, MatFormFieldModule, MatIconModule, MatInputModule,
    MatSelectModule, MatSnackBarModule],
  template: `
    <div class="page emp">
      <div class="head">
        <h1>Employees</h1>
        @if (loaded()) { <p class="mute">{{ shown().length }} {{ status() === 'LEFT' ? 'who left' : 'working here' }}</p> }
      </div>
      <div class="tools">
        <mat-form-field appearance="outline" class="search" subscriptSizing="dynamic"><mat-label>Find someone</mat-label>
          <input matInput [ngModel]="search()" (ngModelChange)="search.set($event)" placeholder="Name, E-number or phone" /></mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="dept"><mat-label>Department</mat-label>
          <mat-select [ngModel]="dept()" (ngModelChange)="dept.set($event)" aria-label="Department">
            <mat-option [value]="null">All departments</mat-option>
            @for (d of departments(); track d.id) { <mat-option [value]="d.id">{{ d.name }}</mat-option> }
          </mat-select></mat-form-field>
        <mat-button-toggle-group [ngModel]="status()" (ngModelChange)="status.set($event); load()" aria-label="Status" hideSingleSelectionIndicator>
          <mat-button-toggle value="ACTIVE">Working</mat-button-toggle>
          <mat-button-toggle value="LEFT">Left</mat-button-toggle>
        </mat-button-toggle-group>
      </div>
      <div class="tools">
        @if (canCreate()) { <button mat-flat-button (click)="add()"><mat-icon>person_add</mat-icon>Employee</button> }
        @if (canUpdate()) { <button mat-stroked-button (click)="manageDepartments()"><mat-icon>account_tree</mat-icon>Departments</button> }
        @if (canRoster()) { <a mat-stroked-button routerLink="/roster"><mat-icon>calendar_view_week</mat-icon>Roster</a> }
      </div>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
      <ul class="cards">
        @for (e of shown(); track e.id) {
          <li [attr.data-employee]="e.code">
            <a [routerLink]="['/employees', e.id]" class="nm">
              <b>{{ e.fullName }}</b> <small class="mute">{{ e.code }}</small>
              <small>{{ e.departmentName }}@if (e.designationName) { · {{ e.designationName }} } · {{ type[e.employmentType] }}</small>
              <small class="mute">@if (e.phone) { {{ e.phone }} · }@if (e.weeklyOff) { off on {{ dayName(e.weeklyOff) }} · }joined {{ dayText(e.joinedOn) }}</small>
              @if (e.status === 'LEFT') { <small class="left">Left on {{ dayText(e.leftOn!) }}: {{ e.leavingReason }}</small> }
              @if (e.userEmail) { <small class="mute"><mat-icon inline>badge</mat-icon> {{ e.userEmail }}</small> }
            </a>
            <mat-icon class="go">chevron_right</mat-icon>
          </li>
        } @empty { @if (loaded()) { <li class="mute">{{ status() === 'LEFT' ? 'Nobody has left.' : 'No employees yet.' }}@if (canCreate() && status() === 'ACTIVE') { Add the first one. }</li> } }
      </ul>
    </div>
  `,
  styles: `
    .emp { max-width: 900px; padding-bottom: 60px; }
    .head { display: flex; justify-content: space-between; align-items: baseline; gap: 8px; flex-wrap: wrap; }
    .mute { color: #5b6b6e; } .err { color: #b3261e; } .left { color: #8a5a00; }
    .tools { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 8px; } .search { flex: 1 1 220px; } .dept { flex: 0 1 200px; }
    ul { list-style: none; margin: 0; padding: 0; }
    .cards li { display: flex; gap: 10px; align-items: center; background: #fff; border: 1px solid #d9e0de; border-radius: 12px; padding: 10px 14px; margin-top: 8px; }
    .nm { flex: 1; display: flex; flex-direction: column; gap: 2px; min-width: 0; overflow-wrap: anywhere; color: inherit; text-decoration: none; }
    .go { color: #8a9a9c; }
    @media (max-width: 640px) { h1 { font-size: 1.35rem; } .dept { flex: 1 1 100%; } }
  `,
})
export class EmployeesPage {
  private api = inject(EmployeesApi);
  private ctx = inject(PropertyContext);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  private router = inject(Router);

  employees = signal<Employee[]>([]);
  departments = signal<Department[]>([]);
  loaded = signal(false);
  error = signal('');
  search = signal('');
  dept = signal<string | null>(null);
  status = signal<'ACTIVE' | 'LEFT'>('ACTIVE');
  type = EMPLOYMENT_LABEL;
  dayName = dayName;
  dayText = dayText;
  private pid = computed(() => this.ctx.current()?.id ?? '');
  canCreate = computed(() => this.auth.can('employee.create', this.pid()));
  canUpdate = computed(() => this.auth.can('employee.update', this.pid()));
  canRoster = computed(() => this.auth.can('roster.read', this.pid()));
  shown = computed(() => {
    const s = this.search().trim().toLowerCase(), d = this.dept();
    return this.employees().filter(e => (!d || e.departmentId === d)
      && (!s || e.fullName.toLowerCase().includes(s) || e.code.toLowerCase().includes(s) || (e.phone ?? '').replace(/\s/g, '').includes(s.replace(/\s/g, ''))));
  });

  constructor() {
    effect(() => { if (this.pid()) { this.load(); this.loadDepartments(); } });
  }

  load() {
    this.api.employees(this.pid(), { status: this.status() }).subscribe({
      next: e => { this.employees.set(e); this.loaded.set(true); this.error.set(''); },
      error: e => this.error.set(toProblem(e).detail),
    });
  }

  loadDepartments() {
    this.api.departments(this.pid()).subscribe({ next: d => this.departments.set(d), error: e => this.error.set(toProblem(e).detail) });
  }

  add() {
    this.dialog.open(EmployeeDialog, { data: { propertyId: this.pid(), employee: null, departments: this.departments() }, maxWidth: '100vw' })
      .afterClosed().subscribe((e?: Employee) => {
        if (!e) return;
        this.snack.open(`${e.fullName} added as ${e.code}.`, 'OK', { duration: 5000 });
        this.router.navigate(['/employees', e.id]);
      });
  }

  manageDepartments() {
    this.dialog.open(DepartmentsDialog, { data: { propertyId: this.pid() }, maxWidth: '100vw' }).afterClosed().subscribe(() => { this.loadDepartments(); this.load(); });
  }
}
