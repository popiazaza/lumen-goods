import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { CHECKOUT_STEPS, CheckoutStep, CheckoutStore } from '../../core/checkout/checkout-store';

/**
 * A checkout step opens only when the steps before it are done (shipping needs an address, review
 * a shipping method, payment a placed order); otherwise it sends the shopper to the first step
 * still to do. The payment step also opens for `?order=` (paying an order from its detail page).
 */
export function checkoutStepGuard(step: CheckoutStep): CanActivateFn {
  return (route) => {
    const store = inject(CheckoutStore);
    if (store.reachable().has(step)) {
      return true;
    }
    if (step === 'payment' && route.queryParamMap.get('order')) {
      return true;
    }
    const firstToDo =
      CHECKOUT_STEPS.filter((candidate) => store.reachable().has(candidate)).findLast(
        (candidate) => candidate !== 'payment'
      ) ?? 'address';
    return inject(Router).createUrlTree(['/checkout', firstToDo]);
  };
}
