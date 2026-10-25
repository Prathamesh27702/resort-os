import { FolioItem } from '../../core/models';
import { categoryLabel, lineNote, liveCharges, quantityText } from './folios.api';

const rupees = (v: number) => `₹${v}`;
const line = (over: Partial<FolioItem>): FolioItem => ({
  id: 'i1', itemType: 'CHARGE', category: 'FOOD', description: 'Dinner', serviceDate: '2026-10-18', quantity: 1, unitPrice: 1450, amount: 1450,
  taxAmount: 0, total: 1450, source: 'MANUAL', packageId: null, packageName: null, groupId: null, reversesItemId: null, reversedByItemId: null,
  reason: null, reversalReason: null, movedFrom: null, movedTo: null, postedAt: '2026-10-18T15:00:00Z', postedByName: 'Rita', live: true, taxes: [], invoiceNumber: null, adjustable: true, ...over,
});

describe('folio helpers (Sprint 8)', () => {
  it('names the categories in words', () => {
    expect(categoryLabel('LAUNDRY')).toBe('Laundry');
    expect(categoryLabel('ROOM')).toBe('Room');
  });

  it('shows the quantity only when it is not 1, and a reversal by the quantity it cancels', () => {
    expect(quantityText(line({}), rupees)).toBe('');
    expect(quantityText(line({ quantity: 2, unitPrice: 650 }), rupees)).toBe('2 × ₹650');
    expect(quantityText(line({ quantity: -2, unitPrice: 650 }), rupees)).toBe('2 × ₹650');
    expect(quantityText(line({ quantity: 1.5, unitPrice: 80 }), rupees)).toBe('1.5 × ₹80');
  });

  it('says what happened to a line', () => {
    expect(lineNote(line({}))).toBe('');
    expect(lineNote(line({ live: false, reversedByItemId: 'r1', reversalReason: 'Wrong guest' }))).toBe('Reversed: Wrong guest');
    expect(lineNote(line({ live: false, reversedByItemId: 'r1', reversalReason: 'Moved to F00012 (Company)', movedTo: 'F00012' }))).toBe('Moved to F00012');
    expect(lineNote(line({ itemType: 'REVERSAL', reason: 'Wrong guest', live: false }))).toBe('Wrong guest');
    expect(lineNote(line({ movedFrom: 'F00011' }))).toBe('Moved from F00011');
  });

  it('offers only charges that still count for reversing or moving', () => {
    const items = [line({ id: 'a' }), line({ id: 'b', live: false, reversedByItemId: 'c', adjustable: false }),
      line({ id: 'c', itemType: 'REVERSAL', live: false, adjustable: false })];
    expect(liveCharges(items).map(i => i.id)).toEqual(['a']);
  });

  it('keeps invoiced or credited charges off the reverse and move lists (Sprint 12: a credit note corrects them)', () => {
    const items = [line({ id: 'a', invoiceNumber: 'BEACH/2627/00001', adjustable: false }), line({ id: 'b' })];
    expect(liveCharges(items).map(i => i.id)).toEqual(['b']);
    expect(lineNote(line({ itemType: 'CREDIT', reason: 'Dinner was charged twice', live: false }))).toBe('Dinner was charged twice');
  });
});
