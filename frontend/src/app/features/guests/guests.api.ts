import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Guest, GuestDocument, GuestDocumentDetail, GuestListItem, GuestStay, Paged, PossibleDuplicate } from '../../core/models';

/** Typed client for the Sprint 3 guest endpoints. Guests belong to the organisation, not to one property. */
@Injectable({ providedIn: 'root' })
export class GuestsApi {
  private http = inject(HttpClient);
  private base = '/api/v1/guests';

  list(q: string, opts: { page?: number; pageSize?: number; includeInactive?: boolean } = {}) {
    let params = new HttpParams().set('page', opts.page ?? 1).set('pageSize', opts.pageSize ?? 25);
    if (q.trim()) params = params.set('q', q.trim());
    if (opts.includeInactive) params = params.set('includeInactive', true);
    return this.http.get<Paged<GuestListItem>>(this.base, { params });
  }
  get(id: string) { return this.http.get<Guest>(`${this.base}/${id}`); }
  create(body: unknown) { return this.http.post<Guest>(this.base, body); }
  update(id: string, body: unknown) { return this.http.put<Guest>(`${this.base}/${id}`, body); }
  stays(id: string) { return this.http.get<GuestStay[]>(`${this.base}/${id}/stays`); }
  recordConsent(id: string, purpose: string, granted: boolean, source: string) {
    return this.http.post<Guest>(`${this.base}/${id}/consents`, { purpose, granted, source });
  }
  anonymise(id: string, reason: string) { return this.http.post<Guest>(`${this.base}/${id}/anonymise`, { reason }); }
  merge(body: { survivorId: string; survivorVersion: number; duplicateId: string; duplicateVersion: number; reason: string }) {
    return this.http.post<Guest>(`${this.base}/merge`, body);
  }
  possibleDuplicates(q: { firstName?: string; lastName?: string; phone?: string; email?: string; excludeId?: string }) {
    let params = new HttpParams();
    for (const [k, v] of Object.entries(q)) if (v) params = params.set(k, v);
    return this.http.get<PossibleDuplicate[]>(`${this.base}/possible-duplicates`, { params });
  }

  documents(id: string) { return this.http.get<GuestDocument[]>(`${this.base}/${id}/documents`); }
  /** Opening a document decrypts its number; the server records who looked, and when. */
  document(id: string, docId: string) { return this.http.get<GuestDocumentDetail>(`${this.base}/${id}/documents/${docId}`); }
  documentFile(id: string, docId: string) { return this.http.get(`${this.base}/${id}/documents/${docId}/file`, { responseType: 'blob' }); }
  uploadDocument(id: string, form: FormData) { return this.http.post<GuestDocument>(`${this.base}/${id}/documents`, form); }
}

// ITU country calling codes that are 2 digits long; 1 (North America) and 7 (Russia/Kazakhstan) are 1 digit; the rest are 3.
const TWO_DIGIT_CODES = new Set(['20', '27', '30', '31', '32', '33', '34', '36', '39', '40', '41', '43', '44', '45', '46', '47', '48', '49',
  '51', '52', '53', '54', '55', '56', '57', '58', '60', '61', '62', '63', '64', '65', '66', '81', '82', '84', '86', '90', '91', '92',
  '93', '94', '95', '98']);

/** "+919820012345" → "+91 98200 12345"; other countries: "+44 2079 460 958" (country code split off, rest grouped for reading). */
export function formatPhone(e164: string | null | undefined): string {
  if (!e164) return '';
  if (/^\+91\d{10}$/.test(e164)) return `+91 ${e164.slice(3, 8)} ${e164.slice(8)}`;
  if (!/^\+\d{7,15}$/.test(e164)) return e164;
  const d = e164.slice(1);
  const cc = d[0] === '1' || d[0] === '7' ? d.slice(0, 1) : TWO_DIGIT_CODES.has(d.slice(0, 2)) ? d.slice(0, 2) : d.slice(0, 3);
  const rest = d.slice(cc.length);
  // Readable groups by length (North America 3-3-4; most others 4-3-3, 3-3-3, 4-4 …). Display only.
  const shape: Record<number, number[]> = { 6: [3, 3], 7: [3, 4], 8: [4, 4], 9: [3, 3, 3], 10: cc === '1' ? [3, 3, 4] : [4, 3, 3], 11: [4, 3, 4], 12: [4, 4, 4] };
  const parts: string[] = [];
  let i = 0;
  for (const n of shape[rest.length] ?? [rest.length]) { parts.push(rest.slice(i, i + n)); i += n; }
  return `+${cc} ${parts.join(' ')}`;
}

/** Country names for the codes guests most often have at an Indian beach resort. Any ISO code is accepted. */
export const COUNTRIES: { code: string; name: string }[] = [
  { code: 'IN', name: 'India' }, { code: 'GB', name: 'United Kingdom' }, { code: 'US', name: 'United States' },
  { code: 'DE', name: 'Germany' }, { code: 'FR', name: 'France' }, { code: 'RU', name: 'Russia' }, { code: 'IL', name: 'Israel' },
  { code: 'AU', name: 'Australia' }, { code: 'NL', name: 'Netherlands' }, { code: 'IT', name: 'Italy' }, { code: 'ES', name: 'Spain' },
  { code: 'AE', name: 'United Arab Emirates' }, { code: 'CA', name: 'Canada' }, { code: 'SE', name: 'Sweden' }, { code: 'CH', name: 'Switzerland' },
  { code: 'JP', name: 'Japan' }, { code: 'SG', name: 'Singapore' }, { code: 'NP', name: 'Nepal' }, { code: 'LK', name: 'Sri Lanka' },
  { code: 'BD', name: 'Bangladesh' },
];
export const countryName = (code: string | null | undefined) => (code ? COUNTRIES.find(c => c.code === code)?.name ?? code : '');
