import { RosterWeek } from '../../core/models';
import { cellShifts, hours, isoDay, mondayOf, plusDays, shortDay, shortagesByDay } from './employees.api';

describe('roster helpers', () => {
  it('finds the Monday of a week and steps days without time zone drift', () => {
    expect(mondayOf('2026-11-05')).toBe('2026-11-02');   // Thursday
    expect(mondayOf('2026-11-08')).toBe('2026-11-02');   // Sunday belongs to the week before
    expect(mondayOf('2026-11-02')).toBe('2026-11-02');
    expect(plusDays('2026-12-29', 7)).toBe('2027-01-05');
    expect(plusDays('2026-03-01', -1)).toBe('2026-02-28');
  });

  it('names days and lengths the way the API does', () => {
    expect(isoDay('2026-11-02')).toBe(1);
    expect(isoDay('2026-11-08')).toBe(7);
    expect(shortDay('2026-11-03')).toBe('Tue 3 Nov');
    expect(hours(450)).toBe('7 h 30 min');
    expect(hours(480)).toBe('8 h');
  });

  it('puts a person’s shifts for a day in time order and groups shortages by day', () => {
    const week = {
      shifts: [
        { id: 'b', employeeId: 'e1', workDate: '2026-11-03', startsAt: '2026-11-03T09:30:00Z' },
        { id: 'a', employeeId: 'e1', workDate: '2026-11-03', startsAt: '2026-11-03T01:30:00Z' },
        { id: 'c', employeeId: 'e2', workDate: '2026-11-03', startsAt: '2026-11-03T01:30:00Z' },
      ],
    } as unknown as RosterWeek;
    expect(cellShifts(week, 'e1', '2026-11-03').map(s => s.id)).toEqual(['a', 'b']);
    expect(cellShifts(week, 'e1', '2026-11-04')).toEqual([]);
    const by = shortagesByDay([{ date: '2026-11-03' }, { date: '2026-11-03' }, { date: '2026-11-05' }] as never);
    expect(Object.keys(by)).toEqual(['2026-11-03', '2026-11-05']);
    expect(by['2026-11-03'].length).toBe(2);
  });
});
