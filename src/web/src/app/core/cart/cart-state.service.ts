import { Service, computed, inject, signal } from '@angular/core';
import { Observable, catchError, defer, noop, switchMap, tap, throwError } from 'rxjs';
import { CART_CHANGED_PROBLEM, CartApi, CartDto } from '../api/cart.api';
import { problemTypeOf } from '../api/problem-details';

export type CartLoadStatus = 'idle' | 'loading' | 'loaded' | 'error';

/**
 * The shopper's cart as the server last described it, for the header badge and the cart page.
 *
 * The server owns the cart and prices every line: each change goes to the API and the whole cart it answers with replaces this copy. Nothing here calculates money or stock.
 */
@Service()
export class CartStateService {
  private readonly api = inject(CartApi);

  private readonly current = signal<CartDto | undefined>(undefined);

  private readonly loadStatus = signal<CartLoadStatus>('idle');

  private readonly merged = signal<string | null>(null);

  /**
  Each request takes a ticket; an answer older than the one shown is dropped.
  */
  private issued = 0;

  private shown = 0;

  /**
  The last cart the server sent; `undefined` until the first answer.
  */
  readonly cart = this.current.asReadonly();

  readonly status = this.loadStatus.asReadonly();

  /**
  Total quantity (the API's `itemCount`), for the mini-cart badge.
  */
  readonly itemCount = computed(() => this.current()?.itemCount ?? 0);

  readonly isEmpty = computed(() => this.itemCount() === 0);

  /**
  "We merged your carts" after sign-in, shown on the cart page until dismissed.
  */
  readonly mergeNotice = this.merged.asReadonly();

  /**
  Shows the answer unless a newer request has already been answered.
  */
  private track(request: Observable<CartDto>): Observable<CartDto> {
    return defer(() => {
      this.issued += 1;
      const ticket = this.issued;
      return request.pipe(
        tap((cart) => {
          if (ticket <= this.shown) {
            return;
          }

          this.shown = ticket;
          this.current.set(cart);
        })
      );
    });
  }

  /**
  Loads the cart once (the header calls this on start-up); later calls do nothing.
  */
  ensureLoaded(): void {
    if (this.loadStatus() === 'idle') {
      this.reload().subscribe({ error: noop });
    }
  }

  /**
   * Fetches the cart again. Run it after sign-in and sign-out (the server switches between the
   * guest and the saved cart), and when a page needs fresh notices. Subscribe to send it.
   */
  reload(): Observable<CartDto> {
    return this.track(this.api.get()).pipe(
      tap({
        subscribe: () => this.loadStatus.set('loading'),
        next: () => this.loadStatus.set('loaded'),
        // A failed refresh keeps showing the last known cart.
        error: () => this.loadStatus.set(this.current() ? 'loaded' : 'error'),
      })
    );
  }

  /**
   * Adds a product, or more of it to its line. The API may answer 422 (BR-CART-03). A 409
   * `cart-changed` means a sign-in in another tab merged the guest cart this request named: the
   * add never happened, so it is sent once more after re-reading the (now merged) cart.
   */
  add(productId: string, quantity: number): Observable<CartDto> {
    if (!productId) {
      throw new Error('A product id is required to add to the cart.');
    }
    if (!Number.isSafeInteger(quantity) || quantity < 1) {
      throw new RangeError(`Quantity must be a whole number of at least 1, got ${quantity}.`);
    }
    const send = () => this.track(this.api.addLine(productId, quantity));
    return send().pipe(
      // eslint-disable-next-line promise/prefer-await-to-callbacks -- RxJS catchError operator, not a Node-style callback
      catchError((error: unknown) =>
        problemTypeOf(error) === CART_CHANGED_PROBLEM
          ? this.reload().pipe(switchMap(() => send()))
          : throwError(() => error)
      )
    );
  }

  setQuantity(lineId: string, quantity: number): Observable<CartDto> {
    return this.track(this.api.updateLine(lineId, quantity));
  }

  remove(lineId: string): Observable<CartDto> {
    return this.track(this.api.removeLine(lineId));
  }

  acknowledge(lineId: string): Observable<CartDto> {
    return this.track(this.api.acknowledge(lineId));
  }

  /**
  Accepts the notices of every line at once (`POST /cart/acknowledge`).
  */
  acknowledgeAll(): Observable<CartDto> {
    return this.track(this.api.acknowledgeAll());
  }

  setMergeNotice(message: string | null): void {
    this.merged.set(message);
  }
}
