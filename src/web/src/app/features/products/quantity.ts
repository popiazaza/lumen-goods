import { ProductDto } from '../../core/api/products.api';

/**
 * Most units one "Add to cart" can add. A UX cap, not stock control: the server checks stock again when the line is added and at checkout.
 */
export const MAX_QUANTITY_PER_ADD = 10;

/**
 * The highest quantity the selector offers, or 0 when the product cannot be bought: not sellable,
 * out of stock, or no units left. An unknown quantity (`null`) that is in stock allows the cap.
 */
export function maxSelectableQuantity(product: ProductDto): number {
  const { status, quantity } = product.availability;
  if (!product.sellable || status === 'out_of_stock') {
    return 0;
  }
  if (quantity === null) {
    return MAX_QUANTITY_PER_ADD;
  }
  return Math.max(0, Math.min(Math.trunc(quantity), MAX_QUANTITY_PER_ADD));
}

/**
Brings a typed quantity into 1..max (whole numbers); empty or junk input becomes 1.
*/
export function clampQuantity(value: number | null | undefined, max: number): number {
  return value == null || !Number.isFinite(value)
    ? 1
    : Math.min(Math.max(Math.trunc(value), 1), Math.max(max, 1));
}
