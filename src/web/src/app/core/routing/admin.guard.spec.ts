import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { AdminOrdersPage } from '../../features/admin/admin-orders-page';
import { NotFoundPage } from '../../features/not-found/not-found-page';
import { ADMIN_SESSION, CUSTOMER_SESSION } from '../../testing/fixtures';
import { provideAppIcons } from '../icons/app-icons';
import { ANONYMOUS_SESSION, Session, SessionApi } from '../api/session.api';

// The pages are imported here as well as lazily by the route table, so their chunks are already
// evaluated when a test navigates (see app.routes.spec.ts).

describe('adminGuard', () => {
  let http: HttpTestingController;

  function configure(): void {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes, withComponentInputBinding()),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  }

  /** Starts the app's router with the session the API answered at start-up. */
  async function start(session: Session): Promise<RouterTestingHarness> {
    configure();
    const ready = TestBed.inject(SessionApi).ready();
    http.expectOne('/api/v1/auth/session').flush(session);
    await ready;
    return RouterTestingHarness.create();
  }

  const url = () => TestBed.inject(Router).url;

  it('opens the admin pages for an admin', async () => {
    const harness = await start(ADMIN_SESSION);

    await harness.navigateByUrl('/admin/orders', AdminOrdersPage);

    expect(url()).toBe('/admin/orders');
  });

  it('sends a signed-out visitor to sign in, coming back to the same page and query', async () => {
    const harness = await start(ANONYMOUS_SESSION);

    await harness.navigateByUrl('/admin/orders?page=2');

    expect(url()).toBe(`/sign-in?returnUrl=${encodeURIComponent('/admin/orders?page=2')}`);
  });

  it('shows a signed-in customer the not-found page, without loading an admin page', async () => {
    const harness = await start(CUSTOMER_SESSION);

    await harness.navigateByUrl('/admin/orders', NotFoundPage);

    expect(url()).toBe('/admin/orders');
    expect(harness.routeNativeElement?.querySelector('h1')?.textContent?.trim()).toBe(
      'Page not found',
    );
    http.expectNone((request) => request.url.includes('/admin/'));
  });

  it('waits for a slow session instead of treating the admin as signed out', async () => {
    configure();
    const harness = await RouterTestingHarness.create();

    const navigation = harness.navigateByUrl('/admin/orders');
    await vi.waitFor(() => http.expectOne('/api/v1/auth/session').flush(ADMIN_SESSION));
    await navigation;

    expect(url()).toBe('/admin/orders');
  });
});
