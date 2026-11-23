import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from './api-base-url';
import { AvailabilityStatus, ProductAvailability } from './products.api';

/**
The product as the cart shows it now (current availability and sellability).
*/
export interface CartProductDto {
  id: string;
  /**
  `lumen` or a partner id.
  */
  source: string;
  /**
  The shop's display name, e.g. "Harbor Supply Co.".
  */
  sellerName: string;
  name: string;
  imageUrl: string | null;
  availability: ProductAvailability;
  sellable: boolean;
}

export interface PriceChangedNotice {
  from: number;
  to: number;
}

export interface AvailabilityChangedNotice {
  from: AvailabilityStatus;
  to: AvailabilityStatus;
}

export type CartLineUnavailableReason = 'no_longer_available' | 'out_of_stock';

export interface QuantityLimitedNotice {
  /**
  What the customer asked for (or the line holds).
  */
  requested: number;
  /**
  What they can have now.
  */
  allowed: number;
}

/**
BR-CART-02 notices; each is `null` when it does not apply.
*/
export interface CartLineNotices {
  priceChanged: PriceChangedNotice | null;
  availabilityChanged: AvailabilityChangedNotice | null;
  unavailable: CartLineUnavailableReason | null;
  quantityLimited: QuantityLimitedNotice | null;
}

export interface CartLineDto {
  id: string;
  product: CartProductDto;
  quantity: number;
  /**
  The quantity selector's upper bound: 10, or less when stock is lower; 0 when it cannot be bought.
  */
  maxQuantity: number;
  /**
  Current USD unit price; `null` when the product no longer has one.
  */
  unitPrice: number | null;
  unitPriceSeen: number;
  lineTotal: number | null;
  notices: CartLineNotices;
  /**
  Must be removed or reduced before checkout (BR-CART-03). Acknowledging does not clear it.
  */
  blocksCheckout: boolean;
  addedUtc: string;
}

/**
Estimated with Standard shipping per seller; checkout prices the real order.
*/
export interface CartTotals {
  /**
  Units the subtotal covers: lines that can't be ordered are left out, unlike the cart's `itemCount`.
  */
  itemCount: number;
  subtotal: number;
  shipping: number;
  tax: number;
  total: number;
  /**
  Always true for the cart: shipping, tax and total are shown as estimates.
  */
  isEstimate: boolean;
}

/**
`GET /cart` and the answer to every cart change: the whole cart, priced by the server.
*/
export interface CartDto {
  lines: CartLineDto[];
  /**
  Sum of all the line quantities (the header badge, A-46); the summary uses `totals.itemCount`.
  */
  itemCount: number;
  totals: CartTotals;
  /**
  False when the cart is empty, a line blocks checkout, or it holds more than `maxLines` lines.
  */
  canCheckout: boolean;
  /**
  The most different products the cart may hold (BR-CART-03). Only a sign-in merge can leave
  more; checkout is then blocked until lines are removed.
  */
  maxLines: number;
}

export const EMPTY_CART: CartDto = {
  lines: [],
  itemCount: 0,
  totals: { itemCount: 0, subtotal: 0, shipping: 0, tax: 0, total: 0, isEstimate: true },
  canCheckout: false,
  maxLines: 50,
};

/**
 * 409: the guest cart this request's cookie named is gone, merged into an account by a sign-in
 * (another tab) that raced the request. Read `GET /cart` again (now the customer's) and retry.
 */
export const CART_CHANGED_PROBLEM = 'urn:lumen:problem:cart-changed';

/**
 * The shopper's cart under `/api/v1/cart`, for guests (cookie) and customers alike. Every call
 * answers 200 with the whole cart. Errors are ProblemDetails: 400 for a quantity outside 1 to 10,
 * 422 when the product cannot be bought or the quantity is above what is allowed (BR-CART-03),
 * 404 for a line that no longer exists, 409 `cart-changed` after a racing sign-in, and 403 for a
 * signed-in staff account (only guests and customers have a cart). `GET /cart` never creates a
 * cart.
 */
@Service()
export class CartApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);

  get(): Observable<CartDto> {
    return this.http.get<CartDto>(`${this.baseUrl}/cart`);
  }

  addLine(productId: string, quantity: number): Observable<CartDto> {
    return this.http.post<CartDto>(`${this.baseUrl}/cart/lines`, { productId, quantity });
  }

  updateLine(lineId: string, quantity: number): Observable<CartDto> {
    return this.http.put<CartDto>(`${this.baseUrl}/cart/lines/${encodeURIComponent(lineId)}`, {
      quantity,
    });
  }

  removeLine(lineId: string): Observable<CartDto> {
    return this.http.delete<CartDto>(`${this.baseUrl}/cart/lines/${encodeURIComponent(lineId)}`);
  }

  /**
  Accepts every line's notices at once.
  */
  acknowledgeAll(): Observable<CartDto> {
    return this.http.post<CartDto>(`${this.baseUrl}/cart/acknowledge`, null);
  }

  /**
  Accepts the line's notices: the seen price and availability become the current ones.
  */
  acknowledge(lineId: string): Observable<CartDto> {
    return this.http.post<CartDto>(
      `${this.baseUrl}/cart/lines/${encodeURIComponent(lineId)}/acknowledge`,
      null
    );
  }
}
