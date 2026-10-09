import { AbstractControl, FormBuilder, ValidationErrors, ValidatorFn, Validators } from '@angular/forms';
import { Guest, GuestPreference } from '../../core/models';

// Client-side rules mirror the API validators (Resort.Application/Validation/GuestValidators.cs).
// They give instant feedback; the API still checks everything and normalises the phone number.

/** Digits, spaces, +, -, brackets and dots, with 7–15 digits. The API decides the exact format. */
export const phoneLike: ValidatorFn = (c: AbstractControl): ValidationErrors | null => {
  const v = (c.value ?? '').toString().trim();
  if (!v) return null;
  const digits = v.replace(/\D/g, '');
  return /^[+\d\s().-]+$/.test(v) && digits.length >= 7 && digits.length <= 15 ? null : { phone: true };
};

export const GSTIN = /^[0-9]{2}[A-Za-z0-9]{13}$/;
export const PIN_CODE = /^[1-9][0-9]{5}$/;

export function guestForm(fb: FormBuilder) {
  return fb.nonNullable.group({
    firstName: ['', [Validators.required, Validators.maxLength(100)]],
    lastName: ['', Validators.maxLength(100)],
    phone: ['', phoneLike],
    email: ['', [Validators.email, Validators.maxLength(254)]],
    dateOfBirth: [''],
    nationality: ['IN', Validators.pattern(/^[A-Za-z]{2}$/)],
    companyName: ['', Validators.maxLength(200)],
    gstin: ['', Validators.pattern(GSTIN)],
    notes: ['', Validators.maxLength(2000)],
    isVip: [false],
    line1: ['', Validators.maxLength(200)],
    line2: ['', Validators.maxLength(200)],
    city: ['', Validators.maxLength(100)],
    state: ['', Validators.maxLength(100)],
    stateCode: [''],
    postalCode: ['', Validators.maxLength(20)],
    countryCode: ['IN', Validators.pattern(/^[A-Za-z]{2}$/)],
  });
}
export type GuestFormValue = ReturnType<ReturnType<typeof guestForm>['getRawValue']>;

const blank = (s: string) => (s.trim() === '' ? null : s.trim());

/** Form value → API body (create or update). Empty strings become null; an empty address is sent as null. */
export function toGuestRequest(v: GuestFormValue, preferences: GuestPreference[]) {
  const address = {
    line1: blank(v.line1), line2: blank(v.line2), city: blank(v.city), state: blank(v.state),
    stateCode: v.countryCode.toUpperCase() === 'IN' ? blank(v.stateCode) : null,
    postalCode: blank(v.postalCode), countryCode: (blank(v.countryCode) ?? 'IN').toUpperCase(),
  };
  const hasAddress = [address.line1, address.line2, address.city, address.state, address.stateCode, address.postalCode].some(x => x !== null);
  return {
    firstName: v.firstName.trim(), lastName: blank(v.lastName), phone: blank(v.phone), email: blank(v.email),
    dateOfBirth: blank(v.dateOfBirth), nationality: blank(v.nationality)?.toUpperCase() ?? null,
    companyName: blank(v.companyName), gstin: blank(v.gstin)?.toUpperCase() ?? null, notes: blank(v.notes), isVip: v.isVip,
    address: hasAddress ? address : null,
    preferences,
  };
}

/** API guest → form value, for editing. */
export function fromGuest(g: Guest): Partial<GuestFormValue> {
  const a = g.address;
  return {
    firstName: g.firstName, lastName: g.lastName ?? '', phone: g.phone ?? '', email: g.email ?? '', dateOfBirth: g.dateOfBirth ?? '',
    nationality: g.nationality ?? '', companyName: g.companyName ?? '', gstin: g.gstin ?? '', notes: g.notes ?? '', isVip: g.isVip,
    line1: a?.line1 ?? '', line2: a?.line2 ?? '', city: a?.city ?? '', state: a?.state ?? '', stateCode: a?.stateCode ?? '',
    postalCode: a?.postalCode ?? '', countryCode: a?.countryCode ?? 'IN',
  };
}
