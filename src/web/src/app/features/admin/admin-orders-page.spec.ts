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
import { AdminOrderSummary } from '../../core/api/admin-orders.api';
import { PagedResult } from '../../core/api/paged-result';
import { provideAppIcons } from '../../core/icons/app-icons';
import { itemAt } from '../../testing/items';
import { aPagedResult, anAdminOrderSummary } from '../../testing/order-fixtures';
import { stubOverlayMeasuring } from '../../testing/overlay-stubs';
import { AdminOrdersPage } from './admin-orders-page';

@Component({ selector: 'app-stub-order-page', template: '<h1>Order</h1>' })
class StubOrderPage {}

/**
The instant the page sends for local midnight of a day (`YYYY-MM-DD` plus `addDays`).
*/
const localMidnight = (day: string, addDays = 0): string => {
  const parts = day.split('-').map(Number);
  const midnight = new Date(itemAt(parts, 0), itemAt(parts, 1) - 1, itemAt(parts, 2) + addDays);
  return midnight.toISOString();
};

const text = (root: ParentNode, selector: string) =>
  root.querySelector(selector)?.textContent?.replaceAll(/\s+/gu, ' ').trim();

/**
An option of the open status list, by its text; throws until the list is open, for `vi.waitFor`.
*/
const statusOption = (label: string): HTMLElement => {
  const option = [...document.querySelectorAll<HTMLElement>('[role="option"]')].find(
    (element) => element.textContent?.trim() === label
  );
  if (!option) {
    throw new Error(`No status option "${label}" open`);
  }
  return option;
};

const PAID = anAdminOrderSummary();
const ATTENTION = anAdminOrderSummary({
  number: 'LG-9QW3E7RT',
  status: 'paid',
  statusText: 'Processing',
  needsAttention: true,
  refundedTotal: 0,
});

