import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
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
import { PageTitleStrategy } from './core/routing/page-title-strategy';

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
        '/admin/orders',
        '/admin/orders/:id',
        '/admin/partners',
        '/**',
      ].sort(),
    );
  });

  // prettier-ignore
  const pages = [
    { url: '/', finalUrl: '/', heading: 'Shop', title: 'Shop' },
    { url: '/products/p-42', finalUrl: '/products/p-42', heading: 'Product details', title: 'Product details' },
    { url: '/cart', finalUrl: '/cart', heading: 'Your cart', title: 'Your cart' },
    { url: '/sign-in', finalUrl: '/sign-in', heading: 'Sign in', title: 'Sign in' },
    { url: '/register', finalUrl: '/register', heading: 'Create an account', title: 'Create an account' },
    { url: '/checkout', finalUrl: '/checkout/address', heading: 'Shipping address', title: 'Shipping address — Checkout' },
    { url: '/checkout/shipping', finalUrl: '/checkout/shipping', heading: 'Shipping method', title: 'Shipping method — Checkout' },
    { url: '/checkout/review', finalUrl: '/checkout/review', heading: 'Review your order', title: 'Review your order — Checkout' },
    { url: '/checkout/payment', finalUrl: '/checkout/payment', heading: 'Payment', title: 'Payment — Checkout' },
    { url: '/orders', finalUrl: '/orders', heading: 'Your orders', title: 'Your orders' },
    { url: '/orders/1001', finalUrl: '/orders/1001', heading: 'Order 1001', title: 'Order details' },
    { url: '/orders/1001/confirmation', finalUrl: '/orders/1001/confirmation', heading: 'Order 1001 confirmed', title: 'Order confirmed' },
    { url: '/admin', finalUrl: '/admin/products', heading: 'Products', title: 'Products — Admin' },
    { url: '/admin/orders', finalUrl: '/admin/orders', heading: 'Orders', title: 'Orders — Admin' },
    { url: '/admin/orders/1001', finalUrl: '/admin/orders/1001', heading: 'Order 1001', title: 'Order details — Admin' },
    { url: '/admin/partners', finalUrl: '/admin/partners', heading: 'Partner status', title: 'Partner status — Admin' },
    { url: '/no-such-page', finalUrl: '/no-such-page', heading: 'Page not found', title: 'Page not found' },
  ];

  it.each(pages)('$url lazy-loads "$heading"', async ({ url, finalUrl, heading, title }) => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes, withComponentInputBinding()),
        { provide: TitleStrategy, useClass: PageTitleStrategy },
        // Pages load data; the requests stay pending, so each page shows its loading heading.
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl(url);

    expect(TestBed.inject(Router).url).toBe(finalUrl);
    expect(harness.routeNativeElement?.querySelector('h1')?.textContent?.trim()).toBe(heading);
    expect(TestBed.inject(Title).getTitle()).toBe(`${title} — Lumen Goods`);
  });
});
