import { FormControl, FormGroup } from '@angular/forms';
import { cvcLength, detectBrand, digitsOf } from './card';

// The card form's fields and their messages.
// The payment page checks and sends the form and lists its problems in the error summary; the card fieldset shows the same messages under each field.

export type CardField = 'number' | 'name' | 'expiry' | 'cvc';

export type CardForm = FormGroup<Record<CardField, FormControl<string>>>;

/**
The API's messages for the fields it refused.
*/
export type CardFieldMessages = Partial<Record<CardField, string>>;

/**
The fields in the order the form shows them, which the error summary keeps.
*/
export const CARD_FIELDS: readonly CardField[] = ['number', 'name', 'expiry', 'cvc'];

/**
Each field's input id: the error summary's links move focus there.
*/
export const CARD_FIELD_IDS: Readonly<Record<CardField, string>> = {
  number: 'card-number',
  name: 'card-name',
  expiry: 'card-expiry',
  cvc: 'card-cvc',
};

const REQUIRED_MESSAGES: Readonly<Record<CardField, string>> = {
  number: 'Enter your card number',
  name: 'Enter the name on the card',
  expiry: 'Enter the expiry date',
  cvc: 'Enter the security code',
};

const INVALID_MESSAGES: Readonly<Record<Exclude<CardField, 'cvc'>, string>> = {
  number: 'Enter a valid card number: check it for typos',
  name: 'The name must be 100 characters or fewer',
  expiry: 'Enter the expiry date as MM/YY, like 08/29',
};

/**
Why a field is invalid: the API's own words for a field it refused, otherwise the form's check.
*/
export const cardFieldError = (
  form: CardForm,
  field: CardField,
  serverErrors: CardFieldMessages
): string => {
  const control = form.controls[field];
  const server = serverErrors[field];
  if (server && control.hasError('server')) {
    return server;
  }
  if (control.hasError('required')) {
    return REQUIRED_MESSAGES[field];
  }
  if (field === 'expiry' && control.hasError('expired')) {
    return 'This card has expired';
  }
  if (field === 'cvc') {
    const digits = cvcLength(detectBrand(digitsOf(form.controls.number.value)));
    return `Enter the ${digits}-digit security code`;
  }
  return INVALID_MESSAGES[field];
};

/**
The message of every invalid field.
*/
export const cardFieldErrors = (
  form: CardForm,
  serverErrors: CardFieldMessages
): CardFieldMessages =>
  Object.fromEntries(
    CARD_FIELDS.filter((field) => form.controls[field].invalid).map((field) => [
      field,
      cardFieldError(form, field, serverErrors),
    ])
  );
