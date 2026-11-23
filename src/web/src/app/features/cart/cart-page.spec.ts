import { LiveAnnouncer } from '@angular/cdk/a11y';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatSnackBarHarness } from '@angular/material/snack-bar/testing';
import { Router, provideRouter } from '@angular/router';
import { CartDto, CartLineDto, EMPTY_CART } from '../../core/api/cart.api';
import { SessionApi } from '../../core/api/session.api';
import { CartStateService } from '../../core/cart/cart-state.service';
import { CheckoutStore } from '../../core/checkout/checkout-store';
import { provideAppIcons } from '../../core/icons/app-icons';
import { CUSTOMER_SESSION, aCart, aCartLine } from '../../testing/fixtures';
import { anOrderDetail } from '../../testing/order-fixtures';
import { CartPage } from './cart-page';

@Component({ selector: 'app-stub-page', template: '<h1>Elsewhere</h1>' })
class StubPage {}

const text = (root: ParentNode | null | undefined, selector: string) =>
  root?.querySelector(selector)?.textContent?.replaceAll(/\s+/gu, ' ').trim();
const button = (root: ParentNode, label: string) =>
  [...root.querySelectorAll<HTMLButtonElement>('button')].find(
    (b) =>
      b.getAttribute('aria-label') === label ||
      b.textContent?.replaceAll(/\s+/gu, ' ').trim() === label
  ) as HTMLButtonElement;

const KESTREL_SOCKS = aCartLine({
  id: 'line-socks',
  product: {
    id: 'kestrel-K-7731',
    source: 'kestrel',
    sellerName: 'Kestrel Outfitters',
    name: 'Merino Trail Socks',
    imageUrl: null,
    availability: { status: 'low_stock', quantity: 2 },
    sellable: true,
  },
  quantity: 1,
  maxQuantity: 2,
  unitPrice: 17.31,
  unitPriceSeen: 16.22,
  lineTotal: 17.31,
  notices: {
    priceChanged: { from: 16.22, to: 17.31 },
    availabilityChanged: { from: 'in_stock', to: 'low_stock' },
    unavailable: null,
    quantityLimited: null,
  },
});

const LUMEN_MUG = aCartLine({
  id: 'line-mug',
  product: {
    id: 'lumen-mug',
    source: 'lumen',
    sellerName: 'Lumen Goods',
    name: 'Enamel Camp Mug',
    imageUrl: null,
    availability: { status: 'in_stock', quantity: null },
    sellable: true,
  },
  quantity: 2,
  maxQuantity: 10,
  unitPrice: 18.5,
  unitPriceSeen: 18.5,
  lineTotal: 37,
});

const GONE_TOTE = aCartLine({
  id: 'line-tote',
  product: { ...aCartLine().product, sellable: false },
  maxQuantity: 0,
  unitPrice: null,
  lineTotal: null,
  notices: {
    priceChanged: null,
    availabilityChanged: null,
    unavailable: 'no_longer_available',
    quantityLimited: null,
  },
  blocksCheckout: true,
});

