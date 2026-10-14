import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Package, RatePlan, RatePrice, RateQuote, SetRatePricesResult } from '../../../core/models';

/** Typed client for the Sprint 4 rate and package endpoints. Every call is for one property. */
@Injectable({ providedIn: 'root' })
export class RatesApi {
  private http = inject(HttpClient);
  private base = (pid: string) => `/api/v1/properties/${pid}`;

  plans(pid: string) { return this.http.get<RatePlan[]>(`${this.base(pid)}/rate-plans`); }
  createPlan(pid: string, body: unknown) { return this.http.post<RatePlan>(`${this.base(pid)}/rate-plans`, body); }
  updatePlan(pid: string, id: string, body: unknown) { return this.http.put<RatePlan>(`${this.base(pid)}/rate-plans/${id}`, body); }

  /** Prices for nights in [from, to). */
  prices(pid: string, planId: string, from: string, to: string) {
    return this.http.get<RatePrice[]>(`${this.base(pid)}/rate-plans/${planId}/prices`, { params: { from, to } });
  }
  setPrices(pid: string, planId: string, body: unknown) {
    return this.http.put<SetRatePricesResult>(`${this.base(pid)}/rate-plans/${planId}/prices`, body);
  }

  quote(pid: string, q: { type?: string; room?: string; ratePlan?: string; from: string; to: string; adults: number; children: number }) {
    let params = new HttpParams();
    for (const [k, v] of Object.entries(q)) if (v !== undefined && v !== null && v !== '') params = params.set(k, String(v));
    return this.http.get<RateQuote>(`${this.base(pid)}/rates/quote`, { params });
  }

  packages(pid: string) { return this.http.get<Package[]>(`${this.base(pid)}/packages`); }
  package(pid: string, id: string) { return this.http.get<Package>(`${this.base(pid)}/packages/${id}`); }
  createPackage(pid: string, body: unknown) { return this.http.post<Package>(`${this.base(pid)}/packages`, body); }
  updatePackage(pid: string, id: string, body: unknown) { return this.http.put<Package>(`${this.base(pid)}/packages/${id}`, body); }
}

const INR = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 2, minimumFractionDigits: 0 });
const INR_PAISE = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** ₹3,500 for whole rupees, ₹10,499.30 when there are paise. */
export function rupees(v: number | null | undefined): string {
  if (v === null || v === undefined || Number.isNaN(v)) return '—';
  return Number.isInteger(v) ? INR.format(v) : INR_PAISE.format(v);
}

/** Always with paise (₹2,550.00): for amounts that are meant to add up, such as a package split. */
export function rupeesExact(v: number): string { return INR_PAISE.format(v); }

/** "Fri 16 Oct" for a YYYY-MM-DD date. */
export function dayLabel(iso: string): string {
  return new Intl.DateTimeFormat('en-IN', { weekday: 'short', day: 'numeric', month: 'short', timeZone: 'UTC' })
    .format(new Date(iso + 'T00:00:00Z'));
}

/** 0 = Sunday … 6 = Saturday, for a YYYY-MM-DD date. */
export function weekday(iso: string): number {
  return new Date(iso + 'T00:00:00Z').getUTCDay();
}

/**
 * The API's package split (Resort.Domain/Rates/QuoteEngine.cs → MoneySplit), so the form can show the amounts while
 * the manager types. Largest remainder: every part gets the whole paise of its exact share, then the paise left over
 * go one each to the parts with the largest discarded fractions (ties: earlier part). Adds up to the total exactly.
 * Returns null while the shares don't add up to 100.
 */
export function splitByPercent(total: number, percents: number[]): number[] | null {
  const hundredths = percents.map(p => Math.round(p * 100));
  if (!percents.length || hundredths.reduce((a, b) => a + b, 0) !== 10_000 || hundredths.some(h => h < 0)) return null;
  const paise = Math.round(total * 100);
  if (paise < 0) return null;
  // exact share in paise = paise × h / 10000; keep integer arithmetic to avoid floating-point drift
  const whole = hundredths.map(h => Math.floor((paise * h) / 10_000));
  const rem = hundredths.map((h, i) => paise * h - whole[i] * 10_000);
  let left = paise - whole.reduce((a, b) => a + b, 0);
  const order = rem.map((r, i) => [r, i] as const).sort((a, b) => b[0] - a[0] || a[1] - b[1]);
  for (const [, i] of order) { if (left-- <= 0) break; whole[i] += 1; }
  return whole.map(p => p / 100);
}
