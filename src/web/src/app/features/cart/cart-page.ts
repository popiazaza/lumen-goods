import { LiveAnnouncer } from '@angular/cdk/a11y';
import { CurrencyPipe, formatCurrency } from '@angular/common';
import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router, RouterLink } from '@angular/router';
import { Observable, finalize, noop } from 'rxjs';
import { CartDto, CartLineDto } from '../../core/api/cart.api';
import { problemMessage } from '../../core/api/problem-details';
import { SessionApi } from '../../core/api/session.api';
import { CartStateService } from '../../core/cart/cart-state.service';
import { ProductImage } from '../../shared/products/product-image';
import { AwaitingPaymentNotice } from '../checkout/awaiting-payment-notice';
import { canAcknowledge, groupBySeller, noticesOf } from './cart-notices';

/**
The quantity selector's ceiling per line (BR-CART-03); the server's `maxQuantity` can be lower.
*/
export const MAX_LINE_QUANTITY = 10;

const money = (amount: number): string => formatCurrency(amount, 'en-US', '$', 'USD', '1.2-2');

const NAME_LIST = /* @__PURE__ */ new Intl.ListFormat('en-US', { type: 'conjunction' });

/**
 * The cart (`/cart`): lines grouped by seller, quantities, notices for lines whose price or stock
 * changed (BR-CART-02), estimated totals, and Checkout, blocked while a line cannot be bought
 * (BR-CART-03). Every change goes to the API; the page shows the cart it answers with.
 */
@Component({
  selector: 'app-cart-page',
  imports: [
    CurrencyPipe,
    RouterLink,
    MatButton,
    MatIcon,
    MatIconButton,
    MatProgressBar,
    AwaitingPaymentNotice,
    ProductImage,
  ],
  templateUrl: './cart-page.html',
  styleUrl: './cart-page.scss',
})
export class CartPage {
  private readonly cartState = inject(CartStateService);

  private readonly router = inject(Router);

  private readonly announcer = inject(LiveAnnouncer);

  private readonly snackBar = inject(MatSnackBar);

  private readonly sessionApi = inject(SessionApi);

  private readonly injector = inject(Injector);

  private readonly busy = signal<ReadonlySet<string>>(new Set());

  private readonly heading = viewChild.required<ElementRef<HTMLElement>>('heading');

  /**
  Which lines the banner lists; a string, so a re-read cart with the same lines is no change.
  */
  private readonly changedLineIds = computed(() =>
    this.changedLines()
      .map((line) => line.id)
      .join(' ')
  );

  protected readonly session = this.sessionApi.session;

  protected readonly cart = this.cartState.cart;

  protected readonly loadFailed = computed(
    () => this.cartState.status() === 'error' && !this.cart()
  );

  protected readonly mergeNotice = this.cartState.mergeNotice;

  protected readonly groups = computed(() => groupBySeller(this.cart()?.lines ?? []));

  protected readonly lineViews = computed(
    () =>
      new Map(
        (this.cart()?.lines ?? []).map((line) => [
          line.id,
          {
            notices: noticesOf(line),
            canAcknowledge: canAcknowledge(line),
            // Never offer more than 10, nor less than what the line holds (so it can be lowered).
            max: Math.min(MAX_LINE_QUANTITY, Math.max(line.maxQuantity, 1)),
          },
        ])
      )
  );

  /**
  Lines with something to say, for the summary banner above the list.
  */
  protected readonly changedLines = computed(() =>
    (this.cart()?.lines ?? []).filter((line) => this.lineViews().get(line.id)?.notices.length)
  );

  /**
   * The banner offers "Acknowledge all" while at least one line has notices the shopper can accept; an unavailable line or a quantity above stock stays until it is fixed.
   */
  protected readonly canAcknowledgeAll = computed(() =>
    this.changedLines().some((line) => this.lineViews().get(line.id)?.canAcknowledge)
  );

  protected readonly acknowledgingAll = signal(false);

  /**
  Why Acknowledge all failed; cleared once the banner lists other lines.
  */
  protected readonly acknowledgeAllError = linkedSignal({
    source: this.changedLineIds,
    computation: () => '',
  });

  protected readonly blockingCount = computed(
    () => (this.cart()?.lines ?? []).filter((line) => line.blocksCheckout).length
  );

  /**
  Why Check out is disabled, linked to the button with `aria-describedby`.
  */
  protected readonly blockedReason = computed(() => {
    const cart = this.cart();
    const extra = (cart?.lines.length ?? 0) - (cart?.maxLines ?? Infinity);
    if (extra > 0 && this.blockingCount() === 0) {
      // Only a sign-in merge can leave a cart above the limit (BR-CART-03).
      return `An order can have at most ${cart?.maxLines} different products, and your cart has ${cart?.lines.length}. Remove ${extra} to check out.`;
    }
    const count = this.blockingCount();
    return count === 1
      ? '1 item can’t be ordered as it is. Remove it or lower its quantity to check out.'
      : `${count} items can’t be ordered as they are. Remove them or lower their quantities to check out.`;
  });