describe('CartPage', () => {
  let fixture: ComponentFixture<CartPage>;
  let http: HttpTestingController;
  let announcer: LiveAnnouncer;

  const host = () => fixture.nativeElement as HTMLElement;
  const lineOf = (name: string) =>
    [...host().querySelectorAll<HTMLElement>('li.line')].find((li) =>
      li.querySelector('.name')?.textContent?.includes(name)
    ) as HTMLElement;

  const open = async (cart: CartDto): Promise<void> => {
    fixture = TestBed.createComponent(CartPage);
    fixture.detectChanges();
    http.expectOne('/api/v1/cart').flush(cart);
    await fixture.whenStable();
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: StubPage },
          { path: 'checkout', component: StubPage },
          { path: 'products/:id', component: StubPage },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    announcer = TestBed.inject(LiveAnnouncer);
    vi.spyOn(announcer, 'announce').mockResolvedValue(undefined);
  });

  afterEach(() => {
    http.verify();
    vi.restoreAllMocks();
    sessionStorage.clear();
  });

  it('says first when an order from this checkout is waiting for payment, and links to it', async () => {
    const ready = TestBed.inject(SessionApi).ready();
    http.expectOne('/api/v1/auth/session').flush(CUSTOMER_SESSION);
    await ready;
    TestBed.inject(CheckoutStore).setOrderNumber('LG-WAIT1');
    fixture = TestBed.createComponent(CartPage);
    fixture.detectChanges();
    http.expectOne('/api/v1/cart').flush(aCart([GONE_TOTE]));
    await vi.waitFor(() =>
      http
        .expectOne('/api/v1/orders/LG-WAIT1')
        .flush(anOrderDetail({ number: 'LG-WAIT1', status: 'awaiting_payment', canPay: true }))
    );
    await fixture.whenStable();

    const notice = host().querySelector('h1 + app-awaiting-payment-notice');

    expect(text(notice, 'strong')).toBe('Order LG-WAIT1 is waiting for payment');
    expect(notice?.querySelector('a')?.getAttribute('href')).toBe(
      '/checkout/payment?order=LG-WAIT1'
    );
  });

  it('groups lines by seller, each shipping separately, with estimated totals', async () => {
    await open(aCart([KESTREL_SOCKS, LUMEN_MUG]));

    const groups = [...host().querySelectorAll('section.group')];

    expect(groups.map((group) => text(group, 'h2'))).toStrictEqual([
      'Sold by Kestrel Outfitters',
      'Sold by Lumen Goods',
    ]);
    expect(groups.every((group) => text(group, '.ships') === 'Ships separately')).toBeTruthy();

    const totals = [...host().querySelectorAll(':scope .totals dt, :scope .totals dd')].map(
      (cell) => cell.textContent?.trim()
    );

    expect(totals).toStrictEqual([
      'Subtotal (3 items)',
      '$49.90',
      'Estimated shipping',
      '$4.99',
      'Estimated tax',
      '$3.99',
      'Estimated total',
      '$58.88',
    ]);
  });

  it('shows each change on its line, a summary banner, and Acknowledge only where it applies', async () => {
    await open(aCart([KESTREL_SOCKS, LUMEN_MUG, GONE_TOTE], { canCheckout: false }));

    expect(text(host(), '.banner.changes p')).toBe(
      '2 items in your cart changed since you added them. Check the notes below.'
    );

    const socks = lineOf('Merino Trail Socks');

    expect(
      [...socks.querySelectorAll(':scope .notices li')].map((li) => li.textContent?.trim())
    ).toStrictEqual([
      'Price went up from $16.22 to $17.31 since you added it.',
      'Stock is running low: only 2 left.',
    ]);
    expect(button(socks, 'Acknowledge changes to Merino Trail Socks')).toBeTruthy();

    const tote = lineOf('Waxed Canvas Tote');

    expect({
      notice: text(tote, '.notices li'),
      unitPrice: text(tote, '.unit'),
      acknowledge: tote.querySelector('.acknowledge'),
    }).toStrictEqual({
      notice: 'No longer available: Harbor Supply Co. stopped selling it. Remove it to check out.',
      unitPrice: 'No longer priced',
      acknowledge: null,
    });
    expect(lineOf('Enamel Camp Mug').querySelector('.notices')).toBeNull();
  });

  it('acknowledges a line through the API and shows the cart it answers with', async () => {
    await open(aCart([KESTREL_SOCKS]));

    button(lineOf('Merino Trail Socks'), 'Acknowledge changes to Merino Trail Socks').click();
    const request = http.expectOne('/api/v1/cart/lines/line-socks/acknowledge');

    expect(request.request.method).toBe('POST');

    request.flush(
      aCart([
        {
          ...KESTREL_SOCKS,
          unitPriceSeen: 17.31,
          notices: { ...GONE_TOTE.notices, unavailable: null },
        },
      ])
    );
    await fixture.whenStable();

    expect(lineOf('Merino Trail Socks').querySelector('.notices')).toBeNull();
    expect(host().querySelector('.banner.changes')).toBeNull();
    expect(announcer.announce).toHaveBeenCalledWith(
      'Noted the changes to Merino Trail Socks.',
      'polite'
    );
  });

  it('acknowledges every change at once from the banner', async () => {
    await open(aCart([KESTREL_SOCKS, LUMEN_MUG]));

    button(host(), 'Acknowledge all changes').click();
    const request = http.expectOne('/api/v1/cart/acknowledge');

    expect(request.request.method).toBe('POST');

    request.flush(
      aCart([
        {
          ...KESTREL_SOCKS,
          unitPriceSeen: 17.31,
          notices: { ...GONE_TOTE.notices, unavailable: null },
        },
        LUMEN_MUG,
      ])
    );
    await fixture.whenStable();

    expect(host().querySelector('.banner.changes')).toBeNull();
    expect(announcer.announce).toHaveBeenCalledWith('Noted the changes to your cart.', 'polite');
    // The button went away with the banner: focus starts again at the heading.
    expect(document.activeElement).toBe(host().querySelector('h1'));
  });

  it('offers Acknowledge all only while a change can be accepted', async () => {
    // Only an unavailable line changed: it stays until it is removed.
    await open(aCart([LUMEN_MUG, GONE_TOTE], { canCheckout: false }));

    expect(text(host(), '.banner.changes p')).toBe(
      '1 item in your cart changed since you added it. Check the notes below.'
    );
    expect(host().querySelector('.acknowledge-all')).toBeNull();
  });

  it('says so when Acknowledge all fails, and shows the cart as it is now', async () => {
    await open(aCart([KESTREL_SOCKS]));

    button(host(), 'Acknowledge all changes').click();
    http
      .expectOne('/api/v1/cart/acknowledge')
      .flush('down', { status: 503, statusText: 'Service Unavailable' });
    http.expectOne('/api/v1/cart').flush(aCart([KESTREL_SOCKS]));
    await fixture.whenStable();

    expect(text(host(), '.banner-error')).toBe('We couldn’t note the changes. Try again.');
    expect(button(host(), 'Acknowledge all changes').getAttribute('aria-disabled')).not.toBe(
      'true'
    );
  });

  it('drops the Acknowledge all error once the banner lists other changes', async () => {
    await open(aCart([KESTREL_SOCKS, GONE_TOTE], { canCheckout: false }));

    button(host(), 'Acknowledge all changes').click();
    http
      .expectOne('/api/v1/cart/acknowledge')
      .flush('down', { status: 503, statusText: 'Service Unavailable' });
    http.expectOne('/api/v1/cart').flush(aCart([KESTREL_SOCKS, GONE_TOTE], { canCheckout: false }));
    await fixture.whenStable();

    expect(text(host(), '.banner-error')).toBe('We couldn’t note the changes. Try again.');

    // The socks' changes are accepted on their line; only the tote is left in the banner.
    button(lineOf('Merino Trail Socks'), 'Acknowledge changes to Merino Trail Socks').click();
    http
      .expectOne('/api/v1/cart/lines/line-socks/acknowledge')
      .flush(
        aCart(
          [{ ...KESTREL_SOCKS, notices: { ...GONE_TOTE.notices, unavailable: null } }, GONE_TOTE],
          { canCheckout: false }
        )
      );
    await fixture.whenStable();

    expect(text(host(), '.banner.changes p')).toBe(
      '1 item in your cart changed since you added it. Check the notes below.'
    );
    expect(host().querySelector('.banner-error')).toBeNull();
  });

  it('blocks checkout with the reason while a line cannot be bought', async () => {
    await open(aCart([LUMEN_MUG, GONE_TOTE], { canCheckout: false }));
    const checkout = host().querySelector<HTMLButtonElement>(
      'button.checkout'
    ) as HTMLButtonElement;

    expect(checkout.getAttribute('aria-disabled')).toBe('true');
    expect(checkout.getAttribute('aria-describedby')).toBe('checkout-blocked');
    expect(text(host(), '#checkout-blocked')).toBe(
      '1 item can’t be ordered as it is. Remove it or lower its quantity to check out.'
    );

    checkout.click();
    await fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/');
  });

  it('blocks checkout with how many to remove when a merge left more lines than an order takes (BR-CART-03)', async () => {
    const lines = Array.from({ length: 52 }, (_, i) =>
      aCartLine({ id: `line-${i}`, product: { ...LUMEN_MUG.product, id: `product-${i}` } })
    );
    await open(aCart(lines, { canCheckout: false }));

    expect({
      disabled: host().querySelector('button.checkout')?.getAttribute('aria-disabled'),
      reason: text(host(), '#checkout-blocked'),
    }).toStrictEqual({
      disabled: 'true',
      reason:
        'An order can have at most 50 different products, and your cart has 52. Remove 2 to check out.',
    });
  });

  it('goes to checkout when the cart can be bought', async () => {
    await open(aCart([LUMEN_MUG]));

    host().querySelector<HTMLButtonElement>('button.checkout')?.click();
    await fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/checkout');
  });

  it('offers a quick fix when stock dropped below the quantity', async () => {
    const short: CartLineDto = {
      ...LUMEN_MUG,
      quantity: 4,
      maxQuantity: 2,
      notices: { ...LUMEN_MUG.notices, quantityLimited: { requested: 4, allowed: 2 } },
      blocksCheckout: true,
    };
    await open(aCart([short], { canCheckout: false }));
    const line = lineOf('Enamel Camp Mug');

    expect(text(line, '.notices li')).toBe(
      'Only 2 available. Reduce the quantity to 2 or fewer to check out.'
    );
    expect(button(line, 'Increase quantity of Enamel Camp Mug').getAttribute('aria-disabled')).toBe(
      'true'
    );

    button(line, 'Change to 2').click();

    expect(http.expectOne('/api/v1/cart/lines/line-mug').request.body).toStrictEqual({
      quantity: 2,
    });
  });

  it('steps the quantity within 1 and the line’s maximum and announces the new total', async () => {
    await open(aCart([{ ...KESTREL_SOCKS, notices: LUMEN_MUG.notices }]));
    const line = lineOf('Merino Trail Socks');

    expect(
      button(line, 'Decrease quantity of Merino Trail Socks').getAttribute('aria-disabled')
    ).toBe('true');

    button(line, 'Increase quantity of Merino Trail Socks').click();
    const request = http.expectOne('/api/v1/cart/lines/line-socks');

    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toStrictEqual({ quantity: 2 });

    request.flush(
      aCart([{ ...KESTREL_SOCKS, quantity: 2, notices: LUMEN_MUG.notices }], {
        totals: { subtotal: 34.62, shipping: 4.99, tax: 2.77, total: 42.38, isEstimate: true },
      })
    );
    await fixture.whenStable();

    expect(
      button(lineOf('Merino Trail Socks'), 'Increase quantity of Merino Trail Socks').getAttribute(
        'aria-disabled'
      )
    ).toBe('true');
    expect(announcer.announce).toHaveBeenCalledWith(
      'Merino Trail Socks: quantity 2. Cart total $42.38.',
      'polite'
    );
  });

  it('shows the API’s reason on the line when a change is refused', async () => {
    await open(aCart([LUMEN_MUG]));

    button(lineOf('Enamel Camp Mug'), 'Increase quantity of Enamel Camp Mug').click();
    http.expectOne('/api/v1/cart/lines/line-mug').flush(
      {
        type: 'urn:lumen:problem:cart-quantity-limit',
        title: 'Not enough stock',
        detail: 'Only 2 available.',
      },
      { status: 422, statusText: 'Unprocessable Entity' }
    );
    http.expectOne('/api/v1/cart').flush(aCart([LUMEN_MUG]));
    await fixture.whenStable();

    expect(text(lineOf('Enamel Camp Mug'), '.line-error')).toBe('Only 2 available.');
  });

  it('removes a line and offers Undo, which adds it back', async () => {
    await open(aCart([LUMEN_MUG, KESTREL_SOCKS]));

    button(lineOf('Enamel Camp Mug'), 'Remove Enamel Camp Mug').click();
    const remove = http.expectOne('/api/v1/cart/lines/line-mug');

    expect(remove.request.method).toBe('DELETE');

    remove.flush(aCart([KESTREL_SOCKS]));
    await fixture.whenStable();

    expect(lineOf('Enamel Camp Mug')).toBeUndefined();

    const snackBar =
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(MatSnackBarHarness);

    await expect(snackBar.getMessage()).resolves.toBe('Removed Enamel Camp Mug.');

    await snackBar.dismissWithAction();
    const add = http.expectOne('/api/v1/cart/lines');

    expect(add.request.body).toStrictEqual({ productId: 'lumen-mug', quantity: 2 });

    add.flush(aCart([LUMEN_MUG, KESTREL_SOCKS]));
  });

  it('shows the empty state', async () => {
    await open(EMPTY_CART);

    expect(text(host(), '.empty p')).toBe('Your cart is empty.');
    expect(host().querySelector(':scope .empty a')?.getAttribute('href')).toBe('/');
    expect(host().querySelector('button.checkout')).toBeNull();
  });

  it('shows the "merged your carts" notice until dismissed', async () => {
    TestBed.inject(CartStateService).setMergeNotice(
      'We merged your cart with the items saved in your account. It now has 3 items.'
    );
    await open(aCart([LUMEN_MUG]));

    expect(text(host(), '.banner.merge p')).toContain('We merged your cart');

    button(host(), 'Dismiss merge notice').click();
    await fixture.whenStable();

    expect(host().querySelector('.banner.merge')).toBeNull();
  });

  it('offers Retry when the cart cannot be loaded', async () => {
    fixture = TestBed.createComponent(CartPage);
    fixture.detectChanges();
    http.expectOne('/api/v1/cart').flush('down', { status: 503, statusText: 'Unavailable' });
    await fixture.whenStable();

    expect(text(host(), '[role="alert"] p')).toContain('couldn’t load your cart');

    button(host(), 'Retry').click();
    http.expectOne('/api/v1/cart').flush(aCart([LUMEN_MUG]));
    await fixture.whenStable();

    expect(lineOf('Enamel Camp Mug')).toBeTruthy();
  });
});
