import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { tap } from 'rxjs';
import { signInQueryParams } from '../routing/return-url';
import { API_BASE_URL } from './api-base-url';
import { SessionApi } from './session.api';

/**
The `reason` query param of `/sign-in` after the session ended under the shopper.
*/
export const SIGNED_OUT_REASON = 'signed-out';

/**
 * A 401 while this tab thinks it is signed in means the session ended elsewhere: a sign-out in
 * another tab, or an expired cookie. The page that called would only say "Unauthorized", and the
 * header would still show the account. So, in one place for every call: forget the session (the
 * header updates), ask the API for the real one, and send the shopper to sign in, coming back to
 * the page they were on. The `/auth` calls answer 401 for a wrong password, and are left to their
 * pages; a 403 (signed in without the right role) is not a lost session either. The error still
 * reaches the caller, so its own clean-up runs.
 */
export const signedOutInterceptor: HttpInterceptorFn = (request, next) => {
  const sessionApi = inject(SessionApi);
  const router = inject(Router);
  const authUrl = `${inject(API_BASE_URL)}/auth/`;
  // Who the call was sent as: a slow call from before a sign-in that fails after it must not sign
  // out the account that is signed in now.
  const sentAs = sessionApi.session();
  return next(request).pipe(
    tap({
      error: (error: unknown) => {
        const now = sessionApi.session();
        const sameAccount = sentAs.isAuthenticated && now.isAuthenticated && now.email === sentAs.email;
        const lostSession =
          error instanceof HttpErrorResponse &&
          error.status === 401 &&
          !request.url.startsWith(authUrl) &&
          sameAccount;
        if (!lostSession) {
          return;
        }
        // Only the first of several failing calls gets here: the session now reads signed out.
        sessionApi.sessionEnded();
        void router.navigate(['/sign-in'], {
          queryParams: { ...signInQueryParams(router.url), reason: SIGNED_OUT_REASON },
        });
      },
    })
  );
};
