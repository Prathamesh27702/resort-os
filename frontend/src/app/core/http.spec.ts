import { HttpErrorResponse } from '@angular/common/http';
import { FormControl, FormGroup } from '@angular/forms';
import { fieldErrors, showFieldErrors, toProblem } from './http';

describe('toProblem', () => {
  it('maps the API error shape', () => {
    const p = toProblem(new HttpErrorResponse({
      status: 400,
      error: { code: 'VALIDATION_FAILED', message: 'Email address is not valid.', details: { Email: ['Email address is not valid.'] }, correlationId: 'abc' },
    }));
    expect(p.code).toBe('VALIDATION_FAILED');
    expect(p.detail).toBe('Email address is not valid.');
    expect(p.correlationId).toBe('abc');
    expect(fieldErrors(p)).toEqual({ email: 'Email address is not valid.' });
  });

  it('reports a network failure as offline', () => {
    expect(toProblem(new HttpErrorResponse({ status: 0 })).code).toBe('OFFLINE');
  });
});

describe('showFieldErrors', () => {
  it('puts the API message on the matching control so it shows under that field', () => {
    const form = new FormGroup({ currentPassword: new FormControl('Welcome123'), newPassword: new FormControl('Welcome123') });
    const p = toProblem(new HttpErrorResponse({
      status: 400,
      error: { code: 'VALIDATION_FAILED', message: 'New password must differ from the current one.', details: { NewPassword: ['New password must differ from the current one.'], Other: ['x'] } },
    }));
    showFieldErrors(form, p);
    expect(form.controls.newPassword.errors).toEqual({ server: 'New password must differ from the current one.' });
    expect(form.controls.newPassword.touched).toBeTrue();
    expect(form.controls.currentPassword.errors).toBeNull();
    form.controls.newPassword.setValue('Another123');   // typing again clears it
    expect(form.controls.newPassword.errors).toBeNull();
  });
});
