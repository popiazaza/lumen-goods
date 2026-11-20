import { inject } from '@angular/core';
import { CanMatchFn, Router } from '@angular/router';
import { SessionApi } from '../api/session.api';

/**
 * Matches `/admin` for admins only (BR-ADM-01). A signed-out visitor goes to sign in and comes
 * back. A signed-in customer gets no match, so the router moves on to the not-found page: the
 * admin area is not advertised, and its pages are never loaded. Like `signedInGuard`, it waits for
 * the session if start-up gave up on a slow API. The API checks the role again on every admin call.
 */
export const adminGuard: CanMatchFn = async (_route, segments) => {
  const sessionApi = inject(SessionApi);
  const router = inject(Router);
  if (!sessionApi.isAuthenticated()) {
    await sessionApi.ready();
  }
  if (sessionApi.isAdmin()) {
    return true;
  }
  if (sessionApi.isAuthenticated()) {
    return false;
  }
  // `canMatch` gets the path segments only; the navigation has the whole URL, query included.
  const target = router.currentNavigation()?.extractedUrl;
  const returnUrl = target
    ? router.serializeUrl(target)
    : `/${segments.map((segment) => segment.path).join('/')}`;
  return router.createUrlTree(['/sign-in'], { queryParams: { returnUrl } });
};
