// Order and admin test data builders shared by specs. Not imported by app code, so never bundled.
import { AdminOrder, AdminOrderSummary, PartnerOrder } from '../core/api/admin-orders.api';
import { AdminProduct } from '../core/api/admin-products.api';
import {
  OrderDetailDto,
  OrderLineDto,
  OrderSummaryDto,
  PaymentSummaryDto,
} from '../core/api/order.models';
import { PagedResult } from '../core/api/paged-result';

export function aPagedResult<T>(
  items: T[],
  overrides: Partial<PagedResult<T>> = {}
): PagedResult<T> {
  return {
    items,
    page: 1,
    pageSize: 10,
    totalCount: items.length,
    totalPages: items.length > 0 ? 1 : 0,
    ...overrides,
  };
}

export function anOrderSummary(overrides: Partial<OrderSummaryDto> = {}): OrderSummaryDto {
  return {
    number: 'LG-7K3M9QX2',
    status: 'paid',
    statusText: 'Processing',
    createdUtc: '2026-11-20T08:00:00Z',
    total: 75.46,
    refundedTotal: 0,
    itemCount: 3,
    sellerNames: ['Lumen Goods', 'Kestrel Outfitters'],
    ...overrides,
  };
}

export function anOrderLine(overrides: Partial<OrderLineDto> = {}): OrderLineDto {
  return {
    id: 'line-1',
    lineNumber: 1,
    productId: 'product-1',
    name: 'Enamel Camp Mug',
    imageUrl: null,
    unitPrice: 18.5,
    quantity: 2,
    subtotal: 37,
    tax: 2.96,
    status: 'confirmed',
    statusText: 'Confirmed',
    refundedAmount: 0,
    ...overrides,
  };
}

export const SOCKS_LINE = anOrderLine({
  id: 'line-2',
  lineNumber: 2,
  productId: 'product-2',
  name: 'Merino Trail Socks',
  unitPrice: 16.22,
  quantity: 1,
  subtotal: 16.22,
  tax: 1.3,
  status: 'awaiting_partner',
  statusText: 'Awaiting Kestrel Outfitters',
});

/**
The contract's example: a Lumen line confirmed, a Kestrel line still awaiting its partner.
*/
export function anOrderDetail(overrides: Partial<OrderDetailDto> = {}): OrderDetailDto {
  return {
    number: 'LG-7K3M9QX2',
    status: 'paid',
    statusText: 'Processing',
    createdUtc: '2026-11-20T08:00:00Z',
    paidUtc: '2026-11-20T08:01:10Z',
    holdExpiresUtc: '2026-11-20T08:15:00Z',
    shipments: [
      {
        source: 'lumen',
        sellerName: 'Lumen Goods',
        method: 'express',
        fee: 12.99,
        feeRefunded: false,
        lines: [anOrderLine()],
      },
      {
        source: 'kestrel',
        sellerName: 'Kestrel Outfitters',
        method: 'standard',
        fee: 4.99,
        feeRefunded: false,
        lines: [SOCKS_LINE],
      },
    ],
    totals: {
      subtotal: 53.22,
      shipping: 17.98,
      tax: 4.26,
      total: 75.46,
      refunded: 0,
      netPaid: 75.46,
    },
    shippingAddress: {
      name: 'Ada Lovelace',
      line1: '1200 NW Naito Pkwy',
      line2: null,
      city: 'Portland',
      region: 'OR',
      postalCode: '97209',
      country: 'US',
    },
    shippingMethod: 'express',
    payment: aCardPayment(),
    refunds: [],
    canPay: false,
    isUpdating: true,
    ...overrides,
  };
}

export function aCardPayment(overrides: Partial<PaymentSummaryDto> = {}): PaymentSummaryDto {
  return {
    paymentId: 'payment-1',
    method: 'card',
    status: 'succeeded',
    amount: 75.46,
    cardBrand: 'visa',
    cardLast4: '1111',
    completedUtc: '2026-11-20T08:01:10Z',
    ...overrides,
  };
}

/**
 * Place order's answer: the same order before payment, Standard shipping for both sellers, every line pending, the payment step open.
 */
