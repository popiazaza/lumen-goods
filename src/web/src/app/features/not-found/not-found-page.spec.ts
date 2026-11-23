import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { MatButtonHarness } from '@angular/material/button/testing';
import { provideRouter } from '@angular/router';
import { provideAppIcons } from '../../core/icons/app-icons';
import { NotFoundPage } from './not-found-page';

describe('NotFoundPage', () => {
  it('shows the empty-state look with one way back to the shop', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideAppIcons()] });
    const fixture = TestBed.createComponent(NotFoundPage);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const shop = await TestbedHarnessEnvironment.loader(fixture).getHarness(
      MatButtonHarness.with({ text: 'Go to the shop' })
    );

    const shopHost = await shop.host();

    expect(host.querySelector(':scope .state h1')?.textContent).toBe('Page not found');
    await expect(shopHost.getAttribute('href')).resolves.toBe('/');
    await expect(shop.getAppearance()).resolves.toBe('filled');
  });
});
