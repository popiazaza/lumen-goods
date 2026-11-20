import { Routes } from '@angular/router';

/** One route per checkout step so Back, refresh and deep links keep the shopper's place. */
export const checkoutRoutes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'address' },
  {
    path: 'address',
    title: 'Shipping address — Checkout',
    loadComponent: () => import('./checkout-address-page').then((m) => m.CheckoutAddressPage),
  },
  {
    path: 'shipping',
    title: 'Shipping method — Checkout',
    loadComponent: () => import('./checkout-shipping-page').then((m) => m.CheckoutShippingPage),
  },
  {
    path: 'review',
    title: 'Review your order — Checkout',
    loadComponent: () => import('./checkout-review-page').then((m) => m.CheckoutReviewPage),
  },
  {
    path: 'payment',
    title: 'Payment — Checkout',
    loadComponent: () => import('./checkout-payment-page').then((m) => m.CheckoutPaymentPage),
  },
];