export function anOrder(overrides: Partial<OrderDetailDto> = {}): OrderDetailDto {
  const order = anOrderDetail();
  return {
    ...order,
    status: 'awaiting_payment',
    statusText: 'Awaiting payment',
    paidUtc: null,
    shipments: order.shipments.map((shipment) => ({
      ...shipment,
      method: 'standard' as const,
      fee: 4.99,
      lines: shipment.lines.map((line) => ({
        ...line,
        status: 'pending' as const,
        statusText: 'Awaiting payment',
      })),
    })),
    totals: { subtotal: 53.22, shipping: 9.98, tax: 4.26, total: 67.46, refunded: 0, netPaid: 0 },
    shippingMethod: 'standard',
    payment: null,
    canPay: true,
    isUpdating: false,
    ...overrides,
  };
}

/**
The same order once Kestrel has answered: every line final, nothing left to wait for.
*/
export function aSettledOrderDetail(overrides: Partial<OrderDetailDto> = {}): OrderDetailDto {
  const order = anOrderDetail();
  return {
    ...order,
    status: 'completed',
    statusText: 'Completed',
    shipments: order.shipments.map((shipment) => ({
      ...shipment,
      lines: shipment.lines.map((line) => ({
        ...line,
        status: 'confirmed' as const,
        statusText:
          shipment.source === 'lumen' ? 'Confirmed' : `Confirmed by ${shipment.sellerName}`,
      })),
    })),
    isUpdating: false,
    ...overrides,
  };
}

export function anAdminOrderSummary(overrides: Partial<AdminOrderSummary> = {}): AdminOrderSummary {
  return {
    number: 'LG-7K3M9QX2',
    status: 'paid',
    statusText: 'Processing',
    createdUtc: '2026-11-20T08:00:00Z',
    customerEmail: 'customer@lumengoods.test',
    total: 75.46,
    refundedTotal: 0,
    sellerNames: ['Lumen Goods', 'Kestrel Outfitters'],
    paymentMethod: 'card',
    needsAttention: false,
    ...overrides,
  };
}

export function aPartnerOrder(overrides: Partial<PartnerOrder> = {}): PartnerOrder {
  return {
    id: 'partner-order-1',
    orderLineId: 'line-2',
    lineNumber: 2,
    source: 'kestrel',
    sellerName: 'Kestrel Outfitters',
    externalRef: 'LG-7K3M9QX2-2',
    partnerOrderId: 'KT-5521',
    status: 'accepted',
    reason: null,
    attempts: 3,
    nextAttemptUtc: '2026-11-20T08:02:10Z',
    lastError: null,
    submittedUtc: '2026-11-20T08:01:15Z',
    acceptedUtc: '2026-11-20T08:01:16Z',
    decidedUtc: null,
    needsAttention: false,
    outcomeUnknown: false,
    ...overrides,
  };
}

export function anAdminOrder(overrides: Partial<AdminOrder> = {}): AdminOrder {
  return {
    order: anOrderDetail(),
    customerEmail: 'customer@lumengoods.test',
    failedPaymentAttempts: 0,
    paymentEvents: [
      {
        occurredUtc: '2026-11-20T08:01:00Z',
        paymentId: 'payment-1',
        type: 'started',
        amount: 75.46,
        detail: 'Card: visa ending 0059',
      },
      {
        occurredUtc: '2026-11-20T08:01:01Z',
        paymentId: 'payment-1',
        type: 'gateway_error',
        amount: null,
        detail: 'Gateway error',
      },
      {
        occurredUtc: '2026-11-20T08:01:04Z',
        paymentId: 'payment-1',
        type: 'approved',
        amount: 75.46,
        detail: null,
      },
    ],
    partnerOrders: [aPartnerOrder()],
    ...overrides,
  };
}

export function anAdminProduct(overrides: Partial<AdminProduct> = {}): AdminProduct {
  return {
    id: '0193a0f1-0000-7000-8000-000000000001',
    sku: 'LG-MUG-01',
    name: 'Enamel Camp Mug',
    description: 'A sturdy enamel mug.',
    category: 'home',
    price: 18.5,
    images: [{ file: 'mug.png', url: '/media/own/mug.png', isPrimary: true }],
    attributes: { color: 'Cream', material: 'Enamel steel' },
    onHand: 10,
    reserved: 2,
    availability: { status: 'in_stock', quantity: 8 },
    isActive: true,
    sellable: true,
    createdUtc: '2026-11-01T08:00:00Z',
    lastUpdatedUtc: '2026-11-19T08:00:00Z',
    version: '41',
    ...overrides,
  };
}
