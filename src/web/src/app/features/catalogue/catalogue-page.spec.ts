import { LiveAnnouncer } from '@angular/cdk/a11y';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ProductPage } from '../../core/api/products.api';
import { LastCatalogueQuery } from '../../core/catalogue/last-catalogue-query';
import { provideAppIcons } from '../../core/icons/app-icons';
import { SELLERS, aPage, aProduct } from '../../testing/fixtures';
import { CataloguePage } from './catalogue-page';

@Component({ template: '<h1>Product</h1>' })
class StubProductPage {}

const TOTE = aProduct({ id: 'harbor-1', name: 'Waxed Canvas Tote', source: 'harbor' });
const LAMP = aProduct({
  id: 'lumen-7',
  name: 'Brass Desk Lamp',
  source: 'lumen',
  category: 'home',
  price: { amount: 129, currency: 'USD' },
});
const PARKA = aProduct({
  id: 'kestrel-K1',
  name: 'Alpine Parka',
  source: 'kestrel',
  category: 'apparel',
  price: null,
  sellable: false,
});

describe('CataloguePage', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let router: Router;
  let announcer: LiveAnnouncer;

  const host = () => harness.routeNativeElement as HTMLElement;
  const text = (selector: string) =>
    host().querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();
  const isProductsRequest = (url: string) => url === '/api/v1/products';

  /** Waits for the page to ask for products (navigation and effects are async). */
  async function nextProductsRequest(): Promise<TestRequest> {
    return vi.waitFor(() => {
      const [request] = http.match((r) => isProductsRequest(r.url));
      if (!request) {
        throw new Error('No products request yet');
      }
      return request;
    });
  }

  async function respond(page: ProductPage): Promise<TestRequest> {
    const request = await nextProductsRequest();
    request.flush(page);
    await harness.fixture.whenStable();
    return request;
  }

  /** Opens the catalogue at `url` and answers its sellers and products calls. */
  async function open(url: string, page: ProductPage = aPage([TOTE, LAMP, PARKA])) {
    await harness.navigateByUrl(url, CataloguePage);
    http.expectOne('/api/v1/sellers').flush(SELLERS);
    return respond(page);
  }

  function checkbox(label: string): HTMLInputElement {
    const box = [...host().querySelectorAll('aside mat-checkbox')].find(
      (element) => element.textContent?.trim() === label,
    );
    const input = box?.querySelector('input');
    if (!input) {
      throw new Error(`No checkbox "${label}"`);
    }
    return input;
  }

  function button(label: string, root: ParentNode = host()): HTMLButtonElement {
    const match = [...root.querySelectorAll('button')].find(
      (element) => element.textContent?.replace(/\s+/g, ' ').trim() === label,
    );
    if (!match) {
      throw new Error(`No button "${label}"`);
    }
    return match;
  }

  async function expectUrl(url: string): Promise<void> {
    await vi.waitFor(() => expect(router.url).toBe(url));
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: CataloguePage },
          { path: 'products/:id', component: StubProductPage },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
        // jsdom fires no animationend, so the bottom sheet would never finish closing.
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    announcer = TestBed.inject(LiveAnnouncer);
    vi.spyOn(announcer, 'announce').mockResolvedValue(undefined);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => {
    http.verify();
    vi.restoreAllMocks();
  });

  it('asks the API for exactly what the URL says', async () => {
    const request = await open(
      '/?q=tote&category=bags&category=apparel&seller=harbor&minPrice=10&maxPrice=50&inStock=true&sort=price_asc&page=2',
    );

    expect(request.request.urlWithParams).toBe(
      '/api/v1/products?q=tote&category=apparel&category=bags&seller=harbor&minPrice=10' +
        '&maxPrice=50&inStock=true&sort=price_asc&page=2&pageSize=24',
    );
  });

  it('renders a card per product with its seller, price and availability', async () => {
    await open('/');

    const cards = host().querySelectorAll('app-product-card');
    expect(cards).toHaveLength(3);
    expect(cards[0].textContent).toContain('Sold by Harbor Supply Co.');
    expect(cards[1].textContent).toContain('$129.00');
    expect(cards[2].textContent).toContain('Sold by Kestrel Outfitters');
    expect(cards[2].textContent).toContain('Price on request');
    expect(cards[2].textContent).toContain('Not available');
    expect(text('#results-heading')).toBe('3 products');
  });

  it('reflects the URL in the controls, including after Back/Forward style navigation', async () => {
    await open('/?category=bags&inStock=true');
    expect(checkbox('Bags').checked).toBe(true);
    expect(checkbox('Home').checked).toBe(false);

    await harness.navigateByUrl('/?category=home', CataloguePage);
    await respond(aPage([LAMP]));

    expect(checkbox('Bags').checked).toBe(false);
    expect(checkbox('Home').checked).toBe(true);
    expect(text('.applied')).toContain('Home');
    expect(text('.applied')).not.toContain('In stock only');
  });

  it('navigates with the new filter and back to page 1 when a filter changes', async () => {
    await open(
      '/?q=tote&category=bags&page=3',
      aPage([TOTE], { page: 3, totalPages: 3, totalCount: 49 }),
    );

    checkbox('Apparel').click();

    await expectUrl('/?q=tote&category=apparel&category=bags');
    const request = await respond(aPage([TOTE]));
    expect(request.request.params.get('page')).toBe('1');
    expect(request.request.params.getAll('category')).toEqual(['apparel', 'bags']);
  });

  it('applies a seller filter from the names GET /sellers returned', async () => {
    await open('/');

    checkbox('Kestrel Outfitters').click();

    await expectUrl('/?seller=kestrel');
    await respond(aPage([PARKA]));
  });

  it('removes one filter with its chip and all of them with "Clear all", keeping the search', async () => {
    await open('/?q=lamp&category=home&seller=lumen&inStock=true');
    expect(text('.applied mat-chip-set')).toContain('Home');

    const removeHome = host().querySelector<HTMLButtonElement>(
      'button[aria-label="Remove filter: Home"]',
    );
    removeHome?.click();
    await expectUrl('/?q=lamp&seller=lumen&inStock=true');
    await respond(aPage([LAMP]));

    button('Clear all').click();
    await expectUrl('/?q=lamp');
    await respond(aPage([LAMP]));
    expect(host().querySelector('.applied')).toBeNull();
  });

  it('changes the sort from the select and resets the page', async () => {
    await open('/?page=2', aPage([TOTE], { page: 2, totalPages: 2, totalCount: 25 }));

    host().querySelector<HTMLElement>('mat-select')?.click();
    await harness.fixture.whenStable();
    const option = [...document.querySelectorAll<HTMLElement>('mat-option')].find(
      (element) => element.textContent?.trim() === 'Price: high to low',
    );
    option?.click();

    await expectUrl('/?sort=price_desc');
    await respond(aPage([LAMP, TOTE]));
  });

  it('applies a validated price range', async () => {
    await open('/');
    const [min, max] = host().querySelectorAll<HTMLInputElement>('aside .price input');

    min.value = '60';
    min.dispatchEvent(new Event('input'));
    max.value = '20';
    max.dispatchEvent(new Event('input'));
    button('Apply price', host().querySelector('aside') as HTMLElement).click();
    await harness.fixture.whenStable();
    expect(text('aside mat-error')).toBe('Max must be at least the min');
    expect(router.url).toBe('/');

    max.value = '80.50';
    max.dispatchEvent(new Event('input'));
    button('Apply price', host().querySelector('aside') as HTMLElement).click();

    await expectUrl('/?minPrice=60&maxPrice=80.5');
    await respond(aPage([LAMP]));
    expect(text('.applied')).toContain('$60.00 – $80.50');
  });

  it('pages with numbered links that keep the filters, and focuses the results heading', async () => {
    await open(
      '/?category=bags&page=2',
      aPage([TOTE], { page: 2, totalPages: 12, totalCount: 280 }),
    );

    const links = [...host().querySelectorAll<HTMLAnchorElement>('nav[aria-label="Pages"] a')];
    expect(links.map((link) => link.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      'Previous',
      '1',
      '2',
      '3',
      '4',
      '12',
      'Next',
    ]);
    const next = links.at(-1) as HTMLAnchorElement;
    expect(next.getAttribute('href')).toBe('/?category=bags&page=3');
    expect(links[1].getAttribute('href')).toBe('/?category=bags');
    expect(host().querySelector('[aria-current="page"]')?.textContent?.trim()).toBe('2');
    expect(text('.range')).toBe('Showing 25–25 of 280');

    next.click();
    await expectUrl('/?category=bags&page=3');
    await respond(aPage([LAMP], { page: 3, totalPages: 12, totalCount: 280 }));

    expect(document.activeElement).toBe(host().querySelector('#results-heading'));
  });

  it('shows an empty state that clears the filters', async () => {
    await open('/?q=zzz&category=home', aPage([]));

    expect(text('.state h3')).toBe('No products match');
    expect(text('.state p')).toBe('Nothing matches “zzz” with these filters.');

    button('Clear all filters').click();
    await expectUrl('/?q=zzz');
    await respond(aPage([]));
  });

  it('shows an error with Retry that keeps the filters', async () => {
    await harness.navigateByUrl('/?category=bags', CataloguePage);
    http.expectOne('/api/v1/sellers').flush(SELLERS);
    (await nextProductsRequest()).flush('Bad gateway', { status: 502, statusText: 'Bad Gateway' });
    await harness.fixture.whenStable();

    expect(text('[role="alert"] h3')).toBe('We couldn’t load products');

    button('Retry').click();
    const retry = await respond(aPage([TOTE]));
    expect(retry.request.params.getAll('category')).toEqual(['bags']);
    expect(host().querySelectorAll('app-product-card')).toHaveLength(1);
    expect(router.url).toBe('/?category=bags');
  });

  it('explains a 400 from the API and offers to clear the filters', async () => {
    await harness.navigateByUrl('/?minPrice=5', CataloguePage);
    http.expectOne('/api/v1/sellers').flush(SELLERS);
    (await nextProductsRequest()).flush(
      { title: 'One or more validation errors occurred.', detail: 'minPrice is too low.' },
      { status: 400, statusText: 'Bad Request' },
    );
    await harness.fixture.whenStable();

    expect(text('[role="alert"] h3')).toBe('Some filters in this link are not valid');
    expect(text('[role="alert"] p')).toBe('minPrice is too low.');
  });

  it('announces the result count after a change, but not on the first load', async () => {
    await open('/');
    expect(announcer.announce).not.toHaveBeenCalled();

    checkbox('Bags').click();
    await respond(aPage([TOTE]));

    expect(announcer.announce).toHaveBeenCalledWith('1 product', 'polite');
  });

  it('opens the filters in a bottom sheet on small screens', async () => {
    await open('/?category=bags');

    button('Filters (1)').click();
    await harness.fixture.whenStable();

    const sheet = document.querySelector('mat-bottom-sheet-container') as HTMLElement;
    expect(sheet).not.toBeNull();
    expect(sheet.querySelector('h2')?.textContent).toBe('Filters');
    button('Show 3 products', sheet).click();
    await vi.waitFor(() => expect(document.querySelector('mat-bottom-sheet-container')).toBeNull());
  });

  it('remembers its query for links back from product pages', async () => {
    await open('/?q=tote&page=2', aPage([TOTE], { page: 2, totalPages: 2, totalCount: 25 }));

    expect(TestBed.inject(LastCatalogueQuery).params()).toEqual({ q: 'tote', page: '2' });
  });
});
