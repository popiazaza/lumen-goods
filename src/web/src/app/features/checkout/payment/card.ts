import { AbstractControl, ValidationErrors } from '@angular/forms';
import { CardBrand } from '../../../core/api/order.models';

// Card-form helpers for inline feedback (UX only): the server validates the card again before the
// gateway sees it (BR-PAY-02), and its answer wins.

export const MIN_CARD_DIGITS = 12;
export const MAX_CARD_DIGITS = 19;

export function digitsOf(value: string | null | undefined): string {
  return (value ?? '').replace(/\D/g, '');
}

/** Issuer prefixes, as the server detects them: Visa 4; Mastercard 51-55, 2221-2720; Amex 34, 37. */
export function detectBrand(digits: string): CardBrand | null {
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
  if ((first2 >= 51 && first2 <= 55) || (digits.length >= 4 && first4 >= 2221 && first4 <= 2720)) {
    return 'mastercard';
  }
  return digits.length >= 4 ? 'other' : null;
}

/** The Luhn (mod 10) check: from the right, double every second digit; the sum ends in 0. */
export function passesLuhn(digits: string): boolean {
  if (!/^\d+$/.test(digits)) {
    return false;
  }
  let sum = 0;
  let double = false;
  for (let i = digits.length - 1; i >= 0; i--) {
    let digit = digits.charCodeAt(i) - 48;
    if (double) {
      digit *= 2;
      if (digit > 9) {
        digit -= 9;
      }
    }
    sum += digit;
    double = !double;
  }
  return sum % 10 === 0;
}

/** Groups digits as printed on the card: 4-6-5 for Amex, otherwise blocks of 4. */
export function formatCardNumber(value: string): string {
  const digits = digitsOf(value).slice(0, MAX_CARD_DIGITS);
  const groups =
    detectBrand(digits) === 'amex'
      ? [digits.slice(0, 4), digits.slice(4, 10), digits.slice(10, 15)]
      : (digits.match(/.{1,4}/g) ?? []);
  return groups.filter(Boolean).join(' ');
}

/**
 * Where the caret belongs after reformatting: after the same number of digits as before, so typing
 * or deleting in the middle does not throw it to the end.
 */
export function caretAfterFormat(raw: string, caret: number, formatted: string): number {
  const digitsBefore = digitsOf(raw.slice(0, caret)).length;
  if (digitsBefore === 0) {
    return 0;
  }
  let seen = 0;
  for (let i = 0; i < formatted.length; i++) {
    if (/\d/.test(formatted[i]) && ++seen === digitsBefore) {
      return i + 1;
    }
  }
  return formatted.length;
}

export function cvcLength(brand: CardBrand | null): number {
  return brand === 'amex' ? 4 : 3;
}

/** "MM/YY" as the shopper types it: digits only, slash added after the month. */
export function formatExpiry(value: string, deleting = false): string {
  let digits = digitsOf(value).slice(0, 4);
  // "4" can only be April: write it as "04".
  if (digits.length === 1 && Number(digits) > 1) {
    digits = `0${digits}`;
  }
  if (digits.length < 2 || (digits.length === 2 && deleting)) {
    return digits;
  }
  return `${digits.slice(0, 2)}/${digits.slice(2)}`;
}

export interface Expiry {
  month: number;
  /** Four digits. */
  year: number;
}

export function parseExpiry(value: string): Expiry | null {
  const match = /^(\d{2})\s*\/\s*(\d{2})$/.exec(value.trim());
  if (!match) {
    return null;
  }
  const month = Number(match[1]);
  return month >= 1 && month <= 12 ? { month, year: 2000 + Number(match[2]) } : null;
}

/** A card works through the last day of its expiry month. */
export function isExpired(expiry: Expiry, now: Date): boolean {
  const year = now.getUTCFullYear();
  return expiry.year < year || (expiry.year === year && expiry.month < now.getUTCMonth() + 1);
}

export function cardNumberValidator(control: AbstractControl<string>): ValidationErrors | null {
  const digits = digitsOf(control.value);
  if (!digits) {
    return null;
  }
  if (digits.length < MIN_CARD_DIGITS || digits.length > MAX_CARD_DIGITS) {
    return { cardLength: true };
  }
  return passesLuhn(digits) ? null : { luhn: true };
}

/** Validates MM/YY and that the month has not passed; `now` is injectable for tests. */
export function expiryValidator(now: () => Date = () => new Date()) {
  return (control: AbstractControl<string>): ValidationErrors | null => {
    if (!control.value) {
      return null;
    }
    const expiry = parseExpiry(control.value);
    if (!expiry) {
      return { expiryFormat: true };
    }
    return isExpired(expiry, now()) ? { expired: true } : null;
  };
}
