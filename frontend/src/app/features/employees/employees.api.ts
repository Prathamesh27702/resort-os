import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import {
  CopyRosterWeekResult, Department, Employee, EmployeeCompensation, EmployeeDocument, EmployeeDocumentDetail, EmployeeDocumentType, EmploymentType,
  LinkableAccount, MyRoster, PayBasis, RosterShift, RosterShortage, RosterWeek, ShiftTemplate,
} from '../../core/models';

export interface SaveEmployee {
  firstName: string; lastName: string | null; phone: string | null; email: string | null; dateOfBirth: string | null; departmentId: string;
  designationId: string | null; employmentType: EmploymentType; joinedOn: string; weeklyOff: number | null; userId: string | null;
  emergencyContactName: string | null; emergencyContactPhone: string | null; address: string | null; note: string | null; version: number | null;
}
export interface SaveTemplate {
  departmentId: string; code: string; name: string; startTime: string; endTime: string; breakMinutes: number; minStaff: number; isActive: boolean;
  version: number | null;
}
export interface SaveShift {
  employeeId: string; workDate: string; templateId: string | null; departmentId: string | null; startTime: string | null; endTime: string | null;
  breakMinutes: number | null; allowOverlap: boolean; overlapReason: string | null; note: string | null; version: number | null;
}
export interface RecordPay { effectiveFrom: string; payBasis: PayBasis; basic: number; allowances: number; pfApplicable: boolean; esiApplicable: boolean; note: string | null; }

export const EMPLOYMENT_LABEL: Record<EmploymentType, string> = {
  FULL_TIME: 'Full time', PART_TIME: 'Part time', CONTRACT: 'Contract', SEASONAL: 'Seasonal',
};
export const DOCUMENT_LABEL: Record<EmployeeDocumentType, string> = {
  AADHAAR: 'Aadhaar', PAN: 'PAN', BANK_ACCOUNT: 'Bank account', PASSPORT: 'Passport', DRIVING_LICENCE: 'Driving licence', OFFER_LETTER: 'Offer letter', OTHER: 'Other',
};
/** ISO day of the week: 1 Monday … 7 Sunday. */
export const WEEKDAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
export const dayName = (iso: number | null) => (iso ? WEEKDAYS[iso - 1] : '');

/** "07:00:00" → "07:00". */
export const hm = (t: string) => t.slice(0, 5);

/** 450 → "7 h 30 min"; 480 → "8 h". */
export function hours(minutes: number): string {
  const h = Math.floor(minutes / 60), m = minutes % 60;
  return m ? `${h} h ${m} min` : `${h} h`;
}

/** Calendar arithmetic on ISO dates, in UTC so no time zone can shift the day. */
export function plusDays(iso: string, days: number): string {
  const [y, m, d] = iso.split('-').map(Number);
  const t = new Date(Date.UTC(y, m - 1, d + days));
  return t.toISOString().slice(0, 10);
}

/** The Monday of the week an ISO date is in. */
export function mondayOf(iso: string): string {
  const [y, m, d] = iso.split('-').map(Number);
  const dow = new Date(Date.UTC(y, m - 1, d)).getUTCDay();   // 0 Sunday
  return plusDays(iso, -((dow + 6) % 7));
}

/** ISO day of the week (1 Monday … 7 Sunday) of an ISO date. */
export function isoDay(iso: string): number {
  const [y, m, d] = iso.split('-').map(Number);
  const dow = new Date(Date.UTC(y, m - 1, d)).getUTCDay();
  return dow === 0 ? 7 : dow;
}

/** "Mon 3 Nov". */
export function shortDay(iso: string): string {
  const [y, m, d] = iso.split('-').map(Number);
  const t = new Date(Date.UTC(y, m - 1, d));
  return `${WEEKDAYS[(t.getUTCDay() + 6) % 7].slice(0, 3)} ${d} ${['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'][m - 1]}`;
}

/** The shifts of one person on one day, earliest first. */
export function cellShifts(week: RosterWeek, employeeId: string, date: string): RosterShift[] {
  return week.shifts.filter(s => s.employeeId === employeeId && s.workDate === date).sort((a, b) => a.startsAt.localeCompare(b.startsAt));
}

/** Shortages per day: { '2026-11-03': [...] }. */
export function shortagesByDay(list: RosterShortage[]): Record<string, RosterShortage[]> {
  const out: Record<string, RosterShortage[]> = {};
  for (const s of list) (out[s.date] ??= []).push(s);
  return out;
}

/** Typed client for the Sprint 17 employee and roster endpoints. */
@Injectable({ providedIn: 'root' })
export class EmployeesApi {
  private http = inject(HttpClient);
  private base = (pid: string) => `/api/v1/properties/${pid}`;

