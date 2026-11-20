import { TestBed } from '@angular/core/testing';
import { US_ADDRESS, anOrder } from '../../testing/fixtures';
import { CHECKOUT_STORAGE_KEY, CheckoutStore, newCheckoutKey } from './checkout-store';

describe('CheckoutStore', () => {
  /** A new store, as after a page refresh: only sessionStorage survives. */
  function freshStore(): CheckoutStore {
    TestBed.resetTestingModule();
    return TestBed.inject(CheckoutStore);
  }

  afterEach(() => sessionStorage.clear());

  it('starts empty with a checkout key, and only the address step open', () => {
    const store = freshStore();

    expect(store.checkoutKey()).toMatch(/^[0-9a-f-]{36}$/);
    expect(store.address()).toBeNull();
    expect([...store.reachable()]).toEqual(['address']);
  });

  it('keeps the entered steps and the same key across a refresh', () => {
    const store = freshStore();
    const key = store.checkoutKey();
    store.setAddress(US_ADDRESS);
    store.setShippingMethod('express');

    const reloaded = freshStore();

    expect(reloaded.checkoutKey()).toBe(key);
    expect(reloaded.address()).toEqual(US_ADDRESS);
    expect(reloaded.shippingMethod()).toBe('express');
    expect([...reloaded.reachable()]).toEqual(['address', 'shipping', 'review']);
  });

  it('opens payment once the order is placed', () => {
    const store = freshStore();
    store.setAddress(US_ADDRESS);
    store.setShippingMethod('standard');
    store.setOrderNumber('LG-1');

    expect(freshStore().reachable().has('payment')).toBe(true);
  });

  it('keeps the key when nothing changed after Place order, and starts a new checkout when something did', () => {
    const store = freshStore();
    store.setAddress(US_ADDRESS);
    store.setShippingMethod('standard');
    store.setOrderNumber('LG-1');
    const key = store.checkoutKey();

    store.setAddress({ ...US_ADDRESS });
    store.setShippingMethod('standard');
    expect(store.checkoutKey()).toBe(key);
    expect(store.orderNumber()).toBe('LG-1');

    store.setShippingMethod('express');
    expect(store.checkoutKey()).not.toBe(key);
    expect(store.orderNumber()).toBeNull();
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

  it('renews only the key when the API says it is taken', () => {
    const store = freshStore();
    store.setAddress(US_ADDRESS);
    store.setShippingMethod('standard');
    const key = store.checkoutKey();

    store.renewCheckoutKey();

    expect(store.checkoutKey()).not.toBe(key);
    expect(store.address()).toEqual(US_ADDRESS);
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
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
    );
  });
});
