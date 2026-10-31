import { FormControl, FormGroup } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { days, monthName, range, short, showError } from './leave.api';

describe('leave helpers', () => {
  it('says days with half days', () => {
    expect(days(1)).toBe('1 day');
    expect(days(0.5)).toBe('0.5 day');
    expect(days(3.5)).toBe('3.5 days');
    expect(days(1.25)).toBe('1.25 days');
  });

  it('says a range with its half days in words', () => {
    expect(short('2026-11-03')).toBe('Tue 3 Nov');
    expect(range({ fromDate: '2026-11-03', toDate: '2026-11-03', halfDayStart: false, halfDayEnd: true })).toBe('Tue 3 Nov (morning)');
    expect(range({ fromDate: '2026-11-03', toDate: '2026-11-05', halfDayStart: true, halfDayEnd: false })).toBe('Tue 3 Nov (afternoon) – Thu 5 Nov');
    expect(monthName('2026-10')).toBe('October 2026');
  });

  it('puts a field reason under its field and any other refusal in words, never the details object', () => {
    const form = new FormGroup({ leaveTypeId: new FormControl('x') });
    let said = '';
    showError(form, new HttpErrorResponse({ status: 400, error: { code: 'VALIDATION_FAILED', message: 'm', details: { LeaveTypeId: ['You have 0 days of Casual leave.'] } } }),
      m => (said = m));
    expect(form.controls.leaveTypeId.errors?.['server']).toBe('You have 0 days of Casual leave.');
    expect(said).toBe('');
    showError(form, new HttpErrorResponse({ status: 409, error: { code: 'LEAVE_OVERLAP', message: 'You already have Casual leave LV00002.', details: { requestId: 'abc' } } }),
      m => (said = m));
    expect(said).toBe('You already have Casual leave LV00002.');
  });
});
