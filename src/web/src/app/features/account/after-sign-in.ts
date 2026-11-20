import { LiveAnnouncer } from '@angular/cdk/a11y';
import { Service, inject } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, map, of, switchMap } from 'rxjs';
import { CartDto } from '../../core/api/cart.api';
import { Session } from '../../core/api/session.api';
import { CartStateService } from '../../core/cart/cart-state.service';
import { safeReturnUrl } from '../../core/routing/return-url';

/**
 * "We merged your carts" when signing in brought saved lines into a guest cart that had some
 * (BR-CART-01), or `null` when nothing visible changed (an empty guest cart just shows the saved
 * one).
 */
export function mergeNoticeFor(guest: CartDto | undefined, merged: CartDto): string | null {
  const guestLines = guest?.lines.length ?? 0;
  if (guestLines === 0 || merged.lines.length <= guestLines) {
    return null;
  }
  const items = merged.itemCount === 1 ? '1 item' : `${merged.itemCount} items`;
  const limited = merged.lines.some((line) => line.notices.quantityLimited)
    ? ' Some quantities were limited to what is available.'
    : '';
  return `We merged your cart with the items saved in your account. It now has ${items}.${limited}`;
}

/** What sign-in and registration do once the API has accepted the shopper. */
@Service()
export class AfterSignIn {
  private readonly cart = inject(CartStateService);
  private readonly router = inject(Router);
  private readonly announcer = inject(LiveAnnouncer);

  /**
   * Runs `signIn`, then loads the (now merged) cart, notes a merge, and goes to `returnUrl`.
   * A cart that fails to load does not stop the sign-in. Errors of `signIn` itself pass through.
   */
  complete(signIn: Observable<Session>, returnUrl: string | undefined): Observable<void> {
    const guestCart = this.cart.cart();
    return signIn.pipe(
      switchMap((session) => {
        return this.cart.reload().pipe(
          map((cart) => mergeNoticeFor(guestCart, cart)),
          catchError(() => of(null)),
          map((notice) => ({ session, notice })),
        );
      }),
      switchMap(({ session, notice }) => {
        this.cart.setMergeNotice(notice);
        void this.announcer.announce(
          notice ? `Signed in as ${session.email}. ${notice}` : `Signed in as ${session.email}.`,
          'polite',
        );
        return this.router.navigateByUrl(safeReturnUrl(returnUrl));
      }),
      map(() => undefined),
    );
  }
}
