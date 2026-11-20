import { formatCurrency } from '@angular/common';
import { CartLineDto } from '../../core/api/cart.api';
import { AvailabilityStatus } from '../../core/api/products.api';

export type NoticeTone = 'info' | 'warning' | 'blocking';

/** One line of a cart line's notice: always an icon and text, never colour alone. */
export interface CartLineNotice {
  kind: 'price' | 'availability' | 'unavailable' | 'quantity';
  tone: NoticeTone;
  icon: string;
  text: string;
}

const usd = (amount: number) => formatCurrency(amount, 'en-US', '$', 'USD', '1.2-2');

const AVAILABILITY_TEXT: Readonly<Record<AvailabilityStatus, string>> = {
  in_stock: 'in stock',
  low_stock: 'low stock',
  out_of_stock: 'out of stock',
};

/**
 * The notices a cart line shows (BR-CART-02, BR-CART-03), worded for the shopper. The server
 * decides which apply; this only turns them into text.
 */
export function noticesOf(line: CartLineDto): CartLineNotice[] {
  const { priceChanged, availabilityChanged, unavailable, quantityLimited } = line.notices;
  const notices: CartLineNotice[] = [];

  if (unavailable) {
    notices.push({
      kind: 'unavailable',
      tone: 'blocking',
      icon: 'block',
      text:
        unavailable === 'out_of_stock'
          ? 'Out of stock. Remove it to check out.'
          : `No longer available: ${line.product.sellerName} stopped selling it. Remove it to check out.`,
    });
  }

  if (priceChanged) {
    const direction = priceChanged.to > priceChanged.from ? 'went up' : 'dropped';
    notices.push({
      kind: 'price',
      tone: 'warning',
      icon: 'info',
      text: `Price ${direction} from ${usd(priceChanged.from)} to ${usd(priceChanged.to)} since you added it.`,
    });
  }

  // "Out of stock" is already said by the unavailable notice.
  if (availabilityChanged && !(unavailable && availabilityChanged.to === 'out_of_stock')) {
    const left = line.product.availability.quantity;
    notices.push({
      kind: 'availability',
      tone: 'warning',
      icon: 'warning',
      text:
        availabilityChanged.to === 'low_stock' && left != null && left > 0
          ? `Stock is running low: only ${left} left.`
          : `Availability changed from ${AVAILABILITY_TEXT[availabilityChanged.from]} to ${AVAILABILITY_TEXT[availabilityChanged.to]}.`,
    });
  }

  if (quantityLimited) {
    const { requested, allowed } = quantityLimited;
    notices.push(
      line.quantity > allowed
        ? {
            kind: 'quantity',
            tone: 'blocking',
            icon: 'warning',
            text:
              allowed > 0
                ? `Only ${allowed} available. Reduce the quantity to ${allowed} or fewer to check out.`
                : 'None available right now. Remove it to check out.',
          }
        : {
            kind: 'quantity',
            tone: 'info',
            icon: 'info',
            text: `We changed the quantity from ${requested} to ${allowed}, the most you can buy.`,
          },
    );
  }

  return notices;
}

/**
 * Whether the line has notices the shopper can accept: a price or availability change, or a
 * quantity the server already capped. "Unavailable" and a quantity above stock stay until fixed.
 */
export function canAcknowledge(line: CartLineDto): boolean {
  const { priceChanged, availabilityChanged, quantityLimited } = line.notices;
  return (
    !!priceChanged ||
    !!availabilityChanged ||
    (!!quantityLimited && line.quantity <= quantityLimited.allowed)
  );
}

/** Lines in the API's order, grouped by seller (first appearance first). */
export function groupBySeller(
  lines: readonly CartLineDto[],
): { source: string; sellerName: string; lines: CartLineDto[] }[] {
  const groups = new Map<string, { source: string; sellerName: string; lines: CartLineDto[] }>();
  for (const line of lines) {
    const source = line.product.source;
    const group = groups.get(source) ?? { source, sellerName: line.product.sellerName, lines: [] };
    group.lines.push(line);
    groups.set(source, group);
  }
  return [...groups.values()];
}
