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
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { OwnMediaFile } from '../../core/api/admin-products.api';
import { provideAppIcons } from '../../core/icons/app-icons';
import { anAdminProduct } from '../../testing/order-fixtures';
import { AdminProductEditPage } from './admin-product-edit-page';
import { unsavedChangesGuard } from './unsaved-changes.guard';

@Component({ selector: 'app-stub-list-page', template: '<h1>Products</h1>' })
class StubListPage {}

const text = (root: ParentNode | null | undefined, selector: string) =>
  root?.querySelector(selector)?.textContent?.replaceAll(/\s+/gu, ' ').trim();

/**
A button of the open dialog; throws until it is there, for `vi.waitFor`.
*/
const dialogButton = (name: string): HTMLButtonElement => {
  const found = [
    ...document.querySelectorAll<HTMLButtonElement>('mat-dialog-container button'),
  ].find((element) => element.textContent?.trim() === name);
  if (!found) {
    throw new Error(`No "${name}" button in a dialog`);
  }
  return found;
};

const MUG = anAdminProduct();
const PRODUCT_URL = `/api/v1/admin/products/${MUG.id}`;
const MEDIA: OwnMediaFile[] = [
  { file: 'mug.png', url: '/media/own/mug.png' },
  { file: 'lamp.png', url: '/media/own/lamp.png' },
  { file: 'tote.png', url: '/media/own/tote.png' },
];

