import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import {
  ChargeCategory, FolioItemTax, GstRegistration, RegistrationType, TaxCalculation, TaxCategory, TaxRule, TaxRuleType, TaxSlab,
} from '../../../core/models';
import { rupees } from '../rates/rates.api';

export interface CreateRegistration {
  gstin: string; legalName: string; tradeName: string | null; registrationType: RegistrationType; effectiveFrom: string; effectiveTo: string | null;
}
export interface CreateCategory { code: string; name: string; hsnSac: string; chargeCategory: ChargeCategory | null; description: string | null; }
export interface CreateRule {
  taxCategoryId: string; effectiveFrom: string; effectiveTo: string | null; ruleType: TaxRuleType; slabs: TaxSlab[]; source: string; notes: string | null;
}
export interface Calculate {
  propertyId: string; taxCategoryId: string | null; chargeCategory: ChargeCategory | null; date: string | null; amount: number;
  unitValue: number | null; placeOfSupply: string | null;
}

/** Typed client for the Sprint 9 GST endpoints. Categories and rules belong to the organisation; registrations to a property. */
@Injectable({ providedIn: 'root' })
export class TaxApi {
  private http = inject(HttpClient);

  registrations(pid: string) { return this.http.get<GstRegistration[]>(`/api/v1/properties/${pid}/gst-registrations`); }
  addRegistration(pid: string, body: CreateRegistration) {
    return this.http.post<GstRegistration>(`/api/v1/properties/${pid}/gst-registrations`, body);
  }
  categories() { return this.http.get<TaxCategory[]>('/api/v1/tax/categories'); }
  addCategory(body: CreateCategory) { return this.http.post<TaxCategory>('/api/v1/tax/categories', body); }
  rules() { return this.http.get<TaxRule[]>('/api/v1/tax/rules'); }
  addRule(body: CreateRule) { return this.http.post<{ rule: TaxRule; closedRule: TaxRule | null }>('/api/v1/tax/rules', body); }
  verify(id: string) { return this.http.post<TaxRule>(`/api/v1/tax/rules/${id}/verify`, {}); }
  calculate(body: Calculate) { return this.http.post<TaxCalculation>('/api/v1/tax/calculate', body); }
}

/** GST state codes (the first two digits of a GSTIN; also the place of supply). Same list as Resort.Domain/Tax → GstStates. */
export const STATES: { code: string; name: string }[] = [
  ['01', 'Jammu and Kashmir'], ['02', 'Himachal Pradesh'], ['03', 'Punjab'], ['04', 'Chandigarh'], ['05', 'Uttarakhand'], ['06', 'Haryana'],
  ['07', 'Delhi'], ['08', 'Rajasthan'], ['09', 'Uttar Pradesh'], ['10', 'Bihar'], ['11', 'Sikkim'], ['12', 'Arunachal Pradesh'], ['13', 'Nagaland'],
  ['14', 'Manipur'], ['15', 'Mizoram'], ['16', 'Tripura'], ['17', 'Meghalaya'], ['18', 'Assam'], ['19', 'West Bengal'], ['20', 'Jharkhand'],
  ['21', 'Odisha'], ['22', 'Chhattisgarh'], ['23', 'Madhya Pradesh'], ['24', 'Gujarat'], ['26', 'Dadra and Nagar Haveli and Daman and Diu'],
  ['27', 'Maharashtra'], ['29', 'Karnataka'], ['30', 'Goa'], ['31', 'Lakshadweep'], ['32', 'Kerala'], ['33', 'Tamil Nadu'], ['34', 'Puducherry'],
  ['35', 'Andaman and Nicobar Islands'], ['36', 'Telangana'], ['37', 'Andhra Pradesh'], ['38', 'Ladakh'], ['97', 'Other Territory'],
].map(([code, name]) => ({ code, name }));

/** "22 Sep 2025" for a YYYY-MM-DD date. */
export function fullDay(iso: string): string {
  return new Intl.DateTimeFormat('en-IN', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' }).format(new Date(iso + 'T00:00:00Z'));
}

/** "From 22 Sep 2025", "22 Sep 2025 – 21 Sep 2026". */
export function periodText(from: string, to: string | null): string {
  return to ? `${fullDay(from)} – ${fullDay(to)}` : `From ${fullDay(from)}`;
}

/** The same inside a sentence: "from 22 Sep 2025", "22 Sep 2025 – 21 Sep 2026". */
export function periodPhrase(from: string, to: string | null): string {
  return to ? periodText(from, to) : `from ${fullDay(from)}`;
}

/** "5%", "12.5%". */
export const pct = (rate: number) => `${Number(rate.toFixed(2))}%`;

/** A rule's rates in words: "5%" (flat) or "Up to ₹7,500: 5% · Above ₹7,500: 18%". "(with ITC)" where input tax credit is available. */
export function slabsText(slabs: TaxSlab[]): string {
  const sorted = [...slabs].sort((a, b) => (a.upTo ?? Infinity) - (b.upTo ?? Infinity));
  const itc = (s: TaxSlab) => (s.inputTaxCredit ? ' with ITC' : '');
  if (sorted.length === 1) return pct(sorted[0].rate) + itc(sorted[0]);
  return sorted.map((s, i) => s.upTo !== null
    ? `Up to ${rupees(s.upTo)}: ${pct(s.rate)}${itc(s)}`
    : `Above ${rupees(sorted[i - 1].upTo)}: ${pct(s.rate)}${itc(s)}`).join(' · ');
}

/** A bill line's GST in words: "GST 5%" (CGST + SGST shown as their total rate), "IGST 18%", "GST 5% + 18%". */
export function taxText(taxes: FolioItemTax[]): string {
  if (!taxes.length) return '';
  const intra = taxes.some(t => t.code === 'CGST');
  const rates = [...new Set(taxes.filter(t => t.code === 'IGST' || t.code === 'CGST').map(t => (t.code === 'CGST' ? t.rate * 2 : t.rate)))];
  return `${intra ? 'GST' : 'IGST'} ${rates.map(pct).join(' + ')}`;
}

export const registrationTypeLabel = (t: RegistrationType) => (t === 'COMPOSITION' ? 'Composition (no GST charged)' : 'Regular');
