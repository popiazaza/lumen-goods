import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import {
  CHECKOUT_CHANGED_PROBLEM,
  CHECKOUT_KEY_TAKEN_PROBLEM,
  CheckoutChangedProblem,
  CheckoutLineChangeDto,
  CheckoutPreviewDto,
  SHIPPING_METHOD_NOT_AVAILABLE_PROBLEM,
} from '../../core/api/checkout.api';
import { CheckoutStore } from '../../core/checkout/checkout-store';
import { provideAppIcons } from '../../core/icons/app-icons';
import { US_ADDRESS, aCart, aPreview } from '../../testing/fixtures';
import { anOrder } from '../../testing/order-fixtures';
import { CheckoutReviewPage } from './checkout-review-page';

@Component({ template: '<h1>Elsewhere</h1>' })
class StubPage {}

const PREVIEW_URL = '/api/v1/checkout/preview?shippingMethod=express';

function aChange(overrides: Partial<CheckoutLineChangeDto> = {}): CheckoutLineChangeDto {
  return {
    cartLineId: 'line-socks',
    productId: 'kestrel-K-7731',
    name: 'Merino Trail Socks',
    sellerName: 'Kestrel Outfitters',
    kind: 'price_changed',
    from: 16.22,
    to: 17.31,
    quantityFrom: null,
    quantityTo: null,
    available: null,
    message: 'Price changed from $16.22 to $17.31',
    ...overrides,
  };
}

function conflict(
  changes: CheckoutLineChangeDto[],
  newTotal: number | null,
  canConfirm: boolean
): CheckoutChangedProblem {
  return {
    type: CHECKOUT_CHANGED_PROBLEM,
    title: 'Your order changed',
    status: 409,
    detail: 'A price changed since you reviewed your order. Check the new total and confirm again.',
    changes,
    newTotal,
    canConfirm,
  };
}

