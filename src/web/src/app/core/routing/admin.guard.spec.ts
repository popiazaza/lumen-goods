import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { AdminLayout } from '../../features/admin/admin-layout';
import { AdminOrdersPage } from '../../features/admin/admin-orders-page';
import { NotFoundPage } from '../../features/not-found/not-found-page';
import { ADMIN_SESSION, CUSTOMER_SESSION } from '../../testing/fixtures';
import { provideAppIcons } from '../icons/app-icons';
import { ANONYMOUS_SESSION, Session, SessionApi } from '../api/session.api';

// The pages are imported here as well as lazily by the route table, so their chunks are already
// evaluated when a test navigates (see app.routes.spec.ts).

const url = () => TestBed.inject(Router).url;

describe('adminGuard', () => {
  let http: HttpTestingController;

  const configure = (): void => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes, withComponentInputBinding()),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  };

  /**
  Starts the app's router with the session the API answered at start-up.
  */
  const start = async (session: Session): Promise<RouterTestingHarness> => {
    configure();
    const ready = TestBed.inject(SessionApi).ready();
    http.expectOne('/api/v1/auth/session').flush(session);
    await ready;
    return RouterTestingHarness.create();
  };

  it('opens the admin pages for an admin', async () => {
    const harness = await start(ADMIN_SESSION);

    await harness.navigateByUrl('/admin/orders', AdminLayout);

    expect(url()).toBe('/admin/orders');
    expect(harness.fixture.debugElement.query(By.directive(AdminOrdersPage))).not.toBeNull();
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
      'Page not found'
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
