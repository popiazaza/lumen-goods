import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { SHIPPING_METHOD_NOT_AVAILABLE_PROBLEM } from '../../core/api/checkout.api';
import { CheckoutStore } from '../../core/checkout/checkout-store';
import { provideAppIcons } from '../../core/icons/app-icons';
import { US_ADDRESS, aPreview } from '../../testing/fixtures';
import { itemAt } from '../../testing/items';
import { aPagedResult, anOrderDetail, anOrderSummary } from '../../testing/order-fixtures';
import { CheckoutAddressPage } from './checkout-address-page';
import { CheckoutShippingPage } from './checkout-shipping-page';

@Component({ selector: 'app-stub-page', template: '<h1>Next</h1>' })
class StubPage {}

const url = () => TestBed.inject(Router).url;

/**
The address step's read of the customer's most recent order, to fill in an empty form.
*/
const LAST_ORDER_URL = '/api/v1/orders?page=1&pageSize=1';

describe('Checkout address and shipping steps', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let store: CheckoutStore;

  const host = () => harness.routeNativeElement as HTMLElement;
  const text = (selector: string) =>
    host().querySelector(selector)?.textContent?.replaceAll(/\s+/gu, ' ').trim();
  const field = (id: string) =>
    host().querySelector<HTMLInputElement | HTMLSelectElement>(`#${id}`) as HTMLInputElement;

  /**
  The radio button whose label matches, found as people find it: by its label.
  */
  const radio = (label: RegExp): HTMLInputElement =>
    [...host().querySelectorAll<HTMLInputElement>('input[type="radio"]')].find((input) =>
      label.test(input.closest('label')?.textContent ?? '')
    ) as HTMLInputElement;

  const type = (id: string, value: string): void => {
    const element = field(id);
    element.value = value;
    element.dispatchEvent(new Event(element.tagName === 'SELECT' ? 'change' : 'input'));
  };

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'checkout/address', component: CheckoutAddressPage },
          { path: 'checkout/shipping', component: CheckoutShippingPage },
          { path: 'checkout/review', component: StubPage },
          { path: 'cart', component: StubPage },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(CheckoutStore);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => {
    http.verify();
    sessionStorage.clear();
  });

  const noPastOrders = (): void => http.expectOne(LAST_ORDER_URL).flush(aPagedResult([]));

  describe('address', () => {
    it('uses autofill tokens for every field', async () => {
      await harness.navigateByUrl('/checkout/address');
      noPastOrders();

      expect(
        ['name', 'line1', 'line2', 'city', 'region', 'postalCode'].map((name) =>
          field(`address-${name}`).getAttribute('autocomplete')
        )
      ).toStrictEqual([
        'shipping name',
        'shipping address-line1',
        'shipping address-line2',
        'shipping address-level2',
        'shipping address-level1',
        'shipping postal-code',
      ]);
    });

    it('checks required fields, the state and the ZIP format, listing problems above the form', async () => {
      await harness.navigateByUrl('/checkout/address');
      noPastOrders();
      type('address-name', 'Ada Lovelace');
      type('address-postalCode', '9720');

      host().querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
      await harness.fixture.whenStable();

      expect(
        [...host().querySelectorAll(':scope app-error-summary li')].map((li) =>
          li.textContent?.trim()
        )
      ).toStrictEqual([
        'Enter address line 1',
        'Enter city',
        'Select a state',
        'Enter a 5-digit ZIP code, like 97209 or 97209-1234',
      ]);

      expect(url()).toBe('/checkout/address');
      expect(store.address()).toBeNull();
    });

    it('keeps the capitals of "ZIP" in the message for an empty ZIP code', async () => {
      await harness.navigateByUrl('/checkout/address');
      noPastOrders();

      host().querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
      await harness.fixture.whenStable();

      expect(
        [...host().querySelectorAll(':scope app-error-summary li')].map((li) =>
          li.textContent?.trim()
        )
      ).toContain('Enter ZIP code');
    });

    it('marks each invalid field and points it at its message, which replaces its hint', async () => {
      await harness.navigateByUrl('/checkout/address');
      noPastOrders();
      type('address-name', 'Ada Lovelace');
      const line1 = field('address-line1');

      expect(line1.getAttribute('aria-describedby')).toBe('address-line1-hint');

      host().querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
      await harness.fixture.whenStable();

      expect(line1.getAttribute('aria-invalid')).toBe('true');
      expect(
        host()
          .querySelector(`#${line1.getAttribute('aria-describedby')}`)
          ?.textContent?.trim()
      ).toBe('Enter address line 1');
      expect(field('address-region').getAttribute('aria-describedby')).toBe('address-region-error');
      expect(field('address-name').getAttribute('aria-invalid')).toBeNull();
    });

    it('saves the address and moves on; coming back shows it again', async () => {
      await harness.navigateByUrl('/checkout/address');
      noPastOrders();
      type('address-name', ' Ada Lovelace ');
      type('address-line1', '1200 NW Naito Pkwy');
      type('address-city', 'Portland');
      type('address-region', 'OR');
      type('address-postalCode', '97209-1234');

      host().querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
      // The shipping step loads its preview.
      await vi.waitFor(() =>
        http.expectOne('/api/v1/checkout/preview?shippingMethod=standard').flush(aPreview())
      );
      await harness.fixture.whenStable();

      expect(url()).toBe('/checkout/shipping');
      expect(store.address()).toStrictEqual({ ...US_ADDRESS, postalCode: '97209-1234' });

      await harness.navigateByUrl('/checkout/address');

      expect(field('address-name').value).toBe('Ada Lovelace');
      expect(field('address-region').value).toBe('OR');
    });
  });

  describe('address from the last order', () => {
    const lastAddress = {
      ...US_ADDRESS,
      name: 'Grace Hopper',
      line2: 'Apt 4',
      region: 'WA',
      postalCode: '98101',
    };

    it('fills in an empty form from the most recent order, which the customer can still edit', async () => {
      await harness.navigateByUrl('/checkout/address');
      http.expectOne(LAST_ORDER_URL).flush(aPagedResult([anOrderSummary({ number: 'LG-LAST1' })]));
      await vi.waitFor(() =>
        http
          .expectOne('/api/v1/orders/LG-LAST1')
          .flush(anOrderDetail({ number: 'LG-LAST1', shippingAddress: lastAddress }))
      );
      await harness.fixture.whenStable();

      expect(
        ['name', 'line1', 'line2', 'city', 'region', 'postalCode'].map(
          (name) => field(`address-${name}`).value
        )
      ).toStrictEqual(['Grace Hopper', '1200 NW Naito Pkwy', 'Apt 4', 'Portland', 'WA', '98101']);
      expect(text('.prefill-note')).toBe(
        'We filled in the address from your last order. Change anything that’s different.'
      );
      expect(store.address()).toBeNull();

      type('address-name', 'Grace B. Hopper');
      host().querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
      await vi.waitFor(() =>
        http.expectOne('/api/v1/checkout/preview?shippingMethod=standard').flush(aPreview())
      );
      await harness.fixture.whenStable();

      expect(store.address()).toStrictEqual({ ...lastAddress, name: 'Grace B. Hopper' });
    });

    it('stays empty and quiet when there is no order or the read fails', async () => {
      await harness.navigateByUrl('/checkout/address');
      http
        .expectOne(LAST_ORDER_URL)
        .flush(null, { status: 500, statusText: 'Internal Server Error' });
      await harness.fixture.whenStable();

      expect(field('address-name').value).toBe('');
      expect(host().querySelector('.prefill-note, [role="alert"]')).toBeNull();
    });

    it('never replaces an address the checkout already has, nor what the customer started typing', async () => {
      store.setAddress(US_ADDRESS);
      await harness.navigateByUrl('/checkout/address');

      // No read at all (`http.verify`): the checkout's own address wins.
      expect(field('address-name').value).toBe('Ada Lovelace');

      store.reset();
      await harness.navigateByUrl('/cart');
      await harness.navigateByUrl('/checkout/address');
      type('address-name', 'Typed first');
      http.expectOne(LAST_ORDER_URL).flush(aPagedResult([anOrderSummary({ number: 'LG-LAST1' })]));
      await vi.waitFor(() =>
        http
          .expectOne('/api/v1/orders/LG-LAST1')
          .flush(anOrderDetail({ number: 'LG-LAST1', shippingAddress: lastAddress }))
      );
      await harness.fixture.whenStable();

      expect(field('address-name').value).toBe('Typed first');
      expect(field('address-city').value).toBe('');
    });
  });

  describe('shipping', () => {
    beforeEach(() => store.setAddress(US_ADDRESS));

    it('offers Express only for Lumen’s own shipment; partners ship Standard', async () => {
      await harness.navigateByUrl('/checkout/shipping');
      http.expectOne('/api/v1/checkout/preview?shippingMethod=standard').flush(aPreview());
      await harness.fixture.whenStable();

      const groups = host().querySelectorAll<HTMLElement>('section.group');
      const own = itemAt(groups, 0);
      const partner = itemAt(groups, 1);

      expect(
        [...own.querySelectorAll(':scope [role="radiogroup"] input[type="radio"]')].map((input) =>
          input.closest('label')?.textContent?.replaceAll(/\s+/gu, ' ').trim()
        )
      ).toStrictEqual(['Standard, $4.99', 'Express, $12.99']);
      expect(text('section.group:first-of-type .methods')).toContain('Express');
      expect(partner.querySelector('input[type="radio"]')).toBeNull();
      expect(
        partner.querySelector('.fixed-method')?.textContent?.replaceAll(/\s+/gu, ' ').trim()
      ).toBe('Standard shipping: $4.99 · Partner shops ship Standard only.');
      expect(text('.muted')).toBe(
        'Your order arrives in 2 packages: each shop ships its own items.'
      );
    });

    it('prices the order again for Express, then remembers the choice', async () => {
      await harness.navigateByUrl('/checkout/shipping');
      http.expectOne('/api/v1/checkout/preview?shippingMethod=standard').flush(aPreview());
      await harness.fixture.whenStable();

      radio(/Express/u).click();
      await vi.waitFor(() =>
        http
          .expectOne('/api/v1/checkout/preview?shippingMethod=express')
          .flush(aPreview({ shippingMethod: 'express', shipping: 17.98, total: 75.46 }))
      );
      await harness.fixture.whenStable();

      expect(text('.totals .grand.price')).toBe('$75.46');

      itemAt(host().querySelectorAll<HTMLButtonElement>(':scope .actions button'), 0).click();
      await harness.fixture.whenStable();

      expect(store.shippingMethod()).toBe('express');
      expect(url()).toBe('/checkout/review');
    });

    it('saves Express as soon as it is picked, so Back to address and Continue keep it', async () => {
      await harness.navigateByUrl('/checkout/shipping');
      http.expectOne('/api/v1/checkout/preview?shippingMethod=standard').flush(aPreview());
      await harness.fixture.whenStable();
      const pricedForExpress = () =>
        http
          .expectOne('/api/v1/checkout/preview?shippingMethod=express')
          .flush(aPreview({ shippingMethod: 'express', shipping: 17.98, total: 75.46 }));

      radio(/Express/u).click();
      await vi.waitFor(pricedForExpress);
      await harness.fixture.whenStable();

      expect(store.shippingMethod()).toBe('express');

      host().querySelector<HTMLAnchorElement>('a[href="/checkout/address"]')?.click();
      await harness.fixture.whenStable();

      expect(url()).toBe('/checkout/address');

      host().querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
      await vi.waitFor(pricedForExpress);
      await harness.fixture.whenStable();

      expect(url()).toBe('/checkout/shipping');
      expect(radio(/Express/u).checked).toBeTruthy();
      expect(store.shippingMethod()).toBe('express');
    });

    it('has no choice to make when every item comes from a partner', async () => {
      const partnerOnly = aPreview();
      await harness.navigateByUrl('/checkout/shipping');
      http.expectOne('/api/v1/checkout/preview?shippingMethod=standard').flush(
        aPreview({
          shipments: partnerOnly.shipments.slice(1),
          shippingOptions: [{ method: 'standard', fee: 4.99, shippingTotal: 4.99 }],
        })
      );
      await harness.fixture.whenStable();

      expect(host().querySelector('input[type="radio"]')).toBeNull();
    });

    it('falls back to Standard when the API refuses Express for a cart without Lumen items (422)', async () => {
      store.setShippingMethod('express');
      const partnerOnly = aPreview();
      await harness.navigateByUrl('/checkout/shipping');
      http.expectOne('/api/v1/checkout/preview?shippingMethod=express').flush(
        {
          type: SHIPPING_METHOD_NOT_AVAILABLE_PROBLEM,
          title: 'This shipping method is not available',
          status: 422,
        },
        { status: 422, statusText: 'Unprocessable Entity' }
      );
      await vi.waitFor(() =>
        http.expectOne('/api/v1/checkout/preview?shippingMethod=standard').flush(
          aPreview({
            shipments: partnerOnly.shipments.slice(1),
            shippingOptions: [{ method: 'standard', fee: 4.99, shippingTotal: 4.99 }],
          })
        )
      );
      await harness.fixture.whenStable();

      expect(host().querySelector('[role="alert"]')).toBeNull();

      itemAt(host().querySelectorAll<HTMLButtonElement>(':scope .actions button'), 0).click();
      await harness.fixture.whenStable();

      expect(store.shippingMethod()).toBe('standard');
    });
  });
});
