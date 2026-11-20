// Wire types shared by checkout, payment and the order pages (Lumen.Api `Orders/OrderContracts.cs`).
// Enums travel as snake_case strings; money as numbers with two decimals, priced by the server.

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

export type RefundStatus = 'pending' | 'succeeded' | 'failed';

/** A US ship-to address in the fields both partners' order formats need. */
export interface ShippingAddress {
  name: string;
  line1: string;
  line2: string | null;
  city: string;
  /** State code, e.g. "OR". */
  region: string;
  /** ZIP or ZIP+4. */
  postalCode: string;
  /** ISO 3166-1 alpha-2; "US" at launch. */
  country: string;
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
  /** BR-ORD-02 wording with the seller's name, e.g. "Awaiting Kestrel Outfitters". */
  statusText: string;
  refundedAmount: number;
}

/** One seller's part of the order: its shipment and its lines. */
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
  total: number;
  refunded: number;
  netPaid: number;
}

export interface PaymentSummaryDto {
  paymentId: string;
  method: PaymentMethod;
  status: PaymentStatus;
  amount: number;
  cardBrand: CardBrand | null;
  cardLast4: string | null;
  completedUtc: string | null;
}

export interface RefundDto {
  id: string;
  amount: number;
  status: RefundStatus;
  reason: string;
  lineIds: string[];
  /** The seller's shipping fee paid back (all its lines rejected); 0 when none. */
  shippingAmount: number;
  createdUtc: string;
  completedUtc: string | null;
}

/** `GET /api/v1/orders/{number}` and the answer to Place order. */
export interface OrderDetailDto {
  number: string;
  status: OrderStatus;
  /** Customer wording ("Processing" for paid). */
  statusText: string;
  createdUtc: string;
  paidUtc: string | null;
  /** Until when the order can be paid (price and stock hold). */
  holdExpiresUtc: string;
  shipments: OrderShipmentDto[];
  totals: OrderTotalsDto;
  shippingAddress: ShippingAddress;
  shippingMethod: ShippingMethod;
  payment: PaymentSummaryDto | null;
  refunds: RefundDto[];
  /** Awaiting payment, hold not over, attempts left: show the payment step. */
  canPay: boolean;
  /** Something can still change on its own (payment confirming, partner deciding): poll. */
  isUpdating: boolean;
}

const BRAND_NAMES: Readonly<Record<CardBrand, string>> = {
  visa: 'Visa',
  mastercard: 'Mastercard',
  amex: 'American Express',
  other: 'Card',
};

export function cardBrandName(brand: CardBrand | null | undefined): string {
  return brand ? BRAND_NAMES[brand] : 'Card';
}
