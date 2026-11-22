import { CurrencyPipe } from '@angular/common';
import { Component, computed, effect, inject, linkedSignal, untracked } from '@angular/core';
import { rxResource, takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatRadioButton, MatRadioGroup } from '@angular/material/radio';
import { Router, RouterLink } from '@angular/router';
import {
  CheckoutApi,
  CheckoutPreviewDto,
  SHIPPING_METHOD_NOT_AVAILABLE_PROBLEM,
} from '../../core/api/checkout.api';
import { OWN_SOURCE, ShippingMethod } from '../../core/api/order.models';
import { problemTypeOf } from '../../core/api/problem-details';
import { CheckoutStore } from '../../core/checkout/checkout-store';
import { CheckoutSteps } from './checkout-steps';
import type { Signal } from '@angular/core';

/**
 * Checkout step 2 (`/checkout/shipping`): one shipment per seller. Lumen's own items can go
 * Standard or Express; partner shipments are always Standard (BR-SHIP-01). The totals below are
 * the server's preview for the chosen method.
 */
@Component({
  selector: 'app-checkout-shipping-page',
  imports: [
    CurrencyPipe,
    ReactiveFormsModule,
    RouterLink,
    MatButton,
    MatIcon,
    MatRadioButton,
    MatRadioGroup,
    CheckoutSteps,
  ],
  templateUrl: './checkout-shipping-page.html',
  styleUrls: ['./checkout.scss', './checkout-shipping-page.scss'],
})
export class CheckoutShippingPage {
  private readonly api = inject(CheckoutApi);

  private readonly store = inject(CheckoutStore);

  private readonly router = inject(Router);

  /**
  The chosen method as a signal. Set in the constructor: it reads `method`, a template-facing field
  declared after the private ones.
  */
  private readonly chosen: Signal<ShippingMethod>;

  protected readonly ownSource = OWN_SOURCE;

  protected readonly method = new FormControl<ShippingMethod>(
    this.store.shippingMethod() ?? 'standard',
    { nonNullable: true }
  );

  protected readonly preview = rxResource({
    params: () => this.chosen(),
    stream: ({ params }) => this.api.preview(params),
  });

  /**
  The last preview, kept on screen while the next one loads so the page does not jump.
  */
  protected readonly shown = linkedSignal<
    CheckoutPreviewDto | undefined,
    CheckoutPreviewDto | undefined
  >({
    source: () => (this.preview.hasValue() ? this.preview.value() : undefined),
    computation: (next, previous) => next ?? previous?.value,
  });

  protected readonly updating = computed(() => this.preview.isLoading() && !!this.shown());

  protected readonly expressOffered = computed(
    () => this.shown()?.shippingOptions.some((option) => option.method === 'express') ?? false
  );

  protected readonly options = computed(() => this.shown()?.shippingOptions ?? []);

  constructor() {
    this.chosen = toSignal(this.method.valueChanges, { initialValue: this.method.value });
    // Saved as soon as it changes, not only on Continue, so Back to address and returning keeps
    // the choice (WCAG 3.3.7). The store compares the whole review when the key is used, so a
    // changed method still gets a new checkout key at Place order.
    this.method.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe((method) => this.store.setShippingMethod(method));
    // Express was chosen earlier but the cart no longer holds Lumen's own items: the API refuses
    // to price Express for it (422) rather than answering without the option, so fall back to
    // Standard, the only method such a cart has.
    effect(() => {
      const isRefused =
        problemTypeOf(this.preview.error()) === SHIPPING_METHOD_NOT_AVAILABLE_PROBLEM ||
        (!!this.shown() && !this.expressOffered());
      if (isRefused && this.chosen() === 'express') {
        untracked(() => this.method.setValue('standard'));
      }
    });
  }

  protected continue(): void {
    const preview = this.shown();
    if (!preview || this.preview.isLoading()) {
      return;
    }
    const method = this.expressOffered() ? this.method.value : 'standard';
    this.store.setShippingMethod(method);
    void this.router.navigate(['/checkout/review']);
  }
}
