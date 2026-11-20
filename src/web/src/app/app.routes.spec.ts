import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import {
  Route,
  Router,
  TitleStrategy,
  provideRouter,
  withComponentInputBinding,
} from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';
import { SessionApi } from './core/api/session.api';
import { CHECKOUT_STORAGE_KEY } from './core/checkout/checkout-store';
import { provideAppIcons } from './core/icons/app-icons';
import { PageTitleStrategy } from './core/routing/page-title-strategy';
import { RegisterPage } from './features/account/register-page';
import { SignInPage } from './features/account/sign-in-page';
import { AdminOrderDetailPage } from './features/admin/admin-order-detail-page';
import { AdminOrdersPage } from './features/admin/admin-orders-page';
import { AdminPartnersPage } from './features/admin/admin-partners-page';
import { AdminProductEditPage } from './features/admin/admin-product-edit-page';
import { AdminProductsPage } from './features/admin/admin-products-page';
import { CartPage } from './features/cart/cart-page';
import { CataloguePage } from './features/catalogue/catalogue-page';
import { CheckoutAddressPage } from './features/checkout/checkout-address-page';
import { CheckoutReviewPage } from './features/checkout/checkout-review-page';
import { CheckoutShippingPage } from './features/checkout/checkout-shipping-page';
import { CheckoutPaymentPage } from './features/checkout/payment/checkout-payment-page';
import { NotFoundPage } from './features/not-found/not-found-page';
import { OrderConfirmationPage } from './features/orders/order-confirmation-page';
import { OrderDetailPage } from './features/orders/order-detail-page';
import { OrderListPage } from './features/orders/order-list-page';
import { ProductDetailPage } from './features/products/product-detail-page';
import { CUSTOMER_SESSION, US_ADDRESS } from './testing/fixtures';

// Every page is imported here as well as lazily by the route table. The routes still load them
// through `loadComponent`, but each page's chunk is already evaluated when its test starts, so a
// test measures the routing, not how long the machine takes to fetch and run a chunk: the first
// lazy load of a heavy page used to blow the test timeout when the suite ran under load.

/** Flattens the route tree into absolute path patterns, e.g. "/orders/:id/confirmation". */
function pathPatterns(table: Route[], parent = ''): string[] {
  return table.flatMap((route) => {
    const path = [parent, route.path ?? ''].filter(Boolean).join('/');
    return [`/${path}`, ...pathPatterns(route.children ?? [], path)];
  });
}

describe('app routes', () => {
  it('define exactly the storefront and admin pages from the brief', () => {
    const patterns = [...new Set(pathPatterns(routes))].sort();

    expect(patterns).toEqual(
      [
        '/',
        '/products/:id',
        '/cart',
        '/sign-in',
        '/register',
        '/checkout',
        '/checkout/address',
        '/checkout/shipping',
        '/checkout/review',
        '/checkout/payment',
        '/orders',
        '/orders/:id',
        '/orders/:id/confirmation',
        '/admin',
        '/admin/products',
        '/admin/products/new',
        '/admin/products/:id',
        '/admin/orders',
        '/admin/orders/:id',
        '/admin/partners',
        '/**',
      ].sort(),
    );
  });

  // prettier-ignore
  const pages: { url: string; finalUrl: string; page: Type<unknown>; heading: string; title: string }[] = [
    { url: '/', finalUrl: '/', page: CataloguePage, heading: 'Shop', title: 'Shop' },
    { url: '/products/p-42', finalUrl: '/products/p-42', page: ProductDetailPage, heading: 'Product details', title: 'Product details' },
    { url: '/cart', finalUrl: '/cart', page: CartPage, heading: 'Your cart', title: 'Your cart' },
    { url: '/sign-in', finalUrl: '/sign-in', page: SignInPage, heading: 'Sign in', title: 'Sign in' },
    { url: '/register', finalUrl: '/register', page: RegisterPage, heading: 'Create an account', title: 'Create an account' },
    { url: '/checkout', finalUrl: '/checkout/address', page: CheckoutAddressPage, heading: 'Shipping address', title: 'Shipping address — Checkout' },
    { url: '/checkout/shipping', finalUrl: '/checkout/shipping', page: CheckoutShippingPage, heading: 'Shipping method', title: 'Shipping method — Checkout' },
    { url: '/checkout/review', finalUrl: '/checkout/review', page: CheckoutReviewPage, heading: 'Review your order', title: 'Review your order — Checkout' },
    { url: '/checkout/payment', finalUrl: '/checkout/payment', page: CheckoutPaymentPage, heading: 'Payment', title: 'Payment — Checkout' },
    { url: '/orders', finalUrl: '/orders', page: OrderListPage, heading: 'Your orders', title: 'Your orders' },
    { url: '/orders/1001', finalUrl: '/orders/1001', page: OrderDetailPage, heading: 'Order 1001', title: 'Order details' },
    { url: '/orders/1001/confirmation', finalUrl: '/orders/1001/confirmation', page: OrderConfirmationPage, heading: 'Order 1001', title: 'Order confirmation' },
    { url: '/admin', finalUrl: '/admin/products', page: AdminProductsPage, heading: 'Products', title: 'Products — Admin' },
    { url: '/admin/products/new', finalUrl: '/admin/products/new', page: AdminProductEditPage, heading: 'New product', title: 'New product — Admin' },
    { url: '/admin/products/p-7', finalUrl: '/admin/products/p-7', page: AdminProductEditPage, heading: 'Edit product', title: 'Edit product — Admin' },
    { url: '/admin/orders', finalUrl: '/admin/orders', page: AdminOrdersPage, heading: 'Orders', title: 'Orders — Admin' },
    { url: '/admin/orders/1001', finalUrl: '/admin/orders/1001', page: AdminOrderDetailPage, heading: 'Order 1001', title: 'Order details — Admin' },
    { url: '/admin/partners', finalUrl: '/admin/partners', page: AdminPartnersPage, heading: 'Partner status', title: 'Partner status — Admin' },
    { url: '/no-such-page', finalUrl: '/no-such-page', page: NotFoundPage, heading: 'Page not found', title: 'Page not found' },
  ];

  afterEach(() => sessionStorage.clear());

  it.each(pages)('$url lazy-loads "$heading"', async ({ url, finalUrl, page, heading, title }) => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes, withComponentInputBinding()),
        { provide: TitleStrategy, useClass: PageTitleStrategy },
        // Pages load data; the requests stay pending, so each page shows its loading heading.
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    // Checkout needs a signed-in customer and the earlier steps done (its guards).
    sessionStorage.setItem(
      CHECKOUT_STORAGE_KEY,
      JSON.stringify({
        checkoutKey: 'key-1',
        address: US_ADDRESS,
        shippingMethod: 'standard',
        orderNumber: 'LG-1',
      }),
    );
    const session = TestBed.inject(SessionApi).ready();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/auth/session').flush(CUSTOMER_SESSION);
    await session;
    const harness = await RouterTestingHarness.create();

    // Fails unless the route's `loadComponent` activated exactly this page.
    await harness.navigateByUrl(url, page);

    expect(TestBed.inject(Router).url).toBe(finalUrl);
    expect(harness.routeNativeElement?.querySelector('h1')?.textContent?.trim()).toBe(heading);
    expect(TestBed.inject(Title).getTitle()).toBe(`${title} — Lumen Goods`);
  });
});
