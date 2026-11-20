// Test data builders shared by specs. Not imported by app code, so never bundled.
import { PartnerStatus } from '../core/api/admin-partners.api';
import { ProductDto, ProductPage } from '../core/api/products.api';
import { Seller } from '../core/api/sellers.api';

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
