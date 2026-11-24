import { LiveAnnouncer } from '@angular/cdk/a11y';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { SessionApi } from '../../core/api/session.api';
import { CartStateService } from '../../core/cart/cart-state.service';
import { provideAppIcons } from '../../core/icons/app-icons';
import { CUSTOMER_SESSION, aCart, aCartLine } from '../../testing/fixtures';
import { mergeNoticeFor } from './after-sign-in';
import { RegisterPage } from './register-page';
import { SignInPage } from './sign-in-page';

@Component({ selector: 'app-stub-page', template: '<h1>Next page</h1>' })
class StubPage {}

const url = () => TestBed.inject(Router).url;

describe('Sign in and register', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  const host = () => harness.routeNativeElement as HTMLElement;
  const text = (selector: string) =>
    host().querySelector(selector)?.textContent?.replaceAll(/\s+/gu, ' ').trim();

  const type = (id: string, value: string): void => {
    const input = host().querySelector<HTMLInputElement>(`#${id}`) as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  };

  const submit = async (): Promise<void> => {
    host().querySelector<HTMLButtonElement>('button[type="submit"]')?.click();
    await harness.fixture.whenStable();
  };

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [
            { path: 'sign-in', component: SignInPage },
            { path: 'register', component: RegisterPage },
            { path: 'checkout/address', component: StubPage },
            { path: '', component: StubPage },
          ],
          withComponentInputBinding()
        ),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue(undefined);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => {
    http.verify();
    vi.restoreAllMocks();
  });

  it('signs in and returns to the page that asked, noting a cart merge', async () => {
    // A guest cart with one line, before signing in.
    TestBed.inject(CartStateService).reload().subscribe();
    http.expectOne('/api/v1/cart').flush(aCart([aCartLine()]));
    await harness.navigateByUrl('/sign-in?returnUrl=%2Fcheckout%2Faddress');

    expect(text('.intro')).toBe('Sign in to check out. Your cart is saved and comes with you.');
    expect(host().querySelector(':scope .switch a')?.getAttribute('href')).toBe(
      '/register?returnUrl=%2Fcheckout%2Faddress'
    );

    type('sign-in-email', ' customer@lumen.test ');
    type('sign-in-password', 'Passw0rd!');
    await submit();
    const request = http.expectOne('/api/v1/auth/sign-in');

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toStrictEqual({
      email: 'customer@lumen.test',
      password: 'Passw0rd!',
    });

    request.flush(CUSTOMER_SESSION);
    http
      .expectOne('/api/v1/cart')
      .flush(aCart([aCartLine(), aCartLine({ id: 'line-2', quantity: 2 })]));
    await harness.fixture.whenStable();

    expect({
      url: url(),
      isAuthenticated: TestBed.inject(SessionApi).isAuthenticated(),
      mergeNotice: TestBed.inject(CartStateService).mergeNotice(),
    }).toStrictEqual({
      url: '/checkout/address',
      isAuthenticated: true,
      mergeNotice: 'We merged your cart with the items saved in your account. It now has 3 items.',
    });
  });

  it('says the shopper was signed out when the session ended under them', async () => {
    await harness.navigateByUrl('/sign-in?returnUrl=%2Fcheckout%2Faddress&reason=signed-out');

    expect(text('[role="status"]')).toBe(
      'You were signed out. Sign in to continue; your cart is saved.'
    );
    expect(host().querySelector('.intro')).toBeNull();
  });

  it('never follows a returnUrl to another site', async () => {
    await harness.navigateByUrl('/sign-in?returnUrl=%2F%2Fevil.example');
    type('sign-in-email', 'customer@lumen.test');
    type('sign-in-password', 'Passw0rd!');

    await submit();
    http.expectOne('/api/v1/auth/sign-in').flush(CUSTOMER_SESSION);
    http.expectOne('/api/v1/cart').flush(aCart([]));
    await harness.fixture.whenStable();

    expect(url()).toBe('/');
  });

  it('lists the problems above the form and focuses the summary on an empty submit', async () => {
    await harness.navigateByUrl('/sign-in');

    await submit();
    await harness.fixture.whenStable();

    const links = [...host().querySelectorAll(':scope app-error-summary a')];

    expect(links.map((a) => [a.textContent?.trim(), a.getAttribute('href')])).toStrictEqual([
      ['Enter your email address', '#sign-in-email'],
      ['Enter your password', '#sign-in-password'],
    ]);
    expect(document.activeElement).toBe(
      host().querySelector(':scope app-error-summary [role="alert"]')
    );

    (links[1] as HTMLAnchorElement).click();

    expect(document.activeElement?.id).toBe('sign-in-password');
  });

  it('says when the email or password is wrong', async () => {
    await harness.navigateByUrl('/sign-in');
    type('sign-in-email', 'customer@lumen.test');
    type('sign-in-password', 'nope-nope');

    await submit();
    http
      .expectOne('/api/v1/auth/sign-in')
      .flush(
        { title: 'Email or password is incorrect', status: 401 },
        { status: 401, statusText: 'Unauthorized' }
      );
    await harness.fixture.whenStable();

    expect(text('.failure .failure-title')).toBe('Email or password is incorrect');
    expect(url()).toBe('/sign-in');
  });

  it('explains a lockout', async () => {
    await harness.navigateByUrl('/sign-in');
    type('sign-in-email', 'customer@lumen.test');
    type('sign-in-password', 'nope-nope');

    await submit();
    http.expectOne('/api/v1/auth/sign-in').flush(
      {
        title: 'Too many failed attempts',
        detail: 'The account is locked for a few minutes.',
        status: 429,
      },
      { status: 429, statusText: 'Too Many Requests' }
    );
    await harness.fixture.whenStable();

    expect(text('.failure .failure-title')).toBe('Too many failed attempts');
    expect(text('.failure .failure-title + p')).toBe(
      'The account is locked for a few minutes. Wait a few minutes, then try again.'
    );
  });

  it('tells a rate limit apart from a lockout', async () => {
    await harness.navigateByUrl('/sign-in');
    type('sign-in-email', 'customer@lumen.test');
    type('sign-in-password', 'Customer!2026');

    await submit();
    http.expectOne('/api/v1/auth/sign-in').flush(
      {
        type: 'urn:lumen:problem:rate-limited',
        title: 'Too many attempts',
        detail: 'Wait 60 seconds, then try again.',
        status: 429,
      },
      { status: 429, statusText: 'Too Many Requests', headers: { 'Retry-After': '60' } }
    );
    await harness.fixture.whenStable();

    expect(text('.failure .failure-title')).toBe('Too many attempts');
    expect(text('.failure .failure-title + p')).toBe('Wait 60 seconds, then try again.');
  });

  it('registers, putting the API’s field errors under their fields', async () => {
    await harness.navigateByUrl('/register?returnUrl=%2Fcheckout%2Faddress');
    type('register-email', 'taken@lumen.test');
    type('register-password', 'Long-enough1');

    await submit();
    http.expectOne('/api/v1/auth/register').flush(
      {
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: {
          Email: ['There is already an account with this email address. Sign in instead.'],
        },
      },
      { status: 400, statusText: 'Bad Request' }
    );
    await harness.fixture.whenStable();

    expect(text('.field-error')).toBe(
      'There is already an account with this email address. Sign in instead.'
    );
    expect(text('app-error-summary li')).toBe(
      'There is already an account with this email address. Sign in instead.'
    );

    type('register-email', 'new@lumen.test');
    await submit();
    http.expectOne('/api/v1/auth/register').flush(CUSTOMER_SESSION);
    http.expectOne('/api/v1/cart').flush(aCart([]));
    await harness.fixture.whenStable();

    expect(url()).toBe('/checkout/address');
  });

  it('states the password rules in the hint', async () => {
    await harness.navigateByUrl('/register');

    expect(text('.hint')).toBe(
      'At least 8 characters, with an upper-case letter, a lower-case letter, a number and a symbol'
    );
  });

  it('says what the password still misses before calling the API', async () => {
    await harness.navigateByUrl('/register');
    type('register-email', 'new@lumen.test');
    type('register-password', 'short');

    await submit();

    expect(text('app-error-summary li')).toBe(
      'Use at least 8 characters. Add an upper-case letter (A-Z). Add a number (0-9). Add a symbol, such as ! or #.'
    );

    type('register-password', 'abcdefgh');
    await submit();

    expect(text('app-error-summary li')).toBe(
      'Add an upper-case letter (A-Z). Add a number (0-9). Add a symbol, such as ! or #.'
    );
  });
});

describe('mergeNoticeFor', () => {
  it('stays quiet when the guest cart was empty or nothing was added', () => {
    expect(mergeNoticeFor(undefined, aCart([aCartLine()]))).toBeNull();
    expect(mergeNoticeFor(aCart([]), aCart([aCartLine()]))).toBeNull();
    expect(mergeNoticeFor(aCart([aCartLine()]), aCart([aCartLine({ quantity: 2 })]))).toBeNull();
  });

  it('mentions limited quantities', () => {
    const limited = aCartLine({
      id: 'line-2',
      quantity: 10,
      notices: {
        priceChanged: null,
        availabilityChanged: null,
        unavailable: null,
        quantityLimited: { requested: 14, allowed: 10 },
      },
    });

    expect(mergeNoticeFor(aCart([aCartLine()]), aCart([aCartLine(), limited]))).toBe(
      'We merged your cart with the items saved in your account. It now has 11 items. Some quantities were limited to what is available.'
    );
  });
});
