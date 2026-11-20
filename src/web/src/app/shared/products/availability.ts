import { ProductDto } from '../../core/api/products.api';

export type AvailabilityTone = 'available' | 'low' | 'out' | 'unavailable';

/**
What a stock badge shows: always an icon and text, never colour alone (WCAG 1.4.1).
*/
export interface AvailabilityLabel {
  tone: AvailabilityTone;
  icon: string;
  text: string;
}

/**
 * The stock badge for a product, straight from the API's `availability` and `sellable` fields.
 * A product that cannot be bought says so instead of showing its stock, so "In stock" never sits next to a disabled Add to cart.
 */
export function availabilityOf(
  product: Pick<ProductDto, 'availability' | 'sellable'>
): AvailabilityLabel {
  if (!product.sellable) {
    return { tone: 'unavailable', icon: 'block', text: 'Not available' };
  }
  const { status, quantity } = product.availability;
  switch (status) {
    case 'in_stock': {
      return { tone: 'available', icon: 'check_circle', text: 'In stock' };
    }
    case 'low_stock': {
      return {
        tone: 'low',
        icon: 'warning',
        text: quantity != null && quantity > 0 ? `Only ${quantity} left` : 'Low stock',
      };
    }
    case 'out_of_stock': {
      return { tone: 'out', icon: 'cancel', text: 'Out of stock' };
    }
  }
}
