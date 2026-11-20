import { HttpClient, HttpParams } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from './api-base-url';

/** Where a product comes from: Lumen Goods itself or one of the two partner shops. */
export const PRODUCT_SOURCES = ['lumen', 'harbor', 'kestrel'] as const;
export type ProductSource = (typeof PRODUCT_SOURCES)[number];

export const PRODUCT_CATEGORIES = [
  'apparel',
  'footwear',
  'bags',
  'home',
  'outdoor',
  'accessories',
] as const;
export type ProductCategory = (typeof PRODUCT_CATEGORIES)[number];

export const PRODUCT_SORTS = ['newest', 'price_asc', 'price_desc', 'name_asc'] as const;
export type ProductSort = (typeof PRODUCT_SORTS)[number];

export type AvailabilityStatus = 'in_stock' | 'low_stock' | 'out_of_stock';

/** Always USD with exactly two decimals; the server prices and converts, the client only shows it. */
export interface Money {
  amount: number;
  currency: 'USD';
}

export interface ProductImage {
  /** Absolute URL served by Lumen (`/media/...`); the browser never calls a partner. */
  url: string;
  isPrimary: boolean;
}

export interface ProductAvailability {
  status: AvailabilityStatus;
  /** `null` when the exact number is not known. */
  quantity: number | null;
}

/** The storefront product format (brief, page 5): the same shape whatever the source. */
export interface ProductDto {
  id: string;
  source: ProductSource;
  sourceProductId: string;
  name: string;
  /** Plain text, or HTML the server sanitised to p, br, b, strong, i, em, ul, ol, li. */
  description: string;
  category: ProductCategory;
  /** `null` means "Price on request": shown, never sellable. */
  price: Money | null;
  images: ProductImage[];
  /** Lower camelCase keys, e.g. `color`, `size`, `fillPower`. */
  attributes: Record<string, string>;
  availability: ProductAvailability;
  sellable: boolean;
  lastUpdatedUtc: string;
}

export interface ProductPage {
  items: ProductDto[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

/** Query for `GET /products`. Omitted fields use the API's defaults. */
export interface ProductSearch {
  q?: string;
  categories?: readonly ProductCategory[];
  sellers?: readonly ProductSource[];
  minPrice?: number | null;
  maxPrice?: number | null;
  inStock?: boolean;
  sort?: ProductSort;
  page?: number;
  pageSize?: number;
}

/** Products per catalogue page (assumption A-22). */
export const PRODUCT_PAGE_SIZE = 24;

/** `GET /api/v1/products` and `GET /api/v1/products/{id}`. */
@Service()
export class ProductsApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);

  /** One page of the catalogue. An invalid query answers 400 with ProblemDetails. */
  search(search: ProductSearch): Observable<ProductPage> {
    return this.http.get<ProductPage>(`${this.baseUrl}/products`, {
      params: toHttpParams(search),
    });
  }

  /** One product; 404 when the id is unknown. */
  get(id: string): Observable<ProductDto> {
    return this.http.get<ProductDto>(`${this.baseUrl}/products/${encodeURIComponent(id)}`);
  }
}

/** Repeated keys for multi-value filters: `category=apparel&category=bags`. */
function toHttpParams(search: ProductSearch): HttpParams {
  let params = new HttpParams();
  const q = search.q?.trim();
  if (q) {
    params = params.set('q', q);
  }
  for (const category of search.categories ?? []) {
    params = params.append('category', category);
  }
  for (const seller of search.sellers ?? []) {
    params = params.append('seller', seller);
  }
  if (search.minPrice != null) {
    params = params.set('minPrice', String(search.minPrice));
  }
  if (search.maxPrice != null) {
    params = params.set('maxPrice', String(search.maxPrice));
  }
  if (search.inStock) {
    params = params.set('inStock', 'true');
  }
  if (search.sort) {
    params = params.set('sort', search.sort);
  }
  if (search.page) {
    params = params.set('page', String(search.page));
  }
  if (search.pageSize) {
    params = params.set('pageSize', String(search.pageSize));
  }
  return params;
}
