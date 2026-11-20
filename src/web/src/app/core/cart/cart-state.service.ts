import { Service, computed, signal } from '@angular/core';

/**
 * Client-side view of the shopper's cart for the header and cart badge.
 *
 * Placeholder until the cart API exists. The server owns the cart and prices every line;
 * this service only mirrors what the API returns and never computes money.
 */
@Service()
export class CartStateService {
  private readonly count = signal(0);

  /** Total quantity of items in the cart, as reported by the server. */
  readonly itemCount = this.count.asReadonly();

  readonly isEmpty = computed(() => this.count() === 0);

  setItemCount(count: number): void {
    this.count.set(Math.max(0, Math.trunc(count)));
  }

  /**
   * Adds a product to the cart. Until the cart API lands this only moves the badge count; it will
   * then POST the line and take the count from the server's answer (which may cap the quantity).
   */
  add(productId: string, quantity: number): void {
    if (!productId) {
      throw new Error('A product id is required to add to the cart.');
    }
    if (!Number.isInteger(quantity) || quantity < 1) {
      throw new RangeError(`Quantity must be a whole number of at least 1, got ${quantity}.`);
    }
    this.count.update((count) => count + quantity);
  }
}
