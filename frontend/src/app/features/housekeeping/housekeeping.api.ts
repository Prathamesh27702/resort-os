import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import {
  ChecklistTemplate, Housekeeping, HousekeepingBoard, HousekeepingRoom, HousekeepingTask, StaffOption, TaskStatus, TaskType,
} from '../../core/models';

export interface SaveChecklist { name: string; taskType: TaskType; accommodationTypeId: string | null; isActive: boolean; items: { label: string; isRequired: boolean }[]; }

/** Typed client for the Sprint 13 housekeeping endpoints. Every step sends the task's version, so a step on a task someone
 *  else just changed is refused (reload) instead of undoing their work. */
@Injectable({ providedIn: 'root' })
export class HousekeepingApi {
  private http = inject(HttpClient);
  private base = (pid: string) => `/api/v1/properties/${pid}/housekeeping`;

  board(pid: string) { return this.http.get<HousekeepingBoard>(`${this.base(pid)}/board`); }
  task(pid: string, id: string) { return this.http.get<HousekeepingTask>(`${this.base(pid)}/tasks/${id}`); }
  staff(pid: string) { return this.http.get<StaffOption[]>(`${this.base(pid)}/staff`); }
  start(pid: string, t: HousekeepingTask) { return this.http.post<HousekeepingTask>(`${this.base(pid)}/tasks/${t.id}/start`, { version: t.version }); }
  /** checked null = every line done (the one-tap Done). */
  finish(pid: string, t: HousekeepingTask, checked: string[] | null, note: string | null) {
    return this.http.post<HousekeepingTask>(`${this.base(pid)}/tasks/${t.id}/finish`, { checked, note, version: t.version });
  }
  inspect(pid: string, t: HousekeepingTask, result: 'PASS' | 'FAIL' | 'START', note: string | null) {
    return this.http.post<HousekeepingTask>(`${this.base(pid)}/tasks/${t.id}/inspect`, { result, note, version: t.version });
  }
  assign(pid: string, t: HousekeepingTask, userId: string | null) {
    return this.http.post<HousekeepingTask>(`${this.base(pid)}/tasks/${t.id}/assign`, { userId, version: t.version });
  }
  create(pid: string, body: { unitId: string; bedId: string | null; taskType: TaskType; note: string | null; assignTo: string | null }) {
    return this.http.post<HousekeepingTask>(`${this.base(pid)}/tasks`, body);
  }
  checklists(pid: string) { return this.http.get<ChecklistTemplate[]>(`${this.base(pid)}/checklists`); }
  saveChecklist(pid: string, id: string | null, body: SaveChecklist) {
    return id ? this.http.put<ChecklistTemplate>(`${this.base(pid)}/checklists/${id}`, body)
      : this.http.post<ChecklistTemplate>(`${this.base(pid)}/checklists`, body);
  }
}

export const TASK_TYPES: TaskType[] = ['DEPARTURE', 'STAYOVER', 'DEEP_CLEAN', 'AFTER_REPAIR'];
const TYPE_LABEL: Record<TaskType, string> = {
  DEPARTURE: 'Departure clean', STAYOVER: 'Stay-over clean', DEEP_CLEAN: 'Deep clean', AFTER_REPAIR: 'Clean after repair',
};
export const taskTypeLabel = (t: TaskType | string) => TYPE_LABEL[t as TaskType] ?? t;

const TASK_LABEL: Record<TaskStatus, string> = { OPEN: 'To clean', IN_PROGRESS: 'Cleaning', CLEANED: 'To inspect', DONE: 'Done', CANCELLED: 'Cancelled' };
export const taskStatusLabel = (s: TaskStatus | string) => TASK_LABEL[s as TaskStatus] ?? s;

export type BoardFilter = 'mine' | 'todo' | 'inspect' | 'all';

/** Which rooms a filter shows. mine: tasks given to me, plus unassigned rooms still to clean (anyone may take one).
 *  todo: everything not ready yet. inspect: cleaned rooms waiting for a supervisor. */
export function filterRooms(rooms: HousekeepingRoom[], filter: BoardFilter, me: string | null): HousekeepingRoom[] {
  switch (filter) {
    case 'mine': return rooms.filter(r => r.task && (r.task.assignedTo === me || (!r.task.assignedTo && r.task.status === 'OPEN')));
    case 'todo': return rooms.filter(r => r.status !== 'READY');
    case 'inspect': return rooms.filter(r => r.status === 'CLEAN' || r.status === 'INSPECTION');
    default: return rooms;
  }
}

/** The one big button on a room's card, if the user has one: Start, Done or Mark ready. */
export function primaryAction(r: HousekeepingRoom): { action: 'start' | 'finish' | 'inspect'; label: string } | null {
  const a = r.task?.nextActions ?? [];
  if (a.includes('start')) return { action: 'start', label: 'Start' };
  if (a.includes('finish')) return { action: 'finish', label: 'Done' };
  if (a.includes('inspect')) return { action: 'inspect', label: 'Mark ready' };
  return null;
}

/** CSS class for a cleaning state's colour. */
export const statusClass = (s: Housekeeping) => s.toLowerCase().replaceAll('_', '-');

// ------------------------------------------------------------------ photos, shrunk on the phone

export const MAX_PHOTO_BYTES = 2 * 1024 * 1024;

/** The size to draw a photo at so its longer side is at most <paramref name="max"/> (never enlarged). */
export function fitWithin(width: number, height: number, max: number): { width: number; height: number } {
  const scale = Math.min(1, max / Math.max(width, height));
  return { width: Math.max(1, Math.round(width * scale)), height: Math.max(1, Math.round(height * scale)) };
}

/**
 * Shrinks a camera photo before it is sent (a phone photo is often 4–8 MB; staff upload on mobile data): at most 1600 px
 * on the longer side, JPEG at 75 % quality, smaller again if it is still over 2 MB. A file that isn't an image is
 * returned as it is, and the server says what is wrong with it.
 */
export async function compressPhoto(file: File): Promise<Blob> {
  if (!file.type.startsWith('image/') || typeof createImageBitmap !== 'function') return file;
  let bitmap: ImageBitmap;
  try { bitmap = await createImageBitmap(file); } catch { return file; }
  for (const [max, quality] of [[1600, 0.75], [1280, 0.6], [960, 0.5]] as const) {
    const size = fitWithin(bitmap.width, bitmap.height, max);
    const canvas = document.createElement('canvas');
    canvas.width = size.width;
    canvas.height = size.height;
    canvas.getContext('2d')!.drawImage(bitmap, 0, 0, size.width, size.height);
    const blob = await new Promise<Blob | null>(res => canvas.toBlob(res, 'image/jpeg', quality));
    if (blob && blob.size <= MAX_PHOTO_BYTES) { bitmap.close(); return blob; }
  }
  bitmap.close();
  return file;
}
