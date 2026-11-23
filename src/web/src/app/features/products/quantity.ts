import { ProductDto } from '../../core/api/products.api';

/**
 * Most units the product page's selector offers. A UX cap mirroring the server's per-line limit
 * (BR-CART-03, `CartLine.MaxQuantity`), which the product API does not return; it is not business
 * logic: the server checks the limit and the stock again when the line is added (and answers with
 * its own message), and the cart page uses each line's `maxQuantity` from the API instead.
 */
export const MAX_QUANTITY_PER_ADD = 10;

/**
 * The highest quantity the selector offers, or 0 when the product cannot be bought: not sellable,
 * out of stock, or no units left. An unknown quantity (`null`) that is in stock allows the cap.
 */
export const maxSelectableQuantity = (product: ProductDto): number => {
  const { status, quantity } = product.availability;
  if (status === 'out_of_stock' || !product.sellable) {
    return 0;
  }
  if (quantity === null) {
    return MAX_QUANTITY_PER_ADD;
  }
  return Math.max(0, Math.min(Math.trunc(quantity), MAX_QUANTITY_PER_ADD));
};

/**
Brings a typed quantity into 1..max (whole numbers); empty or junk input becomes 1.
*/
export const clampQuantity = (value: number | null | undefined, max: number): number =>
  value === null || value === undefined || !Number.isFinite(value)
    ? 1
    : Math.min(Math.max(Math.trunc(value), 1), Math.max(max, 1));
