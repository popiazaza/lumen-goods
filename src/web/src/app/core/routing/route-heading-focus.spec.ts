import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideRouteHeadingFocus } from './route-heading-focus';

@Component({
  template: `
    <h1>Shop</h1>
    <label for="filter">Filter</label>
    <input id="filter" />
  `,
})
class ShopPage {}

@Component({ template: '<h1>Your cart</h1>' })
class CartPage {}

describe('provideRouteHeadingFocus', () => {
  let main: HTMLElement;
  let harness: RouterTestingHarness;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: ShopPage },
          { path: 'cart', component: CartPage },
        ]),
        provideRouteHeadingFocus(),
      ],
    });
    harness = await RouterTestingHarness.create('/');
    // The app shell renders pages inside <main>; mirror that here.
    main = document.createElement('main');
    document.body.append(main);
    main.append(harness.fixture.nativeElement);
  });

  afterEach(() => {
    main.remove();
    vi.restoreAllMocks();
  });

  it('leaves focus alone on the first page load', () => {
    expect(document.activeElement).toBe(document.body);
  });

  it('focuses the new page heading, without scrolling, after a path change', async () => {
    const focus = vi.spyOn(HTMLElement.prototype, 'focus');

    await harness.navigateByUrl('/cart');
    await harness.fixture.whenStable();

    const heading = main.querySelector('h1');

    expect(heading?.textContent).toBe('Your cart');
    expect(document.activeElement).toBe(heading);
    expect(heading?.getAttribute('tabindex')).toBe('-1');
    expect(focus).toHaveBeenCalledWith({ preventScroll: true });
  });

  it('keeps focus where it is when only the query string changes', async () => {
    const filter = main.querySelector('input');
    filter?.focus();

    await harness.navigateByUrl('/?q=lamp');
    await harness.fixture.whenStable();

    expect(document.activeElement).toBe(filter);
  });
});
