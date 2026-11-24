import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { AdminLayout } from './admin-layout';

@Component({ selector: 'app-stub-page', template: '<h1>Admin page</h1>' })
class StubPage {}

describe('AdminLayout', () => {
  let harness: RouterTestingHarness;

  const nav = () =>
    (harness.routeNativeElement as HTMLElement).querySelector(
      'nav[aria-label="Admin sections"]'
    ) as HTMLElement;
  const links = () => [...nav().querySelectorAll('a')];
  const current = () =>
    links()
      .filter((link) => link.getAttribute('aria-current') === 'page')
      .map((link) => link.textContent?.trim());

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          {
            path: 'admin',
            component: AdminLayout,
            children: [
              { path: 'products', component: StubPage },
              { path: 'products/:id', component: StubPage },
              { path: 'orders', component: StubPage },
              { path: 'orders/:id', component: StubPage },
              { path: 'partners', component: StubPage },
            ],
          },
        ]),
      ],
    });
    harness = await RouterTestingHarness.create();
  });

  it('links every admin section, marking the current one', async () => {
    await harness.navigateByUrl('/admin/orders');

    expect(links().map((link) => link.textContent?.trim())).toStrictEqual([
      'Products',
      'Orders',
      'Partners',
    ]);
    expect(links().map((anchor) => anchor.getAttribute('href'))).toStrictEqual([
      '/admin/products',
      '/admin/orders',
      '/admin/partners',
    ]);
    expect(current()).toStrictEqual(['Orders']);
  });

  it('keeps a section current on its detail pages and moves between sections', async () => {
    await harness.navigateByUrl('/admin/products/p-7');

    expect(current()).toStrictEqual(['Products']);

    links()
      .find((link) => link.textContent?.trim() === 'Partners')
      ?.click();
    await harness.fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/admin/partners');
    expect(current()).toStrictEqual(['Partners']);
  });
});
