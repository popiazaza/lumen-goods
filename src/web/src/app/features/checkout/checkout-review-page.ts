import { CurrencyPipe } from '@angular/common';
import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { Router, RouterLink } from '@angular/router';
import { finalize, noop } from 'rxjs';
import {
  CHECKOUT_KEY_TAKEN_PROBLEM,
  CheckoutApi,
  CheckoutChangeKind,
  CheckoutChangedProblem,
  PlaceOrderLine,
  SHIPPING_METHOD_NOT_AVAILABLE_PROBLEM,
  checkoutChangedProblemOf,
  reviewedLines,
  withNewPrices,
} from '../../core/api/checkout.api';
import { SHIPPING_METHOD_NAMES } from '../../core/api/order.models';
import {
  httpStatusOf,
  problemMessage,
  problemTypeOf,
  validationErrorsOf,
} from '../../core/api/problem-details';
import { CartStateService } from '../../core/cart/cart-state.service';
import { CheckoutStore } from '../../core/checkout/checkout-store';
import { CheckoutSteps } from './checkout-steps';
import { stateName } from './us-address';

/**
Changes that only mean the review is out of date (the cart changed in another tab).
*/
const REVIEW_AGAIN_KINDS: ReadonlySet<CheckoutChangeKind> = /* @__PURE__ */ new Set([
  'cart_changed',
  'quantity_changed',
  'price_changed',
]);

/**
 * Checkout step 3 (`/checkout/review`): the server's preview of the order, then Place order.
 * Place order sends exactly the lines and total shown here; if anything changed since
 * (BR-CHK-01, BR-CHK-02), the API answers 409 with each change and nothing is created, and this
 * page asks the shopper to confirm the new amounts, review again, or fix the cart.
 */
@Component({
  selector: 'app-checkout-review-page',
  imports: [CurrencyPipe, RouterLink, MatButton, MatIcon, CheckoutSteps],
  templateUrl: './checkout-review-page.html',
  styleUrls: ['./checkout.scss', './checkout-review-page.scss'],
})
export class CheckoutReviewPage {
  private readonly api = inject(CheckoutApi);

  private readonly store = inject(CheckoutStore);

  private readonly cart = inject(CartStateService);

  private readonly router = inject(Router);

  private readonly injector = inject(Injector);

  private readonly changesBanner = viewChild<ElementRef<HTMLElement>>('changesBanner');

  /**
  The lines the last Place order sent: a confirmation re-sends them at the new prices.
  */
  private sentLines: PlaceOrderLine[] = [];

  protected readonly address = this.store.address;

  protected readonly method = computed(() => this.store.shippingMethod() ?? 'standard');

  protected readonly methodName = computed(() => SHIPPING_METHOD_NAMES[this.method()]);

  protected readonly addressText = computed(() => {
    const address = this.address();
    if (!address) {
      return '';
    }
    const street = [address.line1, address.line2].filter(Boolean).join(', ');
    return `${address.name}, ${street}, ${address.city}, ${stateName(address.region)} ${address.postalCode}, United States`;
  });

  protected readonly preview = rxResource({
    params: () => this.method(),
    stream: ({ params }) => this.api.preview(params),
  });

  protected readonly loaded = computed(() =>
    this.preview.hasValue() ? this.preview.value() : undefined
  );

  protected readonly blocked = computed(() => (this.loaded()?.blockers.length ?? 0) > 0);

  /**
  Express was chosen, but the cart no longer holds Lumen's own items (422 from the preview).
  */
  protected readonly methodRefused = computed(
    () => problemTypeOf(this.preview.error()) === SHIPPING_METHOD_NOT_AVAILABLE_PROBLEM
  );

  protected readonly previewError = computed(() =>
    httpStatusOf(this.preview.error()) === 0
      ? 'We couldn’t load your order summary. Check your connection and try again.'
      : problemMessage(this.preview.error(), 'We couldn’t load your order summary. Try again.')
  );

