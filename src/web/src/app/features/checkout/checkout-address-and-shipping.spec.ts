import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatRadioButtonHarness } from '@angular/material/radio/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { CheckoutStore } from '../../core/checkout/checkout-store';
import { provideAppIcons } from '../../core/icons/app-icons';
import { US_ADDRESS, aPreview } from '../../testing/fixtures';
import { CheckoutAddressPage } from './checkout-address-page';
import { CheckoutShippingPage } from './checkout-shipping-page';

@Component({ template: '<h1>Next</h1>' })
class StubPage {}

describe('Checkout address and shipping steps', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let store: CheckoutStore;

  const host = () => harness.routeNativeElement as HTMLElement;
  const text = (selector: string) =>
    host().querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();
  const field = (id: string) =>
    host().querySelector<HTMLInputElement | HTMLSelectElement>(`#${id}`) as HTMLInputElement;
  const url = () => TestBed.inject(Router).url;

  function type(id: string, value: string): void {
    const element = field(id);
    element.value = value;
    element.dispatchEvent(new Event(element.tagName === 'SELECT' ? 'change' : 'input'));
  }

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

  describe('address', () => {
    it('uses autofill tokens for every field', async () => {
      await harness.navigateByUrl('/checkout/address');

      expect(
        ['name', 'line1', 'line2', 'city', 'region', 'postalCode'].map((name) =>
          field(`address-${name}`).getAttribute('autocomplete'),
        ),
      ).toEqual([
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
      type('address-name', 'Ada Lovelace');
      type('address-postalCode', '9720');

      host().querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
      await harness.fixture.whenStable();

      expect(
        [...host().querySelectorAll('app-error-summary li')].map((li) => li.textContent?.trim()),
      ).toEqual([
        'Enter address line 1',
        'Enter city',
        'Select a state',
        'Enter a 5-digit ZIP code, like 97209 or 97209-1234',
      ]);
      expect(url()).toBe('/checkout/address');
      expect(store.address()).toBeNull();
    });

    it('saves the address and moves on; coming back shows it again', async () => {
      await harness.navigateByUrl('/checkout/address');
      type('address-name', ' Ada Lovelace ');
      type('address-line1', '1200 NW Naito Pkwy');
      type('address-city', 'Portland');
      type('address-region', 'OR');
      type('address-postalCode', '97209-1234');

      host().querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
      // The shipping step loads its preview.
      await vi.waitFor(() =>
        http.expectOne('/api/v1/checkout/preview?shippingMethod=standard').flush(aPreview()),
      );
      await harness.fixture.whenStable();

      expect(url()).toBe('/checkout/shipping');
      expect(store.address()).toEqual({ ...US_ADDRESS, postalCode: '97209-1234' });

      await harness.navigateByUrl('/checkout/address');
      expect(field('address-name').value).toBe('Ada Lovelace');
      expect(field('address-region').value).toBe('OR');
    });
  });

  describe('shipping', () => {
    beforeEach(() => store.setAddress(US_ADDRESS));

    it('offers Express only for Lumen’s own shipment; partners ship Standard', async () => {
      await harness.navigateByUrl('/checkout/shipping');
      http.expectOne('/api/v1/checkout/preview?shippingMethod=standard').flush(aPreview());
      await harness.fixture.whenStable();

      const [own, partner] = [...host().querySelectorAll<HTMLElement>('section.group')];
      expect(own.querySelectorAll('mat-radio-button')).toHaveLength(2);
      expect(text('section.group:first-of-type .methods')).toContain('Express');
      expect(partner.querySelector('mat-radio-button')).toBeNull();
      expect(partner.querySelector('.fixed-method')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
        'Standard shipping, $4.99 · Partner shops ship Standard only.',
      );
      expect(text('.muted')).toBe(
        'Your order arrives in 2 packages: each shop ships its own items.',
      );
    });

    it('prices the order again for Express, then remembers the choice', async () => {
      await harness.navigateByUrl('/checkout/shipping');
      http.expectOne('/api/v1/checkout/preview?shippingMethod=standard').flush(aPreview());
      await harness.fixture.whenStable();

      const express = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
        MatRadioButtonHarness.with({ label: /Express/ }),
      );
      void express.check();
      await vi.waitFor(() =>
        http
          .expectOne('/api/v1/checkout/preview?shippingMethod=express')
          .flush(aPreview({ shippingMethod: 'express', shipping: 17.98, total: 75.46 })),
      );
      await harness.fixture.whenStable();
      expect(text('.totals .grand.price')).toBe('$75.46');

      [...host().querySelectorAll<HTMLButtonElement>('.actions button')][0].click();
      await harness.fixture.whenStable();

      expect(store.shippingMethod()).toBe('express');
      expect(url()).toBe('/checkout/review');
    });

    it('has no choice to make when every item comes from a partner', async () => {
      const partnerOnly = aPreview();
      await harness.navigateByUrl('/checkout/shipping');
      http.expectOne('/api/v1/checkout/preview?shippingMethod=standard').flush(
        aPreview({
          shipments: partnerOnly.shipments.slice(1),
          shippingOptions: [{ method: 'standard', fee: 4.99, shippingTotal: 4.99 }],
        }),
      );
      await harness.fixture.whenStable();

      expect(host().querySelector('mat-radio-button')).toBeNull();
    });
  });
});
