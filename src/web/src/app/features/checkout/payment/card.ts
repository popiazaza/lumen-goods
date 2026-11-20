import { AbstractControl, ValidationErrors } from '@angular/forms';
import { CardBrand } from '../../../core/api/order.models';

// Card-form helpers for inline feedback (UX only): the server validates the card again before the
// gateway sees it (BR-PAY-02), and its answer wins.

export const MIN_CARD_DIGITS = 12;
export const MAX_CARD_DIGITS = 19;

export const digitsOf = (value: string | null | undefined): string =>
  value?.replaceAll(/\D/gu, '') ?? '';

/**
Issuer prefixes, as the server detects them: Visa 4; Mastercard 51-55, 2221-2720; Amex 34, 37.
*/
export const detectBrand = (digits: string): CardBrand | null => {
  if (digits.length < 2) {
    return digits.startsWith('4') ? 'visa' : null;
  }
  const first2 = Number(digits.slice(0, 2));
  const first4 = Number(digits.slice(0, 4).padEnd(4, '0'));
  if (digits.startsWith('4')) {
    return 'visa';
  }
  if (first2 === 34 || first2 === 37) {
    return 'amex';
  }
  const isMastercard5Series = first2 >= 51 && first2 <= 55;
  const isMastercard2Series = digits.length >= 4 && first4 >= 2221 && first4 <= 2720;
  if (isMastercard5Series || isMastercard2Series) {
    return 'mastercard';
  }
  return digits.length >= 4 ? 'other' : null;
};

/**
The Luhn (mod 10) check: from the right, double every second digit; the sum ends in 0.
*/
export const isLuhnValid = (digits: string): boolean => {
  if (!/^\d+$/u.test(digits)) {
    return false;
  }
  let sum = 0;
  let isDouble = false;
  for (let i = digits.length - 1; i >= 0; i -= 1) {
    let digit = Number(digits[i]);
    if (isDouble) {
      digit *= 2;
      if (digit > 9) {
        digit -= 9;
      }
    }
    sum += digit;
    isDouble = !isDouble;
  }
  return sum % 10 === 0;
};

/**
Groups digits as printed on the card: 4-6-5 for Amex, otherwise blocks of 4.
*/
export const formatCardNumber = (value: string): string => {
  const digits = digitsOf(value).slice(0, MAX_CARD_DIGITS);
  const groups =
    detectBrand(digits) === 'amex'
      ? [digits.slice(0, 4), digits.slice(4, 10), digits.slice(10, 15)]
      : (digits.match(/.{1,4}/gu) ?? []);
  return groups.filter(Boolean).join(' ');
};

/**
 * Where the caret belongs after reformatting: after the same number of digits as before, so typing or deleting in the middle does not throw it to the end.
 */
export const caretAfterFormat = (raw: string, caret: number, formatted: string): number => {
  const digitsBefore = digitsOf(raw.slice(0, caret)).length;
  if (digitsBefore === 0) {
    return 0;
  }
  let seen = 0;
  for (const digit of formatted.matchAll(/\d/gu)) {
    seen += 1;
    if (seen === digitsBefore) {
      return digit.index + 1;
    }
  }
  return formatted.length;
};

export const cvcLength = (brand: CardBrand | null): number => (brand === 'amex' ? 4 : 3);

/**
"MM/YY" as the shopper types it: digits only, slash added after the month.
*/
export const formatExpiry = (value: string, isDeleting = false): string => {
  let digits = digitsOf(value).slice(0, 4);
  // "4" can only be April: write it as "04".
  if (digits.length === 1 && Number(digits) > 1) {
    digits = `0${digits}`;
  }
  return digits.length < 2 || (isDeleting && digits.length === 2)
    ? digits
    : `${digits.slice(0, 2)}/${digits.slice(2)}`;
};

export interface Expiry {
  month: number;
  /**
  Four digits.
  */
  year: number;
}

export const parseExpiry = (value: string): Expiry | null => {
  const { month, year } =
    /^(?<month>\d{2})\s*\/\s*(?<year>\d{2})$/u.exec(value.trim())?.groups ?? {};
  if (month === undefined || year === undefined) {
    return null;
  }
  const monthNumber = Number(month);
  return monthNumber >= 1 && monthNumber <= 12
    ? { month: monthNumber, year: 2000 + Number(year) }
    : null;
};

/**
A card works through the last day of its expiry month.
*/
export const isExpired = (expiry: Expiry, now: Date): boolean => {
  const year = now.getUTCFullYear();
  return expiry.year < year || (expiry.year === year && expiry.month < now.getUTCMonth() + 1);
};

export const cardNumberValidator = (control: AbstractControl<string>): ValidationErrors | null => {
  const digits = digitsOf(control.value);
  if (!digits) {
    return null;
  }
  if (digits.length < MIN_CARD_DIGITS || digits.length > MAX_CARD_DIGITS) {
    return { cardLength: true };
  }
  return isLuhnValid(digits) ? null : { luhn: true };
};

/**
Validates MM/YY and that the month has not passed; `now` is injectable for tests.
*/
export const expiryValidator =
  (now: () => Date = () => new Date()) =>
  (control: AbstractControl<string>): ValidationErrors | null => {
    if (!control.value) {
      return null;
    }
    const expiry = parseExpiry(control.value);
    if (!expiry) {
      return { expiryFormat: true };
    }
    return isExpired(expiry, now()) ? { expired: true } : null;
  };