  protected readonly placing = signal(false);

  /**
  The 409 answer to Place order: what changed, and the new total if confirming is enough.
  */
  protected readonly changed = signal<CheckoutChangedProblem | null>(null);

  protected readonly placeError = signal<string | null>(null);

  /**
  The API's reasons for refusing the address (400 keyed `shippingAddress.<field>`).
  */
  protected readonly addressErrors = signal<readonly string[]>([]);

  /**
   * The 409 only needs a fresh review (the cart changed in another tab): reload the preview. Any
   * other line that cannot be confirmed must be fixed in the cart.
   */
  protected readonly needsReview = computed(() => {
    const changes = this.changed()?.changes ?? [];
    return (
      changes.length > 0 &&
      changes.every((change) => REVIEW_AGAIN_KINDS.has(change.kind)) &&
      !this.changed()?.canConfirm
    );
  });

  private send(lines: PlaceOrderLine[], expectedTotal: number): void {
    const address = this.address();
    if (!address || this.placing()) {
      return;
    }
    this.sentLines = lines;
    this.placing.set(true);
    this.placeError.set(null);
    this.addressErrors.set([]);
    const review = {
      lines,
      expectedTotal,
      shippingAddress: address,
      shippingMethod: this.method(),
    };
    this.api
      .placeOrder({ checkoutKey: this.store.checkoutKeyFor(review), ...review })
      .pipe(finalize(() => this.placing.set(false)))
      .subscribe({
        next: (order) => {
          this.store.orderPlaced(order);
          // The order holds the items now; the badge follows whatever the server did to the cart.
          this.cart.reload().subscribe({ error: noop });
          void this.router.navigate(['/checkout/payment'], {
            queryParams: { order: order.number },
          });
        },
        error: (error: unknown) => this.failed(error),
      });
  }

  private failed(error: unknown): void {
    const problem = checkoutChangedProblemOf(error);
    if (problem) {
      this.changed.set(problem);
      // Show the lines at their new prices behind the notice.
      this.preview.reload();
      afterNextRender(
        { write: () => this.changesBanner()?.nativeElement.focus() },
        { injector: this.injector }
      );
      return;
    }
    if (problemTypeOf(error) === CHECKOUT_KEY_TAKEN_PROBLEM) {
      // The API asks for a reload, but a reload would send the same stored key and fail again.
      this.store.renewCheckoutKey();
      this.placeError.set('We couldn’t place your order. You have not been charged. Try again.');
      return;
    }
    const errors = validationErrorsOf(error);
    // The address rules key their errors `shippingAddress.<field>`; the attribute validation
    // that runs first may capitalise the prefix.
    const address = Object.entries(errors)
      .filter(([key]) => key.toLowerCase().startsWith('shippingaddress'))
      .flatMap(([, messages]) => messages);
    if (address.length > 0) {
      this.addressErrors.set(address);
      this.placeError.set('We can’t ship to this address. Change it and try again.');
      return;
    }
    this.placeError.set(
      problemMessage(error, 'We couldn’t place your order. You have not been charged. Try again.')
    );
  }

  /**
   * Also after the order was placed (the shopper came back from Payment): the same review goes
   * with the same key, and the API answers with the order already placed (BR-PAY-01a), so its
   * stock is not reserved a second time.
   */
  protected placeOrder(): void {
    const preview = this.loaded();
    if (!preview || this.blocked() || this.preview.isLoading()) {
      return;
    }
    this.send(reviewedLines(preview), preview.total);
  }

  /**
  Accepts the new amounts from the 409 and places the order with them.
  */
  protected confirmNewTotal(): void {
    const changed = this.changed();
    if (changed?.canConfirm && changed.newTotal !== null) {
      this.send(withNewPrices(this.sentLines, changed.changes), changed.newTotal);
    }
  }

  /**
  Shows the cart as it is now and lets the shopper review it again.
  */
  protected reviewAgain(): void {
    this.changed.set(null);
    this.preview.reload();
  }
}
