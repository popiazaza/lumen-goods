import { LiveAnnouncer } from '@angular/cdk/a11y';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { EMPTY_CART } from '../../api/cart.api';
import { ANONYMOUS_SESSION as ANONYMOUS, Session, SessionApi } from '../../api/session.api';
import { CHECKOUT_STORAGE_KEY, CheckoutStore } from '../../checkout/checkout-store';
import { provideAppIcons } from '../../icons/app-icons';
import {
  ADMIN_SESSION,
  CUSTOMER_SESSION,
  US_ADDRESS,
  aCart,
  aCartLine,
  aWallet,
} from '../../../testing/fixtures';
import { Header } from './header';

@Component({ selector: 'app-stub-page', template: '<h1>Page</h1>' })
class StubPage {}

describe('Header account area', () => {
  let fixture: ComponentFixture<Header>;
  let http: HttpTestingController;

  const host = () => fixture.nativeElement as HTMLElement;
  const link = (name: string) =>
    [...host().querySelectorAll('a')].find((a) => a.textContent?.trim() === name);
  const toggle = () =>
    host().querySelector<HTMLButtonElement>('button[aria-controls="account-panel"]');
  const panel = () => host().querySelector<HTMLElement>('#account-panel');

  const render = async (session: Session): Promise<void> => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: StubPage },
          { path: 'cart', component: StubPage },
          { path: 'orders', component: StubPage },
          { path: 'admin', component: StubPage },
          { path: 'sign-in', component: StubPage },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue(undefined);
    const ready = TestBed.inject(SessionApi).ready();
    http.expectOne('/api/v1/auth/session').flush(session);
    await ready;
    fixture = TestBed.createComponent(Header);
    fixture.detectChanges();
    http.expectOne('/api/v1/cart').flush(aCart([aCartLine({ quantity: 2 })]));
    await fixture.whenStable();
  };

  const openAccount = async (): Promise<void> => {
    toggle()?.click();
    fixture.detectChanges();
    http.expectOne('/api/v1/wallet').flush(aWallet(1924.54));
    await fixture.whenStable();
  };

  afterEach(() => {
    http.verify();
    sessionStorage.clear();
    vi.restoreAllMocks();
  });

  it('shows "Sign in" (returning to the current page) and no account menu when signed out', async () => {
    await render(ANONYMOUS);
    await TestBed.inject(Router).navigateByUrl('/cart');
    await fixture.whenStable();

    expect(link('Sign in')?.getAttribute('href')).toBe('/sign-in?returnUrl=%2Fcart');
    expect(toggle()).toBeNull();
    expect(host().querySelector('a.cart')?.getAttribute('aria-label')).toBe('Cart, 2 items');
  });

  it('gives a customer an account menu with email, orders, wallet balance and sign out, but no admin link', async () => {
    await render(CUSTOMER_SESSION);

    expect(link('Sign in')).toBeUndefined();
    expect({
      expanded: toggle()?.getAttribute('aria-expanded'),
      panelHidden: panel()?.hidden,
    }).toStrictEqual({ expanded: 'false', panelHidden: true });

    await openAccount();
    const openPanel = panel() as HTMLElement;

    expect({
      expanded: toggle()?.getAttribute('aria-expanded'),
      panelHidden: openPanel.hidden,
      email: openPanel.querySelector('.email')?.textContent?.trim(),
      balance: openPanel.querySelector('.balance')?.textContent?.replaceAll(/\s+/gu, ' ').trim(),
      signOut: openPanel.querySelector('button.sign-out')?.textContent?.trim(),
    }).toStrictEqual({
      expanded: 'true',
      panelHidden: false,
      email: 'customer@lumen.test',
      balance: 'Wallet balance: $1,924.54',
      signOut: 'Sign out',
    });
    expect(link('Your orders')?.getAttribute('href')).toBe('/orders');
    expect(link('Admin')).toBeUndefined();
  });

  it('adds the Admin link for the admin role', async () => {
    await render(ADMIN_SESSION);

    toggle()?.click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(link('Admin')?.getAttribute('href')).toBe('/admin');

    // Staff have no wallet: no balance line, and no request for one.
    http.expectNone('/api/v1/wallet');

    expect(panel()?.querySelector('.balance')).toBeNull();
  });

  it('closes the menu with Escape and returns focus to its button', async () => {
    await render(CUSTOMER_SESSION);
    await openAccount();
    const focus = vi.spyOn(toggle() as HTMLButtonElement, 'focus');

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await fixture.whenStable();

    expect(panel()?.hidden).toBeTruthy();
    expect(focus).toHaveBeenCalledWith();
  });

  it('signs out: forgets the checkout, shows the guest cart and goes home', async () => {
    sessionStorage.setItem(
      CHECKOUT_STORAGE_KEY,
      JSON.stringify({
        owner: CUSTOMER_SESSION.email,
        checkoutKey: 'k',
        address: US_ADDRESS,
        shippingMethod: null,
        orderNumber: null,
      })
    );
    await render(CUSTOMER_SESSION);
    await TestBed.inject(Router).navigateByUrl('/orders');
    await openAccount();

    (panel()?.querySelector('button.sign-out') as HTMLButtonElement).click();
    const signOut = http.expectOne('/api/v1/auth/sign-out');

    expect(signOut.request.method).toBe('POST');

    signOut.flush(ANONYMOUS);
    http.expectOne('/api/v1/cart').flush(EMPTY_CART);
    await fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/');
    expect(TestBed.inject(CheckoutStore).address()).toBeNull();
    expect(link('Sign in')).toBeDefined();
    expect(host().querySelector('a.cart')?.getAttribute('aria-label')).toBe('Cart, empty');
  });
});
