import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SessionApi } from '../api/session.api';

/**
 * Lets signed-in customers through; sends everyone else to `/sign-in?returnUrl=<this page>`.
 * Waits for the session if start-up gave up on a slow API, so a signed-in shopper who refreshes a checkout step is not bounced to sign-in. The API checks again on every call.
 */
export const signedInGuard: CanActivateFn = async (_route, state) => {
  const sessionApi = inject(SessionApi);
  const router = inject(Router);
  const session = sessionApi.isAuthenticated() ? sessionApi.session() : await sessionApi.ready();
  return session.isAuthenticated
    ? true
    : router.createUrlTree(['/sign-in'], { queryParams: { returnUrl: state.url } });
};
