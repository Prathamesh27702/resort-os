import { HousekeepingRoom, HousekeepingTask, TaskAction } from '../../core/models';
import { addDays, localToday } from '../maintenance/maintenance.api';
import { filterRooms, fitWithin, primaryAction, statusClass } from './housekeeping.api';

function room(place: string, status: HousekeepingRoom['status'], task: Partial<HousekeepingTask> | null = null): HousekeepingRoom {
  return {
    unitId: place, unitCode: place, unitName: place, bedId: null, bedCode: null, typeName: 'Deluxe', place, status,
    occupied: false, departureToday: false, arrivalToday: false, rush: false, repair: null,
    task: task ? { status: 'OPEN', assignedTo: null, nextActions: [] as TaskAction[], ...task } as HousekeepingTask : null,
  };
}

describe('housekeeping board', () => {
  const rooms = [
    room('101', 'DIRTY', { assignedTo: 'me' }),
    room('102', 'DIRTY', { assignedTo: null }),
    room('103', 'CLEANING', { status: 'IN_PROGRESS', assignedTo: 'someone' }),
    room('104', 'CLEAN', { status: 'CLEANED', assignedTo: 'me' }),
    room('105', 'READY'),
  ];

  it('mine: my tasks plus unassigned rooms still to clean', () => {
    expect(filterRooms(rooms, 'mine', 'me').map(r => r.place)).toEqual(['101', '102', '104']);
  });

  it('todo: every room not ready; inspect: cleaned rooms', () => {
    expect(filterRooms(rooms, 'todo', 'me').map(r => r.place)).toEqual(['101', '102', '103', '104']);
    expect(filterRooms(rooms, 'inspect', 'me').map(r => r.place)).toEqual(['104']);
    expect(filterRooms(rooms, 'all', 'me').length).toBe(5);
  });

  it('the big button follows what the user may do next', () => {
    expect(primaryAction(room('1', 'DIRTY', { nextActions: ['start', 'assign'] }))?.label).toBe('Start');
    expect(primaryAction(room('1', 'CLEANING', { nextActions: ['finish'] }))?.label).toBe('Done');
    expect(primaryAction(room('1', 'CLEAN', { nextActions: ['inspect'] }))?.label).toBe('Mark ready');
    expect(primaryAction(room('1', 'DIRTY', { nextActions: ['assign'] }))).toBeNull();
    expect(primaryAction(room('1', 'READY'))).toBeNull();
  });

  it('colour classes', () => {
    expect(statusClass('OUT_OF_ORDER')).toBe('out-of-order');
    expect(statusClass('READY')).toBe('ready');
  });
});

describe('photo shrinking', () => {
  it('fits the longer side and never enlarges', () => {
    expect(fitWithin(4000, 3000, 1600)).toEqual({ width: 1600, height: 1200 });
    expect(fitWithin(3000, 4000, 1600)).toEqual({ width: 1200, height: 1600 });
    expect(fitWithin(800, 600, 1600)).toEqual({ width: 800, height: 600 });
  });
});

describe('out-of-order dates', () => {
  it('adds days across a month end', () => {
    expect(addDays('2026-10-30', 3)).toBe('2026-11-02');
  });

  it("today is the property's date, not the browser's", () => {
    // 20:00 UTC on 31 Oct is already 1 Nov in India.
    expect(localToday('Asia/Kolkata', new Date('2026-10-31T20:00:00Z'))).toBe('2026-11-01');
    expect(localToday('UTC', new Date('2026-10-31T20:00:00Z'))).toBe('2026-10-31');
  });
});
