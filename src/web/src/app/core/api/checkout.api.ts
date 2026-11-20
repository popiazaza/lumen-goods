import { HttpClient, HttpParams } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from './api-base-url';
import { OrderDetailDto, ShippingAddress, ShippingMethod } from './order.models';
import { ProblemDetails, httpErrorOf } from './problem-details';

/**
 * A priced line of the preview. `productId`, `quantity` and `unitPrice` go back unchanged in
 * Place order's `lines`; `tax` is the line's own tax (rounded per line on the server).
 */
export interface CheckoutLineDto {
  cartLineId: string;
  productId: string;
  name: string;
  imageUrl: string | null;
  unitPrice: number;
  quantity: number;
  /**
  Unit price × quantity.
  */
  lineTotal: number;
  tax: number;
}

/**
One seller's shipment in the preview.
*/
export interface CheckoutShipmentDto {
  source: string;
  sellerName: string;
  method: ShippingMethod;
  fee: number;
  lines: CheckoutLineDto[];
}

/**
A method this cart may use: Express only when it holds Lumen's own items.
*/
export interface ShippingOptionDto {
  method: ShippingMethod;
  /**
  The fee for Lumen's own shipment with this method.
  */
  fee: number;
  /**
  The order's whole shipping charge with this method (all sellers' shipments).
  */
  shippingTotal: number;
}

export type CheckoutChangeKind =
  | 'price_changed'
  | 'quantity_changed'
  | 'cart_changed'
  | 'quantity_limited'
  | 'unavailable'
  | 'temporarily_unavailable';

/**
A line that differs from what the customer reviewed, or that blocks the order.
*/
export interface CheckoutLineChangeDto {
  /**
  `null` for a reviewed product that is no longer in the cart (cart_changed).
  */
  cartLineId: string | null;
  productId: string;
  name: string;
  sellerName: string;
  kind: CheckoutChangeKind;
  /**
  price_changed: the unit price reviewed.
  */
  from: number | null;
  /**
  price_changed: the unit price now (already saved to the product).
  */
  to: number | null;
  /**
  quantity_changed, cart_changed: the quantity reviewed (0 for a line added since).
  */
  quantityFrom: number | null;
  /**
  quantity_changed, cart_changed: the quantity in the cart now (0 for a line removed since).
  */
  quantityTo: number | null;
  /**
  quantity_limited: how many can be bought now.
  */
  available: number | null;
  /**
  Ready to show, e.g. "Price changed from $16.22 to $17.31".
  */
  message: string;
}

/**
`GET /checkout/preview`: the cart priced as an order would be.
*/
export interface CheckoutPreviewDto {
  shipments: CheckoutShipmentDto[];
  subtotal: number;
  shipping: number;
  tax: number;
  /**
  What the review page shows and Place order sends back as `expectedTotal`.
  */
  total: number;
  shippingMethod: ShippingMethod;
  shippingOptions: ShippingOptionDto[];
  /**
  Lines that stop the order; empty when it can be placed.
  */
  blockers: CheckoutLineChangeDto[];
}

/**
One reviewed line, as the preview showed it.
*/
export interface PlaceOrderLine {
  productId: string;
  quantity: number;
  /**
  The USD unit price the customer saw.
  */
  unitPrice: number;
}

/**
 * Place exactly what was reviewed: the server compares every line and the total with the cart
 * re-priced now (BR-CHK-01) and answers 409 on any difference.
 */
export interface PlaceOrderRequest {
  /**
  Created once per checkout; a repeated Place order with it returns the same order (BR-PAY-01).
  */
  checkoutKey: string;
  /**
  Every reviewed line (the preview's shipments' lines, flattened).
  */
  lines: PlaceOrderLine[];
  /**
  The total the customer reviewed.
  */
  expectedTotal: number;
  shippingAddress: ShippingAddress;
  shippingMethod: ShippingMethod;
}

