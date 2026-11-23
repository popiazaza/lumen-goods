import { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

/**
The API's password rules for new accounts (`PasswordRules` in Lumen.Api/Identity, A-50), stated here for the hint and
an early check. The API checks again and its answer wins; keep the two in step.
*/
export const MIN_PASSWORD_LENGTH = 8;

export const PASSWORD_HINT = `At least ${MIN_PASSWORD_LENGTH} characters, with an upper-case letter, a lower-case letter, a number and a symbol`;

interface PasswordRule {
  readonly test: RegExp;
  readonly message: string;
}

// Same tests as ASP.NET Identity's PasswordValidator (ASCII letters and digits, as JS `\d` is; a symbol is anything
// but a letter or a decimal digit) and the same wording as the API's PlainIdentityErrorDescriber, so both read alike.
const RULES: readonly PasswordRule[] = [
  { test: /[A-Z]/u, message: 'Add an upper-case letter (A-Z).' },
  { test: /[a-z]/u, message: 'Add a lower-case letter (a-z).' },
  { test: /\d/u, message: 'Add a number (0-9).' },
  { test: /[^\p{L}\p{Nd}]/u, message: 'Add a symbol, such as ! or #.' },
];

/**
What a password still misses, one plain sentence per rule; empty when it meets them all.
*/
export const passwordProblems = (password: string): string[] => [
  ...(password.length < MIN_PASSWORD_LENGTH
    ? [`Use at least ${MIN_PASSWORD_LENGTH} characters.`]
    : []),
  ...RULES.filter((rule) => !rule.test.test(password)).map((rule) => rule.message),
];

/**
`{ passwordRules: string[] }` with the missing rules, or null. An empty value is left to `Validators.required`.
*/
export const passwordRulesValidator: ValidatorFn = (
  control: AbstractControl<string>
): ValidationErrors | null => {
  if (!control.value) {
    return null;
  }
  const problems = passwordProblems(control.value);
  return problems.length > 0 ? { passwordRules: problems } : null;
};
