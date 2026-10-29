import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import {
  ApproveTimesheetWeekResult, AttendanceDay, AttendanceEvent, ClockAction, Geofence, MyAttendance, Timesheet, TimesheetLineStatus, TimesheetStatus,
  TimesheetWeek,
} from '../../core/models';
import { hours } from '../employees/employees.api';

export interface Position { latitude: number; longitude: number; accuracyM: number; }
export interface SaveGeofence { name: string; latitude: number; longitude: number; radiusM: number; isActive: boolean; version: number | null; }
export interface Correction { workDate: string; clockIn: string | null; clockOut: string | null; breakMinutes: number; reason: string; version: number | null; }

export const ACTION_LABEL: Record<ClockAction, string> = {
  CLOCK_IN: 'Clock in', BREAK_START: 'Start break', BREAK_END: 'End break', CLOCK_OUT: 'Clock out',
};
export const EVENT_LABEL: Record<ClockAction, string> = {
  CLOCK_IN: 'In', BREAK_START: 'Break', BREAK_END: 'Back', CLOCK_OUT: 'Out',
};
export const STATUS_LABEL: Record<TimesheetStatus, string> = {
  DRAFT: 'Draft', SUBMITTED: 'Waiting for approval', APPROVED: 'Approved', REJECTED: 'Sent back', LOCKED: 'Locked for payroll',
};
export const LINE_LABEL: Record<TimesheetLineStatus, string> = {
  WORKED: 'Worked', ABSENT: 'Not in', ON_SHIFT: 'At work now', MISSING_OUT: 'No clock-out',
};

/** "Main gate · 24 m from the centre", "Recorded by Rohan: No signal at the beach": where an event was recorded, in words. */
export function where(e: AttendanceEvent): string {
  if (e.source === 'MANAGER') return `Recorded by ${e.recordedBy ?? 'a manager'}: ${e.reason}`;
  if (e.locationStatus === 'INSIDE') return `${e.geofenceName}${e.distanceM !== null ? ` · ${e.distanceM} m from the centre` : ''}`;
  return 'Location not checked';
}

/**
 * The phone's position for clocking in. The server decides whether it is inside the geofence; this only asks the browser.
 * Rejects with a message that says what to do (allow location, turn on GPS).
 */
/** "25 min" under an hour, else "13 h 51 min". */
export function mins(minutes: number): string {
  return minutes < 60 ? `${minutes} min` : hours(minutes);
}

export function currentPosition(timeoutMs = 15000): Promise<Position> {
  return new Promise((resolve, reject) => {
    if (!('geolocation' in navigator)) { reject(new Error('This browser can’t share its location. Use Chrome or Safari on your phone.')); return; }
    navigator.geolocation.getCurrentPosition(
      p => resolve({ latitude: +p.coords.latitude.toFixed(6), longitude: +p.coords.longitude.toFixed(6), accuracyM: Math.round(p.coords.accuracy) }),
      e => reject(new Error(e.code === e.PERMISSION_DENIED
        ? 'Location is blocked for this app. Allow location in the browser’s site settings, then try again.'
        : e.code === e.TIMEOUT ? 'Your phone took too long to find where it is. Step outside or turn on GPS, then try again.'
        : 'Your phone couldn’t find where it is. Turn on location (GPS), then try again.')),
      { enableHighAccuracy: true, timeout: timeoutMs, maximumAge: 0 },
    );
  });
}

@Injectable({ providedIn: 'root' })
export class AttendanceApi {
  private http = inject(HttpClient);
  private base = (pid: string) => `/api/v1/properties/${pid}`;

  me(pid: string) { return this.http.get<MyAttendance>(`${this.base(pid)}/attendance/me`); }
  clock(pid: string, action: ClockAction, pos: Position | null) {
    return this.http.post<MyAttendance>(`${this.base(pid)}/attendance/me/clock`,
      { action, latitude: pos?.latitude ?? null, longitude: pos?.longitude ?? null, accuracyM: pos?.accuracyM ?? null });
  }
  day(pid: string, date?: string | null) { return this.http.get<AttendanceDay>(`${this.base(pid)}/attendance${date ? `?date=${date}` : ''}`); }
  record(pid: string, body: { employeeId: string; action: ClockAction; date: string | null; time: string | null; reason: string }) {
    return this.http.post<AttendanceDay>(`${this.base(pid)}/attendance/events`, body);
  }
  geofences(pid: string) { return this.http.get<Geofence[]>(`${this.base(pid)}/attendance/geofences`); }
  saveGeofence(pid: string, id: string | null, body: SaveGeofence) {
    return id ? this.http.put<Geofence>(`${this.base(pid)}/attendance/geofences/${id}`, body)
      : this.http.post<Geofence>(`${this.base(pid)}/attendance/geofences`, body);
  }

  week(pid: string, week?: string | null) { return this.http.get<TimesheetWeek>(`${this.base(pid)}/timesheets${week ? `?week=${week}` : ''}`); }
  mine(pid: string, week?: string | null) { return this.http.get<Timesheet>(`${this.base(pid)}/timesheets/mine${week ? `?week=${week}` : ''}`); }
  timesheet(pid: string, id: string) { return this.http.get<Timesheet>(`${this.base(pid)}/timesheets/${id}`); }
  submit(pid: string, id: string, note: string | null, version: number | null) {
    return this.http.post<Timesheet>(`${this.base(pid)}/timesheets/${id}/submit`, { note, version });
  }
  approve(pid: string, id: string, version: number | null) { return this.http.post<Timesheet>(`${this.base(pid)}/timesheets/${id}/approve`, { version }); }
  reject(pid: string, id: string, reason: string, version: number | null) {
    return this.http.post<Timesheet>(`${this.base(pid)}/timesheets/${id}/reject`, { reason, version });
  }
  lock(pid: string, id: string, version: number | null) { return this.http.post<Timesheet>(`${this.base(pid)}/timesheets/${id}/lock`, { version }); }
  correct(pid: string, id: string, body: Correction) { return this.http.post<Timesheet>(`${this.base(pid)}/timesheets/${id}/corrections`, body); }
  approveWeek(pid: string, weekStart: string) {
    return this.http.post<ApproveTimesheetWeekResult>(`${this.base(pid)}/timesheets/approve-week`, { weekStart });
  }
}
