import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppIcons } from '../../core/icons/app-icons';
import { aPagedResult, anOrderSummary } from '../../testing/order-fixtures';
import { OrderListPage, parsePage } from './order-list-page';

@Component({ template: '<h1>Order</h1>' })
class StubOrderPage {}

const PROCESSING = anOrderSummary();
const REFUNDED = anOrderSummary({
  number: 'LG-2B8N4WQ1',
  status: 'partially_refunded',
  statusText: 'Partially refunded',
  createdUtc: '2026-11-18T10:00:00Z',
  total: 120.4,
  refundedTotal: 40.03,
  itemCount: 4,
  sellerNames: ['Harbor Supply Co.', 'Kestrel Outfitters'],
});

describe('OrderListPage', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  const host = () => harness.routeNativeElement as HTMLElement;
  const text = (root: ParentNode, selector: string) =>
    root.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();
  const cards = () => [...host().querySelectorAll<HTMLElement>('.orders mat-card')];
  const pager = () => [
    ...[...host().querySelectorAll('.pager a')].map((link) => link.textContent?.trim()),
    text(host(), '.pager .position'),
  ];
  const isOrdersRequest = (request: { url: string }) => request.url === '/api/v1/orders';

  async function nextRequest(): Promise<TestRequest> {
    return vi.waitFor(() => {
      const [request] = http.match(isOrdersRequest);
      if (!request) {
        throw new Error('No orders request yet');
      }
      return request;
    });
  }

  async function open(url: string): Promise<TestRequest> {
    await harness.navigateByUrl(url, OrderListPage);
    return nextRequest();
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'orders', component: OrderListPage },
          { path: 'orders/:id', component: StubOrderPage },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => http.verify());

  it('lists orders newest first with number, date, total, refund, status and shops', async () => {
    const request = await open('/orders');
    expect(request.request.urlWithParams).toBe('/api/v1/orders?page=1&pageSize=10');
    request.flush(aPagedResult([PROCESSING, REFUNDED]));
    await harness.fixture.whenStable();

    expect(cards()).toHaveLength(2);
    const [first, second] = cards();
    expect(text(first, 'h3')).toBe('Order LG-7K3M9QX2');
    expect(first.querySelector('h3 a')?.getAttribute('href')).toBe('/orders/LG-7K3M9QX2');
    expect(text(first, 'app-status-badge')).toBe('Processing');
    expect(first.querySelector('time')?.getAttribute('datetime')).toBe('2026-11-20T08:00:00Z');
    expect(text(first, '.price')).toBe('$75.46');
    expect(first.querySelector('.refunded')).toBeNull();
    expect(text(first, '.sellers dd')).toBe('Lumen Goods, Kestrel Outfitters');

    expect(text(second, 'app-status-badge')).toBe('Partially refunded');
    expect(
      second.querySelector('app-status-badge mat-icon')?.getAttribute('data-mat-icon-name'),
    ).toBe('undo');
    expect(text(second, '.refunded dd')).toBe('$40.03');
    expect(text(second, '.sellers dd')).toBe('Harbor Supply Co., Kestrel Outfitters');
    expect(text(host(), '.count')).toBe('2 orders');
  });

  it('pages through the history with ?page=, asking the API for that page', async () => {
    const pageOf = (page: number) =>
      aPagedResult([anOrderSummary({ number: `LG-PAGE-${page}` })], {
        page,
        totalCount: 25,
        totalPages: 3,
      });

    (await open('/orders')).flush(pageOf(1));
    await harness.fixture.whenStable();
    expect(pager()).toEqual(['Next', 'Page 1 of 3']);

    const next = [...host().querySelectorAll<HTMLAnchorElement>('.pager a')].find((link) =>
      link.textContent?.includes('Next'),
    ) as HTMLAnchorElement;
    expect(next.getAttribute('href')).toBe('/orders?page=2');
    next.click();

    const second = await nextRequest();
    expect(second.request.urlWithParams).toBe('/api/v1/orders?page=2&pageSize=10');
    expect(TestBed.inject(Router).url).toBe('/orders?page=2');
    second.flush(pageOf(2));
    await harness.fixture.whenStable();

    expect(pager()).toEqual(['Previous', 'Next', 'Page 2 of 3']);
    const previous = host().querySelector<HTMLAnchorElement>('.pager a');
    // Page 1 has no page parameter, so it has one URL.
    expect(previous?.getAttribute('href')).toBe('/orders');
    expect(text(cards()[0], 'h3')).toBe('Order LG-PAGE-2');
  });

  it('treats a page that is not a positive whole number as page 1', () => {
    expect(parsePage(null)).toBe(1);
    expect(parsePage('0')).toBe(1);
    expect(parsePage('2.5')).toBe(1);
    expect(parsePage('abc')).toBe(1);
    expect(parsePage('4')).toBe(4);
  });

  it('shows an empty state with a way to start shopping', async () => {
    (await open('/orders')).flush(aPagedResult([]));
    await harness.fixture.whenStable();

    expect(text(host(), '.state strong')).toBe('No orders yet.');
    expect(host().querySelector('.state a')?.getAttribute('href')).toBe('/');
    expect(host().querySelector('.pager')).toBeNull();
  });

  it('asks a signed-out visitor to sign in', async () => {
    (await open('/orders')).flush(
      { title: 'Unauthorized', status: 401 },
      { status: 401, statusText: 'Unauthorized' },
    );
    await harness.fixture.whenStable();

    expect(text(host(), '.state strong')).toBe('Sign in to see your orders.');
    expect(host().querySelector('.state a')?.getAttribute('href')).toBe(
      '/sign-in?returnUrl=%2Forders',
    );
  });

  it('offers Retry when the history fails to load', async () => {
    (await open('/orders')).flush('down', { status: 500, statusText: 'Error' });
    await harness.fixture.whenStable();

    (host().querySelector('[role="alert"] button') as HTMLButtonElement).click();
    (await nextRequest()).flush(aPagedResult([PROCESSING]));
    await harness.fixture.whenStable();

    expect(cards()).toHaveLength(1);
  });
});
