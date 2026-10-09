import { FormBuilder } from '@angular/forms';
import { formatPhone } from './guests.api';
import { guestForm, toGuestRequest } from './guests.forms';

describe('guest form', () => {
  const fb = new FormBuilder();

  it('needs a first name', () => {
    const f = guestForm(fb);
    expect(f.valid).toBeFalse();
    f.patchValue({ firstName: 'Riya' });
    expect(f.valid).toBeTrue();
  });

  it('accepts phone numbers typed the local or international way and rejects text', () => {
    const f = guestForm(fb);
    for (const ok of ['98200 12345', '+91-98200-12345', '(0832) 227 1234', '+44 20 7946 0958']) {
      f.controls.phone.setValue(ok);
      expect(f.controls.phone.valid).withContext(ok).toBeTrue();
    }
    for (const bad of ['12345', 'call me', '98200 12345 ext 2']) {
      f.controls.phone.setValue(bad);
      expect(f.controls.phone.valid).withContext(bad).toBeFalse();
    }
  });

  it('checks GSTIN shape', () => {
    const f = guestForm(fb);
    f.controls.gstin.setValue('30AAACR5055K1Z5');
    expect(f.controls.gstin.valid).toBeTrue();
    f.controls.gstin.setValue('123');
    expect(f.controls.gstin.valid).toBeFalse();
  });

  it('sends blanks as null and leaves out an empty address', () => {
    const f = guestForm(fb);
    f.patchValue({ firstName: '  Riya ', lastName: '', email: ' ', gstin: '30aaacr5055k1z5' });
    const body = toGuestRequest(f.getRawValue(), []);
    expect(body.firstName).toBe('Riya');
    expect(body.lastName).toBeNull();
    expect(body.email).toBeNull();
    expect(body.gstin).toBe('30AAACR5055K1Z5');
    expect(body.address).toBeNull();
  });

  it('sends the GST state code only for Indian addresses', () => {
    const f = guestForm(fb);
    f.patchValue({ firstName: 'Lukas', city: 'Berlin', stateCode: '30', countryCode: 'de' });
    const body = toGuestRequest(f.getRawValue(), []);
    expect(body.address?.countryCode).toBe('DE');
    expect(body.address?.stateCode).toBeNull();
  });

  it('formats phone numbers for reading', () => {
    expect(formatPhone('+919820012345')).toBe('+91 98200 12345');
    expect(formatPhone('+442079460958')).toMatch(/^\+44 /);
    expect(formatPhone('+14155550123')).toBe('+1 415 555 0123');
    expect(formatPhone('+447700900456')).toBe('+44 7700 900 456');
    expect(formatPhone('+61491570156')).toBe('+61 491 570 156');
    expect(formatPhone('+9779812345678')).toMatch(/^\+977 /);
    expect(formatPhone(null)).toBe('');
  });
});
