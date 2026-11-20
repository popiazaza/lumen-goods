import { TestBed } from '@angular/core/testing';
import { US_ADDRESS } from '../../testing/fixtures';
import { anOrder } from '../../testing/order-fixtures';
import {
  CHECKOUT_STORAGE_KEY,
  CheckoutStore,
  ReviewedOrder,
  newCheckoutKey,
} from './checkout-store';

/**
What Place order sends for the reviewed cart, apart from the key.
*/
const aReview = (overrides: Partial<ReviewedOrder> = {}): ReviewedOrder => ({
  lines: [
    { productId: 'lumen-mug', quantity: 2, unitPrice: 18.5 },
    { productId: 'kestrel-K-7731', quantity: 1, unitPrice: 16.22 },
  ],
  expectedTotal: 67.46,
  shippingAddress: US_ADDRESS,
  shippingMethod: 'standard',
  ...overrides,
});

/**
A new store, as after a page refresh: only sessionStorage survives.
*/
const freshStore = (): CheckoutStore => {
  TestBed.resetTestingModule();
  return TestBed.inject(CheckoutStore);
};

describe('CheckoutStore', () => {
  afterEach(() => sessionStorage.clear());

  it('starts empty with a checkout key, and only the address step open', () => {
    const store = freshStore();

    expect(store.checkoutKey()).toMatch(/^[0-9a-f-]{36}$/u);
    expect(store.address()).toBeNull();
    expect([...store.reachable()]).toStrictEqual(['address']);
  });

  it('keeps the entered steps and the same key across a refresh', () => {
    const store = freshStore();
    const key = store.checkoutKey();
    store.setAddress(US_ADDRESS);
    store.setShippingMethod('express');

    const reloaded = freshStore();

    expect(reloaded.checkoutKey()).toBe(key);
    expect(reloaded.address()).toStrictEqual(US_ADDRESS);
    expect(reloaded.shippingMethod()).toBe('express');
    expect([...reloaded.reachable()]).toStrictEqual(['address', 'shipping', 'review']);
  });

  it('opens payment once the order is placed', () => {
    const store = freshStore();
    store.setAddress(US_ADDRESS);
    store.setShippingMethod('standard');
    store.setOrderNumber('LG-1');

    expect(freshStore().reachable().has('payment')).toBeTruthy();
  });

  it('keeps the order when nothing changed after Place order, and closes payment when something did', () => {
    const store = freshStore();
    store.setAddress(US_ADDRESS);
    store.setShippingMethod('standard');
    const used = store.checkoutKeyFor(aReview());
    store.orderPlaced(anOrder({ number: 'LG-1' }));

    store.setAddress({ ...US_ADDRESS });
    store.setShippingMethod('standard');

    expect(store.orderNumber()).toBe('LG-1');

    store.setShippingMethod('express');

    expect(store.orderNumber()).toBeNull();
    expect(store.reachable().has('payment')).toBeFalsy();
    // The new review gets a new key, so it can never be answered with LG-1.
    expect(store.checkoutKeyFor(aReview({ shippingMethod: 'express' }))).not.toBe(used);
  });

  it('keeps Place order’s answer in memory only, for as long as it is this checkout’s order', () => {
    const store = freshStore();
    store.setAddress(US_ADDRESS);
    store.setShippingMethod('standard');
    store.orderPlaced(anOrder({ number: 'LG-1' }));

    expect(store.orderNumber()).toBe('LG-1');
    expect(store.placedOrder()?.totals.total).toBe(67.46);
    expect(freshStore().placedOrder()).toBeNull();

    store.setShippingMethod('express');

    expect(store.placedOrder()).toBeNull();
  });

  it('sends the same key only to resubmit the same review, so Place order never returns an earlier order', () => {
    const store = freshStore();
    const first = store.checkoutKeyFor(aReview());

    // A plain resubmit (the first attempt failed on the network), also after a page refresh, and
    // with the lines in another order: the API answers with the order the first one may have made.
    expect(store.checkoutKeyFor(aReview())).toBe(first);

    const reversed = aReview().lines.toReversed();

    expect(freshStore().checkoutKeyFor(aReview({ lines: reversed }))).toBe(first);

    // Anything reviewed differently is a different order: a quantity, a line, a price and total,
    // the address or the shipping method.
    const changes: Partial<ReviewedOrder>[] = [
      { lines: [{ productId: 'lumen-mug', quantity: 3, unitPrice: 18.5 }], expectedTotal: 59.95 },
      { lines: [{ productId: 'lumen-mug', quantity: 2, unitPrice: 18.5 }], expectedTotal: 44.95 },
      { expectedTotal: 68.63 },
      { shippingAddress: { ...US_ADDRESS, line2: 'Apt 4' } },
      { shippingMethod: 'express', expectedTotal: 75.46 },
    ];
    const keys = changes.map((change) => store.checkoutKeyFor(aReview(change)));
    const distinctKeys = new Set([first, ...keys]);

    expect(distinctKeys.size).toBe(changes.length + 1);
    // Each new key is kept for its own resubmit.
    expect(store.checkoutKeyFor(aReview(changes[4]))).toBe(keys[4]);
  });

  it('sends the same key for the same review after Place order, so the API returns that order (BR-PAY-01a)', () => {
    const store = freshStore();
    store.setAddress(US_ADDRESS);
    store.setShippingMethod('standard');
    const used = store.checkoutKeyFor(aReview());

    store.orderPlaced(anOrder({ number: 'LG-1' }));

    // Payment, then the stepper's Review, then Place order again: the same order, not a second
    // one reserving the same stock. Also after a page refresh.
    expect(store.orderNumber()).toBe('LG-1');
    expect(store.checkoutKey()).toBe(used);
    expect(store.checkoutKeyFor(aReview())).toBe(used);
    expect(freshStore().checkoutKeyFor(aReview())).toBe(used);
  });

  it('starts a new key after Place order only when the review changes', () => {
    const store = freshStore();
    store.setAddress(US_ADDRESS);
    store.setShippingMethod('standard');
    const used = store.checkoutKeyFor(aReview());
    store.orderPlaced(anOrder({ number: 'LG-1' }));

    // The shopper went back and changed the cart: a new order, never LG-1.
    const next = store.checkoutKeyFor(aReview({ expectedTotal: 59.95 }));

    expect(next).not.toBe(used);
    expect(store.checkoutKeyFor(aReview({ expectedTotal: 59.95 }))).toBe(next);
  });

  it('starts a new key once the checkout ends (paid or closed)', () => {
    const store = freshStore();
    const used = store.checkoutKeyFor(aReview());
    store.orderPlaced(anOrder({ number: 'LG-1' }));

    store.reset();

    expect(store.checkoutKeyFor(aReview())).not.toBe(used);
  });

  it('reads a checkout saved before keys were tied to a review', () => {
    sessionStorage.setItem(
      CHECKOUT_STORAGE_KEY,
      JSON.stringify({
        checkoutKey: 'key-1',
        address: US_ADDRESS,
        shippingMethod: 'standard',
        orderNumber: null,
      })
    );
    const store = freshStore();

    expect(store.address()).toStrictEqual(US_ADDRESS);
    expect(store.checkoutKeyFor(aReview())).toBe('key-1');
  });

  it('renews only the key when the API says it is taken', () => {
    const store = freshStore();
    store.setAddress(US_ADDRESS);
    store.setShippingMethod('standard');
    const key = store.checkoutKey();

    store.renewCheckoutKey();

    expect(store.checkoutKey()).not.toBe(key);
    expect(store.address()).toStrictEqual(US_ADDRESS);
    expect(freshStore().checkoutKey()).toBe(store.checkoutKey());
  });

  it('forgets everything on reset, with a new key', () => {
    const store = freshStore();
    store.setAddress(US_ADDRESS);
    const key = store.checkoutKey();

    store.reset();

    expect(store.address()).toBeNull();
    expect(store.checkoutKey()).not.toBe(key);
    expect(freshStore().address()).toBeNull();
  });

  it('ignores corrupt or foreign saved state', () => {
    sessionStorage.setItem(CHECKOUT_STORAGE_KEY, '{"checkoutKey": 42}');

    expect(freshStore().address()).toBeNull();

    sessionStorage.setItem(CHECKOUT_STORAGE_KEY, 'not json');

    expect(freshStore().checkoutKey()).toBeTruthy();
  });

  it('makes v4 UUIDs', () => {
    expect(newCheckoutKey()).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u
    );
  });
});
