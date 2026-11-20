import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from './api-base-url';
import { PagedResult, toQueryParams } from './paged-result';

// Contract: Lumen.Api/Orders/OrderContracts.cs. Enums are snake_case strings; money is a USD number
// with two decimals that the server computed (the client never adds, rounds or converts it).

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

/** BR-ORD-02: each line's own status. */
export type OrderLineStatus =
  'pending' | 'awaiting_partner' | 'needs_attention' | 'confirmed' | 'rejected';

export type ShippingMethod = 'standard' | 'express';
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

/** One row of the customer's order history, newest first. */
export interface OrderSummary {
  number: string;
  status: OrderStatus;
  /** Customer wording: "Processing", "Partially refunded", ... */
  statusText: string;
  createdUtc: string;
  total: number;
  refundedTotal: number;
  itemCount: number;
  sellerNames: string[];
}

export interface OrderLine {
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
  /** BR-ORD-02 wording with the seller's name: "Awaiting Kestrel Outfitters", "Not supplied, refunded $40.03". */
  statusText: string;
  /** Subtotal + tax refunded for this line; 0 when nothing was. */
  refundedAmount: number;
}

/** One seller's part of the order: its shipment and lines. */
export interface OrderShipment {
  source: string;
  sellerName: string;
  method: ShippingMethod;
  fee: number;
  feeRefunded: boolean;
  lines: OrderLine[];
}

export interface OrderTotals {
  subtotal: number;
  shipping: number;
  tax: number;
  /** What was charged. */
  total: number;
  refunded: number;
  /** Total minus refunded: what the customer paid in the end. */
  netPaid: number;
}

export interface ShippingAddress {
  name: string;
  line1: string;
  line2: string | null;
  city: string;
  region: string;
  postalCode: string;
  country: string;
}

/** The order's latest payment. */
export interface PaymentSummary {
  paymentId: string;
  method: PaymentMethod;
  status: PaymentStatus;
  amount: number;
  cardBrand: CardBrand | null;
  cardLast4: string | null;
  completedUtc: string | null;
}

/** A refund to the original payment method (BR-ORD-03). A failed one is retried; it is still owed. */
export interface Refund {
  id: string;
  /** The lines' `refundedAmount`s plus `shippingAmount`. */
  amount: number;
  status: RefundStatus;
  reason: string;
  /** The order lines it pays back (`OrderLine.id`), each with its subtotal + tax. */
  lineIds: string[];
  /** The seller's shipping fee it pays back (every line of that seller was rejected); 0 when none. */
  shippingAmount: number;
  createdUtc: string;
  completedUtc: string | null;
}

export interface OrderDetail {
  number: string;
  status: OrderStatus;
  statusText: string;
  createdUtc: string;
  paidUtc: string | null;
  holdExpiresUtc: string;
  shipments: OrderShipment[];
  totals: OrderTotals;
  shippingAddress: ShippingAddress;
  shippingMethod: ShippingMethod;
  payment: PaymentSummary | null;
  refunds: Refund[];
  canPay: boolean;
  /** Something can still change on its own (a payment being confirmed, a line awaiting its partner). */
  isUpdating: boolean;
}

/** Orders per history page. */
export const ORDER_PAGE_SIZE = 10;

/** `GET /api/v1/orders` and `GET /api/v1/orders/{number}`: the signed-in customer's own orders. */
@Service()
export class OrdersApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);

  /** Newest first. 401 when signed out. */
  list(page: number, pageSize = ORDER_PAGE_SIZE): Observable<PagedResult<OrderSummary>> {
    return this.http.get<PagedResult<OrderSummary>>(`${this.baseUrl}/orders`, {
      params: toQueryParams({ page, pageSize }),
    });
  }

  /** 404 for an unknown number and for another customer's order; 401 when signed out. */
  get(number: string): Observable<OrderDetail> {
    return this.http.get<OrderDetail>(`${this.baseUrl}/orders/${encodeURIComponent(number)}`);
  }
}
