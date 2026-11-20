import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import {
  aPagedResult,
  anAdminOrder,
  anAdminProduct,
  anOrderDetail,
} from '../../testing/order-fixtures';
import { AdminOrdersApi } from './admin-orders.api';
import { AdminProductsApi } from './admin-products.api';
import { OrdersApi } from './orders.api';

describe('Order and admin API services', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('OrdersApi lists a page of the history and reads one order by its escaped number', async () => {
    const api = TestBed.inject(OrdersApi);
    api.list(3).subscribe();

    expect(http.expectOne(() => true).request.urlWithParams).toBe(
      '/api/v1/orders?page=3&pageSize=10'
    );

    const order = firstValueFrom(api.get('LG 7/K'));
    http.expectOne('/api/v1/orders/LG%207%2FK').flush(anOrderDetail());
    const detail = await order;

    expect(detail.number).toBe('LG-7K3M9QX2');
  });

  it('AdminOrdersApi sends only the filters that are set, repeating status (snake_case)', () => {
    const api = TestBed.inject(AdminOrdersApi);
    api
      .list({
        statuses: ['awaiting_payment', 'partially_refunded', 'needs_attention'],
        from: '2026-11-01',
        to: null,
        page: 2,
        pageSize: 24,
      })
      .subscribe();
    // The API reads a bare date as a UTC day; the admin means their own, so local midnight goes.
    const firstLocalMidnight = new Date(2026, 10, 1);
    const fromInstant = firstLocalMidnight.toISOString();
    const { params } = http.expectOne((r) => r.url === '/api/v1/admin/orders').request;

    expect({
      keys: params.keys(),
      status: params.getAll('status'),
      from: params.get('from'),
      page: params.get('page'),
      pageSize: params.get('pageSize'),
    }).toStrictEqual({
      keys: ['status', 'from', 'page', 'pageSize'],
      status: ['awaiting_payment', 'partially_refunded', 'needs_attention'],
      from: fromInstant,
      page: '2',
      pageSize: '24',
    });

    api.list({ from: '2026-11-01', to: '2026-11-01' }).subscribe();
    const day = http.expectOne(() => true).request.params;
    // `to` is exclusive on the API, so the whole last day is the next local midnight.
    const nextLocalMidnight = new Date(2026, 10, 2);

    expect({ from: day.get('from'), to: day.get('to') }).toStrictEqual({
      from: fromInstant,
      to: nextLocalMidnight.toISOString(),
    });

    api.list({}).subscribe();

    expect(http.expectOne('/api/v1/admin/orders').request.params.keys()).toStrictEqual([]);
  });

  it('AdminOrdersApi resolves a line with the decision and a trimmed note', async () => {
    const api = TestBed.inject(AdminOrdersApi);
    const updated = firstValueFrom(api.resolveLine('LG-1', 'line-2', 'reject', '  lost  '));

    const request = http.expectOne('/api/v1/admin/orders/LG-1/lines/line-2/resolve');

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toStrictEqual({ decision: 'reject', note: 'lost' });

    request.flush(anAdminOrder());
    const order = await updated;

    expect(order.customerEmail).toBe('customer@lumengoods.test');

    api.resolveLine('LG-1', 'line-2', 'confirm', ' '.repeat(3)).subscribe();

    expect(
      http.expectOne('/api/v1/admin/orders/LG-1/lines/line-2/resolve').request.body
    ).toStrictEqual({
      decision: 'confirm',
      note: null,
    });
  });

  it('AdminProductsApi lists with search and filters, leaving out "all"', () => {
    const api = TestBed.inject(AdminProductsApi);
    api.list({ q: ' mug ', status: 'inactive', category: 'home', page: 2 }).subscribe();

    expect(http.expectOne(() => true).request.urlWithParams).toBe(
      '/api/v1/admin/products?q=mug&status=inactive&category=home&page=2'
    );

    api.list({ q: '', status: 'all' }).subscribe();
    http.expectOne('/api/v1/admin/products').flush(aPagedResult([anAdminProduct()]));
  });

  it('AdminProductsApi creates, updates with the version, (de)activates and lists media', () => {
    const api = TestBed.inject(AdminProductsApi);
    const input = {
      name: 'Mug',
      description: '',
      category: 'home' as const,
      price: 18.5,
      attributes: { color: 'Cream' },
      images: ['mug.png'],
      onHand: 4,
    };

    api.create({ ...input, sku: null }).subscribe();
    const create = http.expectOne('/api/v1/admin/products');

    expect({ method: create.request.method, body: create.request.body }).toStrictEqual({
      method: 'POST',
      body: { ...input, sku: null },
    });

    api.update('p 1', { ...input, version: '41' }).subscribe();
    const update = http.expectOne('/api/v1/admin/products/p%201');

    expect({ method: update.request.method, version: update.request.body.version }).toStrictEqual({
      method: 'PUT',
      version: '41',
    });

    api.deactivate('p1').subscribe();

    expect(http.expectOne('/api/v1/admin/products/p1/deactivate').request.method).toBe('POST');

    api.reactivate('p1').subscribe();

    expect(http.expectOne('/api/v1/admin/products/p1/reactivate').request.method).toBe('POST');

    api.media().subscribe();

    expect(http.expectOne('/api/v1/admin/own-media').request.method).toBe('GET');
  });
});
