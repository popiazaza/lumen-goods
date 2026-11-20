import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import {
  CUSTOMER_SESSION,
  US_ADDRESS,
  aPaymentStatus,
  aPreview,
  aWallet,
} from '../../testing/fixtures';
import { anOrder } from '../../testing/order-fixtures';
import { CartApi } from './cart.api';
import {
  CHECKOUT_CHANGED_PROBLEM,
  CheckoutApi,
  checkoutChangedProblemOf,
  reviewedLines,
  withNewPrices,
} from './checkout.api';
import { PAYMENT_IN_PROGRESS_PROBLEM, PaymentsApi, paymentInProgressOf } from './payments.api';
import { ANONYMOUS_SESSION, SessionApi } from './session.api';
import { WalletApi } from './wallet.api';

describe('purchase flow API services', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('SessionApi signs in, registers and signs out, keeping the session it gets back', async () => {
    const sessionApi = TestBed.inject(SessionApi);

    const signedIn = firstValueFrom(sessionApi.signIn({ email: 'a@b.test', password: 'pw' }));
    const signIn = http.expectOne('/api/v1/auth/sign-in');
    expect(signIn.request.method).toBe('POST');
    expect(signIn.request.body).toEqual({ email: 'a@b.test', password: 'pw' });
    signIn.flush(CUSTOMER_SESSION);
    expect(await signedIn).toEqual(CUSTOMER_SESSION);
    expect(sessionApi.isAuthenticated()).toBe(true);
    expect(sessionApi.isAdmin()).toBe(false);
    // The guard's `ready()` answers from the sign-in, without another request.
    expect(await sessionApi.ready()).toEqual(CUSTOMER_SESSION);

    sessionApi.register({ email: 'n@b.test', password: 'longenough' }).subscribe();
    http.expectOne('/api/v1/auth/register').flush({ ...CUSTOMER_SESSION, roles: ['admin'] });
    expect(sessionApi.isAdmin()).toBe(true);

    sessionApi.signOut().subscribe();
    http.expectOne('/api/v1/auth/sign-out').flush(ANONYMOUS_SESSION);
    expect(sessionApi.isAuthenticated()).toBe(false);
  });

  it('CartApi acknowledges every line at once', () => {
    TestBed.inject(CartApi).acknowledgeAll().subscribe();

    expect(http.expectOne('/api/v1/cart/acknowledge').request.method).toBe('POST');
  });

  it('CheckoutApi previews a method and places an order', () => {
    const api = TestBed.inject(CheckoutApi);

    api.preview('express').subscribe();
    http.expectOne('/api/v1/checkout/preview?shippingMethod=express').flush(aPreview());

    const request = {
      checkoutKey: 'key-1',
      lines: reviewedLines(aPreview()),
      expectedTotal: 67.46,
      shippingAddress: US_ADDRESS,
      shippingMethod: 'standard' as const,
    };
    api.placeOrder(request).subscribe();
    const place = http.expectOne('/api/v1/checkout/orders');
    expect(place.request.method).toBe('POST');
    expect(place.request.body).toEqual(request);
    place.flush(anOrder());
  });

  it('flattens the reviewed lines and swaps in confirmed new prices', () => {
    const lines = reviewedLines(aPreview());
    expect(lines).toEqual([
      { productId: 'lumen-mug', quantity: 2, unitPrice: 18.5 },
      { productId: 'kestrel-K-7731', quantity: 1, unitPrice: 16.22 },
    ]);

    const changes = [
      {
        cartLineId: 'line-socks',
        productId: 'kestrel-K-7731',
        name: 'Merino Trail Socks',
        sellerName: 'Kestrel Outfitters',
        kind: 'price_changed' as const,
        from: 16.22,
        to: 17.31,
        quantityFrom: null,
        quantityTo: null,
        available: null,
        message: 'Price changed from $16.22 to $17.31',
      },
    ];
    expect(withNewPrices(lines, changes)[1]).toEqual({
      productId: 'kestrel-K-7731',
      quantity: 1,
      unitPrice: 17.31,
    });
  });

  it('recognises the checkout-changed 409 only by its type', () => {
    const body = {
      type: CHECKOUT_CHANGED_PROBLEM,
      title: 'Your order changed',
      changes: [],
      newTotal: 70.1,
      canConfirm: true,
    };
    const conflict = new HttpErrorResponse({ status: 409, error: body });

    expect(checkoutChangedProblemOf(conflict)).toMatchObject({ newTotal: 70.1, canConfirm: true });
    expect(
      checkoutChangedProblemOf(new HttpErrorResponse({ status: 409, error: { title: 'Other' } })),
    ).toBeUndefined();
    expect(
      checkoutChangedProblemOf(new HttpErrorResponse({ status: 400, error: body })),
    ).toBeUndefined();
  });

  it('PaymentsApi pays, reads the current payment and sends the one-time code', () => {
    const api = TestBed.inject(PaymentsApi);

    api.pay('LG-1', { method: 'wallet', card: null }).subscribe();
    const pay = http.expectOne('/api/v1/orders/LG-1/payments');
    expect(pay.request.method).toBe('POST');
    expect(pay.request.body).toEqual({ method: 'wallet', card: null });
    pay.flush(aPaymentStatus(), { status: 202, statusText: 'Accepted' });

    api.current('LG-1').subscribe();
    http.expectOne('/api/v1/orders/LG-1/payments/current').flush(aPaymentStatus());

    api.submitOneTimeCode('LG-1', '123456').subscribe();
    const code = http.expectOne('/api/v1/orders/LG-1/payments/current/one-time-code');
    expect(code.request.body).toEqual({ code: '123456' });
    code.flush(aPaymentStatus({ status: 'succeeded' }));
  });

  it('reads the running payment out of a payment-in-progress 409', () => {
    const running = aPaymentStatus({ status: 'processing' });
    const error = new HttpErrorResponse({
      status: 409,
      error: {
        type: PAYMENT_IN_PROGRESS_PROBLEM,
        title: 'A payment is already in progress',
        payment: running,
      },
    });

    expect(paymentInProgressOf(error)?.payment).toEqual(running);
    expect(paymentInProgressOf(new HttpErrorResponse({ status: 409, error: {} }))).toBeUndefined();
  });

  it('WalletApi reads the wallet', async () => {
    const wallet = firstValueFrom(TestBed.inject(WalletApi).get());

    http.expectOne('/api/v1/wallet').flush(aWallet(1924.54));

    expect((await wallet).balance).toBe(1924.54);
  });
});
