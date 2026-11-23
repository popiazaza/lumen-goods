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

    expect(host.querySelector('.state h1')?.textContent).toBe('Page not found');
    expect(await (await shop.host()).getAttribute('href')).toBe('/');
    expect(await shop.getAppearance()).toBe('filled');
  });
});
