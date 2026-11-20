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
}
