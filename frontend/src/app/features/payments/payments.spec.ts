import { looksLikeCardNumber, luhn, methodLabel, paymentStatusClass, paymentStatusLabel, referenceHint, varianceOf } from './payments.api';

describe('payment helpers (Sprint 10)', () => {
  it('spots a card number however it is typed, and only a real one', () => {
    expect(looksLikeCardNumber('4111111111111111')).toBeTrue();
    expect(looksLikeCardNumber('4111 1111 1111 1111')).toBeTrue();
    expect(looksLikeCardNumber('card 4111-1111-1111-1111 swiped')).toBeTrue();
    expect(looksLikeCardNumber('4111111111111112')).toBeFalse();
    expect(looksLikeCardNumber('412345678901')).toBeFalse();
    expect(looksLikeCardNumber('Approval 004512')).toBeFalse();
    expect(looksLikeCardNumber('')).toBeFalse();
    expect(looksLikeCardNumber(null)).toBeFalse();
  });

  it('checks digits with Luhn', () => {
    expect(luhn('79927398713')).toBeTrue();
    expect(luhn('79927398710')).toBeFalse();
  });

  it('works out the drawer variance: negative is short', () => {
    expect(varianceOf(5500, 5450)).toEqual({ amount: -50, words: 'Short' });
    expect(varianceOf(5500, 5500)).toEqual({ amount: 0, words: 'Balanced' });
    expect(varianceOf(100.1, 100.3)).toEqual({ amount: 0.2, words: 'Over' });
    expect(varianceOf(5500, null)).toBeNull();
  });

  it('names methods and statuses in words', () => {
    expect(methodLabel('BANK_TRANSFER')).toBe('Bank transfer');
    expect(methodLabel('CHEQUE')).toBe('CHEQUE');
    expect(paymentStatusLabel('PARTIALLY_REFUNDED')).toBe('Part refunded');
    expect(paymentStatusLabel('CANCELLED')).toBe('Voided');
    expect(paymentStatusClass('CAPTURED')).toBe('ok');
    expect(paymentStatusClass('CANCELLED')).toBe('off');
    expect(paymentStatusClass('REFUNDED')).toBe('back');
  });

  it('says what the reference is for each method, and warns off card numbers', () => {
    expect(referenceHint('UPI').label).toBe('UPI transaction id');
    expect(referenceHint('CARD').hint).toContain('Never type the card number');
  });
});
