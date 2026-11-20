import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatBadgeHarness } from '@angular/material/badge/testing';
import { Router, provideRouter } from '@angular/router';
import { App } from './app';
import { routes } from './app.routes';
import { CartStateService } from './core/cart/cart-state.service';
import { provideAppIcons } from './core/icons/app-icons';
import { EMPTY_CART } from './core/api/cart.api';
import { aCart, aCartLine } from './testing/fixtures';

function query<T extends Element>(fixture: ComponentFixture<App>, selector: string): T {
  const element = (fixture.nativeElement as HTMLElement).querySelector<T>(selector);
  if (!element) {
    throw new Error(`No element matches ${selector}`);
  }
  return element;
}

describe('App shell', () => {
  let fixture: ComponentFixture<App>;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter(routes),
        provideAppIcons(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    // The header loads the server's cart for the badge on every page.
    http.expectOne('/api/v1/cart').flush(EMPTY_CART);
    await fixture.whenStable();
  });

  it('renders the brand as a link to the home page', () => {
    const brand = query<HTMLAnchorElement>(fixture, 'header a.brand');

    expect(brand.textContent?.trim()).toBe('Lumen Goods');
    expect(brand.getAttribute('href')).toBe('/');
  });

  it('shows the cart signal on the mini-cart badge and in its accessible name', async () => {
    const badge = await TestbedHarnessEnvironment.loader(fixture).getHarness(MatBadgeHarness);
    const cartLink = query<HTMLAnchorElement>(fixture, 'header a.cart');

    expect(await badge.isHidden()).toBe(true);
    expect(cartLink.getAttribute('aria-label')).toBe('Cart, empty');

    TestBed.inject(CartStateService).reload().subscribe();
    http.expectOne('/api/v1/cart').flush(aCart([aCartLine({ quantity: 3 })]));
    await fixture.whenStable();

    expect(await badge.isHidden()).toBe(false);
    expect(await badge.getText()).toBe('3');
    expect(cartLink.getAttribute('aria-label')).toBe('Cart, 3 items');
  });

  it('submits the header search to the catalogue URL', async () => {
    const input = query<HTMLInputElement>(fixture, '#site-search');
    input.value = '  desk lamp ';
    input.dispatchEvent(new Event('input'));
    query<HTMLFormElement>(fixture, 'form[role="search"]').dispatchEvent(new Event('submit'));

    // Not whenStable(): the catalogue's product request stays pending in HttpTestingController.
    await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/?q=desk%20lamp'));
  });

  it('opens the mobile menu and closes it with Escape, returning focus to the toggle', async () => {
    const toggle = query<HTMLButtonElement>(fixture, 'button.menu-toggle');
    const menu = query<HTMLElement>(fixture, '#primary-menu');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');

    toggle.click();
    await fixture.whenStable();
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(menu.classList).toContain('open');

    // jsdom lays out at 1024px, where the toggle is display:none and cannot take real focus.
    const focus = vi.spyOn(toggle, 'focus');
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await fixture.whenStable();
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(menu.classList).not.toContain('open');
    expect(focus).toHaveBeenCalled();
  });
});
