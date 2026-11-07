import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import {
  JobCode, JobRun, JobRunResult, Jobs, Message, MessageChannel, MessageListItem, MessageStatus, MessageTemplate, MessageVariable, MessagingSetup,
  NotificationList, Paged, ScheduledJob,
} from '../../core/models';

export interface MessageFilter { status?: string; channel?: string; reservationId?: string; q?: string; page?: number; pageSize?: number; }
export interface SaveTemplate { providerTemplate: string | null; language: string | null; subject: string | null; body: string; isActive: boolean; version: number; }

export const CHANNEL: Record<MessageChannel, string> = { WHATSAPP: 'WhatsApp', EMAIL: 'Email', IN_APP: 'In the app' };
export const CHANNEL_ICON: Record<MessageChannel, string> = { WHATSAPP: 'chat', EMAIL: 'mail', IN_APP: 'notifications' };
/** The filter chips, in the order a receptionist looks for them. PROBLEM is failed and not sent together. */
export const STATUS_FILTERS: { value: string; label: string }[] = [
  { value: '', label: 'All' }, { value: 'PROBLEM', label: 'Needs a look' }, { value: 'QUEUED', label: 'Waiting' }, { value: 'SENT', label: 'Sent' },
  { value: 'DELIVERED', label: 'Delivered' }, { value: 'READ', label: 'Read' }, { value: 'FAILED', label: 'Failed' }, { value: 'SKIPPED', label: 'Not sent' },
];
export const STATUS_LABEL: Record<MessageStatus, string> = {
  QUEUED: 'Waiting', SENT: 'Sent', DELIVERED: 'Delivered', READ: 'Read', FAILED: 'Failed', SKIPPED: 'Not sent',
};

/** The chip colour for a message status (the shared .chip classes: ok, wait, off, bad). */
export function statusClass(s: MessageStatus): string {
  return s === 'READ' || s === 'DELIVERED' ? 'ok' : s === 'FAILED' ? 'bad' : s === 'SKIPPED' ? 'off' : 'wait';
}

/** "10:00:00" (the API's time of day) → "10:00". */
export function hhmm(t: string): string { return t.slice(0, 5); }

/** "10:00" (a time input) → "10:00:00", what the API takes. */
export function apiTime(t: string): string { return /^\d{2}:\d{2}$/.test(t) ? `${t}:00` : t; }

/** The {{name}} placeholders a text uses, each once, in the order they appear. */
export function placeholders(text: string): string[] {
  const out: string[] = [];
  for (const m of (text ?? '').matchAll(/\{\{\s*([a-z_]+)\s*\}\}/g)) if (!out.includes(m[1])) out.push(m[1]);
  return out;
}

/** The placeholders the message can't fill in (the API refuses them with the list of the ones it can). */
export function unknownPlaceholders(text: string, vars: MessageVariable[]): string[] {
  return placeholders(text).filter(p => !vars.some(v => v.name === p));
}

/** The text with each {{name}} replaced by its sample, as the guest would read it; an unknown one stays as typed. */
export function preview(text: string, vars: MessageVariable[]): string {
  return (text ?? '').replace(/\{\{\s*([a-z_]+)\s*\}\}/g, (all, name: string) => vars.find(v => v.name === name)?.sample ?? all);
}

/** Typed client for the Sprint 24 messages, templates, scheduled jobs and the bell. */
@Injectable({ providedIn: 'root' })
export class MessagesApi {
  private http = inject(HttpClient);
  private m = (pid: string) => `/api/v1/properties/${pid}/messages`;

  setup(pid: string) { return this.http.get<MessagingSetup>(`${this.m(pid)}/setup`); }
  list(pid: string, f: MessageFilter) {
    const params: Record<string, string> = {};
    for (const [k, v] of Object.entries(f)) if (v !== undefined && v !== '' && v !== null) params[k] = String(v);
    return this.http.get<Paged<MessageListItem>>(this.m(pid), { params });
  }
  get(pid: string, id: string) { return this.http.get<Message>(`${this.m(pid)}/${id}`); }
  retry(pid: string, id: string) { return this.http.post<Message>(`${this.m(pid)}/${id}/retry`, {}); }
  dispatch(pid: string) { return this.http.post<{ handled: number }>(`${this.m(pid)}/dispatch`, {}); }
  /** Only with the built-in test WhatsApp: WhatsApp's webhook says the message was delivered, read or failed. */
  fakeStatus(pid: string, id: string, status: 'DELIVERED' | 'READ' | 'FAILED') {
    return this.http.post<Message>(`${this.m(pid)}/${id}/fake-status`, { status });
  }

  templates(pid: string) { return this.http.get<MessageTemplate[]>(`/api/v1/properties/${pid}/message-templates`); }
  saveTemplate(pid: string, id: string, body: SaveTemplate) {
    return this.http.put<MessageTemplate>(`/api/v1/properties/${pid}/message-templates/${id}`, body);
  }

  jobs(pid: string) { return this.http.get<Jobs>(`/api/v1/properties/${pid}/jobs`); }
  runs(pid: string, job: JobCode | null, limit = 50) {
    const params: Record<string, string> = { limit: String(limit) };
    if (job) params['job'] = job;
    return this.http.get<JobRun[]>(`/api/v1/properties/${pid}/jobs/runs`, { params });
  }
  saveJob(pid: string, j: ScheduledJob, enabled: boolean, runAt: string) {
    return this.http.put<ScheduledJob>(`/api/v1/properties/${pid}/jobs/${j.job}`, { enabled, runAt: apiTime(runAt), version: j.version });
  }
  runJob(pid: string, job: JobCode) { return this.http.post<JobRunResult>(`/api/v1/properties/${pid}/jobs/${job}/run`, {}); }

  mine(unreadOnly = false) { return this.http.get<NotificationList>('/api/v1/notifications', { params: unreadOnly ? { unread: 'true' } : {} }); }
  read(id: string) { return this.http.post<NotificationList>(`/api/v1/notifications/${id}/read`, {}); }
  readAll() { return this.http.post<NotificationList>('/api/v1/notifications/read-all', {}); }
}
