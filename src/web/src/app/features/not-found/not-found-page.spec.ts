import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideAppIcons } from '../../core/icons/app-icons';
import { NotFoundPage } from './not-found-page';

describe('NotFoundPage', () => {
  it('shows the empty-state look with one way back to the shop', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideAppIcons()] });
    const fixture = TestBed.createComponent(NotFoundPage);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const shop = [...host.querySelectorAll('a')].find(
      (link) => link.textContent?.trim() === 'Go to the shop'
    );

    expect(host.querySelector(':scope .state h1')?.textContent).toBe('Page not found');
    expect(shop?.getAttribute('href')).toBe('/');
    // The one way on is the page's main button (the filled, primary look).
    expect(shop?.dataset['slot']).toBe('button');
    expect(shop?.className).toContain('bg-primary');
  });
});
