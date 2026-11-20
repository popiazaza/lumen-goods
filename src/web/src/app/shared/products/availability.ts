import { AvailabilityStatus, ProductDto } from '../../core/api/products.api';

export type AvailabilityTone = 'available' | 'low' | 'out' | 'unavailable';

/**
What a stock badge shows: always an icon and text, never colour alone (WCAG 1.4.1).
*/
export interface AvailabilityLabel {
  tone: AvailabilityTone;
  icon: string;
  text: string;
}

const STOCK_LABELS: Readonly<Record<AvailabilityStatus, AvailabilityLabel>> = {
  in_stock: { tone: 'available', icon: 'check_circle', text: 'In stock' },
  low_stock: { tone: 'low', icon: 'warning', text: 'Low stock' },
  out_of_stock: { tone: 'out', icon: 'cancel', text: 'Out of stock' },
};

/**
 * The stock badge for a product, straight from the API's `availability` and `sellable` fields.
 * A product that cannot be bought says so instead of showing its stock, so "In stock" never sits next to a disabled Add to cart.
 */
export const availabilityOf = (
  product: Pick<ProductDto, 'availability' | 'sellable'>
): AvailabilityLabel => {
  if (!product.sellable) {
    return { tone: 'unavailable', icon: 'block', text: 'Not available' };
  }
  const { status, quantity } = product.availability;
  const label = STOCK_LABELS[status];
  // Low stock names the count when the API knows it.
  return status === 'low_stock' && quantity !== null && quantity > 0
    ? { ...label, text: `Only ${quantity} left` }
    : label;
};
