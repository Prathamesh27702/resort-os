import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Property } from './models';

const KEY = 'resort.currentProperty';

/** The property the user is working in. Business dates/times are always shown in that property's timezone. */
@Injectable({ providedIn: 'root' })
export class PropertyContext {
  private http = inject(HttpClient);
  readonly properties = signal<Property[]>([]);
  private selectedId = signal<string | null>(readStored());
  readonly current = computed(() =>
    this.properties().find(p => p.id === this.selectedId()) ?? this.properties()[0] ?? null);

  async load() {
    try {
      this.properties.set(await firstValueFrom(this.http.get<Property[]>('/api/v1/properties')));
    } catch {
      this.properties.set([]);
    }
  }

  select(id: string) {
    this.selectedId.set(id);
    try { localStorage.setItem(KEY, id); } catch { /* storage unavailable */ }
  }

  upsert(p: Property) {
    this.properties.update(list => {
      const i = list.findIndex(x => x.id === p.id);
      return i < 0 ? [...list, p].sort((a, b) => a.name.localeCompare(b.name)) : list.map(x => (x.id === p.id ? p : x));
    });
  }
}

function readStored(): string | null {
  try { return localStorage.getItem(KEY); } catch { return null; }
}

/** Formats an instant in a given IANA timezone. */
export function inZone(iso: string | Date, tz: string, opts: Intl.DateTimeFormatOptions): string {
  return new Intl.DateTimeFormat('en-IN', { timeZone: tz, ...opts }).format(typeof iso === 'string' ? new Date(iso) : iso);
}
