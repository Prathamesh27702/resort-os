import { HttpErrorResponse } from '@angular/common/http';
import { fieldErrors, toProblem } from './http';

describe('toProblem', () => {
  it('maps the API error shape', () => {
    const p = toProblem(new HttpErrorResponse({
      status: 400,
      error: { code: 'VALIDATION_FAILED', message: 'Some fields need fixing.', details: { Email: ['Email address is not valid.'] }, correlationId: 'abc' },
    }));
    expect(p.code).toBe('VALIDATION_FAILED');
    expect(p.detail).toBe('Some fields need fixing.');
    expect(p.correlationId).toBe('abc');
    expect(fieldErrors(p)).toEqual({ email: 'Email address is not valid.' });
  });

  it('reports a network failure as offline', () => {
    expect(toProblem(new HttpErrorResponse({ status: 0 })).code).toBe('OFFLINE');
  });
});
