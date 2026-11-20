import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HarnessLoader } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSelectHarness } from '@angular/material/select/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { AdminProduct } from '../../core/api/admin-products.api';
import { PagedResult } from '../../core/api/paged-result';
import { provideAppIcons } from '../../core/icons/app-icons';
import { aPagedResult, anAdminProduct } from '../../testing/order-fixtures';
import { AdminProductsPage } from './admin-products-page';

@Component({ template: '<h1>Edit product</h1>' })
class StubEditPage {}

const MUG = anAdminProduct();
const LAMP = anAdminProduct({
  id: '0193a0f1-0000-7000-8000-000000000002',
  sku: 'LG-LAMP-01',
  name: 'Brass Desk Lamp',
  price: 129,
  onHand: 0,
  reserved: 0,
  isActive: false,
  sellable: false,
});

describe('AdminProductsPage', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let loader: HarnessLoader;
  let router: Router;
  let announcer: LiveAnnouncer;

  const host = () => harness.routeNativeElement as HTMLElement;
  const text = (root: ParentNode | null | undefined, selector: string) =>
    root?.querySelector(selector)?.textContent?.replaceAll(/\s+/g, ' ').trim();
  const rows = () => [...host().querySelectorAll<HTMLElement>('tr.mat-mdc-row')];
  const button = (label: string) =>
    host().querySelector<HTMLButtonElement>(
      `button[aria-label="${CSS.escape(label)}"]`
    ) as HTMLButtonElement;
  const dialog = () => document.querySelector<HTMLElement>('app-confirm-dialog');
  const isList = (request: { url: string }) => request.url === '/api/v1/admin/products';

  async function nextRequest(): Promise<TestRequest> {
    return vi.waitFor(() => {
      const [request] = http.match(isList);
      if (!request) {
        throw new Error('No products request yet');
      }
      return request;
    });
  }

  async function respond(page: PagedResult<AdminProduct>): Promise<TestRequest> {
    const request = await nextRequest();
    request.flush(page);
    await harness.fixture.whenStable();
    return request;
  }

  async function open(url: string, page = aPagedResult([MUG, LAMP])): Promise<TestRequest> {
    await harness.navigateByUrl(url, AdminProductsPage);
    return respond(page);
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'admin/products', component: AdminProductsPage },
          { path: 'admin/products/:id', component: StubEditPage },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    announcer = TestBed.inject(LiveAnnouncer);
    vi.spyOn(announcer, 'announce').mockResolvedValue(undefined);
    harness = await RouterTestingHarness.create();
    loader = TestbedHarnessEnvironment.documentRootLoader(harness.fixture);
  });

  afterEach(() => {
    http.verify();
    vi.restoreAllMocks();
  });

  it('asks the API for what the URL says and shows it in the filters', async () => {
    const request = await open('/admin/products?q=mug&status=inactive&category=home&page=2');

    expect(request.request.urlWithParams).toBe(
      '/api/v1/admin/products?q=mug&status=inactive&category=home&page=2&pageSize=24'
    );
    expect((host().querySelector('input[name="q"]') as HTMLInputElement).value).toBe('mug');

    const [status, category] = await loader.getAllHarnesses(MatSelectHarness);

    await expect(status.getValueText()).resolves.toBe('Inactive');
    await expect(category.getValueText()).resolves.toBe('Home');
  });

  it('searches on submit and filters by status, each starting at page 1', async () => {
    await open('/admin/products?page=2');

    const search = host().querySelector('input[name="q"]') as HTMLInputElement;
    search.value = ' lamp ';
    search.dispatchEvent(new Event('input'));
    (host().querySelector('form[role="search"] button') as HTMLButtonElement).click();
    const searched = await respond(aPagedResult([LAMP]));

    expect(router.url).toBe('/admin/products?q=lamp');
    expect(searched.request.params.get('q')).toBe('lamp');

    const [status] = await loader.getAllHarnesses(MatSelectHarness);
    await status.open();
    const choosing = status.clickOptions({ text: 'Active' });
    await respond(aPagedResult([]));
    await choosing;

    expect(router.url).toBe('/admin/products?q=lamp&status=active');
    expect(text(host(), '.state strong')).toBe('No products match these filters.');
  });

  it('lists products with a link to edit, SKU, price, stock and status', async () => {
    await open('/admin/products');

    const [mug, lamp] = rows();
    const link = mug.querySelector('a') as HTMLAnchorElement;

    expect(link.textContent?.trim()).toBe('Enamel Camp Mug');
    expect(link.getAttribute('href')).toBe(`/admin/products/${MUG.id}`);
    expect(mug.textContent).toContain('SKU LG-MUG-01');
    expect(mug.textContent).toContain('$18.50');
    expect(mug.textContent).toContain('2 reserved');
    expect(text(mug, 'app-status-badge')).toBe('Active');
    expect(text(lamp, 'app-status-badge')).toBe('Inactive');
    expect(button('Reactivate Brass Desk Lamp').textContent?.trim()).toBe('Reactivate');
  });

  it('deactivates a product after confirmation, then reloads the list', async () => {
    await open('/admin/products');

    button('Deactivate Enamel Camp Mug').click();
    await harness.fixture.whenStable();

    expect(text(dialog(), 'h2')).toBe('Deactivate Enamel Camp Mug?');
    expect(text(dialog(), '.message')).toContain('Past orders keep it');

    (dialog()?.querySelector('button.confirm') as HTMLButtonElement).click();
    const post = await vi.waitFor(() =>
      http.expectOne(`/api/v1/admin/products/${MUG.id}/deactivate`)
    );

    expect(post.request.method).toBe('POST');

    post.flush({ ...MUG, isActive: false });
    await respond(aPagedResult([{ ...MUG, isActive: false }, LAMP]));

    expect(text(rows()[0], 'app-status-badge')).toBe('Inactive');
    expect(text(host(), '.action-message')).toBe('Enamel Camp Mug is now inactive.');
    expect(announcer.announce).toHaveBeenCalledWith('Enamel Camp Mug is now inactive.', 'polite');
  });

  it('does nothing when the admin keeps the product active', async () => {
    await open('/admin/products');

    button('Deactivate Enamel Camp Mug').click();
    await harness.fixture.whenStable();
    (dialog()?.querySelector('button.cancel') as HTMLButtonElement).click();
    await harness.fixture.whenStable();

    expect(dialog()).toBeNull();

    http.expectNone(`/api/v1/admin/products/${MUG.id}/deactivate`);
  });

  it('reactivates an inactive product after confirmation', async () => {
    await open('/admin/products');

    button('Reactivate Brass Desk Lamp').click();
    await harness.fixture.whenStable();

    expect(text(dialog(), 'h2')).toBe('Reactivate Brass Desk Lamp?');

    (dialog()?.querySelector('button.confirm') as HTMLButtonElement).click();
    (await vi.waitFor(() => http.expectOne(`/api/v1/admin/products/${LAMP.id}/reactivate`))).flush({
      ...LAMP,
      isActive: true,
    });
    await respond(aPagedResult([MUG, { ...LAMP, isActive: true }]));

    expect(text(host(), '.action-message')).toBe('Brass Desk Lamp is now active.');
  });

  it('shows "No products yet" with a way to add one when there are none at all', async () => {
    await open('/admin/products', aPagedResult([]));

    expect(text(host(), '.state strong')).toBe('No products yet.');
    expect(host().querySelector('.state a')?.getAttribute('href')).toBe('/admin/products/new');
  });

  it.each([401, 403])('asks the user to sign in as an admin on %d', async (status) => {
    await harness.navigateByUrl('/admin/products', AdminProductsPage);
    (await nextRequest()).flush({ title: 'Denied', status }, { status, statusText: 'Denied' });
    await harness.fixture.whenStable();

    expect(text(host(), 'app-admin-access-denied strong')).toBe(
      'Sign in as an admin to see this page.'
    );
    expect(host().querySelector('a[href="/admin/products/new"]')).toBeNull();
  });
});
