import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { aPage, aPartner, aProduct } from '../../testing/fixtures';
import { AdminPartnersApi, hasRefreshRun } from './admin-partners.api';
import { ProductsApi } from './products.api';
import { SellersApi } from './sellers.api';

describe('API services', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('ProductsApi.search sends repeated filter keys and the page size', async () => {
    const page = aPage([aProduct()]);
    const result = firstValueFrom(
      TestBed.inject(ProductsApi).search({
        q: ' tote ',
        categories: ['apparel', 'bags'],
        sellers: ['harbor', 'kestrel'],
        minPrice: 10,
        maxPrice: 49.5,
        inStock: true,
        sort: 'price_desc',
        page: 2,
        pageSize: 24,
      })
    );

    const request = http.expectOne((r) => r.url === '/api/v1/products');

    expect(request.request.method).toBe('GET');
    expect(request.request.urlWithParams).toBe(
      '/api/v1/products?q=tote&category=apparel&category=bags&seller=harbor&seller=kestrel' +
        '&minPrice=10&maxPrice=49.5&inStock=true&sort=price_desc&page=2&pageSize=24'
    );

    request.flush(page);

    await expect(result).resolves.toStrictEqual(page);
  });

  it('ProductsApi.search leaves out empty filters', () => {
    TestBed.inject(ProductsApi)
      .search({ q: '', categories: [], minPrice: null, inStock: false })
      .subscribe();

    expect(http.expectOne('/api/v1/products').request.params.keys()).toStrictEqual([]);
  });

  it('ProductsApi.get escapes the id', () => {
    TestBed.inject(ProductsApi).get('kestrel/K 1').subscribe();

    http.expectOne('/api/v1/products/kestrel%2FK%201').flush(aProduct());
  });

  it('SellersApi.list reads the sellers', async () => {
    const result = firstValueFrom(TestBed.inject(SellersApi).list());

    http.expectOne('/api/v1/sellers').flush([{ source: 'harbor', name: 'Harbor Supply Co.' }]);

    await expect(result).resolves.toStrictEqual([{ source: 'harbor', name: 'Harbor Supply Co.' }]);
  });

  it('AdminPartnersApi lists partners and POSTs a refresh, reporting the server accept time', async () => {
    const api = TestBed.inject(AdminPartnersApi);
    api.list().subscribe();
    http.expectOne('/api/v1/admin/partners').flush([aPartner()]);

    const accepted = firstValueFrom(api.refresh('harbor'));
    const refresh = http.expectOne('/api/v1/admin/partners/harbor/refresh');

    expect(refresh.request.method).toBe('POST');

    refresh.flush(null, {
      status: 202,
      statusText: 'Accepted',
      headers: { Date: 'Fri, 20 Nov 2026 06:10:00 GMT' },
    });

    await expect(accepted).resolves.toBe(Date.parse('2026-11-20T06:10:00Z'));

    const withoutDate = firstValueFrom(api.refresh('kestrel'));
    http
      .expectOne('/api/v1/admin/partners/kestrel/refresh')
      .flush(null, { status: 202, statusText: 'Accepted' });

    await expect(withoutDate).resolves.toBeNull();
  });

  it('hasRefreshRun needs the request consumed and an attempt at or after it', () => {
    const at = Date.parse('2026-11-20T06:10:00Z');
    const ran = aPartner({ refreshRequestedUtc: null, lastAttemptUtc: '2026-11-20T06:10:00.000Z' });

    expect(hasRefreshRun(ran, at)).toBeTruthy();
    expect(hasRefreshRun({ ...ran, refreshRequestedUtc: '2026-11-20T06:10:00Z' }, at)).toBeFalsy();
    expect(hasRefreshRun({ ...ran, lastAttemptUtc: '2026-11-20T06:09:59.999Z' }, at)).toBeFalsy();
    expect(hasRefreshRun({ ...ran, lastAttemptUtc: null }, at)).toBeFalsy();
    expect(hasRefreshRun({ ...ran, lastAttemptUtc: '2026-11-20T08:10:00+02:00' }, at)).toBeTruthy();
  });
});
