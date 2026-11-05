import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  ChannelReport, CommissionRate, CostReport, Dashboard, GstReport, MonthReport, OccupancyReport, PnlReport, ReportFreshness, RevenueReport,
} from '../../core/models';

export interface ReportPeriod { from?: string | null; to?: string | null; by?: 'day' | 'month'; }

/** The reports (Sprint 23). Every figure is read from the reporting summary, refreshed every few minutes. */
@Injectable({ providedIn: 'root' })
export class ReportsApi {
  private http = inject(HttpClient);
  private base(pid: string) { return `/api/v1/properties/${pid}/reports`; }
  private params(p: ReportPeriod = {}) {
    let q = new HttpParams();
    if (p.from) q = q.set('from', p.from);
    if (p.to) q = q.set('to', p.to);
    if (p.by) q = q.set('by', p.by);
    return q;
  }

  dashboard(pid: string): Observable<Dashboard> { return this.http.get<Dashboard>(`${this.base(pid)}/dashboard`); }
  occupancy(pid: string, p: ReportPeriod) { return this.http.get<OccupancyReport>(`${this.base(pid)}/occupancy`, { params: this.params(p) }); }
  revenue(pid: string, p: ReportPeriod) { return this.http.get<RevenueReport>(`${this.base(pid)}/revenue`, { params: this.params(p) }); }
  channels(pid: string, p: ReportPeriod) { return this.http.get<ChannelReport>(`${this.base(pid)}/channels`, { params: this.params({ from: p.from, to: p.to }) }); }
  gst(pid: string, p: ReportPeriod) { return this.http.get<GstReport>(`${this.base(pid)}/gst`, { params: this.params({ from: p.from, to: p.to }) }); }
  costs(pid: string, p: ReportPeriod) { return this.http.get<CostReport>(`${this.base(pid)}/costs`, { params: this.params({ from: p.from, to: p.to }) }); }
  pnl(pid: string, p: ReportPeriod) { return this.http.get<PnlReport>(`${this.base(pid)}/pnl`, { params: this.params({ from: p.from, to: p.to }) }); }
  month(pid: string, month: string | null) {
    return this.http.get<MonthReport>(`${this.base(pid)}/month`, { params: month ? new HttpParams().set('month', month) : undefined });
  }
  refresh(pid: string) { return this.http.post<ReportFreshness>(`${this.base(pid)}/refresh`, {}); }
  commission(pid: string) { return this.http.get<CommissionRate[]>(`${this.base(pid)}/commission`); }
  saveCommission(pid: string, rates: { sourceId: string; percent: number | null }[]) {
    return this.http.put<CommissionRate[]>(`${this.base(pid)}/commission`, { rates });
  }
}

export const CHANNEL: Record<string, string> = { DIRECT: 'Direct', OTA: 'Online travel agents', TRAVEL_AGENT: 'Travel agents', CORPORATE: 'Corporate', OTHER: 'Other' };

/** "₹1,23,457" rounded to the rupee, or "—" when hidden or unknown. */
export function rs(n: number | null | undefined): string {
  return n === null || n === undefined ? '—' : '₹' + Math.round(n).toLocaleString('en-IN');
}
/** "₹1.2 L" / "₹3.4 Cr" for tiles and charts. */
export function rsShort(n: number | null | undefined): string {
  if (n === null || n === undefined) return '—';
  const a = Math.abs(n);
  if (a >= 1e7) return '₹' + (n / 1e7).toFixed(2).replace(/\.?0+$/, '') + ' Cr';
  if (a >= 1e5) return '₹' + (n / 1e5).toFixed(2).replace(/\.?0+$/, '') + ' L';
  return rs(n);
}
export function pc(n: number | null | undefined): string { return n === null || n === undefined ? '—' : `${n.toLocaleString('en-IN', { maximumFractionDigits: 1 })} %`; }

/** The change from b to a, in percent ("+4.2 %"), or null when there is nothing to compare. */
export function change(a: number | null | undefined, b: number | null | undefined): number | null {
  if (a === null || a === undefined || b === null || b === undefined || b === 0) return null;
  return Math.round(1000 * (a - b) / Math.abs(b)) / 10;
}
/** For occupancy: the difference in points ("+3.1 pts"). */
export function points(a: number | null | undefined, b: number | null | undefined): number | null {
  if (a === null || a === undefined || b === null || b === undefined) return null;
  return Math.round(10 * (a - b)) / 10;
}

/** "Updated 4 min ago" from the freshness line. */
export function freshText(f: ReportFreshness | null | undefined, now = Date.now()): string {
  if (!f?.refreshedAt) return 'Not added up yet';
  const min = Math.max(0, Math.round((now - new Date(f.refreshedAt).getTime()) / 60000));
  const ago = min < 1 ? 'just now' : min < 60 ? `${min} min ago` : min < 1440 ? `${Math.round(min / 60)} h ago` : `${Math.round(min / 1440)} days ago`;
  return `Updated ${ago} (every ${f.everyMinutes} min)`;
}

/** A CSV file the spreadsheet opens: quoted where needed, numbers as they are. */
export function toCsv(head: string[], rows: (string | number | null | undefined)[][]): string {
  const cell = (v: string | number | null | undefined) => {
    if (v === null || v === undefined) return '';
    const s = String(v);
    return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  return [head, ...rows].map(r => r.map(cell).join(',')).join('\r\n') + '\r\n';
}

export function downloadCsv(name: string, csv: string) {
  const url = URL.createObjectURL(new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8' }));
  const a = document.createElement('a');
  a.href = url; a.download = name; a.click();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

/** The first day of the month an ISO day falls in, moved by n months. */
export function monthOf(iso: string, n = 0): string {
  const [y, m] = iso.split('-').map(Number);
  const d = new Date(Date.UTC(y, m - 1 + n, 1));
  return d.toISOString().slice(0, 10);
}
/** The last day of the month that starts on iso. */
export function monthEnd(iso: string): string {
  const [y, m] = iso.split('-').map(Number);
  return new Date(Date.UTC(y, m, 0)).toISOString().slice(0, 10);
}