  /**
  Names the lines the totals leave out (the API prices only what can be ordered), so the summary's item count and
  amounts never seem to contradict a line's own total.
  */
  protected readonly excludedNote = computed(() => {
    const names = (this.cart()?.lines ?? [])
      .filter((line) => line.notices.unavailable)
      .map((line) => line.product.name);
    if (names.length === 0) {
      return null;
    }
    const list = NAME_LIST.format(names);
    return names.length === 1
      ? `Not included, as it can’t be ordered: ${list}.`
      : `Not included, as they can’t be ordered: ${list}.`;
  });

  protected readonly lineErrors = signal<Readonly<Record<string, string>>>({});

  constructor() {
    // Fresh notices and prices every time the cart opens.
    this.cartState.reload().subscribe({ error: noop });
  }

  private undoRemove(line: CartLineDto): void {
    this.cartState.add(line.product.id, line.quantity).subscribe({
      next: () => this.say(`Put ${line.product.name} back in your cart.`),
      error: (error: unknown) =>
        this.say(problemMessage(error, `We couldn’t put ${line.product.name} back.`)),
    });
  }

  private run(
    line: CartLineDto,
    request: Observable<CartDto>,
    message: (cart: CartDto) => string,
    afterSuccess?: () => void
  ): void {
    this.busy.update((busy) => {
      const next = new Set(busy);
      next.add(line.id);
      return next;
    });
    this.setLineError(line.id, null);
    request
      .pipe(
        finalize(() =>
          this.busy.update((busy) => {
            const next = new Set(busy);
            next.delete(line.id);
            return next;
          })
        )
      )
      .subscribe({
        next: (cart) => {
          const text = message(cart);
          if (text) {
            this.say(text);
          }
          afterSuccess?.();
        },
        error: (error: unknown) => {
          this.setLineError(
            line.id,
            problemMessage(error, 'We couldn’t update this item. Try again.')
          );
          // The cart may have changed under us (422: stock moved); show the server's version.
          this.cartState.reload().subscribe({ error: noop });
        },
      });
  }

  private setLineError(lineId: string, message: string | null): void {
    this.lineErrors.update((errors) =>
      message
        ? { ...errors, [lineId]: message }
        : Object.fromEntries(Object.entries(errors).filter(([id]) => id !== lineId))
    );
  }

  private say(message: string): void {
    void this.announcer.announce(message, 'polite');
  }

  protected isBusy(lineId: string): boolean {
    return this.busy().has(lineId);
  }

  protected retry(): void {
    this.cartState.reload().subscribe({ error: noop });
  }

  protected dismissMergeNotice(): void {
    this.cartState.setMergeNotice(null);
  }

  protected step(line: CartLineDto, delta: number): void {
    this.setQuantity(line, line.quantity + delta);
  }

  /**
  A typed quantity, committed on change (blur or Enter).
  */
  protected typed(line: CartLineDto, event: Event): void {
    const input = event.target as HTMLInputElement;
    const value = Math.trunc(Number(input.value));
    if (!Number.isFinite(value) || value < 1) {
      input.value = String(line.quantity);
      return;
    }
    this.setQuantity(line, value, input);
  }

  protected setQuantity(line: CartLineDto, quantity: number, input?: HTMLInputElement): void {
    const max = this.lineViews().get(line.id)?.max ?? MAX_LINE_QUANTITY;
    // Lowering is always allowed (a line above stock must come down); raising stops at `max`.
    const next = quantity < line.quantity ? Math.max(quantity, 1) : Math.min(quantity, max);
    if (next === line.quantity || this.isBusy(line.id)) {
      if (input) {
        input.value = String(line.quantity);
      }
      return;
    }
    this.run(line, this.cartState.setQuantity(line.id, next), (cart) => {
      const updated = cart.lines.find((candidate) => candidate.id === line.id);
      return `${line.product.name}: quantity ${updated?.quantity ?? next}. Cart total ${money(cart.totals.total)}.`;
    });
  }

  protected acknowledge(line: CartLineDto): void {
    this.run(
      line,
      this.cartState.acknowledge(line.id),
      () => `Noted the changes to ${line.product.name}.`
    );
  }

  protected acknowledgeAll(): void {
    if (this.acknowledgingAll()) {
      return;
    }
    this.acknowledgingAll.set(true);
    this.acknowledgeAllError.set('');
    this.cartState
      .acknowledgeAll()
      .pipe(finalize(() => this.acknowledgingAll.set(false)))
      .subscribe({
        next: () => {
          this.say('Noted the changes to your cart.');
          // The button (and often the whole banner) is gone: start again at the heading, not at the top of the page.
          afterNextRender(
            {
              write: () => {
                if (!this.canAcknowledgeAll()) {
                  this.heading().nativeElement.focus();
                }
              },
            },
            { injector: this.injector }
          );
        },
        error: (error: unknown) => {
          this.acknowledgeAllError.set(
            problemMessage(error, 'We couldn’t note the changes. Try again.')
          );
          this.cartState.reload().subscribe({ error: noop });
        },
      });
  }

  protected remove(line: CartLineDto): void {
    this.run(
      line,
      this.cartState.remove(line.id),
      () => '',
      () => {
        const ref = this.snackBar.open(`Removed ${line.product.name}.`, 'Undo', {
          duration: 10_000,
        });
        ref.onAction().subscribe(() => this.undoRemove(line));
      }
    );
  }

  protected checkout(): void {
    if (this.cart()?.canCheckout) {
      void this.router.navigate(['/checkout']);
    }
  }
}
