import { HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationInitStatus } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { appConfig } from './app.config';
import { ANONYMOUS_SESSION, SESSION_STARTUP_WAIT_MS, SessionApi } from './core/api/session.api';

const SIGNED_IN = { isAuthenticated: true, email: 'customer@lumen.test', roles: ['Customer'] };

describe('appConfig', () => {
  let httpTesting: HttpTestingController;

  /**
  Creates the app injector, which runs the app initializers as bootstrap would.
  */
  const startApp = (): ApplicationInitStatus => {
    httpTesting = TestBed.inject(HttpTestingController);
    return TestBed.inject(ApplicationInitStatus);
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [...appConfig.providers, provideHttpClientTesting()],
    });
  });

  afterEach(() => {
    httpTesting.verify();
    // eslint-disable-next-line unicorn/no-document-cookie -- Angular's XSRF interceptor reads document.cookie
    document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('loads the session (and with it the XSRF cookie) at startup', async () => {
    const init = startApp();

    const request = httpTesting.expectOne('/api/v1/auth/session');

    expect(request.request.method).toBe('GET');

    request.flush(SIGNED_IN);
    await init.donePromise;

    expect(TestBed.inject(SessionApi).session()).toStrictEqual(SIGNED_IN);
  });

  it('still starts, signed out, when the session call fails', async () => {
    const consoleError = vi.spyOn(console, 'error').mockReturnValue(undefined);
    const init = startApp();

    httpTesting
      .expectOne('/api/v1/auth/session')
      .flush('Bad gateway', { status: 502, statusText: 'Bad Gateway' });
    await init.donePromise;

    expect(init.done).toBeTruthy();
    expect(TestBed.inject(SessionApi).session()).toStrictEqual(ANONYMOUS_SESSION);
    expect(consoleError).toHaveBeenCalledWith(
      'Could not load the session; continuing signed out.',
      expect.objectContaining({ name: 'HttpErrorResponse', status: 502 })
    );
  });

  it('does not hold the first render for a slow API, and keeps the late session', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    vi.spyOn(console, 'warn').mockReturnValue(undefined);
    const init = startApp();
    const request = httpTesting.expectOne('/api/v1/auth/session');

    vi.advanceTimersByTime(SESSION_STARTUP_WAIT_MS);
    await init.donePromise;

    expect(TestBed.inject(SessionApi).session()).toStrictEqual(ANONYMOUS_SESSION);

    request.flush(SIGNED_IN);

    expect(TestBed.inject(SessionApi).session()).toStrictEqual(SIGNED_IN);
  });

  it('echoes the XSRF-TOKEN cookie as X-XSRF-TOKEN on a relative POST', () => {
    startApp();
    httpTesting.expectOne('/api/v1/auth/session').flush(ANONYMOUS_SESSION);
    // eslint-disable-next-line unicorn/no-document-cookie -- Angular's XSRF interceptor reads document.cookie
    document.cookie = 'XSRF-TOKEN=token-from-api; path=/';

    TestBed.inject(HttpClient).post('/api/v1/auth/sign-out', {}).subscribe();

    const request = httpTesting.expectOne('/api/v1/auth/sign-out');

    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('token-from-api');

    request.flush(ANONYMOUS_SESSION);
  });
});
