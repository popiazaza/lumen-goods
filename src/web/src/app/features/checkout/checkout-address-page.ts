import { Component, Injector, afterNextRender, inject, signal, viewChild } from '@angular/core';
import {
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidatorFn,
  Validators,
} from '@angular/forms';
import { MatButton } from '@angular/material/button';
import { MatError, MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { Router, RouterLink } from '@angular/router';
import { CheckoutStore } from '../../core/checkout/checkout-store';
import { ErrorSummary, FormProblem } from '../../shared/forms/error-summary';
import { CheckoutSteps } from './checkout-steps';
import { US_STATES, usStateValidator, zipValidator } from './us-address';

type AddressField = 'name' | 'line1' | 'line2' | 'city' | 'region' | 'postalCode';

/** The API's limits (`ShippingAddressDto`). */
const MAX_LENGTH: Readonly<Record<AddressField, number>> = {
  name: 100,
  line1: 200,
  line2: 200,
  city: 100,
  region: 100,
  postalCode: 20,
};

const LABELS: Readonly<Record<AddressField, string>> = {
  name: 'Full name',
  line1: 'Address line 1',
  line2: 'Address line 2',
  city: 'City',
  region: 'State',
  postalCode: 'ZIP code',
};

function text(field: AddressField, required: boolean, ...more: ValidatorFn[]): FormControl<string> {
  return new FormControl('', {
    nonNullable: true,
    validators: [
      ...(required ? [Validators.required] : []),
      Validators.maxLength(MAX_LENGTH[field]),
      ...more,
    ],
  });
}

/** Checkout step 1 (`/checkout/address`): where to ship, US only. */
@Component({
  selector: 'app-checkout-address-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatButton,
    MatError,
    MatFormField,
    MatHint,
    MatInput,
    MatLabel,
    CheckoutSteps,
    ErrorSummary,
  ],
  templateUrl: './checkout-address-page.html',
  styleUrl: './checkout.scss',
})
export class CheckoutAddressPage {
  private readonly store = inject(CheckoutStore);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly summary = viewChild(ErrorSummary);

  protected readonly states = US_STATES;
  protected readonly problems = signal<readonly FormProblem[]>([]);

  protected readonly form = new FormGroup({
    name: text('name', true),
    line1: text('line1', true),
    line2: text('line2', false),
    city: text('city', true),
    region: text('region', true, usStateValidator),
    postalCode: text('postalCode', true, zipValidator),
  });

  constructor() {
    // Back from a later step (or a refresh) shows what was entered.
    const saved = this.store.address();
    if (saved) {
      this.form.setValue({
        name: saved.name,
        line1: saved.line1,
        line2: saved.line2 ?? '',
        city: saved.city,
        region: saved.region,
        postalCode: saved.postalCode,
      });
    }
  }

  protected errorFor(field: AddressField): string {
    const control = this.form.controls[field];
    if (control.hasError('required')) {
      return field === 'region' ? 'Select a state' : `Enter ${LABELS[field].toLowerCase()}`;
    }
    if (control.hasError('maxlength')) {
      return `${LABELS[field]} must be ${MAX_LENGTH[field]} characters or fewer`;
    }
    if (control.hasError('zip')) {
      return 'Enter a 5-digit ZIP code, like 97209 or 97209-1234';
    }
    if (control.hasError('state')) {
      return 'Select a state';
    }
    return '';
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      const fields = Object.keys(this.form.controls) as AddressField[];
      this.problems.set(
        fields
          .filter((field) => this.form.controls[field].invalid)
          .map((field) => ({ fieldId: `address-${field}`, message: this.errorFor(field) })),
      );
      afterNextRender({ write: () => this.summary()?.focus() }, { injector: this.injector });
      return;
    }
    this.problems.set([]);
    const value = this.form.getRawValue();
    this.store.setAddress({
      name: value.name.trim(),
      line1: value.line1.trim(),
      line2: value.line2.trim() || null,
      city: value.city.trim(),
      region: value.region.trim().toUpperCase(),
      postalCode: value.postalCode.trim(),
      country: 'US',
    });
    void this.router.navigate(['/checkout/shipping']);
  }
}
