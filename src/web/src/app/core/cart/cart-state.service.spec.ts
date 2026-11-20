import { TestBed } from '@angular/core/testing';
import { CartStateService } from './cart-state.service';

describe('CartStateService', () => {
  let cart: CartStateService;

  beforeEach(() => {
    cart = TestBed.inject(CartStateService);
  });

  it('adds the quantity to the badge count', () => {
    cart.add('harbor-1', 2);
    cart.add('lumen-7', 1);

    expect(cart.itemCount()).toBe(3);
    expect(cart.isEmpty()).toBe(false);
  });

  it.each([0, -1, 1.5, Number.NaN])('rejects quantity %d', (quantity) => {
    expect(() => cart.add('harbor-1', quantity)).toThrow(RangeError);
    expect(cart.itemCount()).toBe(0);
  });

  it('needs a product id', () => {
    expect(() => cart.add('', 1)).toThrow();
  });
});
