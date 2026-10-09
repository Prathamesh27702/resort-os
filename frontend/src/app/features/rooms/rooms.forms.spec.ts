import { TestBed } from '@angular/core/testing';
import { FormBuilder } from '@angular/forms';
import { addDays, nightsBetween } from './rooms.api';
import { blockForm, roomForm, typeForm } from './rooms.forms';

describe('room editor form', () => {
  const fb = () => TestBed.inject(FormBuilder);

  it('requires code, name and type', () => {
    const f = roomForm(fb());
    expect(f.valid).toBeFalse();
    f.patchValue({ code: '103', name: 'Room 103', accommodationTypeId: 'type-1' });
    expect(f.valid).toBeTrue();
  });

  it('rejects room codes with spaces or over 20 characters', () => {
    const f = roomForm(fb());
    f.patchValue({ name: 'x', accommodationTypeId: 't' });
    f.controls.code.setValue('Room 103');
    expect(f.controls.code.hasError('pattern')).toBeTrue();
    f.controls.code.setValue('A'.repeat(21));
    expect(f.controls.code.hasError('pattern')).toBeTrue();
    f.controls.code.setValue('DORM-A');
    expect(f.controls.code.valid).toBeTrue();
  });

  it('limits floor and notes length', () => {
    const f = roomForm(fb());
    f.controls.floor.setValue('x'.repeat(21));
    f.controls.notes.setValue('x'.repeat(1001));
    expect(f.controls.floor.hasError('maxlength')).toBeTrue();
    expect(f.controls.notes.hasError('maxlength')).toBeTrue();
  });
});

describe('room type form', () => {
  it('forces one guest per bed for bed-based types', () => {
    const f = typeForm(TestBed.inject(FormBuilder));
    f.patchValue({ code: 'DORM', name: 'Dorm', inventoryMode: 'BED', baseOccupancy: 2, maxOccupancy: 2 });
    expect(f.hasError('bedOccupancy')).toBeTrue();
    f.patchValue({ baseOccupancy: 1, maxOccupancy: 1 });
    expect(f.valid).toBeTrue();
  });

  it('rejects maximum guests below standard guests', () => {
    const f = typeForm(TestBed.inject(FormBuilder));
    f.patchValue({ code: 'DELUXE', name: 'Deluxe', baseOccupancy: 3, maxOccupancy: 2 });
    expect(f.hasError('maxBelowBase')).toBeTrue();
  });
});

describe('block form', () => {
  const today = '2026-10-07';

  it('needs a reason of at least 3 characters', () => {
    const f = blockForm(TestBed.inject(FormBuilder), today);
    f.patchValue({ unitId: 'u', endDate: '2026-10-09', reason: '  a ' });
    expect(f.controls.reason.hasError('minTrimmed')).toBeTrue();
    f.patchValue({ reason: 'AC repair' });
    expect(f.valid).toBeTrue();
  });

  it('treats the end date as exclusive and must end after today', () => {
    const f = blockForm(TestBed.inject(FormBuilder), today);
    f.patchValue({ unitId: 'u', reason: 'Painting', startDate: '2026-10-08', endDate: '2026-10-08' });
    expect(f.hasError('endBeforeStart')).toBeTrue();
    f.patchValue({ startDate: '2026-10-01', endDate: '2026-10-07' });
    expect(f.hasError('inPast')).toBeTrue();
    f.patchValue({ startDate: '2026-10-07', endDate: '2026-10-08' });
    expect(f.valid).toBeTrue();
  });

  it('date helpers work across month ends', () => {
    expect(addDays('2026-10-31', 1)).toBe('2026-11-01');
    expect(nightsBetween('2026-10-30', '2026-11-02')).toBe(3);
  });
});
