import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatTabNavBarHarness } from '@angular/material/tabs/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { AdminLayout } from './admin-layout';

@Component({ selector: 'app-stub-page', template: '<h1>Admin page</h1>' })
class StubPage {}

describe('AdminLayout', () => {
  let harness: RouterTestingHarness;

  const tabs = (): Promise<MatTabNavBarHarness> =>
    TestbedHarnessEnvironment.loader(harness.fixture).getHarness(MatTabNavBarHarness);
  const activeLabel = async (): Promise<string> => {
    const bar = await tabs();
    const link = await bar.getActiveLink();
    return link.getLabel();
  };

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

  it('links every admin section as a tab, marking the current one', async () => {
    await harness.navigateByUrl('/admin/orders');

    const bar = await tabs();
    const links = await bar.getLinks();
    const labels = await Promise.all(links.map((link) => link.getLabel()));
    const anchors = [...(harness.routeNativeElement as HTMLElement).querySelectorAll('a')];

    expect(labels).toStrictEqual(['Products', 'Orders', 'Partners']);
    expect(anchors.map((anchor) => anchor.getAttribute('href'))).toStrictEqual([
      '/admin/products',
      '/admin/orders',
      '/admin/partners',
    ]);
    expect(anchors.map((anchor) => anchor.getAttribute('aria-selected'))).toStrictEqual([
      'false',
      'true',
      'false',
    ]);
    await expect(activeLabel()).resolves.toBe('Orders');
  });

  it('keeps a section active on its detail pages and moves between sections', async () => {
    await harness.navigateByUrl('/admin/products/p-7');

    await expect(activeLabel()).resolves.toBe('Products');

    const bar = await tabs();
    await bar.clickLink({ label: 'Partners' });

    expect(TestBed.inject(Router).url).toBe('/admin/partners');
    await expect(activeLabel()).resolves.toBe('Partners');
  });
});
