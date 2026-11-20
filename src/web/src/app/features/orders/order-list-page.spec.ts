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
import { itemAt } from '../../testing/items';
import { aPagedResult, anOrderSummary } from '../../testing/order-fixtures';
import { OrderListPage, parsePage } from './order-list-page';

@Component({ selector: 'app-stub-order-page', template: '<h1>Order</h1>' })
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

const text = (root: ParentNode, selector: string) =>
  root.querySelector(selector)?.textContent?.replaceAll(/\s+/gu, ' ').trim();
const isOrdersRequest = (request: { url: string }) => request.url === '/api/v1/orders';

/**
One order on the given page of a 25-order history.
*/
const pageOf = (page: number) =>
  aPagedResult([anOrderSummary({ number: `LG-PAGE-${page}` })], {
    page,
    totalCount: 25,
    totalPages: 3,
  });

describe('OrderListPage', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  const host = () => harness.routeNativeElement as HTMLElement;
  const cards = () => [...host().querySelectorAll<HTMLElement>(':scope .orders mat-card')];
  const pager = () => [
    ...[...host().querySelectorAll(':scope .pager a')].map((link) => link.textContent?.trim()),
    text(host(), '.pager .position'),
  ];

  const nextRequest = (): Promise<TestRequest> =>
    vi.waitFor(() => {
      const [request] = http.match(isOrdersRequest);
      if (!request) {
        throw new Error('No orders request yet');
      }
      return request;
    });

  const openOrders = async (): Promise<TestRequest> => {
    await harness.navigateByUrl('/orders', OrderListPage);
    return nextRequest();
  };

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
    const request = await openOrders();

    expect(request.request.urlWithParams).toBe('/api/v1/orders?page=1&pageSize=10');

    request.flush(aPagedResult([PROCESSING, REFUNDED]));
    await harness.fixture.whenStable();

    expect(cards()).toHaveLength(2);

    const shown = cards();
    const first = itemAt(shown, 0);
    const second = itemAt(shown, 1);

    expect({
      title: text(first, 'h3'),
      link: first.querySelector(':scope h3 a')?.getAttribute('href'),
      status: text(first, 'app-status-badge'),
      date: first.querySelector('time')?.getAttribute('datetime'),
      total: text(first, '.price'),
      refund: first.querySelector('.refunded'),
      shops: text(first, '.sellers dd'),
    }).toStrictEqual({
      title: 'Order LG-7K3M9QX2',
      link: '/orders/LG-7K3M9QX2',
      status: 'Processing',
      date: '2026-11-20T08:00:00Z',
      total: '$75.46',
      refund: null,
      shops: 'Lumen Goods, Kestrel Outfitters',
    });
    expect({
      status: text(second, 'app-status-badge'),
      statusIcon: second
        .querySelector(':scope app-status-badge mat-icon')
        ?.getAttribute('data-mat-icon-name'),
      refund: text(second, '.refunded dd'),
      shops: text(second, '.sellers dd'),
    }).toStrictEqual({
      status: 'Partially refunded',
      statusIcon: 'undo',
      refund: '$40.03',
      shops: 'Harbor Supply Co., Kestrel Outfitters',
    });
    expect(text(host(), '.count')).toBe('2 orders');
  });

  it('pages through the history with ?page=, asking the API for that page', async () => {
    const first = await openOrders();
    first.flush(pageOf(1));
    await harness.fixture.whenStable();

    expect(pager()).toStrictEqual(['Next', 'Page 1 of 3']);

    const next = [...host().querySelectorAll<HTMLAnchorElement>(':scope .pager a')].find((link) =>
      link.textContent?.includes('Next')
    ) as HTMLAnchorElement;

    expect(next.getAttribute('href')).toBe('/orders?page=2');

    next.click();

    const second = await nextRequest();

    expect({
      request: second.request.urlWithParams,
      url: TestBed.inject(Router).url,
    }).toStrictEqual({
      request: '/api/v1/orders?page=2&pageSize=10',
      url: '/orders?page=2',
    });

    second.flush(pageOf(2));
    await harness.fixture.whenStable();

    const previous = host().querySelector<HTMLAnchorElement>(':scope .pager a');

    // Page 1 has no page parameter, so it has one URL.
    expect({
      pager: pager(),
      previousLink: previous?.getAttribute('href'),
      firstOrder: text(itemAt(cards(), 0), 'h3'),
    }).toStrictEqual({
      pager: ['Previous', 'Next', 'Page 2 of 3'],
      previousLink: '/orders',
      firstOrder: 'Order LG-PAGE-2',
    });
  });

  it('treats a page that is not a positive whole number as page 1', () => {
    expect(parsePage(null)).toBe(1);
    expect(parsePage('0')).toBe(1);
    expect(parsePage('2.5')).toBe(1);
    expect(parsePage('abc')).toBe(1);
    expect(parsePage('4')).toBe(4);
  });

  it('shows an empty state with a way to start shopping', async () => {
    const request = await openOrders();
    request.flush(aPagedResult([]));
    await harness.fixture.whenStable();

    expect(text(host(), '.state strong')).toBe('No orders yet.');
    expect(host().querySelector(':scope .state a')?.getAttribute('href')).toBe('/');
    expect(host().querySelector('.pager')).toBeNull();
  });

  it('asks a signed-out visitor to sign in', async () => {
    const request = await openOrders();
    request.flush(
      { title: 'Unauthorized', status: 401 },
      { status: 401, statusText: 'Unauthorized' }
    );
    await harness.fixture.whenStable();

    expect(text(host(), '.state strong')).toBe('Sign in to see your orders.');
    expect(host().querySelector(':scope .state a')?.getAttribute('href')).toBe(
      '/sign-in?returnUrl=%2Forders'
    );
  });

  it('offers Retry when the history fails to load', async () => {
    const failed = await openOrders();
    failed.flush('down', { status: 500, statusText: 'Error' });
    await harness.fixture.whenStable();

    (host().querySelector(':scope [role="alert"] button') as HTMLButtonElement).click();
    const retry = await nextRequest();
    retry.flush(aPagedResult([PROCESSING]));
    await harness.fixture.whenStable();

    expect(cards()).toHaveLength(1);
  });
});
