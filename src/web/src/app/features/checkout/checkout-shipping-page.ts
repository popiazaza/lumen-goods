import { CurrencyPipe } from '@angular/common';
import { Component, computed, effect, inject, linkedSignal, untracked } from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatRadioButton, MatRadioGroup } from '@angular/material/radio';
import { Router, RouterLink } from '@angular/router';
import { CheckoutApi, CheckoutPreviewDto } from '../../core/api/checkout.api';
import { OWN_SOURCE, ShippingMethod } from '../../core/api/order.models';
import { CheckoutStore } from '../../core/checkout/checkout-store';
import { CheckoutSteps } from './checkout-steps';

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
  styleUrl: './checkout.scss',
  styles: `
    .items {
      margin: 0 0 var(--lg-space-3);
      padding-left: var(--lg-space-6);
    }

    .methods {
      display: flex;
      flex-direction: column;
    }

    .method-fee {
      margin-left: var(--lg-space-2);
    }

    .fixed-method {
      margin: 0;
    }
  `,
})
export class CheckoutShippingPage {
  private readonly api = inject(CheckoutApi);
  private readonly store = inject(CheckoutStore);
  private readonly router = inject(Router);

  protected readonly ownSource = OWN_SOURCE;
  protected readonly method = new FormControl<ShippingMethod>(
    this.store.shippingMethod() ?? 'standard',
    { nonNullable: true },
  );
  private readonly chosen = toSignal(this.method.valueChanges, {
    initialValue: this.method.value,
  });

  protected readonly preview = rxResource({
    params: () => this.chosen(),
    stream: ({ params }) => this.api.preview(params),
  });
  /** The last preview, kept on screen while the next one loads so the page does not jump. */
  protected readonly shown = linkedSignal<
    CheckoutPreviewDto | undefined,
    CheckoutPreviewDto | undefined
  >({
    source: () => (this.preview.hasValue() ? this.preview.value() : undefined),
    computation: (next, previous) => next ?? previous?.value,
  });
  protected readonly updating = computed(() => this.preview.isLoading() && !!this.shown());

  protected readonly expressOffered = computed(
    () => this.shown()?.shippingOptions.some((option) => option.method === 'express') ?? false,
  );
  protected readonly options = computed(() => this.shown()?.shippingOptions ?? []);

  constructor() {
    // Express was chosen earlier but the cart no longer holds Lumen's own items.
    effect(() => {
      if (this.shown() && !this.expressOffered()) {
        untracked(() => {
          if (this.method.value === 'express') {
            this.method.setValue('standard');
          }
        });
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
