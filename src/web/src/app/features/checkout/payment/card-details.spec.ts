import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { CardDetails } from './card-details';
import { CardFieldMessages, CardForm } from './card-form';

const requiredField = () =>
  new FormControl('', { nonNullable: true, validators: [Validators.required] });

/**
The payment page's part: it owns the form and marks it touched on Pay.
*/
@Component({
  selector: 'app-card-details-host',
  imports: [CardDetails, ReactiveFormsModule],
  template: `
    <form [formGroup]="form">
      <app-card-details [form]="form" [serverErrors]="serverErrors()" />
    </form>
  `,
})
class CardDetailsHost {
  readonly form: CardForm = new FormGroup({
    number: requiredField(),
    name: requiredField(),
    expiry: requiredField(),
    cvc: requiredField(),
  });

  readonly serverErrors = signal<CardFieldMessages>({});
}

describe('CardDetails', () => {
  let fixture: ComponentFixture<CardDetailsHost>;

  const host = () => fixture.nativeElement as HTMLElement;
  const errors = () =>
    [...host().querySelectorAll('.field-error')].map((error) => error.textContent?.trim());
  /**
  The text a field is described by (its hint, or its error once shown).
  */
  const descriptionOf = (id: string) =>
    host()
      .querySelector(`#${id}`)
      ?.getAttribute('aria-describedby')
      ?.split(' ')
      .map((describedBy) => host().querySelector(`#${describedBy}`)?.textContent?.trim())
      .join(' ');
  const numberField = () =>
    host().querySelector<HTMLInputElement>('#card-number') as HTMLInputElement;
  const typeNumber = (value: string): void => {
    const field = numberField();
    field.value = value;
    field.dispatchEvent(new Event('input'));
    field.dispatchEvent(new Event('blur'));
  };

  beforeEach(async () => {
    fixture = TestBed.createComponent(CardDetailsHost);
    await fixture.whenStable();
  });

  it('shows every field’s error when the page marks the form touched', async () => {
    // Leaving the number empty touched it, and with it the form; Pay then touches the other fields.
    typeNumber('');
    await fixture.whenStable();

    fixture.componentInstance.form.markAllAsTouched();
    await fixture.whenStable();

    expect(errors()).toStrictEqual([
      'Enter your card number',
      'Enter the name on the card',
      'Enter the expiry date',
      'Enter the security code',
    ]);
  });

  it('shows the API’s message for a field it refused', async () => {
    const { form, serverErrors } = fixture.componentInstance;
    form.setValue({ number: '4111 1111 1111 1111', name: 'Ada', expiry: '08/29', cvc: '123' });
    form.markAllAsTouched();

    form.controls.number.setErrors({ server: true });
    serverErrors.set({ number: 'This card can’t be used.' });
    await fixture.whenStable();

    expect(errors()).toStrictEqual(['This card can’t be used.']);
  });

  it('names an Amex card and asks for its 4-digit code on the front', async () => {
    typeNumber('378282246310005');
    await fixture.whenStable();

    expect({
      number: numberField().value,
      brand: host().querySelector('.brand')?.textContent?.trim(),
      cvcHint: descriptionOf('card-cvc'),
    }).toStrictEqual({
      number: '3782 822463 10005',
      brand: 'American Express',
      cvcHint: '4 digits on the front',
    });
  });
});
