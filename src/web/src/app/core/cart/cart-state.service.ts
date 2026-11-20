import { Service, computed, inject, signal } from '@angular/core';
import { Observable, defer, tap } from 'rxjs';
import { CartApi, CartDto } from '../api/cart.api';

export type CartLoadStatus = 'idle' | 'loading' | 'loaded' | 'error';

/**
 * The shopper's cart as the server last described it, for the header badge and the cart page.
 *
 * The server owns the cart and prices every line: each change goes to the API and the whole cart
 * it answers with replaces this copy. Nothing here calculates money or stock.
 */
@Service()
export class CartStateService {
  private readonly api = inject(CartApi);
  private readonly current = signal<CartDto | undefined>(undefined);
  private readonly loadStatus = signal<CartLoadStatus>('idle');
  private readonly merged = signal<string | null>(null);
  /** Each request takes a ticket; an answer older than the one shown is dropped. */
  private issued = 0;
  private shown = 0;

  /** The last cart the server sent; `undefined` until the first answer. */
  readonly cart = this.current.asReadonly();
  readonly status = this.loadStatus.asReadonly();

  /** Total quantity (the API's `itemCount`), for the mini-cart badge. */
  readonly itemCount = computed(() => this.current()?.itemCount ?? 0);
  readonly isEmpty = computed(() => this.itemCount() === 0);

  /** "We merged your carts" after sign-in, shown on the cart page until dismissed. */
  readonly mergeNotice = this.merged.asReadonly();

  /** Loads the cart once (the header calls this on start-up); later calls do nothing. */
  ensureLoaded(): void {
    if (this.loadStatus() === 'idle') {
      this.reload().subscribe({ error: () => undefined });
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
      }),
    );
  }

  /** Adds a product, or more of it to its line. The API may answer 422 (BR-CART-03). */
  add(productId: string, quantity: number): Observable<CartDto> {
    if (!productId) {
      throw new Error('A product id is required to add to the cart.');
    }
    if (!Number.isInteger(quantity) || quantity < 1) {
      throw new RangeError(`Quantity must be a whole number of at least 1, got ${quantity}.`);
    }
    return this.track(this.api.addLine(productId, quantity));
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

  setMergeNotice(message: string | null): void {
    this.merged.set(message);
  }

  /** Shows the answer unless a newer request has already been answered. */
  private track(request: Observable<CartDto>): Observable<CartDto> {
    return defer(() => {
      const ticket = ++this.issued;
      return request.pipe(
        tap((cart) => {
          if (ticket > this.shown) {
            this.shown = ticket;
            this.current.set(cart);
          }
        }),
      );
    });
  }
}
