import { AbstractControl, FormBuilder, ValidationErrors, ValidatorFn, Validators } from '@angular/forms';
import { nightsBetween } from './rooms.api';

// Client-side rules mirror the API validators (Resort.Application/Validation/AccommodationValidators.cs).
// They give instant feedback; the API still checks everything.

export const ROOM_CODE = /^[A-Za-z0-9_-]{1,20}$/;
export const TYPE_CODE = /^[A-Za-z0-9_]{2,30}$/;

/** Text that is at least `min` characters after trimming. */
export const minTrimmed = (min: number): ValidatorFn => (c: AbstractControl): ValidationErrors | null =>
  (c.value ?? '').toString().trim().length >= min ? null : { minTrimmed: { min } };

export function roomForm(fb: FormBuilder) {
  return fb.nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(ROOM_CODE)]],
    name: ['', [Validators.required, Validators.maxLength(100)]],
    accommodationTypeId: ['', Validators.required],
    floor: ['', Validators.maxLength(20)],
    notes: ['', Validators.maxLength(1000)],
    sortOrder: [0, [Validators.min(0), Validators.max(100000)]],
    isActive: [true],
  });
}

/** End date after start date (end is exclusive, like a check-out date) and after the property's today. */
export const blockDates = (today: string): ValidatorFn => (g: AbstractControl): ValidationErrors | null => {
  const start = g.get('startDate')?.value as string;
  const end = g.get('endDate')?.value as string;
  if (!start || !end) return null;
  if (nightsBetween(start, end) < 1) return { endBeforeStart: true };
  if (nightsBetween(start, end) > 366) return { tooLong: true };
  if (end <= today) return { inPast: true };
  return null;
};

export function blockForm(fb: FormBuilder, today: string) {
  return fb.group({
    unitId: ['', Validators.required],
    bedId: [null as string | null],
    blockType: ['MAINTENANCE', Validators.required],
    startDate: [today, Validators.required],
    endDate: ['', Validators.required],
    reason: ['', [minTrimmed(3), Validators.maxLength(500)]],
  }, { validators: blockDates(today) });
}

/** BED types sleep one guest per bed; max guests can't be below standard guests. */
export const occupancyRule: ValidatorFn = (g: AbstractControl): ValidationErrors | null => {
  const mode = g.get('inventoryMode')?.value;
  const base = Number(g.get('baseOccupancy')?.value);
  const max = Number(g.get('maxOccupancy')?.value);
  if (mode === 'BED' && (base !== 1 || max !== 1)) return { bedOccupancy: true };
  if (max < base) return { maxBelowBase: true };
  return null;
};

export function typeForm(fb: FormBuilder) {
  return fb.nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(TYPE_CODE)]],
    name: ['', [Validators.required, Validators.maxLength(100)]],
    description: ['', Validators.maxLength(2000)],
    inventoryMode: ['UNIT' as 'UNIT' | 'BED', Validators.required],
    baseOccupancy: [2, [Validators.required, Validators.min(1), Validators.max(50)]],
    maxOccupancy: [2, [Validators.required, Validators.min(1), Validators.max(50)]],
    sizeSqm: [null as number | null, Validators.min(0.1)],
    sortOrder: [0, [Validators.min(0), Validators.max(100000)]],
    amenityIds: [[] as string[]],
    isActive: [true],
  }, { validators: occupancyRule });
}
