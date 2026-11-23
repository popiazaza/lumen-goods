// The one set of order wire types: checkout, payment, the confirmation, the order history and
// order pages, and the admin order pages all read these (Lumen.Api `Orders/OrderContracts.cs`,
// `Checkout/CheckoutContracts.cs`, `Payments/PaymentContracts.cs`). Enums travel as snake_case
// strings; money as numbers with two decimals that the server computed (the client never adds,
// rounds or converts it).

export type ShippingMethod = 'standard' | 'express';

/**
 * Lumen Goods' own products. Their shipment is the only one that can go Express (BR-SHIP-01):
 * partner orders carry no shipping method. Partners themselves are API configuration.
 */
export const OWN_SOURCE = 'lumen';

export const SHIPPING_METHOD_NAMES: Readonly<Record<ShippingMethod, string>> = {
  standard: 'Standard',
  express: 'Express',
};

export type OrderStatus =
  | 'awaiting_payment'
  | 'payment_processing'
  | 'paid'
  | 'completed'
  | 'partially_refunded'
  | 'refunded'
  | 'expired';

export const ORDER_STATUSES: readonly OrderStatus[] = [
  'awaiting_payment',
  'payment_processing',
  'paid',
  'completed',
  'partially_refunded',
  'refunded',
  'expired',
];

/**
BR-ORD-02: each line's own status.
*/
export type OrderLineStatus =
  'pending' | 'awaiting_partner' | 'needs_attention' | 'confirmed' | 'rejected';

export type PaymentMethod = 'card' | 'wallet';

export type PaymentStatus =
  | 'created'
  | 'processing'
  | 'requires_action'
  | 'succeeded'
  | 'declined'
  | 'error'
  | 'failed'
  | 'expired';

export type CardBrand = 'visa' | 'mastercard' | 'amex' | 'other';

/**
A failed refund is retried with the same id: it is still owed.
*/
export type RefundStatus = 'pending' | 'succeeded' | 'failed';

/**
A US ship-to address in the fields both partners' order formats need.
*/
export interface ShippingAddress {
  name: string;
  line1: string;
  line2: string | null;
  city: string;
  /**
  State code, e.g. "OR".
  */
  region: string;
  /**
  ZIP or ZIP+4.
  */
  postalCode: string;
  /**
  ISO 3166-1 alpha-2; "US" at launch.
  */
  country: string;
}

/**
One row of the customer's order history (`GET /orders`), newest first.
*/
export interface OrderSummaryDto {
  number: string;
  status: OrderStatus;
  /**
  Customer wording: "Processing", "Partial refund pending", "Partially refunded", ...
  */
  statusText: string;
  createdUtc: string;
  total: number;
  refundedTotal: number;
  /**
  Sum of the line quantities.
  */
  itemCount: number;
  sellerNames: string[];
  /**
  Awaiting payment, hold not over, attempts left: offer Pay now (as `OrderDetailDto.canPay`).
  */
  canPay: boolean;
  /**
  The current payment waits for the customer's one-time code: offer Enter the code.
  */
  awaitingOneTimeCode: boolean;
}

export interface OrderLineDto {
  id: string;
  lineNumber: number;
  productId: string;
  name: string;
  imageUrl: string | null;
  unitPrice: number;
  quantity: number;
  subtotal: number;
  tax: number;
  status: OrderLineStatus;
  /**
   * BR-ORD-02 wording with the seller's name: "Awaiting Kestrel Outfitters", "Delayed", "Not supplied, refund pending" until the refund is paid back, then "Not supplied, refunded $40.03".
   */
  statusText: string;
  /**
  Subtotal + tax refunded for this line; 0 when nothing was.
  */
  refundedAmount: number;
}

/**
One seller's part of the order: its shipment and its lines.
*/
export interface OrderShipmentDto {
  source: string;
  sellerName: string;
  method: ShippingMethod;
  fee: number;
  feeRefunded: boolean;
  lines: OrderLineDto[];
}

export interface OrderTotalsDto {
  subtotal: number;
  shipping: number;
  tax: number;
  /**
  What was charged.
  */
  total: number;
  /**
  Every refund recorded, paid back or not (shipping fees included).
  */
  refunded: number;
  /**
  Total minus refunded: what the customer paid in the end; 0 before payment.
  */
  netPaid: number;
}

/**
The order's latest payment: "Paid by Visa ending 1111" or "Paid from your wallet".
*/
export interface PaymentSummaryDto {
  paymentId: string;
  method: PaymentMethod;
  status: PaymentStatus;
  amount: number;
  cardBrand: CardBrand | null;
  cardLast4: string | null;
  completedUtc: string | null;
}

/**
A refund to the original payment method (BR-ORD-03).
*/
export interface RefundDto {
  id: string;
  /**
  The lines' `refundedAmount`s plus `shippingAmount`.
  */
  amount: number;
  status: RefundStatus;
  reason: string;
  /**
  The order lines it pays back (`OrderLineDto.id`).
  */
  lineIds: string[];
  /**
  The seller's shipping fee it pays back (every line of that seller was rejected); 0 when none.
  */
  shippingAmount: number;
  createdUtc: string;
  completedUtc: string | null;
}

/**
 * `GET /api/v1/orders/{number}` and the answer to Place order: lines grouped by seller under their
 * shipment, each with its own status (BR-ORD-02) and refund (BR-ORD-03). The admin order is this
 * plus what only the admin sees (`AdminOrder.order`).
 */
export interface OrderDetailDto {
  number: string;
  status: OrderStatus;
  /**
  Customer wording for `status` (paid is "Processing").
  */
  statusText: string;
  createdUtc: string;
  paidUtc: string | null;
  /**
  Until when the order can be paid (price and stock hold).
  */
  holdExpiresUtc: string;
  shipments: OrderShipmentDto[];
  totals: OrderTotalsDto;
  shippingAddress: ShippingAddress;
  /**
  The method chosen for Lumen's own items (each shipment carries its own `method`).
  */
  shippingMethod: ShippingMethod;
  /**
  The latest payment, or null before the first one.
  */
  payment: PaymentSummaryDto | null;
  refunds: RefundDto[];
  /**
  Awaiting payment, hold not over, attempts left: show the payment step.
  */
  canPay: boolean;
  /**
   * Something can still change on its own: a payment being confirmed, a line awaiting its partner, or a refund being paid back. The order pages poll while it is true.
   */
  isUpdating: boolean;
}

const BRAND_NAMES: Readonly<Record<CardBrand, string>> = {
  visa: 'Visa',
  mastercard: 'Mastercard',
  amex: 'American Express',
  other: 'Card',
};

export const cardBrandName = (brand: CardBrand | null | undefined): string =>
  (brand && BRAND_NAMES[brand]) || 'Card';
