import { Routes } from '@angular/router';
import { signedInGuard } from '../../core/routing/signed-in.guard';
import { checkoutStepGuard } from './checkout-step.guard';

/**
 * One route per checkout step so Back, refresh and deep links keep the shopper's place. Signing
 * in is required (brief, page 3); the step guard stops a step opening before the ones it needs.
 */
export const checkoutRoutes: Routes = [
  {
    path: '',
    canActivateChild: [signedInGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'address' },
      {
        path: 'address',
        title: 'Shipping address — Checkout',
        loadComponent: () => import('./checkout-address-page').then((m) => m.CheckoutAddressPage),
      },
      {
        path: 'shipping',
        title: 'Shipping method — Checkout',
        canActivate: [checkoutStepGuard('shipping')],
        loadComponent: () => import('./checkout-shipping-page').then((m) => m.CheckoutShippingPage),
      },
      {
        path: 'review',
        title: 'Review your order — Checkout',
        canActivate: [checkoutStepGuard('review')],
        loadComponent: () => import('./checkout-review-page').then((m) => m.CheckoutReviewPage),
      },
      {
        path: 'payment',
        title: 'Payment — Checkout',
        canActivate: [checkoutStepGuard('payment')],
        loadComponent: () =>
          import('./payment/checkout-payment-page').then((m) => m.CheckoutPaymentPage),
      },
    ],
  },
];