describe('AdminProductEditPage', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let loader: HarnessLoader;
  let router: Router;

  const host = () => harness.routeNativeElement as HTMLElement;
  const field = (label: string) =>
    [...host().querySelectorAll<HTMLElement>('mat-form-field')].find(
      (element) => element.querySelector('mat-label')?.textContent?.trim() === label
    ) as HTMLElement;
  const input = (label: string) =>
    field(label).querySelector('input, textarea') as HTMLInputElement;
  const errorOf = (label: string) => text(field(label), 'mat-error');
  const hintOf = (label: string) => text(field(label), 'mat-hint');
  const button = (name: string) =>
    [...host().querySelectorAll<HTMLButtonElement>('button')].find(
      (element) => element.textContent?.replaceAll(/\s+/gu, ' ').trim() === name
    ) as HTMLButtonElement;
  const libraryImage = (file: string) =>
    [...host().querySelectorAll<HTMLButtonElement>('.library-image')].find((element) =>
      element.textContent?.includes(file)
    ) as HTMLButtonElement;

  const type = async (label: string, value: string): Promise<void> => {
    const element = input(label);
    element.value = value;
    element.dispatchEvent(new Event('input'));
    element.dispatchEvent(new Event('blur'));
    await harness.fixture.whenStable();
  };

  const submit = async (): Promise<void> => {
    (host().querySelector('button[type="submit"]') as HTMLButtonElement).click();
    await harness.fixture.whenStable();
  };

  const openNew = async (): Promise<void> => {
    await harness.navigateByUrl('/admin/products/new', AdminProductEditPage);
    http.expectOne('/api/v1/admin/own-media').flush(MEDIA);
    await harness.fixture.whenStable();
  };

  const openMug = async (): Promise<void> => {
    await harness.navigateByUrl(`/admin/products/${MUG.id}`, AdminProductEditPage);
    http.expectOne(PRODUCT_URL).flush(MUG);
    http.expectOne('/api/v1/admin/own-media').flush(MEDIA);
    await harness.fixture.whenStable();
  };

  const nextSave = (method: 'POST' | 'PUT'): Promise<TestRequest> =>
    vi.waitFor(() =>
      http.expectOne(
        (r) =>
          r.method === method &&
          r.url === (method === 'POST' ? '/api/v1/admin/products' : PRODUCT_URL)
      )
    );

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [
            { path: 'admin/products', component: StubListPage },
            {
              path: 'admin/products/new',
              component: AdminProductEditPage,
              canDeactivate: [unsavedChangesGuard],
            },
            {
              path: 'admin/products/:id',
              component: AdminProductEditPage,
              canDeactivate: [unsavedChangesGuard],
            },
          ],
          withComponentInputBinding()
        ),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue(undefined);
    harness = await RouterTestingHarness.create();
    loader = TestbedHarnessEnvironment.documentRootLoader(harness.fixture);
  });

  afterEach(() => {
    http.verify();
    vi.restoreAllMocks();
  });

  it('checks the form before sending anything', async () => {
    await openNew();

    await submit();
    http.expectNone('/api/v1/admin/products');

    expect({
      name: errorOf('Name'),
      category: errorOf('Category'),
      price: errorOf('Price'),
      // Keyboard users land on the first field to fix.
      isNameFocused: document.activeElement === input('Name'),
    }).toStrictEqual({
      name: 'Name is required.',
      category: 'Category is required.',
      price: 'Price is required.',
      isNameFocused: true,
    });

    await type('Price', '18.555');

    expect(errorOf('Price')).toBe('Enter dollars and cents, e.g. 18.50.');

    await type('Price', '0');

    expect(errorOf('Price')).toBe('Price must be more than $0.00.');

    await type('On hand', '2.5');
    await type('SKU (optional)', '-bad');

    expect({ onHand: errorOf('On hand'), sku: errorOf('SKU (optional)') }).toStrictEqual({
      onHand: 'Enter a whole number.',
      sku: 'Use 2 to 32 letters, digits or dashes, starting with a letter or digit.',
    });
  });

  it('caps the price as the API does, so the largest order still fits the order totals (BR-ADM-01)', async () => {
    await openNew();

    await type('Price', '1000000');
    const above = errorOf('Price');
    await type('Price', '999999.99');

    expect({ above, atMax: errorOf('Price') ?? null }).toStrictEqual({
      above: 'Price can be at most $999,999.99.',
      atMax: null,
    });
  });

  it('creates a product with attributes and images, then returns to the list', async () => {
    await openNew();

    await type('Name', '  Trail Mug ');
    await type('Description', 'Keeps coffee warm.');
    const category = await loader.getHarness(MatSelectHarness);
    await category.clickOptions({ text: 'Home' });
    await type('Price', '24.5');
    await type('On hand', '12');
    button('Add attribute').click();
    await harness.fixture.whenStable();
    await type('Name 1', 'Color');
    await type('Value 1', 'Olive');
    libraryImage('tote.png').click();
    libraryImage('mug.png').click();
    await harness.fixture.whenStable();

    expect(libraryImage('mug.png').getAttribute('aria-pressed')).toBe('true');
    expect(text(libraryImage('mug.png'), '.picked')).toBe('Chosen');

    // Make the mug the main image.
    (
      host().querySelector('button[aria-label="Make mug.png the main image"]') as HTMLButtonElement
    ).click();
    await harness.fixture.whenStable();

    await submit();
    const post = await nextSave('POST');

    expect(post.request.body).toStrictEqual({
      sku: null,
      name: 'Trail Mug',
      description: 'Keeps coffee warm.',
      category: 'home',
      price: 24.5,
      attributes: { Color: 'Olive' },
      images: ['mug.png', 'tote.png'],
      onHand: 12,
    });

    post.flush(anAdminProduct({ name: 'Trail Mug' }), { status: 201, statusText: 'Created' });
    await harness.fixture.whenStable();

    expect(router.url).toBe('/admin/products');
  });

  it('edits a product, sending the version it was loaded with', async () => {
    await openMug();

    expect({ heading: text(host(), 'h1'), subtitle: text(host(), '.subtitle') }).toStrictEqual({
      heading: 'Edit product',
      subtitle: expect.stringContaining('Enamel Camp Mug · SKU LG-MUG-01 · Active'),
    });
    // The SKU can only be set when the product is created.
    expect(field('SKU (optional)')).toBeUndefined();
    expect({
      name: input('Name').value,
      price: input('Price').value,
      onHand: input('On hand').value,
      onHandHint: hintOf('On hand'),
      firstAttributeName: input('Name 1').value,
      secondAttributeValue: input('Value 2').value,
      firstImage: text(host(), '.chosen li'),
    }).toStrictEqual({
      name: 'Enamel Camp Mug',
      price: '18.50',
      onHand: '10',
      onHandHint: '2 reserved by orders awaiting payment',
      firstAttributeName: 'color',
      secondAttributeValue: 'Enamel steel',
      firstImage: expect.stringContaining('Main image'),
    });

    await type('Name', 'Enamel Camp Mug, 12 oz');
    (
      host().querySelector('button[aria-label="Remove attribute material"]') as HTMLButtonElement
    ).click();
    await submit();

    const put = await nextSave('PUT');

    expect(put.request.body).toStrictEqual({
      version: '41',
      name: 'Enamel Camp Mug, 12 oz',
      description: 'A sturdy enamel mug.',
      category: 'home',
      price: 18.5,
      attributes: { color: 'Cream' },
      images: ['mug.png'],
      onHand: 10,
    });

    put.flush({ ...MUG, name: 'Enamel Camp Mug, 12 oz', version: '42' });
    await harness.fixture.whenStable();

    expect(router.url).toBe('/admin/products');
  });

  it('does not let on hand go below what is reserved', async () => {
    await openMug();

    await type('On hand', '1');
    await submit();

    http.expectNone(PRODUCT_URL);

    expect(errorOf('On hand')).toBe(
      '2 reserved by orders awaiting payment: on hand must be at least 2.'
    );
  });

  it('shows the API’s validation errors under their fields', async () => {
    await openMug();

    await submit();
    const put = await nextSave('PUT');
    put.flush(
      {
        title: 'The product is not valid',
        status: 400,
        errors: {
          name: ['Name cannot contain control characters such as tabs or line breaks.'],
          attributes: ["Two attributes are both named 'color'."],
          version: ['version is required.'],
        },
      },
      { status: 400, statusText: 'Bad Request' }
    );
    await harness.fixture.whenStable();

    expect(errorOf('Name')).toBe(
      'Name cannot contain control characters such as tabs or line breaks.'
    );
    expect(text(host(), '.group-error')).toBe("Two attributes are both named 'color'.");
    expect(text(host(), '#form-error')).toBe('version is required.');
    expect(router.url).toBe(`/admin/products/${MUG.id}`);
  });

  it('on "changed by someone else" reloads, keeps the edits visible and saves on the new version', async () => {
    await openMug();
    await type('Name', 'Camp Mug (my edit)');
    await type('Price', '19.00');

    await submit();
    const put = await nextSave('PUT');
    put.flush(
      {
        title: 'Changed by someone else',
        status: 409,
        detail: 'This product was changed by someone else since you loaded it.',
      },
      { status: 409, statusText: 'Conflict' }
    );
    // Someone else changed the description and a customer reserved one more unit.
    const latest = anAdminProduct({
      description: 'Their new description.',
      onHand: 10,
      reserved: 3,
      version: '44',
    });
    const reload = await vi.waitFor(() =>
      http.expectOne((r) => r.method === 'GET' && r.url === PRODUCT_URL)
    );
    reload.flush(latest);
    await harness.fixture.whenStable();

    expect({
      title: text(host(), '#conflict strong'),
      focusedId: document.activeElement?.id,
      subtitle: text(host(), '.subtitle'),
    }).toStrictEqual({
      title: 'Changed by someone else',
      focusedId: 'conflict',
      subtitle: expect.stringContaining('Enamel Camp Mug'),
    });
    // The admin's edits are still in the form...
    expect({
      name: input('Name').value,
      price: input('Price').value,
      description: input('Description').value,
    }).toStrictEqual({
      name: 'Camp Mug (my edit)',
      price: '19.00',
      description: 'A sturdy enamel mug.',
    });
    // ...and every field where the saved version differs says what it is now.
    expect({
      name: hintOf('Name'),
      price: hintOf('Price'),
      description: hintOf('Description'),
      hasCategoryHint: hintOf('Category') !== undefined,
    }).toStrictEqual({
      name: 'Saved now: Enamel Camp Mug',
      price: 'Saved now: $18.50',
      description: 'Saved now: Their new description.',
      hasCategoryHint: false,
    });

    await submit();
    const retry = await nextSave('PUT');

    expect(retry.request.body).toMatchObject({
      version: '44',
      name: 'Camp Mug (my edit)',
      description: 'A sturdy enamel mug.',
    });

    retry.flush({ ...latest, name: 'Camp Mug (my edit)', version: '45' });
    await harness.fixture.whenStable();

    expect(router.url).toBe('/admin/products');
  });

  it('can drop the edits and take the saved version after a conflict', async () => {
    await openMug();
    await type('Name', 'Mine');
    await submit();
    const put = await nextSave('PUT');
    put.flush(
      { title: 'Changed by someone else', status: 409 },
      { status: 409, statusText: 'Conflict' }
    );
    const reload = await vi.waitFor(() =>
      http.expectOne((r) => r.method === 'GET' && r.url === PRODUCT_URL)
    );
    reload.flush(anAdminProduct({ name: 'Theirs', version: '50' }));
    await harness.fixture.whenStable();

    button('Discard my edits and use the saved version').click();
    await harness.fixture.whenStable();

    expect(input('Name').value).toBe('Theirs');
    expect(host().querySelector('#conflict')).toBeNull();
  });

  it('explains a 409 for on hand below newly reserved stock at the field', async () => {
    await openMug();
    await type('On hand', '2');

    await submit();
    const put = await nextSave('PUT');
    put.flush(
      {
        title: 'On hand cannot go below reserved stock',
        status: 409,
        detail: '4 unit(s) are reserved by orders awaiting payment, so on hand must be at least 4.',
        reserved: 4,
      },
      { status: 409, statusText: 'Conflict' }
    );
    await harness.fixture.whenStable();

    expect(errorOf('On hand')).toBe(
      '4 unit(s) are reserved by orders awaiting payment, so on hand must be at least 4.'
    );
    expect(host().querySelector('#conflict')).toBeNull();

    // The form now knows about the 4 reserved units.
    await type('On hand', '3');

    expect(errorOf('On hand')).toBe(
      '4 reserved by orders awaiting payment: on hand must be at least 4.'
    );
  });

  it('shows a taken SKU at the SKU field when creating', async () => {
    await openNew();
    await type('SKU (optional)', 'lg-mug-01');
    await type('Name', 'Mug');
    const category = await loader.getHarness(MatSelectHarness);
    await category.clickOptions({ text: 'Home' });
    await type('Price', '10');

    await submit();
    const post = await nextSave('POST');

    expect(post.request.body.sku).toBe('lg-mug-01');

    post.flush(
      {
        title: 'SKU already in use',
        status: 409,
        detail: "Another own product already has SKU 'LG-MUG-01'.",
      },
      { status: 409, statusText: 'Conflict' }
    );
    await harness.fixture.whenStable();

    expect(errorOf('SKU (optional)')).toBe("Another own product already has SKU 'LG-MUG-01'.");
  });

  it('asks before leaving unsaved edits, and stays unless the admin discards them', async () => {
    await openMug();
    await type('Name', 'Enamel Camp Mug, large');

    const stay = router.navigateByUrl('/admin/products');
    await vi.waitFor(() => dialogButton('Keep editing').click());

    await expect(stay).resolves.toBeFalsy();
    expect(router.url).toBe(`/admin/products/${MUG.id}`);

    const leave = router.navigateByUrl('/admin/products');
    await vi.waitFor(() => dialogButton('Discard changes').click());

    await expect(leave).resolves.toBeTruthy();
    expect(router.url).toBe('/admin/products');
  });

  it('leaves without asking when nothing was changed', async () => {
    await openMug();

    await expect(router.navigateByUrl('/admin/products')).resolves.toBeTruthy();
    expect(document.querySelector('mat-dialog-container')).toBeNull();
  });

  it('says when the product does not exist', async () => {
    await harness.navigateByUrl('/admin/products/nope', AdminProductEditPage);
    http
      .expectOne('/api/v1/admin/products/nope')
      .flush({ title: 'Product not found', status: 404 }, { status: 404, statusText: 'Not Found' });
    http.expectOne('/api/v1/admin/own-media').flush(MEDIA);
    await harness.fixture.whenStable();

    expect(text(host(), '.state strong')).toBe('There is no Lumen Goods product with this id.');
    expect(host().querySelector('form')).toBeNull();
  });

  it.each([401, 403])('asks the user to sign in as an admin on %d', async (status) => {
    await harness.navigateByUrl(`/admin/products/${MUG.id}`, AdminProductEditPage);
    http
      .expectOne(PRODUCT_URL)
      .flush({ title: 'Denied', status }, { status, statusText: 'Denied' });
    http
      .expectOne('/api/v1/admin/own-media')
      .flush({ title: 'Denied', status }, { status, statusText: 'Denied' });
    await harness.fixture.whenStable();

    expect(text(host(), 'app-admin-access-denied strong')).toBe(
      'Sign in as an admin to see this page.'
    );
  });
});