/**
The 409 body of Place order when what was reviewed is no longer true (BR-CHK-01, BR-CHK-02).
*/
export interface CheckoutChangedProblem extends ProblemDetails {
  type: typeof CHECKOUT_CHANGED_PROBLEM;
  /**
  One entry per differing line; empty when only the total moved (shipping or tax).
  */
  changes: CheckoutLineChangeDto[];
  /**
  The total now, at the saved prices; `null` when a line has to change first.
  */
  newTotal: number | null;
  /**
  Only prices (or just the total) changed: confirming the new amounts is enough.
  */
  canConfirm: boolean;
}

export const CHECKOUT_CHANGED_PROBLEM = 'urn:lumen:problem:checkout-changed';
/**
 * 422 from the preview and Place order: Express was asked for a cart without Lumen's own items
 * (BR-SHIP-01), e.g. they were removed in another tab after Express was chosen.
 */
export const SHIPPING_METHOD_NOT_AVAILABLE_PROBLEM =
  'urn:lumen:problem:shipping-method-not-available';
/**
409 from Place order: the checkout key is another customer's; only a new key can place it.
*/
export const CHECKOUT_KEY_TAKEN_PROBLEM = 'urn:lumen:problem:checkout-key-taken';

/**
The lines of a preview as Place order sends them back.
*/
export const reviewedLines = (preview: CheckoutPreviewDto): PlaceOrderLine[] =>
  preview.shipments.flatMap((shipment) =>
    shipment.lines.map(({ productId, quantity, unitPrice }) => ({
      productId,
      quantity,
      unitPrice,
    }))
  );

/**
 * The reviewed lines with each `price_changed` line at its new price: what a confirmed Place order
 * sends after a 409 whose new amounts the customer accepted.
 */
export const withNewPrices = (
  lines: readonly PlaceOrderLine[],
  changes: readonly CheckoutLineChangeDto[]
): PlaceOrderLine[] => {
  const newPrices = new Map(
    changes
      .filter((change) => change.kind === 'price_changed' && change.to !== null)
      .map((change) => [change.productId, change.to as number])
  );
  return lines.map((line) => ({
    ...line,
    unitPrice: newPrices.get(line.productId) ?? line.unitPrice,
  }));
};

/**
The checkout-changed problem behind a failed Place order, if that is what it was.
*/
export const checkoutChangedProblemOf = (error: unknown): CheckoutChangedProblem | undefined => {
  const response = httpErrorOf(error);
  const body = response?.error as Partial<CheckoutChangedProblem> | null | undefined;
  if (response?.status === 409 && body?.type === CHECKOUT_CHANGED_PROBLEM) {
    return {
      ...body,
      changes: body.changes ?? [],
      newTotal: body.newTotal ?? null,
      canConfirm: body.canConfirm ?? false,
    } as CheckoutChangedProblem;
  }
  return undefined;
};

/**
 * Checkout (signed-in customers): `GET /checkout/preview` and `POST /checkout/orders`. The placed
 * order is read back with `OrdersApi.get`.
 */
@Service()
export class CheckoutApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);

  preview(shippingMethod: ShippingMethod): Observable<CheckoutPreviewDto> {
    return this.http.get<CheckoutPreviewDto>(`${this.baseUrl}/checkout/preview`, {
      params: new HttpParams({ fromObject: { shippingMethod } }),
    });
  }

  /**
   * 201 with the new order, or 200 with the same order for a repeated checkout key. 409
   * `CheckoutChangedProblem` when lines, prices, stock or the total changed (nothing was created),
   * or `checkout-key-taken`; 400 validation problem keyed `shippingAddress.<field>` for an invalid
   * address; 422 `cart-empty` or `shipping-method-not-available`.
   */
  placeOrder(request: PlaceOrderRequest): Observable<OrderDetailDto> {
    return this.http.post<OrderDetailDto>(`${this.baseUrl}/checkout/orders`, request);
  }
}