describe('AdminOrdersPage', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let router: Router;
  let restoreOverlayMeasuring: () => void;

  const host = () => harness.routeNativeElement as HTMLElement;
  /**
  The status select's trigger (a combobox button named by its label).
  */
  const statusSelect = () => host().querySelector('button#order-status') as HTMLButtonElement;
  const pager = () => host().querySelector('app-admin-paginator') as HTMLElement;
  const dateInput = (name: 'from' | 'to') =>
    host().querySelector(`input[name="${CSS.escape(name)}"]`) as HTMLInputElement;

  const nextRequest = (): Promise<TestRequest> =>
    vi.waitFor(() => {
      const [request] = http.match((r) => r.url === '/api/v1/admin/orders');
      if (!request) {
        throw new Error('No orders request yet');
      }
      return request;
    });

  const respond = async (page: PagedResult<AdminOrderSummary>): Promise<TestRequest> => {
    const request = await nextRequest();
    request.flush(page);
    await harness.fixture.whenStable();
    return request;
  };

  const open = async (url: string, page = aPagedResult([PAID, ATTENTION])) => {
    await harness.navigateByUrl(url, AdminOrdersPage);
    return respond(page);
  };

  beforeEach(async () => {
    restoreOverlayMeasuring = stubOverlayMeasuring();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'admin/orders', component: AdminOrdersPage },
          { path: 'admin/orders/:id', component: StubOrderPage },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => {
    http.verify();
    restoreOverlayMeasuring();
  });

  it('asks the API for exactly what the URL says and shows it in the filters', async () => {
    const request = await open(
      '/admin/orders?status=needs_attention&status=refunded&status=completed&status=paid&status=bogus' +
        '&from=2026-11-01&to=2026-11-20&page=2'
    );

    // Unknown statuses are dropped; the rest go in a canonical order. The days are the admin's
    // own: from local midnight, to the next local midnight after the last day (exclusive).
    const { params } = request.request;

    expect({
      status: params.getAll('status'),
      from: params.get('from'),
      to: params.get('to'),
      page: params.get('page'),
      pageSize: params.get('pageSize'),
    }).toStrictEqual({
      status: ['paid', 'completed', 'refunded', 'needs_attention'],
      from: localMidnight('2026-11-01'),
      to: localMidnight('2026-11-20', 1),
      page: '2',
      pageSize: '24',
    });

    expect(statusSelect().textContent?.trim()).toBe(
      // Completed reads "Confirmed": every seller confirmed, nothing has shipped.
      'Paid, in progress, Confirmed, Refunded, Needs attention'
    );
    expect({ from: dateInput('from').value, to: dateInput('to').value }).toStrictEqual({
      from: '2026-11-01',
      to: '2026-11-20',
    });
  });

  it('ignores dates that are not real days', async () => {
    const request = await open('/admin/orders?from=2026-02-30&to=yesterday');

    expect(request.request.urlWithParams).toBe('/api/v1/admin/orders?page=1&pageSize=24');
  });

  it('writes a status choice to the URL and starts again at page 1', async () => {
    await open('/admin/orders?page=3');

    expect({
      name: host().querySelector(':scope label[for="order-status"]')?.textContent?.trim(),
      placeholder: statusSelect().textContent?.trim(),
    }).toStrictEqual({ name: 'Status', placeholder: 'All statuses' });

    statusSelect().click();
    await harness.fixture.whenStable();

    expect(document.querySelector('[role="listbox"]')?.getAttribute('aria-multiselectable')).toBe(
      'true'
    );

    const option = await vi.waitFor(() => statusOption('Partially refunded'));
    option.click();
    const request = await nextRequest();
    request.flush(aPagedResult([]));
    await harness.fixture.whenStable();

    expect(router.url).toBe('/admin/orders?status=partially_refunded');
    expect(request.request.urlWithParams).toBe(
      '/api/v1/admin/orders?status=partially_refunded&page=1&pageSize=24'
    );
  });

  it('writes the placed-date range to the URL', async () => {
    await open('/admin/orders');

    dateInput('from').value = '2026-11-01';
    dateInput('from').dispatchEvent(new Event('change'));
    await respond(aPagedResult([PAID]));

    expect(router.url).toBe('/admin/orders?from=2026-11-01');

    dateInput('to').value = '2026-11-15';
    dateInput('to').dispatchEvent(new Event('change'));
    const request = await respond(aPagedResult([PAID]));

    expect(router.url).toBe('/admin/orders?from=2026-11-01&to=2026-11-15');
    expect(request.request.params.get('to')).toBe(localMidnight('2026-11-15', 1));
    // The browser's own picker keeps the range the right way round.
    expect(dateInput('from').max).toBe('2026-11-15');
    expect(dateInput('to').min).toBe('2026-11-01');
  });

  it('pages with the paginator, keeping the filters in the URL', async () => {
    await open(
      '/admin/orders?status=paid',
      aPagedResult([PAID], { pageSize: 24, totalCount: 60, totalPages: 3 })
    );

    expect({
      label: pager().getAttribute('aria-label'),
      range: text(pager(), '.range'),
      previousDisabled: pager().querySelector<HTMLButtonElement>(
        'button[aria-label="Previous page"]'
      )?.disabled,
    }).toStrictEqual({ label: 'Order pages', range: '1 – 24 of 60', previousDisabled: true });

    pager().querySelector<HTMLButtonElement>('button[aria-label="Next page"]')?.click();
    const request = await nextRequest();
    request.flush(aPagedResult([PAID], { page: 2, pageSize: 24, totalCount: 60, totalPages: 3 }));
    await harness.fixture.whenStable();

    expect(router.url).toBe('/admin/orders?status=paid&page=2');
    expect(request.request.params.get('page')).toBe('2');
  });

  it('lists each order with a link, customer, shops, total and status badges', async () => {
    await open('/admin/orders');

    const rows = [...host().querySelectorAll<HTMLElement>(':scope tbody tr')];

    expect(rows).toHaveLength(2);

    const paid = itemAt(rows, 0);
    const attention = itemAt(rows, 1);
    const link = paid.querySelector('a') as HTMLAnchorElement;

    expect({
      number: link.textContent?.trim(),
      href: link.getAttribute('href'),
      customer: text(paid, '.col-customer'),
      sellers: text(paid, '.col-sellers'),
      total: text(paid, '.col-total'),
      // The number never breaks; a long email may break only before its @.
      numberNowrap: paid.querySelector(':scope .col-number')?.classList.contains('nowrap'),
      emailBreak: paid.querySelector(':scope .col-customer wbr')?.nextSibling?.textContent?.trim(),
    }).toStrictEqual({
      number: 'LG-7K3M9QX2',
      href: '/admin/orders/LG-7K3M9QX2',
      customer: 'customer@lumengoods.test',
      sellers: 'Lumen Goods, Kestrel Outfitters',
      total: '$75.46',
      numberNowrap: true,
      emailBreak: '@lumengoods.test',
    });
    expect(
      [...attention.querySelectorAll('app-status-badge')].map((badge) => badge.textContent?.trim())
    ).toStrictEqual(['Processing', 'Needs attention']);
    expect(text(host(), '.count')).toBe('2 orders');
  });

  it('tells "no orders yet" apart from "no orders match", and clears filters', async () => {
    await open('/admin/orders', aPagedResult([]));

    expect(text(host(), '.state strong')).toBe('No orders yet.');

    await harness.navigateByUrl('/admin/orders?status=expired', AdminOrdersPage);
    await respond(aPagedResult([]));

    expect(text(host(), '.state strong')).toBe('No orders match these filters.');

    (host().querySelector(':scope .state button') as HTMLButtonElement).click();
    await respond(aPagedResult([PAID]));

    expect(router.url).toBe('/admin/orders');
  });

  it('shows the API’s message for filters it rejects', async () => {
    await harness.navigateByUrl('/admin/orders?from=2026-11-20&to=2026-11-01', AdminOrdersPage);
    const request = await nextRequest();
    request.flush(
      { title: 'The order list query is not valid', detail: 'from must be on or before to.' },
      { status: 400, statusText: 'Bad Request' }
    );
    await harness.fixture.whenStable();

    expect(text(host(), '[role="alert"] p')).toBe('from must be on or before to.');
  });

  it.each([401, 403])('asks the user to sign in as an admin on %d', async (status) => {
    await harness.navigateByUrl('/admin/orders', AdminOrdersPage);
    const request = await nextRequest();
    request.flush({ title: 'Denied', status }, { status, statusText: 'Denied' });
    await harness.fixture.whenStable();

    expect(text(host(), 'app-admin-access-denied strong')).toBe(
      'Sign in as an admin to see this page.'
    );
    expect(host().querySelector('table')).toBeNull();
    expect(host().textContent?.includes('does not have the admin role')).toBe(status === 403);
  });
});
