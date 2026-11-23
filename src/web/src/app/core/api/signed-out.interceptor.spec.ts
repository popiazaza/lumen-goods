import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ADMIN_SESSION, CUSTOMER_SESSION } from '../../testing/fixtures';
import { ANONYMOUS_SESSION, Session, SessionApi } from './session.api';
import { signedOutInterceptor } from './signed-out.interceptor';

@Component({ selector: 'app-stub-page', template: '<h1>Page</h1>' })
class StubPage {}

const UNAUTHORIZED = { status: 401, statusText: 'Unauthorized' };

describe('signedOutInterceptor', () => {
  let http: HttpTestingController;
  let client: HttpClient;
  let sessionApi: SessionApi;
  let router: Router;

  /**
  Starts signed in as `session` on `/checkout/review`.
  */
  const start = async (session: Session): Promise<void> => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'checkout/review', component: StubPage },
          { path: 'sign-in', component: StubPage },
        ]),
        provideHttpClient(withInterceptors([signedOutInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
    sessionApi = TestBed.inject(SessionApi);
    router = TestBed.inject(Router);
    const ready = sessionApi.ready();
    http.expectOne('/api/v1/auth/session').flush(session);
    await ready;
    await router.navigateByUrl('/checkout/review');
  };

  /**
  Sends a call, answers it with `status`, and returns the error the caller got.
  */
  const fail = async (
    method: 'GET' | 'POST',
    url: string,
    status = UNAUTHORIZED
  ): Promise<unknown> => {
    const call = firstValueFrom(client.request(method, url));
    http.expectOne(url).flush({ title: status.statusText }, status);
    try {
      await call;
      return undefined;
    } catch (error: unknown) {
      return error;
    }
  };

  afterEach(() => http.verify());

  it('on a 401 from a signed-in call, reads the session again and sends the shopper to sign in, back to this page', async () => {
    await start(CUSTOMER_SESSION);

    const error = await fail('POST', '/api/v1/checkout/orders');

    // The page that called still gets its error, and the header already reads signed out.
    expect(error).toMatchObject({ status: 401 });
    expect(sessionApi.isAuthenticated()).toBeFalsy();

    http.expectOne('/api/v1/auth/session').flush(ANONYMOUS_SESSION);
    await vi.waitFor(() =>
      expect(router.url).toBe('/sign-in?returnUrl=%2Fcheckout%2Freview&reason=signed-out')
    );
  });

  it('handles several failing calls once', async () => {
    await start(CUSTOMER_SESSION);

    await fail('GET', '/api/v1/orders/LG-1');
    await fail('GET', '/api/v1/wallet');

    http.expectOne('/api/v1/auth/session').flush(ANONYMOUS_SESSION);
    await vi.waitFor(() => expect(router.url).toContain('/sign-in'));
  });

  it('leaves a wrong password (401 from /auth) and a missing role (403) to their pages', async () => {
    await start(ADMIN_SESSION);

    await fail('POST', '/api/v1/auth/sign-in');
    await fail('GET', '/api/v1/wallet', { status: 403, statusText: 'Forbidden' });

    expect(sessionApi.session()).toStrictEqual(ADMIN_SESSION);
    expect(router.url).toBe('/checkout/review');
  });

  it('leaves a slow call sent before a sign-in alone when it fails after it', async () => {
    await start(ANONYMOUS_SESSION);
    const slow = firstValueFrom(client.get('/api/v1/orders'));

    const signIn = firstValueFrom(
      sessionApi.signIn({ email: 'customer@lumen.test', password: 'x' })
    );
    http.expectOne('/api/v1/auth/sign-in').flush(CUSTOMER_SESSION);
    await signIn;
    http.expectOne('/api/v1/orders').flush({ title: 'Unauthorized' }, UNAUTHORIZED);

    await expect(slow).rejects.toMatchObject({ status: 401 });

    http.expectNone('/api/v1/auth/session');

    expect(sessionApi.session()).toStrictEqual(CUSTOMER_SESSION);
    expect(router.url).toBe('/checkout/review');
  });

  it('leaves a 401 alone when the tab already knows it is signed out', async () => {
    await start(ANONYMOUS_SESSION);

    await fail('GET', '/api/v1/orders');

    http.expectNone('/api/v1/auth/session');

    expect(router.url).toBe('/checkout/review');
  });
});
