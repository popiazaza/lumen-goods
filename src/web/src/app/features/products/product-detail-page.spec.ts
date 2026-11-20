import { LiveAnnouncer } from '@angular/cdk/a11y';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ProductDto } from '../../core/api/products.api';
import { CartStateService } from '../../core/cart/cart-state.service';
import { LastCatalogueQuery } from '../../core/catalogue/last-catalogue-query';
import { provideAppIcons } from '../../core/icons/app-icons';
import { SELLERS, aCart, aCartLine, aProduct } from '../../testing/fixtures';
import { ProductDetailPage } from './product-detail-page';

@Component({ selector: 'app-stub-page', template: '<h1>Elsewhere</h1>' })
class StubPage {}

/**
At a limit the quantity buttons stay focusable and say they are disabled (disabledInteractive).
*/
const isDisabled = (button: HTMLButtonElement) =>
  !button.disabled && button.getAttribute('aria-disabled') === 'true';

describe('ProductDetailPage', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let announcer: LiveAnnouncer;

  const host = () => harness.routeNativeElement as HTMLElement;
  const text = (selector: string) =>
    host().querySelector(selector)?.textContent?.replaceAll(/\s+/gu, ' ').trim();
  const mainImage = () => host().querySelector(':scope .main-image img') as HTMLImageElement;
  const quantityInput = () =>
    host().querySelector<HTMLInputElement>(':scope .quantity input') as HTMLInputElement;
  const byLabel = (label: string) =>
    host().querySelector<HTMLButtonElement>(
      `button[aria-label="${CSS.escape(label)}"]`
    ) as HTMLButtonElement;
  const addButton = () =>
    host().querySelector<HTMLButtonElement>('button.add') as HTMLButtonElement;

  const open = async (product: ProductDto): Promise<void> => {
    await harness.navigateByUrl(`/products/${product.id}`, ProductDetailPage);
    http.expectOne('/api/v1/sellers').flush(SELLERS);
    http.expectOne(`/api/v1/products/${product.id}`).flush(product);
    await harness.fixture.whenStable();
  };

  const typeQuantity = async (value: string): Promise<void> => {
    const input = quantityInput();
    input.value = value;
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('blur'));
    await harness.fixture.whenStable();
  };

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [
            { path: 'products/:id', component: ProductDetailPage },
            { path: '', component: StubPage },
            { path: 'cart', component: StubPage },
          ],
          withComponentInputBinding()
        ),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    announcer = TestBed.inject(LiveAnnouncer);
    vi.spyOn(announcer, 'announce').mockResolvedValue(undefined);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => {
    http.verify();
    vi.restoreAllMocks();
  });

  it('shows the product: name, seller, price, availability and humanised attributes', async () => {
    await open(
      aProduct({
        id: 'kestrel-K1',
        source: 'kestrel',
        name: 'Alpine Down Parka',
        price: { amount: 389.5, currency: 'USD' },
        attributes: { color: 'Navy', fillPower: '800', sizeEU: '50' },
      })
    );

    expect({
      heading: text('h1'),
      title: TestBed.inject(Title).getTitle(),
      seller: text('.seller'),
      price: text('.buy app-product-price'),
      availability: text('.buy app-availability-badge'),
    }).toStrictEqual({
      heading: 'Alpine Down Parka',
      title: 'Alpine Down Parka — Lumen Goods',
      seller: 'Sold by Kestrel Outfitters',
      price: '$389.50',
      availability: 'In stock',
    });

    const terms = [...host().querySelectorAll(':scope .attributes dt')].map((dt) => dt.textContent);
    const values = [...host().querySelectorAll(':scope .attributes dd')].map(
      (dd) => dd.textContent
    );

    expect({ terms, values }).toStrictEqual({
      terms: ['Color', 'Fill power', 'Size EU'],
      values: ['Navy', '800', '50'],
    });
  });

  it('renders an HTML description through Angular’s sanitiser, and plain text as text', async () => {
    vi.spyOn(console, 'warn').mockReturnValue(undefined);
    await open(
      aProduct({
        description: '<p>Warm <strong>800 fill</strong> down.</p><script>alert(1)</script>',
      })
    );
    const description = host().querySelector('.description') as HTMLElement;

    expect(description.querySelector('strong')?.textContent).toBe('800 fill');
    expect(description.querySelector('script')).toBeNull();

    await harness.navigateByUrl('/products/lumen-2', ProductDetailPage);
    http
      .expectOne('/api/v1/products/lumen-2')
      .flush(aProduct({ id: 'lumen-2', description: 'Line one\n5 < 6 & <tag> stays text' }));
    await harness.fixture.whenStable();
    const plain = host().querySelector('.description') as HTMLElement;

    expect(plain.classList).toContain('plain');
    expect(plain.textContent).toBe('Line one\n5 < 6 & <tag> stays text');
  });

  it('shows the primary image first and switches images from the thumbnails', async () => {
    await open(
      aProduct({
        name: 'Tote',
        images: [
          { url: 'http://localhost/media/side.jpg', isPrimary: false },
          { url: 'http://localhost/media/front.jpg', isPrimary: true },
        ],
      })
    );

    expect(mainImage().getAttribute('src')).toBe('http://localhost/media/front.jpg');
    expect(mainImage().getAttribute('alt')).toBe('Tote, image 1 of 2');

    const second = byLabel('Show image 2 of 2');
    second.click();
    await harness.fixture.whenStable();

    expect(mainImage().getAttribute('src')).toBe('http://localhost/media/side.jpg');
    expect(second.getAttribute('aria-pressed')).toBe('true');
  });

  it('bounds the quantity at 1, keeping Decrease focused', async () => {
    await open(aProduct({ availability: { status: 'low_stock', quantity: 3 } }));

    expect({
      value: quantityInput().value,
      decreaseDisabled: isDisabled(byLabel('Decrease quantity')),
      hint: text('mat-hint'),
    }).toStrictEqual({ value: '1', decreaseDisabled: true, hint: '3 available' });

    byLabel('Decrease quantity').focus();
    byLabel('Decrease quantity').click();
    await harness.fixture.whenStable();

    expect(quantityInput().value).toBe('1');
    expect(document.activeElement).toBe(byLabel('Decrease quantity'));
  });

  it('bounds the quantity at the stock left, keeping Increase focused', async () => {
    await open(aProduct({ availability: { status: 'low_stock', quantity: 3 } }));

    byLabel('Increase quantity').click();
    byLabel('Increase quantity').focus();
    byLabel('Increase quantity').click();
    await harness.fixture.whenStable();

    expect({
      value: quantityInput().value,
      increaseDisabled: isDisabled(byLabel('Increase quantity')),
    }).toStrictEqual({ value: '3', increaseDisabled: true });
    expect(document.activeElement).toBe(byLabel('Increase quantity'));

    byLabel('Increase quantity').click();
    await harness.fixture.whenStable();

    expect(quantityInput().value).toBe('3');
  });

  it('bounds a typed quantity between 1 and the stock left', async () => {
    await open(aProduct({ availability: { status: 'low_stock', quantity: 3 } }));

    await typeQuantity('25');

    expect(quantityInput().value).toBe('3');

    await typeQuantity('0');

    expect(quantityInput().value).toBe('1');
  });

  it('allows up to 10 when the stock is unknown but in stock', async () => {
    await open(aProduct({ availability: { status: 'in_stock', quantity: null } }));

    await typeQuantity('99');

    expect(quantityInput().value).toBe('10');
    expect(text('mat-hint')).toBe('Up to 10 at a time');
  });

  it.each([
    [
      'out of stock',
      aProduct({ availability: { status: 'out_of_stock', quantity: 0 } }),
      'This product is out of stock right now.',
    ],
    ['not sellable', aProduct({ sellable: false }), 'This product is no longer available.'],
    [
      'priced on request',
      aProduct({ sellable: false, price: null }),
      'This product has no online price, so it can’t be bought here.',
    ],
  ])('disables Add to cart when %s and says why', async (_, product, reason) => {
    await open(product);

    expect(addButton().getAttribute('aria-disabled')).toBe('true');
    expect(addButton().getAttribute('aria-describedby')).toBe('buy-note');
    expect(text('#buy-note')).toBe(reason);
    expect(quantityInput().disabled).toBeTruthy();

    addButton().click();
    await harness.fixture.whenStable();

    expect(TestBed.inject(CartStateService).itemCount()).toBe(0);
  });

  it('adds to the cart through the API, updates the badge count and announces it', async () => {
    const cart = TestBed.inject(CartStateService);
    await open(aProduct({ id: 'harbor-1', name: 'Waxed Canvas Tote' }));
    byLabel('Increase quantity').click();
    await harness.fixture.whenStable();

    addButton().click();
    const request = http.expectOne('/api/v1/cart/lines');

    expect({ method: request.request.method, body: request.request.body }).toStrictEqual({
      method: 'POST',
      body: { productId: 'harbor-1', quantity: 2 },
    });

    request.flush(aCart([aCartLine({ quantity: 3 })]));
    await harness.fixture.whenStable();

    expect(cart.itemCount()).toBe(3);
    expect(text('.added span')).toBe('Added 2 to cart.');
    expect(host().querySelector(':scope .added a')?.getAttribute('href')).toBe('/cart');
    expect(announcer.announce).toHaveBeenCalledWith(
      'Added to cart: 2 × Waxed Canvas Tote. Cart has 3 items.',
      'polite'
    );
  });

  it('shows the API’s reason when the add is refused, and re-reads the product', async () => {
    await open(aProduct({ id: 'harbor-1' }));

    addButton().click();
    http.expectOne('/api/v1/cart/lines').flush(
      {
        type: 'urn:lumen:problem:cart-quantity-limit',
        title: 'Not enough stock',
        detail: 'Only 1 more can be added: you already have 9 in your cart.',
        maxQuantity: 10,
        inCart: 9,
      },
      { status: 422, statusText: 'Unprocessable Entity' }
    );
    await vi.waitFor(() =>
      http.expectOne('/api/v1/products/harbor-1').flush(aProduct({ id: 'harbor-1' }))
    );
    await harness.fixture.whenStable();

    expect(text('.add-error')).toBe('Only 1 more can be added: you already have 9 in your cart.');
    expect(host().querySelector('.added')).toBeNull();
  });

  it('links the breadcrumb back to the catalogue’s last search', async () => {
    TestBed.inject(LastCatalogueQuery).remember({ q: 'tote', category: ['bags'], page: '2' });
    await open(aProduct({ category: 'bags', name: 'Waxed Canvas Tote' }));

    const crumbs = host().querySelectorAll(':scope nav[aria-label="Breadcrumb"] li');

    expect([...crumbs].map((li) => li.textContent?.trim())).toStrictEqual([
      'Shop',
      'Bags',
      'Waxed Canvas Tote',
    ]);
    expect(crumbs[0].querySelector('a')?.getAttribute('href')).toBe(
      '/?q=tote&category=bags&page=2'
    );
    expect(crumbs[1].querySelector('a')?.getAttribute('href')).toBe('/?category=bags');
    expect(crumbs[2].getAttribute('aria-current')).toBe('page');
  });

  it('shows a friendly not-found page for a 404', async () => {
    await harness.navigateByUrl('/products/nope', ProductDetailPage);
    http.expectOne('/api/v1/sellers').flush(SELLERS);
    http
      .expectOne('/api/v1/products/nope')
      .flush({ title: 'Not Found', status: 404 }, { status: 404, statusText: 'Not Found' });
    await harness.fixture.whenStable();

    expect(text('h1')).toBe('Product not found');
    expect(text('.state a')).toBe('Back to the shop');
  });

  it('offers Retry when the product fails to load', async () => {
    await harness.navigateByUrl('/products/harbor-1', ProductDetailPage);
    http.expectOne('/api/v1/sellers').flush(SELLERS);
    http
      .expectOne('/api/v1/products/harbor-1')
      .flush('down', { status: 503, statusText: 'Unavailable' });
    await harness.fixture.whenStable();

    expect(text('[role="alert"] p')).toContain('couldn’t load this product');

    (host().querySelector(':scope [role="alert"] button') as HTMLButtonElement).click();
    await vi.waitFor(() => http.expectOne('/api/v1/products/harbor-1').flush(aProduct()));
    await harness.fixture.whenStable();

    expect(text('h1')).toBe('Waxed Canvas Tote');
  });
});