  departments(pid: string) { return this.http.get<Department[]>(`${this.base(pid)}/departments`); }
  saveDepartment(pid: string, id: string | null, body: { code: string; name: string; sortOrder: number; isActive: boolean; version: number | null }) {
    return id ? this.http.put<Department>(`${this.base(pid)}/departments/${id}`, body) : this.http.post<Department>(`${this.base(pid)}/departments`, body);
  }
  saveDesignation(pid: string, id: string | null, body: { departmentId: string; name: string; isActive: boolean; version: number | null }) {
    return id ? this.http.put<Department>(`${this.base(pid)}/designations/${id}`, body) : this.http.post<Department>(`${this.base(pid)}/designations`, body);
  }

  employees(pid: string, q?: { q?: string; departmentId?: string | null; status?: 'ACTIVE' | 'LEFT' | 'ALL' }) {
    const p = new URLSearchParams();
    if (q?.q) p.set('q', q.q);
    if (q?.departmentId) p.set('departmentId', q.departmentId);
    if (q?.status) p.set('status', q.status);
    const s = p.toString();
    return this.http.get<Employee[]>(`${this.base(pid)}/employees${s ? '?' + s : ''}`);
  }
  employee(pid: string, id: string) { return this.http.get<Employee>(`${this.base(pid)}/employees/${id}`); }
  accounts(pid: string) { return this.http.get<LinkableAccount[]>(`${this.base(pid)}/employees/accounts`); }
  saveEmployee(pid: string, id: string | null, body: SaveEmployee) {
    return id ? this.http.put<Employee>(`${this.base(pid)}/employees/${id}`, body) : this.http.post<Employee>(`${this.base(pid)}/employees`, body);
  }
  leave(pid: string, id: string, body: { leftOn: string; reason: string; version: number | null }) {
    return this.http.post<Employee>(`${this.base(pid)}/employees/${id}/leave`, body);
  }
  upcoming(pid: string, id: string) { return this.http.get<RosterShift[]>(`${this.base(pid)}/employees/${id}/shifts`); }

  pay(pid: string, id: string) { return this.http.get<EmployeeCompensation>(`${this.base(pid)}/employees/${id}/compensation`); }
  recordPay(pid: string, id: string, body: RecordPay) { return this.http.post<EmployeeCompensation>(`${this.base(pid)}/employees/${id}/compensation`, body); }

  documents(pid: string, id: string) { return this.http.get<EmployeeDocument[]>(`${this.base(pid)}/employees/${id}/documents`); }
  openDocument(pid: string, id: string, docId: string) { return this.http.get<EmployeeDocumentDetail>(`${this.base(pid)}/employees/${id}/documents/${docId}`); }
  documentFile(pid: string, id: string, docId: string) {
    return this.http.get(`${this.base(pid)}/employees/${id}/documents/${docId}/file`, { responseType: 'blob' });
  }
  uploadDocument(pid: string, id: string, form: FormData) { return this.http.post<EmployeeDocument>(`${this.base(pid)}/employees/${id}/documents`, form); }
  removeDocument(pid: string, id: string, docId: string, reason: string) {
    return this.http.post<EmployeeDocument>(`${this.base(pid)}/employees/${id}/documents/${docId}/remove`, { reason });
  }

  week(pid: string, week?: string | null) { return this.http.get<RosterWeek>(`${this.base(pid)}/roster${week ? `?week=${week}` : ''}`); }
  mine(pid: string) { return this.http.get<MyRoster>(`${this.base(pid)}/roster/mine`); }
  templates(pid: string) { return this.http.get<ShiftTemplate[]>(`${this.base(pid)}/roster/templates`); }
  saveTemplate(pid: string, id: string | null, body: SaveTemplate) {
    return id ? this.http.put<ShiftTemplate>(`${this.base(pid)}/roster/templates/${id}`, body) : this.http.post<ShiftTemplate>(`${this.base(pid)}/roster/templates`, body);
  }
  saveShift(pid: string, id: string | null, body: SaveShift) {
    return id ? this.http.put<RosterShift>(`${this.base(pid)}/roster/shifts/${id}`, body) : this.http.post<RosterShift>(`${this.base(pid)}/roster/shifts`, body);
  }
  cancelShift(pid: string, id: string, reason: string, version: number | null) {
    return this.http.post<null>(`${this.base(pid)}/roster/shifts/${id}/cancel`, { reason, version });
  }
  copyWeek(pid: string, fromWeek: string, toWeek: string) { return this.http.post<CopyRosterWeekResult>(`${this.base(pid)}/roster/copy-week`, { fromWeek, toWeek }); }
}
