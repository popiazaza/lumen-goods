import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { ANONYMOUS_SESSION, Session, SessionApi } from '../../core/api/session.api';
import { CHECKOUT_STORAGE_KEY, CheckoutState } from '../../core/checkout/checkout-store';
import { provideAppIcons } from '../../core/icons/app-icons';
import { CUSTOMER_SESSION, US_ADDRESS } from '../../testing/fixtures';

describe('checkout routes', () => {
  let http: HttpTestingController;

  /**
  Starts the app's router with a session and (optionally) checkout state saved in this tab.
  */
  async function start(
    session: Session,
    saved?: Partial<CheckoutState>
  ): Promise<RouterTestingHarness> {
    if (saved) {
      sessionStorage.setItem(
        CHECKOUT_STORAGE_KEY,
        JSON.stringify({
          checkoutKey: 'key-1',
          address: null,
          shippingMethod: null,
          orderNumber: null,
          ...saved,
        })
      );
    }
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes, withComponentInputBinding()),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const ready = TestBed.inject(SessionApi).ready();
    http.expectOne('/api/v1/auth/session').flush(session);
    await ready;
    return RouterTestingHarness.create();
  }

  const url = () => TestBed.inject(Router).url;

  afterEach(() => sessionStorage.clear());

  it('sends a signed-out shopper to sign in, coming back to the step afterwards', async () => {
    const harness = await start(ANONYMOUS_SESSION);

    await harness.navigateByUrl('/checkout/review');

    expect(url()).toBe('/sign-in?returnUrl=%2Fcheckout%2Freview');
  });

  it('waits for a slow session instead of treating the shopper as signed out', async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    const harness = await RouterTestingHarness.create();

    const navigation = harness.navigateByUrl('/checkout/address');
    await vi.waitFor(() => http.expectOne('/api/v1/auth/session').flush(CUSTOMER_SESSION));
    await navigation;

    expect(url()).toBe('/checkout/address');
  });

  it.each([
    ['/checkout/shipping', {}, '/checkout/address'],
    ['/checkout/review', {}, '/checkout/address'],
    ['/checkout/review', { address: US_ADDRESS }, '/checkout/shipping'],
    ['/checkout/payment', { address: US_ADDRESS, shippingMethod: 'standard' }, '/checkout/review'],
  ] as const)(
    'opens %s only after the steps before it (%j → %s)',
    async (target, saved, expected) => {
      const harness = await start(CUSTOMER_SESSION, saved);

      await harness.navigateByUrl(target);

      expect(url()).toBe(expected);
    }
  );

  it('keeps the shopper on their step after a refresh (state from sessionStorage)', async () => {
    const harness = await start(CUSTOMER_SESSION, {
      address: US_ADDRESS,
      shippingMethod: 'express',
    });

    await harness.navigateByUrl('/checkout/review');

    expect(url()).toBe('/checkout/review');

    // The review reads the preview for the saved method.
    http.expectOne('/api/v1/checkout/preview?shippingMethod=express');
  });

  it('opens payment for an order from its detail page (?order=)', async () => {
    const harness = await start(CUSTOMER_SESSION);

    await harness.navigateByUrl('/checkout/payment?order=LG-42');

    expect(url()).toBe('/checkout/payment?order=LG-42');
    expect(http.match((request) => request.url.includes('/orders/LG-42')).length).toBeGreaterThan(
      0
    );
  });
});
