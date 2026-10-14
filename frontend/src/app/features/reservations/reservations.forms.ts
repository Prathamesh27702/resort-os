import { AbstractControl, FormBuilder, ValidationErrors, ValidatorFn, Validators } from '@angular/forms';
import { addDays, nightsBetween } from '../rooms/rooms.api';
import { dayLabel } from '../settings/rates/rates.api';

// Client-side rules mirror the API validators (Resort.Application/Validation/ReservationValidators.cs and the checks in
// ReservationService). They give instant feedback with the same wording; the API still checks everything.

export const MAX_NIGHTS = 180;

/** Check-in today or later, check-out after check-in, at most 180 nights. Errors sit on the field they concern. */
export const stayDates = (today: string): ValidatorFn => (g: AbstractControl): ValidationErrors | null => {
  const checkIn = g.get('checkIn');
  const checkOut = g.get('checkOut');
  const a = checkIn?.value as string;
  const b = checkOut?.value as string;
  setOwn(checkIn, 'past', !!a && a < today);
  setOwn(checkOut, 'order', !!a && !!b && nightsBetween(a, b) < 1);
  setOwn(checkOut, 'tooLong', !!a && !!b && nightsBetween(a, b) > MAX_NIGHTS);
  return null;
};

/** Adds or removes one error key on a control without touching its other errors (e.g. `required`, `server`). */
function setOwn(c: AbstractControl | null | undefined, key: string, on: boolean) {
  if (!c) return;
  const errs = { ...(c.errors ?? {}) };
  if (on) errs[key] = true; else delete errs[key];
  c.setErrors(Object.keys(errs).length ? errs : null, { emitEvent: false });
}

/** The message for a date field, in the API's words. */
export function dateError(c: AbstractControl, today: string): string {
  const e = c.errors ?? {};
  if (e['server']) return e['server'];
  if (e['required']) return 'Choose a date.';
  if (e['past']) return `Check-in can't be before today (${dayLabel(today)} at the property).`;
  if (e['order']) return 'The check-out date must be after check-in.';
  if (e['tooLong']) return `A stay can be at most ${MAX_NIGHTS} nights.`;
  return '';
}

export function stayForm(fb: FormBuilder, today: string) {
  return fb.nonNullable.group({
    checkIn: [today, Validators.required],
    checkOut: [addDays(today, 1), Validators.required],
    adults: [2, [Validators.required, Validators.min(1), Validators.max(50)]],
    children: [0, [Validators.min(0), Validators.max(50)]],
  }, { validators: stayDates(today) });
}

/** Quick-add of a walk-in guest: a first name and a way to reach them. */
export function newGuestForm(fb: FormBuilder) {
  return fb.nonNullable.group({
    firstName: ['', [Validators.required, Validators.maxLength(100)]],
    lastName: ['', Validators.maxLength(100)],
    phone: ['', Validators.maxLength(30)],
    email: ['', [Validators.email, Validators.maxLength(254)]],
  });
}

export function bookingForm(fb: FormBuilder) {
  return fb.nonNullable.group({
    ratePlanId: ['', Validators.required],
    sourceId: ['', Validators.required],
    status: ['CONFIRMED' as 'CONFIRMED' | 'PENDING' | 'INQUIRY'],
    specialRequests: ['', Validators.maxLength(2000)],
    note: ['', Validators.maxLength(2000)],
  });
}

/** Guests in a room line: at least one adult; a room type's maximum occupancy (dorms: one guest per bed, no maximum). */
export function occupancyError(adults: number, children: number, maxOccupancy: number, bedBased: boolean): string {
  if (adults < 1) return 'Each room needs at least one adult.';
  if (!bedBased && adults + children > maxOccupancy)
    return `Sleeps at most ${maxOccupancy} guest${maxOccupancy === 1 ? '' : 's'}; this room has ${adults + children}. Add another room for the others.`;
  return '';
}
