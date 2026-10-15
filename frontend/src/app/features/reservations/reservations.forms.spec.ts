import { TestBed } from '@angular/core/testing';
import { FormBuilder } from '@angular/forms';
import { guestsText, statusClass, statusLabel } from './reservations.api';
import { bookingForm, dateError, newGuestForm, occupancyError, stayForm } from './reservations.forms';

describe('booking stay form', () => {
  const fb = () => TestBed.inject(FormBuilder);
  const today = '2026-10-14';

  it('starts with tonight, one night, two adults', () => {
    const f = stayForm(fb(), today);
    expect(f.getRawValue()).toEqual({ checkIn: '2026-10-14', checkOut: '2026-10-15', adults: 2, children: 0 });
    expect(f.valid).toBeTrue();
  });

  it('puts a past check-in error on check-in, in the API’s words', () => {
    const f = stayForm(fb(), today);
    f.patchValue({ checkIn: '2026-10-13' });
    expect(f.controls.checkIn.hasError('past')).toBeTrue();
    expect(dateError(f.controls.checkIn, today)).toContain("Check-in can't be before today");
    expect(f.valid).toBeFalse();
  });

  it('puts order and length errors on check-out', () => {
    const f = stayForm(fb(), today);
    f.patchValue({ checkOut: '2026-10-14' });
    expect(dateError(f.controls.checkOut, today)).toBe('The check-out date must be after check-in.');
    f.patchValue({ checkOut: '2027-04-13' }); // 181 nights
    expect(dateError(f.controls.checkOut, today)).toBe('A stay can be at most 180 nights.');
    f.patchValue({ checkOut: '2027-04-12' }); // 180 nights
    expect(f.controls.checkOut.valid).toBeTrue();
  });

  it('keeps a server message on the field it concerns', () => {
    const f = stayForm(fb(), today);
    f.controls.checkIn.setErrors({ server: 'The property is closed that day.' });
    expect(dateError(f.controls.checkIn, today)).toBe('The property is closed that day.');
  });

  it('needs at least one adult', () => {
    const f = stayForm(fb(), today);
    f.patchValue({ adults: 0 });
    expect(f.controls.adults.hasError('min')).toBeTrue();
  });
});

describe('quick-add guest and booking forms', () => {
  const fb = () => TestBed.inject(FormBuilder);

  it('quick-add needs a first name and a valid email if given', () => {
    const f = newGuestForm(fb());
    expect(f.valid).toBeFalse();
    f.patchValue({ firstName: 'Asha', email: 'not-an-email' });
    expect(f.controls.email.hasError('email')).toBeTrue();
    f.patchValue({ email: 'asha@example.com' });
    expect(f.valid).toBeTrue();
  });

  it('booking needs a rate plan and a source, and defaults to confirmed', () => {
    const f = bookingForm(fb());
    expect(f.controls.status.value).toBe('CONFIRMED');
    expect(f.valid).toBeFalse();
    f.patchValue({ ratePlanId: 'p', sourceId: 's' });
    expect(f.valid).toBeTrue();
  });
});

describe('occupancy and labels', () => {
  it('explains why the guests don’t fit', () => {
    expect(occupancyError(0, 1, 3, false)).toBe('Each room needs at least one adult.');
    expect(occupancyError(3, 1, 3, false)).toBe('Sleeps at most 3 guests; this room has 4. Add another room for the others.');
    expect(occupancyError(2, 1, 3, false)).toBe('');
    expect(occupancyError(6, 0, 1, true)).toBe(''); // dorm: one guest per bed
  });

  it('labels statuses and guests in plain words', () => {
    expect(statusLabel('INQUIRY')).toBe('Enquiry');
    expect(statusLabel('NO_SHOW')).toBe('No-show');
    expect(statusClass('CONFIRMED')).toBe('ok');
    expect(statusClass('CANCELLED')).toBe('off');
    expect(guestsText(1, 0)).toBe('1 adult');
    expect(guestsText(2, 1)).toBe('2 adults, 1 child');
    expect(guestsText(2, 3)).toBe('2 adults, 3 children');
  });
});
