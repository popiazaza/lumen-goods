import { Component, computed, inject, input } from '@angular/core';
import { MatIcon } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { CHECKOUT_STEPS, CheckoutStep, CheckoutStore } from '../../core/checkout/checkout-store';

const LABELS: Readonly<Record<CheckoutStep, string>> = {
  address: 'Address',
  shipping: 'Shipping',
  review: 'Review',
  payment: 'Payment',
};

/**
 * Checkout progress: a list of the four steps, URL-driven (each step is a route). Steps the
 * shopper may open are links; the current one has `aria-current="step"`; done ones say so in
 * text, not colour alone. Changing an earlier step after Place order starts a new order (the
 * store renews the checkout key); going back without changes returns the same one.
 */
@Component({
  selector: 'app-checkout-steps',
  imports: [RouterLink, MatIcon],
  templateUrl: './checkout-steps.html',
  styleUrl: './checkout-steps.scss',
})
export class CheckoutSteps {
  private readonly store = inject(CheckoutStore);

  readonly current = input.required<CheckoutStep>();

  protected readonly steps = CHECKOUT_STEPS;

  protected readonly index = computed(() => CHECKOUT_STEPS.indexOf(this.current()));

  protected readonly items = computed(() => {
    const reachable = this.store.reachable();
    const index = this.index();
    return CHECKOUT_STEPS.map((id, n) => {
      const isCurrent = n === index;
      return {
        id,
        label: LABELS[id],
        current: isCurrent,
        done: n < index,
        link: !isCurrent && reachable.has(id),
      };
    });
  });
}
