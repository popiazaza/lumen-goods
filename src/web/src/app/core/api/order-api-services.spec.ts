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
      '/api/v1/orders?page=3&pageSize=10',
    );

    const order = firstValueFrom(api.get('LG 7/K'));
    http.expectOne('/api/v1/orders/LG%207%2FK').flush(anOrderDetail());
    expect((await order).number).toBe('LG-7K3M9QX2');
  });

  it('AdminOrdersApi sends only the filters that are set, repeating status (snake_case)', () => {
    const api = TestBed.inject(AdminOrdersApi);
    api
      .list({
        statuses: ['awaiting_payment', 'partially_refunded'],
        from: '2026-11-01',
        to: null,
        page: 2,
        pageSize: 24,
      })
      .subscribe();
    expect(http.expectOne(() => true).request.urlWithParams).toBe(
      '/api/v1/admin/orders?status=awaiting_payment&status=partially_refunded&from=2026-11-01' +
        '&page=2&pageSize=24',
    );

    api.list({}).subscribe();
    expect(http.expectOne('/api/v1/admin/orders').request.params.keys()).toEqual([]);
  });

  it('AdminOrdersApi resolves a line with the decision and a trimmed note', async () => {
    const api = TestBed.inject(AdminOrdersApi);
    const updated = firstValueFrom(api.resolveLine('LG-1', 'line-2', 'reject', '  lost  '));

    const request = http.expectOne('/api/v1/admin/orders/LG-1/lines/line-2/resolve');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ decision: 'reject', note: 'lost' });
    request.flush(anAdminOrder());
    expect((await updated).customerEmail).toBe('customer@lumengoods.test');

    api.resolveLine('LG-1', 'line-2', 'confirm', '   ').subscribe();
    expect(http.expectOne('/api/v1/admin/orders/LG-1/lines/line-2/resolve').request.body).toEqual({
      decision: 'confirm',
      note: null,
    });
  });

  it('AdminProductsApi lists with search and filters, leaving out "all"', () => {
    const api = TestBed.inject(AdminProductsApi);
    api.list({ q: ' mug ', status: 'inactive', category: 'home', page: 2 }).subscribe();
    expect(http.expectOne(() => true).request.urlWithParams).toBe(
      '/api/v1/admin/products?q=mug&status=inactive&category=home&page=2',
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
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual({ ...input, sku: null });

    api.update('p 1', { ...input, version: '41' }).subscribe();
    const update = http.expectOne('/api/v1/admin/products/p%201');
    expect(update.request.method).toBe('PUT');
    expect(update.request.body.version).toBe('41');

    api.deactivate('p1').subscribe();
    expect(http.expectOne('/api/v1/admin/products/p1/deactivate').request.method).toBe('POST');
    api.reactivate('p1').subscribe();
    expect(http.expectOne('/api/v1/admin/products/p1/reactivate').request.method).toBe('POST');
    api.media().subscribe();
    expect(http.expectOne('/api/v1/admin/own-media').request.method).toBe('GET');
  });
});
