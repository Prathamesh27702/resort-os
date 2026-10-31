import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { FormGroup } from '@angular/forms';
import { Observable } from 'rxjs';
import { controlPath, fieldErrors, toProblem } from '../../core/http';
import {
  AccrualResult, EmployeeLeaveBalances, Holiday, LeaveAccrual, LeaveEntryType, LeaveLedger, LeaveList, LeavePreview, LeaveRequest, LeaveStatus,
  LeaveType, MyLeave, PayrollExport, PayrollPreview,
} from '../../core/models';

export interface AskLeave {
  employeeId: string | null; leaveTypeId: string; fromDate: string; toDate: string; halfDayStart: boolean; halfDayEnd: boolean; reason: string | null;
}
export interface SaveLeaveType {
  code: string; name: string; isPaid: boolean; needsBalance: boolean; allowHalfDay: boolean; sortOrder: number; isActive: boolean; daysPerYear: number;
  accrual: LeaveAccrual; maxBalance: number | null; version: number | null;
}
export interface SaveHoliday { date: string; name: string; isActive: boolean; version: number | null; }
export interface Adjust { employeeId: string; leaveTypeId: string; days: number; reason: string; opening: boolean; }

export const LEAVE_STATUS: Record<LeaveStatus, string> = {
  PENDING: 'Waiting for approval', APPROVED: 'Approved', REJECTED: 'Refused', CANCELLED: 'Cancelled',
};
export const ENTRY_LABEL: Record<LeaveEntryType, string> = {
  OPENING: 'Brought in', ACCRUAL: 'Earned', TAKEN: 'Taken', RETURNED: 'Given back', ADJUSTMENT: 'Adjusted',
};
export const ACCRUAL_LABEL: Record<LeaveAccrual, string> = {
  MONTHLY: 'Every month', YEARLY: 'Once a year (1 January)', NONE: 'Only by hand',
};

/** "1 day", "0.5 day", "3.5 days". */
export function days(n: number): string {
  const t = Number.isInteger(n) ? String(n) : String(+n.toFixed(2));
  return `${t} ${n === 1 || n === 0.5 ? 'day' : 'days'}`;
}

/** "Tue 3 Nov", or "Tue 3 – Thu 5 Nov", with "(afternoon)" / "(morning)" on half days. */
export function range(r: { fromDate: string; toDate: string; halfDayStart: boolean; halfDayEnd: boolean }): string {
  const f = short(r.fromDate) + (r.halfDayStart ? ' (afternoon)' : '');
  if (r.fromDate === r.toDate) return r.halfDayEnd ? `${short(r.fromDate)} (morning)` : f;
  return `${f} – ${short(r.toDate)}${r.halfDayEnd ? ' (morning)' : ''}`;
}

const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
/** "2026-11-03" → "Tue 3 Nov". */
export function short(iso: string): string {
  const [y, m, d] = iso.split('-').map(Number);
  const dt = new Date(Date.UTC(y, m - 1, d));
  return `${DAYS[dt.getUTCDay()]} ${d} ${MONTHS[m - 1]}`;
}

/** "2026-10" → "October 2026". */
export function monthName(ym: string): string {
  const [y, m] = ym.split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, 1)).toLocaleString('en-GB', { month: 'long', year: 'numeric', timeZone: 'UTC' });
}

/**
 * Puts the server's reasons under the fields they are about (400 VALIDATION_FAILED); any other refusal (an overlap, the
 * month not ready) is shown in words with its own message, never the details object.
 */
export function showError(form: FormGroup | null, e: unknown, error: (m: string) => void) {
  const p = toProblem(e);
  if (p.code !== 'VALIDATION_FAILED' || !form) { error(p.detail); return; }
  const left: string[] = [];
  for (const [k, msg] of Object.entries(fieldErrors(p))) {
    const c = form.get(k) ?? form.get(controlPath(k));
    if (c) { c.setErrors({ ...c.errors, server: msg }); c.markAsTouched(); } else left.push(msg);
  }
  if (left.length) error(left.join(' '));
}

@Injectable({ providedIn: 'root' })
export class LeaveApi {
  private http = inject(HttpClient);
  private url = (pid: string) => `/api/v1/properties/${pid}/leave`;

  mine(pid: string) { return this.http.get<MyLeave>(`${this.url(pid)}/me`); }
  types(pid: string) { return this.http.get<LeaveType[]>(`${this.url(pid)}/types`); }
  saveType(pid: string, id: string | null, body: SaveLeaveType): Observable<LeaveType> {
    return id ? this.http.put<LeaveType>(`${this.url(pid)}/types/${id}`, body) : this.http.post<LeaveType>(`${this.url(pid)}/types`, body);
  }
  holidays(pid: string, year: number) { return this.http.get<Holiday[]>(`${this.url(pid)}/holidays`, { params: { year } }); }
  saveHoliday(pid: string, id: string | null, body: SaveHoliday): Observable<Holiday> {
    return id ? this.http.put<Holiday>(`${this.url(pid)}/holidays/${id}`, body) : this.http.post<Holiday>(`${this.url(pid)}/holidays`, body);
  }
  balances(pid: string) { return this.http.get<EmployeeLeaveBalances[]>(`${this.url(pid)}/balances`); }
  ledger(pid: string, employeeId: string) { return this.http.get<LeaveLedger>(`${this.url(pid)}/balances/${employeeId}`); }
  adjust(pid: string, body: Adjust) { return this.http.post<LeaveLedger>(`${this.url(pid)}/adjustments`, body); }
  runAccrual(pid: string, month: string) { return this.http.post<AccrualResult>(`${this.url(pid)}/accruals/run`, { month }); }

  requests(pid: string, q: { status?: LeaveStatus; from?: string; to?: string } = {}) {
    const params: Record<string, string> = {};
    for (const [k, v] of Object.entries(q)) if (v) params[k] = v;
    return this.http.get<LeaveList>(`${this.url(pid)}/requests`, { params });
  }
  preview(pid: string, body: AskLeave) { return this.http.post<LeavePreview>(`${this.url(pid)}/requests/preview`, body); }
  ask(pid: string, body: AskLeave) { return this.http.post<LeaveRequest>(`${this.url(pid)}/requests`, body); }
  approve(pid: string, r: LeaveRequest, note: string | null) {
    return this.http.post<LeaveRequest>(`${this.url(pid)}/requests/${r.id}/approve`, { note, version: r.version });
  }
  reject(pid: string, r: LeaveRequest, reason: string) {
    return this.http.post<LeaveRequest>(`${this.url(pid)}/requests/${r.id}/reject`, { reason, version: r.version });
  }
  cancel(pid: string, r: LeaveRequest, reason: string) {
    return this.http.post<LeaveRequest>(`${this.url(pid)}/requests/${r.id}/cancel`, { reason, version: r.version });
  }

  payrollPreview(pid: string, month: string) { return this.http.get<PayrollPreview>(`/api/v1/properties/${pid}/payroll/preview`, { params: { month } }); }
  payrollExports(pid: string) { return this.http.get<PayrollExport[]>(`/api/v1/properties/${pid}/payroll/exports`); }
  exportPayroll(pid: string, month: string, confirmWarnings: boolean) {
    return this.http.post<PayrollExport>(`/api/v1/properties/${pid}/payroll/exports`, { month, confirmWarnings });
  }
  payrollFile(pid: string, id: string) { return this.http.get(`/api/v1/properties/${pid}/payroll/exports/${id}/file`, { responseType: 'blob' }); }
}
