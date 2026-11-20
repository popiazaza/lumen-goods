import { AbstractControl, ValidationErrors } from '@angular/forms';

/**
US states and DC, as [code, name]: Lumen ships to the US only at launch (A-51).
*/
export const US_STATES: readonly (readonly [string, string])[] = [
  ['AL', 'Alabama'],
  ['AK', 'Alaska'],
  ['AZ', 'Arizona'],
  ['AR', 'Arkansas'],
  ['CA', 'California'],
  ['CO', 'Colorado'],
  ['CT', 'Connecticut'],
  ['DE', 'Delaware'],
  ['DC', 'District of Columbia'],
  ['FL', 'Florida'],
  ['GA', 'Georgia'],
  ['HI', 'Hawaii'],
  ['ID', 'Idaho'],
  ['IL', 'Illinois'],
  ['IN', 'Indiana'],
  ['IA', 'Iowa'],
  ['KS', 'Kansas'],
  ['KY', 'Kentucky'],
  ['LA', 'Louisiana'],
  ['ME', 'Maine'],
  ['MD', 'Maryland'],
  ['MA', 'Massachusetts'],
  ['MI', 'Michigan'],
  ['MN', 'Minnesota'],
  ['MS', 'Mississippi'],
  ['MO', 'Missouri'],
  ['MT', 'Montana'],
  ['NE', 'Nebraska'],
  ['NV', 'Nevada'],
  ['NH', 'New Hampshire'],
  ['NJ', 'New Jersey'],
  ['NM', 'New Mexico'],
  ['NY', 'New York'],
  ['NC', 'North Carolina'],
  ['ND', 'North Dakota'],
  ['OH', 'Ohio'],
  ['OK', 'Oklahoma'],
  ['OR', 'Oregon'],
  ['PA', 'Pennsylvania'],
  ['RI', 'Rhode Island'],
  ['SC', 'South Carolina'],
  ['SD', 'South Dakota'],
  ['TN', 'Tennessee'],
  ['TX', 'Texas'],
  ['UT', 'Utah'],
  ['VT', 'Vermont'],
  ['VA', 'Virginia'],
  ['WA', 'Washington'],
  ['WV', 'West Virginia'],
  ['WI', 'Wisconsin'],
  ['WY', 'Wyoming'],
];

const STATE_NAMES = /* @__PURE__ */ new Map(US_STATES);

export const stateName = (code: string): string => STATE_NAMES.get(code) ?? code;

/**
ZIP (97209) or ZIP+4 (97209-1234).
*/
export const ZIP_PATTERN = /^\d{5}(?:-\d{4})?$/u;

export const usStateValidator = (control: AbstractControl<string>): ValidationErrors | null => {
  const value = control.value?.trim().toUpperCase();
  return !value || STATE_NAMES.has(value) ? null : { state: true };
};

export const zipValidator = (control: AbstractControl<string>): ValidationErrors | null => {
  const value = control.value?.trim();
  return !value || ZIP_PATTERN.test(value) ? null : { zip: true };
};
