// Test data builders shared by specs. Not imported by app code, so never bundled.
import { PartnerStatus } from '../core/api/admin-partners.api';
import { CartDto, CartLineDto } from '../core/api/cart.api';
import { CheckoutPreviewDto } from '../core/api/checkout.api';
import { OrderDetailDto, OrderLineDto, ShippingAddress } from '../core/api/order.models';
import { PaymentStatusDto } from '../core/api/payments.api';
import { ProductDto, ProductPage } from '../core/api/products.api';
import { Seller } from '../core/api/sellers.api';
import { Session } from '../core/api/session.api';
import { WalletDto } from '../core/api/wallet.api';

export const SELLERS: Seller[] = [
  { source: 'lumen', name: 'Lumen Goods' },
  { source: 'harbor', name: 'Harbor Supply Co.' },
  { source: 'kestrel', name: 'Kestrel Outfitters' },
];

export function aProduct(overrides: Partial<ProductDto> = {}): ProductDto {
  return {
    id: 'harbor-HSC-10021',
    source: 'harbor',
    sourceProductId: 'HSC-10021',
    name: 'Waxed Canvas Tote',
    description: 'Heavy waxed canvas tote with leather handles.',
    category: 'bags',
    price: { amount: 49.9, currency: 'USD' },
    images: [{ url: 'http://localhost/media/harbor/HSC-10021.jpg', isPrimary: true }],
    attributes: { color: 'Olive', material: 'Waxed canvas' },
    availability: { status: 'in_stock', quantity: 12 },
    sellable: true,
    lastUpdatedUtc: '2026-11-20T06:00:00Z',
    ...overrides,
  };
}

export function aPage(items: ProductDto[], overrides: Partial<ProductPage> = {}): ProductPage {
  return {
    items,
    page: 1,
    pageSize: 24,
    totalCount: items.length,
    totalPages: items.length > 0 ? 1 : 0,
    ...overrides,
  };
}

export function aPartner(overrides: Partial<PartnerStatus> = {}): PartnerStatus {
  return {
    partner: 'harbor',
    name: 'Harbor Supply Co.',
    lastSuccessUtc: '2026-11-20T06:00:00Z',
    lastAttemptUtc: '2026-11-20T06:00:00Z',
    lastError: null,
    liveProductCount: 214,
    refreshRequestedUtc: null,
    recentErrors: [],
    ...overrides,
  };
}

// ---- Purchase flow (cart, checkout, payment, orders) -------------------------------------------

export const CUSTOMER_SESSION: Session = {
  isAuthenticated: true,
  email: 'customer@lumen.test',
  roles: ['customer'],
};

export const ADMIN_SESSION: Session = {
  isAuthenticated: true,
  email: 'admin@lumen.test',
  roles: ['admin'],
};

export function aCartLine(overrides: Partial<CartLineDto> = {}): CartLineDto {
  return {
    id: 'line-1',
    product: {
      id: 'harbor-HSC-10021',
      source: 'harbor',
      sellerName: 'Harbor Supply Co.',
      name: 'Waxed Canvas Tote',
      imageUrl: null,
      availability: { status: 'in_stock', quantity: 12 },
      sellable: true,
    },
    quantity: 1,
    maxQuantity: 10,
    unitPrice: 49.9,
    unitPriceSeen: 49.9,
    lineTotal: 49.9,
    notices: {
      priceChanged: null,
      availabilityChanged: null,
      unavailable: null,
      quantityLimited: null,
    },
    blocksCheckout: false,
    addedUtc: '2026-11-20T08:00:00Z',
    ...overrides,
  };
}

export function aCart(
  lines: CartLineDto[] = [aCartLine()],
  overrides: Partial<CartDto> = {},
): CartDto {
  return {
    lines,
    itemCount: lines.reduce((count, line) => count + line.quantity, 0),
    totals: { subtotal: 49.9, shipping: 4.99, tax: 3.99, total: 58.88, isEstimate: true },
    canCheckout: lines.length > 0 && lines.every((line) => !line.blocksCheckout),
    ...overrides,
  };
}

