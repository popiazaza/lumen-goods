import { aProduct } from '../../testing/fixtures';
import { MAX_QUANTITY_PER_ADD, clampQuantity, maxSelectableQuantity } from './quantity';

describe('maxSelectableQuantity', () => {
  it('is the known stock when it is below the cap', () => {
    expect(
      maxSelectableQuantity(aProduct({ availability: { status: 'low_stock', quantity: 3 } }))
    ).toBe(3);
  });

  it('is capped at 10 per add', () => {
    expect(
      maxSelectableQuantity(aProduct({ availability: { status: 'in_stock', quantity: 250 } }))
    ).toBe(MAX_QUANTITY_PER_ADD);
  });

  it('allows the cap when the quantity is unknown but in stock', () => {
    expect(
      maxSelectableQuantity(aProduct({ availability: { status: 'in_stock', quantity: null } }))
    ).toBe(10);
  });

  it('is 0 (cannot buy) when out of stock or not sellable', () => {
    expect(
      maxSelectableQuantity(aProduct({ availability: { status: 'out_of_stock', quantity: 0 } }))
    ).toBe(0);
    expect(maxSelectableQuantity(aProduct({ sellable: false }))).toBe(0);
    expect(
      maxSelectableQuantity(aProduct({ availability: { status: 'in_stock', quantity: 0 } }))
    ).toBe(0);
  });
});

describe('clampQuantity', () => {
  it.each([
    [5, 10, 5],
    [0, 10, 1],
    [-3, 10, 1],
    [25, 10, 10],
    [4, 3, 3],
    [2.7, 10, 2],
    [NaN, 10, 1],
    [null, 10, 1],
  ])('clamps %j into 1..%d as %d', (value, max, expected) => {
    expect(clampQuantity(value, max)).toBe(expected);
  });
});