describe('CheckoutReviewPage', () => {
  let fixture: ComponentFixture<CheckoutReviewPage>;
  let http: HttpTestingController;
  let store: CheckoutStore;

  const host = () => fixture.nativeElement as HTMLElement;
  const text = (selector: string) =>
    host().querySelector(selector)?.textContent?.replaceAll(/\s+/g, ' ').trim();
  const placeButton = () =>
    host().querySelector<HTMLButtonElement>('button.place') as HTMLButtonElement;

  async function open(preview: CheckoutPreviewDto = aPreview()): Promise<void> {
    fixture = TestBed.createComponent(CheckoutReviewPage);
    fixture.detectChanges();
    await vi.waitFor(() => http.expectOne(PREVIEW_URL).flush(preview));
    await fixture.whenStable();
  }

  async function placeAndConflict(problem: CheckoutChangedProblem): Promise<TestRequest> {
    placeButton().click();
    const place = http.expectOne('/api/v1/checkout/orders');
    place.flush(problem, { status: 409, statusText: 'Conflict' });
    await vi.waitFor(() => http.expectOne(PREVIEW_URL).flush(aPreview({ total: 68.63 })));
    await fixture.whenStable();
    return place;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'checkout/payment', component: StubPage },
          { path: 'cart', component: StubPage },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(CheckoutStore);
    store.setAddress(US_ADDRESS);
    store.setShippingMethod('express');
  });

  afterEach(() => {
    http.verify();
    sessionStorage.clear();
  });

  it('shows the address, method, items by seller and the server’s totals', async () => {
    await open();

    expect(text('.summary-list div:first-child dd')).toBe(
      'Ada Lovelace, 1200 NW Naito Pkwy, Portland, Oregon 97209, United States'
    );
    expect(
      [...host().querySelectorAll('section.group h3')].map((h) => h.textContent?.trim())
    ).toEqual(['Sold by Lumen Goods', 'Sold by Kestrel Outfitters']);
    expect(text('.review-total')).toBe('$67.46');
  });

  it('places exactly what was reviewed with the checkout key, then opens payment', async () => {
    await open();

    placeButton().click();
    const request = http.expectOne('/api/v1/checkout/orders');

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      checkoutKey: store.checkoutKey(),
      lines: [
        { productId: 'lumen-mug', quantity: 2, unitPrice: 18.5 },
        { productId: 'kestrel-K-7731', quantity: 1, unitPrice: 16.22 },
      ],
      expectedTotal: 67.46,
      shippingAddress: US_ADDRESS,
      shippingMethod: 'express',
    });

    const sentKey = request.request.body.checkoutKey;
    request.flush(anOrder({ number: 'LG-NEW1' }), { status: 201, statusText: 'Created' });
    http.expectOne('/api/v1/cart').flush(aCart([]));
    await fixture.whenStable();

    expect(store.orderNumber()).toBe('LG-NEW1');
    // The key stays with this review: placing it again must get LG-NEW1 back, not a second order.
    expect(store.checkoutKey()).toBe(sentKey);
    // The payment step opens on this answer instead of reading the order again.
    expect(store.placedOrder()?.number).toBe('LG-NEW1');
    expect(TestBed.inject(Router).url).toBe('/checkout/payment?order=LG-NEW1');
  });

  it('places the same review again with the same key, so the API returns the first order', async () => {
    await open();
    placeButton().click();
    const first = http.expectOne('/api/v1/checkout/orders');
    first.flush(anOrder({ number: 'LG-NEW1' }), { status: 201, statusText: 'Created' });
    http.expectOne('/api/v1/cart').flush(aCart([]));
    await fixture.whenStable();
    fixture.destroy();

    // Payment, then the stepper's Review, then Place order with nothing changed.
    await open();
    placeButton().click();
    const again = http.expectOne('/api/v1/checkout/orders');

    expect(again.request.body.checkoutKey).toBe(first.request.body.checkoutKey);

    again.flush(anOrder({ number: 'LG-NEW1' }), { status: 200, statusText: 'OK' });
    http.expectOne('/api/v1/cart').flush(aCart([]));
    await fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/checkout/payment?order=LG-NEW1');
  });

  it('on a 409, shows each price change (old → new) and the new total, and confirms them', async () => {
    await open();

    const first = await placeAndConflict(conflict([aChange()], 68.63, true));

    const banner = host().querySelector<HTMLElement>('.banner.changed') as HTMLElement;

    expect(document.activeElement).toBe(banner);
    expect(text('.changes li')).toBe(
      'Merino Trail Socks (Kestrel Outfitters): price changed from was $16.22 to $17.31'
    );
    expect(banner.querySelector('del')?.textContent).toContain('$16.22');
    expect(text('.new-total')).toBe('$68.63');
    expect(host().querySelector('button.place')).toBeNull();

    host().querySelector<HTMLButtonElement>('button.confirm')?.click();
    const retry = http.expectOne('/api/v1/checkout/orders');

    // New prices and total are a different review, so a different key (the 409 made no order).
    expect(retry.request.body.checkoutKey).not.toBe(first.request.body.checkoutKey);
    expect(retry.request.body).toMatchObject({
      expectedTotal: 68.63,
      lines: [
        { productId: 'lumen-mug', quantity: 2, unitPrice: 18.5 },
        { productId: 'kestrel-K-7731', quantity: 1, unitPrice: 17.31 },
      ],
    });

    retry.flush(anOrder({ number: 'LG-NEW2' }), { status: 201, statusText: 'Created' });
    http.expectOne('/api/v1/cart').flush(aCart([]));
    await fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/checkout/payment?order=LG-NEW2');
  });

  it('sends the shopper to the cart when a line cannot be supplied in full', async () => {
    await open();

    await placeAndConflict(
      conflict(
        [
          aChange({
            kind: 'quantity_limited',
            from: null,
            to: null,
            available: 1,
            message: 'Only 1 available',
          }),
        ],
        null,
        false
      )
    );

    expect(text('.changes li')).toBe('Merino Trail Socks (Kestrel Outfitters): Only 1 available');
    expect(host().querySelector('button.confirm')).toBeNull();
    expect(host().querySelector('.banner.changed a')?.getAttribute('href')).toBe('/cart');

    // Reviewing again shows the saved stock as a blocker, so Place order waits for the cart.
    host().querySelector<HTMLButtonElement>('.banner.changed button.review-again')?.click();
    const limited = aChange({ kind: 'quantity_limited', available: 1, message: 'Only 1 left' });
    await vi.waitFor(() => http.expectOne(PREVIEW_URL).flush(aPreview({ blockers: [limited] })));
    await fixture.whenStable();

    expect(host().querySelector('.banner.changed')).toBeNull();
    expect(placeButton().getAttribute('aria-disabled')).toBe('true');
  });

  it('offers a fresh review when the cart changed in another tab', async () => {
    await open();

    await placeAndConflict(
      conflict(
        [
          aChange({
            kind: 'cart_changed',
            cartLineId: null,
            from: null,
            to: null,
            quantityFrom: 0,
            quantityTo: 1,
            message: 'Added to your cart since you reviewed it',
          }),
        ],
        null,
        false
      )
    );

    expect(text('.changes li')).toBe(
      'Merino Trail Socks (Kestrel Outfitters): Added to your cart since you reviewed it'
    );

    host().querySelector<HTMLButtonElement>('button.review-again')?.click();
    await vi.waitFor(() => http.expectOne(PREVIEW_URL).flush(aPreview()));
    await fixture.whenStable();

    expect(host().querySelector('.banner.changed')).toBeNull();
    expect(placeButton()).toBeTruthy();
  });

  it('cannot place the order while the preview has blockers', async () => {
    await open(
      aPreview({ blockers: [aChange({ kind: 'unavailable', message: 'No longer available' })] })
    );

    expect(placeButton().getAttribute('aria-disabled')).toBe('true');
    expect(text('#place-blocked')).toBe('Update your cart before placing the order.');

    placeButton().click();
    http.expectNone('/api/v1/checkout/orders');
  });

  it('lists the API’s reasons when it refuses the address (400), with a way to change it', async () => {
    await open();

    placeButton().click();
    http.expectOne('/api/v1/checkout/orders').flush(
      {
        title: 'The order is not valid',
        status: 400,
        errors: { 'shippingAddress.postalCode': ['Enter a 5-digit ZIP code, e.g. 97209.'] },
      },
      { status: 400, statusText: 'Bad Request' }
    );
    await fixture.whenStable();

    expect(text('.place-error p')).toBe('We can’t ship to this address. Change it and try again.');
    expect(text('.place-error li')).toBe('Enter a 5-digit ZIP code, e.g. 97209.');
    expect(host().querySelector('.place-error a')?.getAttribute('href')).toBe('/checkout/address');
  });

  it('starts a new checkout key when the API says this one is taken (409)', async () => {
    await open();
    const key = store.checkoutKey();

    placeButton().click();
    http.expectOne('/api/v1/checkout/orders').flush(
      {
        type: CHECKOUT_KEY_TAKEN_PROBLEM,
        title: 'This checkout was already used',
        status: 409,
        detail: 'Reload the checkout page and place the order again.',
      },
      { status: 409, statusText: 'Conflict' }
    );
    await fixture.whenStable();

    expect(store.checkoutKey()).not.toBe(key);

    placeButton().click();
    const retry = http.expectOne('/api/v1/checkout/orders');

    expect(retry.request.body).toMatchObject({ checkoutKey: store.checkoutKey() });

    retry.flush(anOrder(), { status: 201, statusText: 'Created' });
    http.expectOne('/api/v1/cart').flush(aCart([]));
    await fixture.whenStable();
  });

  it('sends the shopper back to shipping when Express is no longer offered (422)', async () => {
    fixture = TestBed.createComponent(CheckoutReviewPage);
    fixture.detectChanges();
    await vi.waitFor(() =>
      http.expectOne(PREVIEW_URL).flush(
        {
          type: SHIPPING_METHOD_NOT_AVAILABLE_PROBLEM,
          title: 'This shipping method is not available',
          status: 422,
          detail:
            "Express shipping is only offered for Lumen Goods' own items, and this cart has none.",
        },
        { status: 422, statusText: 'Unprocessable Entity' }
      )
    );
    await fixture.whenStable();

    expect(text('.banner.error p')).toBe(
      "Express shipping is only offered for Lumen Goods' own items, and this cart has none."
    );
    expect(host().querySelector('.banner.error a')?.getAttribute('href')).toBe(
      '/checkout/shipping'
    );
  });

  it('says nothing was charged when Place order fails for another reason', async () => {
    await open();

    placeButton().click();
    http
      .expectOne('/api/v1/checkout/orders')
      .flush('down', { status: 503, statusText: 'Unavailable' });
    await fixture.whenStable();

    expect(text('.banner.error[role="alert"] p')).toBe(
      'We couldn’t place your order. You have not been charged. Try again.'
    );
  });
});
