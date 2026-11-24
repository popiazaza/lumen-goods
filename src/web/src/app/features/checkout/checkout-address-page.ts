import { LiveAnnouncer } from '@angular/cdk/a11y';
import { Component, Injector, afterNextRender, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidatorFn,
  Validators,
} from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { EMPTY, catchError, filter, map, switchMap } from 'rxjs';
import { HlmButtonImports } from '@spartan-ng/helm/button';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmNativeSelectImports } from '@spartan-ng/helm/native-select';
import { ShippingAddress } from '../../core/api/order.models';
import { OrdersApi } from '../../core/api/orders.api';
import { CheckoutStore } from '../../core/checkout/checkout-store';
import { ErrorSummary, FormProblem } from '../../shared/forms/error-summary';
import { CheckoutSteps } from './checkout-steps';
import { US_STATES, usStateValidator, zipValidator } from './us-address';

type AddressField = 'name' | 'line1' | 'line2' | 'city' | 'region' | 'postalCode';

/**
The API's limits (`ShippingAddressDto`).
*/
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

/**
Whether the field has a hint under it.
*/
const hasHint = (field: AddressField): boolean => field === 'line1' || field === 'line2';

const CAPITALISED_WORD = /^[A-Z][a-z]/u;

/**
"Full name" becomes "full name" inside a sentence; "ZIP code" keeps its capitals.
*/
const lowerFirst = (label: string): string =>
  CAPITALISED_WORD.test(label) ? `${label.charAt(0).toLowerCase()}${label.slice(1)}` : label;

const text = (
  field: AddressField,
  isRequired: boolean,
  ...more: ValidatorFn[]
): FormControl<string> =>
  new FormControl('', {
    nonNullable: true,
    validators: [
      ...(isRequired ? [Validators.required] : []),
      Validators.maxLength(MAX_LENGTH[field]),
      ...more,
    ],
  });

/**
Checkout step 1 (`/checkout/address`): where to ship, US only.
*/
@Component({
  selector: 'app-checkout-address-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    HlmButtonImports,
    HlmInputImports,
    HlmLabelImports,
    HlmNativeSelectImports,
    CheckoutSteps,
    ErrorSummary,
  ],
  templateUrl: './checkout-address-page.html',
  styleUrl: './checkout.scss',
})
export class CheckoutAddressPage {
  private readonly store = inject(CheckoutStore);
  private readonly orders = inject(OrdersApi);
  private readonly announcer = inject(LiveAnnouncer);
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

  /**
  The form was filled in from the customer's last order (not from this checkout).
  */
  protected readonly prefilled = signal(false);
  protected readonly prefilledText =
    'We filled in the address from your last order. Change anything that’s different.';

  constructor() {
    // Back from a later step (or a refresh) shows what was entered.
    const saved = this.store.address();
    if (saved) {
      this.fill(saved);
    } else {
      this.prefillFromLastOrder();
    }
  }

  private fill(address: ShippingAddress): void {
    this.form.setValue({
      name: address.name,
      line1: address.line1,
      line2: address.line2 ?? '',
      city: address.city,
      region: address.region,
      postalCode: address.postalCode,
    });
  }

  /**
   * A new checkout starts from where the customer's most recent order went, so they don't retype it. Only the form is filled: the store gets the address on Continue, as when typed. Nothing happens if they have no orders, the read fails, or they started typing first.
   */
  private prefillFromLastOrder(): void {
    this.orders
      .list(1, 1)
      .pipe(
        map((page) => page.items[0]?.number),
        filter((number): number is string => !!number),
        switchMap((number) => this.orders.get(number)),
        catchError(() => EMPTY),
        takeUntilDestroyed()
      )
      .subscribe((order) => {
        if (this.store.address() || this.form.dirty || order.shippingAddress.country !== 'US') {
          return;
        }
        this.fill(order.shippingAddress);
        this.prefilled.set(true);
        void this.announcer.announce(this.prefilledText, 'polite');
      });
  }

  /**
  A field's error shows once it is left or the form is submitted (which touches every field).
  */
  protected shows(field: AddressField): boolean {
    const control = this.form.controls[field];
    return control.invalid && control.touched;
  }

  /**
  The error when it shows, else the hint (as Material's form field did), for `aria-describedby`.
  */
  protected describedBy(field: AddressField): string | null {
    if (this.shows(field)) {
      return `address-${field}-error`;
    }
    return hasHint(field) ? `address-${field}-hint` : null;
  }

  protected errorFor(field: AddressField): string {
    const control = this.form.controls[field];
    if (control.hasError('required')) {
      return field === 'region' ? 'Select a state' : `Enter ${lowerFirst(LABELS[field])}`;
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
          .map((field) => ({ fieldId: `address-${field}`, message: this.errorFor(field) }))
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
