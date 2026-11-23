// Test data builders shared by specs. Not imported by app code, so never bundled.
import { PartnerStatus } from '../core/api/admin-partners.api';
import { CartDto, CartLineDto } from '../core/api/cart.api';
import { CheckoutPreviewDto } from '../core/api/checkout.api';
import { ShippingAddress } from '../core/api/order.models';
import { PaymentStatusDto } from '../core/api/payments.api';
import { ProductDto, ProductPage } from '../core/api/products.api';
import { Seller } from '../core/api/sellers.api';
import { Session } from '../core/api/session.api';
import { WalletDto } from '../core/api/wallet.api';

const HARBOR_NAME = 'Harbor Supply Co.';

/**
When the fixture products and partners were last synced.
*/
const LAST_SYNC_UTC = '2026-11-20T06:00:00Z';

export const SELLERS: Seller[] = [
  { source: 'lumen', name: 'Lumen Goods' },
  { source: 'harbor', name: HARBOR_NAME },
  { source: 'kestrel', name: 'Kestrel Outfitters' },
];

export const aProduct = (overrides: Partial<ProductDto> = {}): ProductDto => ({
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
  lastUpdatedUtc: LAST_SYNC_UTC,
  lastCheckedUtc: LAST_SYNC_UTC,
  ...overrides,
});

export const aPage = (items: ProductDto[], overrides: Partial<ProductPage> = {}): ProductPage => ({
  items,
  page: 1,
  pageSize: 24,
  totalCount: items.length,
  totalPages: items.length > 0 ? 1 : 0,
  ...overrides,
});

export const aPartner = (overrides: Partial<PartnerStatus> = {}): PartnerStatus => ({
  partner: 'harbor',
  name: HARBOR_NAME,
  lastSuccessUtc: LAST_SYNC_UTC,
  lastAttemptUtc: LAST_SYNC_UTC,
  lastError: null,
  liveProductCount: 214,
  refreshRequestedUtc: null,
  heldRemovals: 0,
  recentErrors: [],
  ...overrides,
});

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

export const aCartLine = (overrides: Partial<CartLineDto> = {}): CartLineDto => ({
  id: 'line-1',
  product: {
    id: 'harbor-HSC-10021',
    source: 'harbor',
    sellerName: HARBOR_NAME,
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
});

export const aCart = (lines: CartLineDto[], overrides: Partial<CartDto> = {}): CartDto => ({
  lines,
  itemCount: lines.reduce((count, line) => count + line.quantity, 0),
  totals: { subtotal: 49.9, shipping: 4.99, tax: 3.99, total: 58.88, isEstimate: true },
  canCheckout:
    lines.length > 0 && lines.length <= 50 && lines.every((line) => !line.blocksCheckout),
  maxLines: 50,
  ...overrides,
});

export const US_ADDRESS: ShippingAddress = {
  name: 'Ada Lovelace',
  line1: '1200 NW Naito Pkwy',
  line2: null,
  city: 'Portland',
  region: 'OR',
  postalCode: '97209',
  country: 'US',
};

export const aPreview = (overrides: Partial<CheckoutPreviewDto> = {}): CheckoutPreviewDto => ({
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
});

export const aPaymentStatus = (overrides: Partial<PaymentStatusDto> = {}): PaymentStatusDto => ({
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
});

export const aWallet = (balance = 2000): WalletDto => ({
  balance,
  currency: 'USD',
  recentTransactions: [],
});