export const US_ADDRESS: ShippingAddress = {
  name: 'Ada Lovelace',
  line1: '1200 NW Naito Pkwy',
  line2: null,
  city: 'Portland',
  region: 'OR',
  postalCode: '97209',
  country: 'US',
};

export function aPreview(overrides: Partial<CheckoutPreviewDto> = {}): CheckoutPreviewDto {
  return {
    shipments: [
      {
        source: 'lumen',
        sellerName: 'Lumen Goods',
        method: 'standard',
        fee: 4.99,
        lines: [
          {
            cartLineId: 'line-mug',
            productId: 'lumen-mug',
            name: 'Enamel Camp Mug',
            imageUrl: null,
            unitPrice: 18.5,
            quantity: 2,
            lineTotal: 37,
            tax: 2.96,
          },
        ],
      },
      {
        source: 'kestrel',
        sellerName: 'Kestrel Outfitters',
        method: 'standard',
        fee: 4.99,
        lines: [
          {
            cartLineId: 'line-socks',
            productId: 'kestrel-K-7731',
            name: 'Merino Trail Socks',
            imageUrl: null,
            unitPrice: 16.22,
            quantity: 1,
            lineTotal: 16.22,
            tax: 1.3,
          },
        ],
      },
    ],
    subtotal: 53.22,
    shipping: 9.98,
    tax: 4.26,
    total: 67.46,
    shippingMethod: 'standard',
    shippingOptions: [
      { method: 'standard', fee: 4.99, shippingTotal: 9.98 },
      { method: 'express', fee: 12.99, shippingTotal: 17.98 },
    ],
    blockers: [],
    ...overrides,
  };
}

export function anOrderLine(overrides: Partial<OrderLineDto> = {}): OrderLineDto {
  return {
    id: 'order-line-1',
    lineNumber: 1,
    productId: 'lumen-mug',
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

export function anOrder(overrides: Partial<OrderDetailDto> = {}): OrderDetailDto {
  return {
    number: 'LG-7K3M9QX2',
    status: 'awaiting_payment',
    statusText: 'Awaiting payment',
    createdUtc: '2026-11-20T08:00:00Z',
    paidUtc: null,
    holdExpiresUtc: '2026-11-20T08:15:00Z',
    shipments: [
      {
        source: 'lumen',
        sellerName: 'Lumen Goods',
        method: 'standard',
        fee: 4.99,
        feeRefunded: false,
        lines: [anOrderLine({ status: 'pending', statusText: 'Pending' })],
      },
      {
        source: 'kestrel',
        sellerName: 'Kestrel Outfitters',
        method: 'standard',
        fee: 4.99,
        feeRefunded: false,
        lines: [
          anOrderLine({
            id: 'order-line-2',
            lineNumber: 2,
            productId: 'kestrel-K-7731',
            name: 'Merino Trail Socks',
            unitPrice: 16.22,
            quantity: 1,
            subtotal: 16.22,
            tax: 1.3,
            status: 'pending',
            statusText: 'Pending',
          }),
        ],
      },
    ],
    totals: {
      subtotal: 53.22,
      shipping: 9.98,
      tax: 4.26,
      total: 67.46,
      refunded: 0,
      netPaid: 67.46,
    },
    shippingAddress: US_ADDRESS,
    shippingMethod: 'standard',
    payment: null,
    refunds: [],
    canPay: true,
    isUpdating: false,
    ...overrides,
  };
}

export function aPaymentStatus(overrides: Partial<PaymentStatusDto> = {}): PaymentStatusDto {
  return {
    paymentId: 'payment-1',
    orderNumber: 'LG-7K3M9QX2',
    method: 'card',
    status: 'processing',
    amount: 67.46,
    cardBrand: 'visa',
    cardLast4: '1111',
    needsOneTimeCode: false,
    oneTimeCodeExpiresUtc: null,
    testOneTimeCode: null,
    declineMessage: null,
    attemptsLeft: 5,
    canRetry: false,
    retryMode: 'none',
    orderStatus: 'payment_processing',
    holdExpiresUtc: '2026-11-20T08:15:00Z',
    serverTimeUtc: '2026-11-20T08:00:00Z',
    ...overrides,
  };
}

export function aWallet(balance = 2000): WalletDto {
  return { balance, currency: 'USD', recentTransactions: [] };
}
