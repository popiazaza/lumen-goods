import { FormControl } from '@angular/forms';
import {
  caretAfterFormat,
  cardNumberValidator,
  cvcLength,
  detectBrand,
  expiryValidator,
  formatCardNumber,
  formatExpiry,
  isExpired,
  parseExpiry,
  passesLuhn,
} from './card';

const control = (value: string) => new FormControl(value, { nonNullable: true });

describe('card helpers', () => {
  describe('passesLuhn', () => {
    it.each([
      '4111111111111111',
      '4242000000000018',
      '4242000000000026',
      '4242000000000034',
      '4242000000000042',
      '4242000000000059',
      '5555555555554444',
      '378282246310005',
    ])('accepts the test card %s', (digits) => {
      expect(passesLuhn(digits)).toBe(true);
    });

    it.each(['4111111111111112', '4242000000000010', '', '4111 1111'])('rejects %j', (digits) => {
      expect(passesLuhn(digits)).toBe(false);
    });
  });

  describe('detectBrand', () => {
    it.each([
      ['4', 'visa'],
      ['4111111111111111', 'visa'],
      ['51', 'mastercard'],
      ['5555555555554444', 'mastercard'],
      ['2221000000000009', 'mastercard'],
      ['2720990000000007', 'mastercard'],
      ['34', 'amex'],
      ['378282246310005', 'amex'],
      ['6011111111111117', 'other'],
      ['', null],
      ['6', null],
    ])('%j is %j', (digits, brand) => {
      expect(detectBrand(digits)).toBe(brand);
    });
  });

  describe('formatCardNumber', () => {
    it('spaces Visa and Mastercard in blocks of 4 and drops anything but digits', () => {
      expect(formatCardNumber('4242000000000034')).toBe('4242 0000 0000 0034');
      expect(formatCardNumber('4242-0000 00a0')).toBe('4242 0000 000');
      expect(formatCardNumber('42420')).toBe('4242 0');
    });

    it('spaces Amex 4-6-5', () => {
      expect(formatCardNumber('378282246310005')).toBe('3782 822463 10005');
    });

    it('stops at 19 digits', () => {
      expect(formatCardNumber('1'.repeat(25)).replace(/ /g, '')).toHaveLength(19);
    });

    it('keeps the caret after the same digit when reformatting', () => {
      // Typed "9" after "4242" (caret at 5) in "42429": formatted "4242 9", caret after the 9.
      expect(caretAfterFormat('42429', 5, '4242 9')).toBe(6);
      expect(caretAfterFormat('4242 0000', 2, '4242 0000')).toBe(2);
      expect(caretAfterFormat('', 0, '')).toBe(0);
    });
  });

  describe('expiry', () => {
    it('adds the slash after the month and pads a single month digit above 1', () => {
      expect(formatExpiry('1')).toBe('1');
      expect(formatExpiry('12')).toBe('12/');
      expect(formatExpiry('1229')).toBe('12/29');
      expect(formatExpiry('4')).toBe('04/');
      expect(formatExpiry('12/', true)).toBe('12');
    });

    it('parses MM/YY into a four-digit year and rejects bad months', () => {
      expect(parseExpiry('08/29')).toEqual({ month: 8, year: 2029 });
      expect(parseExpiry('13/29')).toBeNull();
      expect(parseExpiry('0829')).toBeNull();
    });

    it('treats a card as valid through the last day of its month', () => {
      const now = new Date('2026-11-30T23:00:00Z');
      expect(isExpired({ month: 11, year: 2026 }, now)).toBe(false);
      expect(isExpired({ month: 10, year: 2026 }, now)).toBe(true);
      expect(isExpired({ month: 1, year: 2025 }, now)).toBe(true);
    });

    it('validates format and expiry', () => {
      const validate = expiryValidator(() => new Date('2026-11-20T00:00:00Z'));
      expect(validate(control('11/26'))).toBeNull();
      expect(validate(control('10/26'))).toEqual({ expired: true });
      expect(validate(control('1/2'))).toEqual({ expiryFormat: true });
      expect(validate(control(''))).toBeNull();
    });
  });

  it('flags a number that fails Luhn or has the wrong length', () => {
    expect(cardNumberValidator(control('4111 1111 1111 1111'))).toBeNull();
    expect(cardNumberValidator(control('4111 1111 1111 1112'))).toEqual({ luhn: true });
    expect(cardNumberValidator(control('4111 1111'))).toEqual({ cardLength: true });
  });

  it('wants 4 CVC digits for Amex and 3 otherwise', () => {
    expect(cvcLength('amex')).toBe(4);
    expect(cvcLength('visa')).toBe(3);
    expect(cvcLength(null)).toBe(3);
  });
});
