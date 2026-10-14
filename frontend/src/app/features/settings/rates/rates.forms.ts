import { AbstractControl, FormArray, FormBuilder, ValidationErrors, ValidatorFn, Validators } from '@angular/forms';
import { nightsBetween } from '../../rooms/rooms.api';

// Client-side rules mirror the API validators (Resort.Application/Validation/RateValidators.cs).
// They give instant feedback with the same reasons; the API still checks everything.

export const PLAN_CODE = /^[A-Za-z0-9_]{2,30}$/;
export const MAX_MONEY = 10_000_000;
export const MAX_NIGHTS = 366;

/** Money in whole paise, 0 … ₹1 crore (or more than 0 when `positive`). Error keys say which rule failed. */
export const money = (positive = false): ValidatorFn => (c: AbstractControl): ValidationErrors | null => {
  const raw = c.value;
  if (raw === null || raw === undefined || raw === '') return { required: true };
  const v = Number(raw);
  if (Number.isNaN(v)) return { notNumber: true };
  if (positive ? v <= 0 : v < 0) return positive ? { notPositive: true } : { negative: true };
  if (v > MAX_MONEY) return { tooBig: true };
  if (Math.round(v * 100) / 100 !== v) return { paise: true };
  return null;
};

/** The reason for a money field's error, phrased like the API's. */
export function moneyError(c: AbstractControl, label: string): string {
  const e = c.errors ?? {};
  if (e['server']) return e['server'];
  if (e['required']) return `Enter the ${label.toLowerCase()} (0 if none).`;
  if (e['notNumber']) return `${label} must be a number.`;
  if (e['negative']) return `${label} can't be negative.`;
  if (e['notPositive']) return `${label} must be more than ₹0.`;
  if (e['tooBig']) return `${label} can't be more than ₹1,00,00,000.`;
  if (e['paise']) return `${label} can have at most 2 decimal places (paise).`;
  return '';
}

export function planForm(fb: FormBuilder) {
  return fb.nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(PLAN_CODE)]],
    name: ['', [Validators.required, Validators.maxLength(100)]],
    description: ['', Validators.maxLength(1000)],
    mealPlan: ['EP', Validators.required],
    isDefault: [false],
    sortOrder: [0, [Validators.min(0), Validators.max(100000)]],
    isActive: [true],
  });
}

/** First night ≤ last night, at most 366 nights. (The API takes an exclusive end date: last night + 1.) */
export const nightRange: ValidatorFn = (g: AbstractControl): ValidationErrors | null => {
  const first = g.get('firstNight')?.value as string;
  const last = g.get('lastNight')?.value as string;
  if (!first || !last) return null;
  const n = nightsBetween(first, last) + 1;
  if (n < 1) return { lastBeforeFirst: true };
  if (n > MAX_NIGHTS) return { tooManyNights: { nights: n } };
  return null;
};

export function pricesForm(fb: FormBuilder, today: string) {
  return fb.nonNullable.group({
    accommodationTypeIds: [[] as string[], Validators.required],
    firstNight: [today, Validators.required],
    lastNight: [today, Validators.required],
    daysOfWeek: [[] as string[]],
    price: [null as number | null, money()],
    extraAdult: [0 as number | null, money()],
    extraChild: [0 as number | null, money()],
    minStay: [1, [Validators.required, Validators.min(1), Validators.max(30)]],
  }, { validators: nightRange });
}

export function quoteForm(fb: FormBuilder, today: string) {
  return fb.nonNullable.group({
    target: ['', Validators.required],     // "type:<id>" or "room:<code>"
    ratePlan: [''],
    from: [today, Validators.required],
    nights: [1, [Validators.required, Validators.min(1), Validators.max(30)]],
    adults: [2, [Validators.required, Validators.min(1), Validators.max(50)]],
    children: [0, [Validators.min(0), Validators.max(50)]],
  });
}

// ------------------------------------------------------------------ packages

export function componentGroup(fb: FormBuilder, c: { category?: string; description?: string; sharePercent?: number | null } = {}) {
  return fb.nonNullable.group({
    category: [c.category ?? 'ROOM', Validators.required],
    description: [c.description ?? '', [Validators.required, Validators.maxLength(200)]],
    sharePercent: [c.sharePercent ?? (null as number | null),
      [Validators.required, Validators.min(0.01), Validators.max(100), (x: AbstractControl) =>
        x.value !== null && x.value !== '' && Math.round(Number(x.value) * 100) / 100 !== Number(x.value) ? { decimals: true } : null]],
  });
}

export type ComponentGroup = ReturnType<typeof componentGroup>;

/** Shares add up to exactly 100 % and there is a ROOM component. */
export const splitRule: ValidatorFn = (a: AbstractControl): ValidationErrors | null => {
  const rows = (a as FormArray<ComponentGroup>).controls.map(c => c.getRawValue());
  if (!rows.length) return { noComponents: true };
  if (!rows.some(r => r.category === 'ROOM')) return { noRoom: true };
  const hundredths = rows.reduce((s, r) => s + Math.round(Number(r.sharePercent ?? 0) * 100), 0);
  if (hundredths !== 10_000) return { sharesNot100: { total: hundredths / 100 } };
  return null;
};

/** The reason the components don't add up, phrased like the API's. */
export function splitError(a: AbstractControl): string {
  const e = a.errors ?? {};
  if (e['server']) return e['server'];
  if (e['noComponents']) return 'Add at least one component; a package always includes the room.';
  if (e['noRoom']) return 'Add a ROOM component: every package includes the stay.';
  if (e['sharesNot100']) return `The shares add up to ${e['sharesNot100'].total}%; they must add up to exactly 100%.`;
  return '';
}

export function packageForm(fb: FormBuilder) {
  return fb.nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(PLAN_CODE)]],
    name: ['', [Validators.required, Validators.maxLength(100)]],
    description: ['', Validators.maxLength(2000)],
    accommodationTypeId: ['', Validators.required],
    nights: [1, [Validators.required, Validators.min(1), Validators.max(30)]],
    adults: [2, [Validators.required, Validators.min(1), Validators.max(50)]],
    children: [0, [Validators.min(0), Validators.max(50)]],
    price: [null as number | null, money(true)],
    sortOrder: [0, [Validators.min(0), Validators.max(100000)]],
    isActive: [true],
    components: fb.array<ComponentGroup>([], { validators: splitRule }),
  });
}

export type PackageForm = ReturnType<typeof packageForm>;
