import { LiveAnnouncer } from '@angular/cdk/a11y';
import { CurrencyPipe } from '@angular/common';
import { Component, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatBadge } from '@angular/material/badge';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import {
  IsActiveMatchOptions,
  NavigationEnd,
  PRIMARY_OUTLET,
  Router,
  RouterLink,
  RouterLinkActive,
} from '@angular/router';
import { filter, finalize, switchMap } from 'rxjs';
import { SessionApi } from '../../api/session.api';
import { WalletApi } from '../../api/wallet.api';
import { CartStateService } from '../../cart/cart-state.service';
import { CheckoutStore } from '../../checkout/checkout-store';
import { signInQueryParams } from '../../routing/return-url';

@Component({
  selector: 'app-header',
  imports: [
    CurrencyPipe,
    ReactiveFormsModule,
    RouterLink,
    RouterLinkActive,
    MatBadge,
    MatButton,
    MatIconButton,
    MatIcon,
  ],
  templateUrl: './header.html',
  styleUrl: './header.scss',
  host: {
    '(document:keydown.escape)': 'onEscape()',
    '(document:click)': 'closeAccountOnOutsideClick($event)',
  },
})
export class Header {
  private readonly router = inject(Router);

  private readonly cart = inject(CartStateService);

  private readonly sessionApi = inject(SessionApi);

  private readonly walletApi = inject(WalletApi);

  private readonly checkout = inject(CheckoutStore);

  private readonly announcer = inject(LiveAnnouncer);

  private readonly menuToggle = viewChild.required('menuToggle', { read: ElementRef });

  private readonly accountToggle = viewChild('accountToggle', { read: ElementRef });

  private readonly accountArea = viewChild('accountArea', { read: ElementRef });

  private readonly currentUrl = signal('/');

  /**
  "Shop" stays active while the catalogue's query string changes.
  */
  protected readonly shopLinkMatch: IsActiveMatchOptions = {
    paths: 'exact',
    queryParams: 'ignored',
    matrixParams: 'ignored',
    fragment: 'ignored',
  };

  protected readonly menuOpen = signal(false);

  protected readonly accountOpen = signal(false);

  protected readonly signingOut = signal(false);

  protected readonly session = this.sessionApi.session;

  protected readonly isAdmin = this.sessionApi.isAdmin;

  /**
  Staff accounts such as the admin have no wallet, so they get no balance line (and no 404 behind it).
  */
  protected readonly hasWallet = this.sessionApi.isCustomer;

  /**
  Read when the account panel opens, so the balance is current after a payment or refund.
  */
  protected readonly wallet = rxResource({
    params: () => (this.accountOpen() && this.hasWallet() ? true : undefined),
    stream: () => this.walletApi.get(),
  });

  /**
  "Sign in" brings the shopper back to the page they were on.
  */
  protected readonly signInParams = computed(() => signInQueryParams(this.currentUrl()));

  protected readonly itemCount = this.cart.itemCount;

  protected readonly cartLabel = computed(() => {
    const count = this.itemCount();
    if (count === 0) {
      return 'Cart, empty';
    }
    return count === 1 ? 'Cart, 1 item' : `Cart, ${count} items`;
  });

  protected readonly searchForm = new FormGroup({
    q: new FormControl('', { nonNullable: true }),
  });

  constructor() {
    // The badge needs the server's cart on every page, the first one included.
    this.cart.ensureLoaded();

    this.router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        takeUntilDestroyed()
      )
      .subscribe((event) => {
        this.menuOpen.set(false);
        this.accountOpen.set(false);
        this.currentUrl.set(event.urlAfterRedirects);
        // Keep the field in sync with the URL so Back and shared links show the current search.
        // Only the catalogue's own `q`: other pages (the admin product search) have theirs.
        const url = this.router.parseUrl(event.urlAfterRedirects);
        // The catalogue is the root path: no primary child segment.
        const isCatalogue = !url.root.children[PRIMARY_OUTLET];
        const q = isCatalogue ? (url.queryParamMap.get('q') ?? '') : '';
        this.searchForm.controls.q.setValue(q, { emitEvent: false });
      });
  }

  protected toggleMenu(): void {
    this.menuOpen.update((open) => !open);
  }

  protected toggleAccount(): void {
    this.accountOpen.update((open) => !open);
  }

  /**
  Escape closes the innermost open panel and returns focus to the button that opened it.
  */
  protected onEscape(): void {
    if (this.accountOpen()) {
      this.accountOpen.set(false);
      this.accountToggle()?.nativeElement.focus();
      return;
    }
    this.closeMenu(true);
  }

  protected closeAccountOnOutsideClick(event: Event): void {
    const area = this.accountArea()?.nativeElement as HTMLElement | undefined;
    if (area && this.accountOpen() && !area.contains(event.target as Node)) {
      this.accountOpen.set(false);
    }
  }

  protected closeMenu(shouldReturnFocus = false): void {
    if (!this.menuOpen()) {
      return;
    }
    this.menuOpen.set(false);
    if (shouldReturnFocus) {
      this.menuToggle().nativeElement.focus();
    }
  }

  protected search(): void {
    const q = this.searchForm.controls.q.value.trim();
    // A new search starts from page 1 with no filters; the catalogue owns the rest of the query.
    void this.router.navigate(['/'], { queryParams: q ? { q } : {} });
  }

  /**
   * Signs out, then shows the guest cart (the saved cart stays on the server) and forgets the
   * checkout, which belonged to the signed-in customer.
   */
  protected signOut(): void {
    if (this.signingOut()) {
      return;
    }
    this.signingOut.set(true);
    this.sessionApi
      .signOut()
      .pipe(
        switchMap(() => {
          this.checkout.reset();
          this.cart.setMergeNotice(null);
          return this.cart.reload();
        }),
        finalize(() => this.signingOut.set(false))
      )
      .subscribe({
        next: () => {
          this.accountOpen.set(false);
          void this.router.navigateByUrl('/');
          void this.announcer.announce('You are signed out.', 'polite');
        },
        error: () => {
          // Signed out but the cart refresh failed, or the sign-out itself failed.
          const isSignedOut = !this.sessionApi.isAuthenticated();
          void this.announcer.announce(
            isSignedOut ? 'You are signed out.' : 'We couldn’t sign you out. Try again.',
            'assertive'
          );
          if (!isSignedOut) {
            return;
          }

          this.accountOpen.set(false);
          void this.router.navigateByUrl('/');
        },
      });
  }
}
