import { FormControl } from '@angular/forms';
import { passwordProblems, passwordRulesValidator } from './password-rules';

describe('password rules', () => {
  it('accepts a password that meets every rule', () => {
    expect(passwordProblems('Shopper!2026')).toStrictEqual([]);
  });

  it('lists each rule a password misses, as the API words it', () => {
    expect(passwordProblems('abcdefgh')).toStrictEqual([
      'Add an upper-case letter (A-Z).',
      'Add a number (0-9).',
      'Add a symbol, such as ! or #.',
    ]);
    expect(passwordProblems('ABCDEFG1!')).toStrictEqual(['Add a lower-case letter (a-z).']);
  });

  it('counts a space as a symbol and an accented letter as no symbol, as ASP.NET Identity does', () => {
    expect(passwordProblems('Shopper 2026')).toStrictEqual([]);
    expect(passwordProblems('Shoppé2026')).toStrictEqual(['Add a symbol, such as ! or #.']);
  });

  it('leaves an empty value to the required validator', () => {
    expect(passwordRulesValidator(new FormControl(''))).toBeNull();
    expect(passwordRulesValidator(new FormControl('Ab1!'))).toStrictEqual({
      passwordRules: ['Use at least 8 characters.'],
    });
  });
});
