import { AttendanceEvent } from '../../core/models';
import { mins, where } from './attendance.api';

describe('attendance helpers', () => {
  it('shows short spans in minutes and long ones in hours', () => {
    expect(mins(4)).toBe('4 min');
    expect(mins(59)).toBe('59 min');
    expect(mins(60)).toBe('1 h');
    expect(mins(831)).toBe('13 h 51 min');
  });

  it('says where an entry was recorded, in words', () => {
    const base = { source: 'DEVICE', locationStatus: 'INSIDE', geofenceName: 'Main gate', distanceM: 22, recordedBy: null, reason: null } as unknown as AttendanceEvent;
    expect(where(base)).toBe('Main gate · 22 m from the centre');
    expect(where({ ...base, locationStatus: 'NO_GEOFENCE' })).toBe('Location not checked');
    expect(where({ ...base, source: 'MANAGER', locationStatus: 'OVERRIDE', recordedBy: 'Mira', reason: 'No signal at the beach' })).toBe('Recorded by Mira: No signal at the beach');
  });
});
