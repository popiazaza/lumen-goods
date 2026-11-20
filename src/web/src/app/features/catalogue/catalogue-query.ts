import { ParamMap, Params } from '@angular/router';
import {
  PRODUCT_CATEGORIES,
  PRODUCT_PAGE_SIZE,
  PRODUCT_SORTS,
  ProductCategory,
  ProductSearch,
  ProductSort,
  ProductSource,
} from '../../core/api/products.api';

/**
 * Everything that decides what the catalogue shows. The URL's query string is the only place it is stored: the page parses it on every navigation and writes changes back by navigating, so Back, Forward, reloads and shared links all reproduce the same view.
 */
export interface CatalogueQuery {
  q: string;
  categories: readonly ProductCategory[];
  sellers: readonly ProductSource[];
  minPrice: number | null;
  maxPrice: number | null;
  inStock: boolean;
  sort: ProductSort;
  page: number;
}

export const DEFAULT_SORT: ProductSort = 'newest';

export const DEFAULT_CATALOGUE_QUERY: CatalogueQuery = {
  q: '',
  categories: [],
  sellers: [],
  minPrice: null,
  maxPrice: null,
  inStock: false,
  sort: DEFAULT_SORT,
  page: 1,
};

export const SORT_LABELS: Readonly<Record<ProductSort, string>> = {
  newest: 'Newest',
  price_asc: 'Price: low to high',
  price_desc: 'Price: high to low',
  name_asc: 'Name: A to Z',
};

/**
The search box allows this much; longer values in a hand-edited URL are cut, not rejected.
*/
export const MAX_QUERY_LENGTH = 200;

/**
Whole dollars or dollars and cents, no sign or exponent: "10", "10.5", "10.50".
*/
const PRICE_PATTERN = /^\d{1,7}(\.\d{1,2})?$/;
const PAGE_PATTERN = /^[1-9]\d{0,5}$/;
/**
A seller id as the API writes them (`harbor`, `kestrel`, `north-fjord`); anything else is junk.
*/
const SELLER_PATTERN = /^[a-z0-9][a-z0-9_-]{0,63}$/;

/**
 * Reads the catalogue state from the URL. Unknown or malformed values are dropped rather than sent to the API, so a mistyped shared link still shows products instead of an error.
 *
 * Sellers are configuration on the API, so only their shape is checked here; `restrictSellers`
 * drops the ones `GET /sellers` does not list once that list has loaded.
 */
export function parseCatalogueQuery(params: ParamMap): CatalogueQuery {
  let minPrice = parsePrice(params.get('minPrice'));
  let maxPrice = parsePrice(params.get('maxPrice'));
  if (minPrice !== null && maxPrice !== null && minPrice > maxPrice) {
    // An impossible range has no "right" half to keep.
    minPrice = null;
    maxPrice = null;
  }
  const sort = params.get('sort');
  const page = params.get('page');
  return {
    q: (params.get('q') ?? '').trim().slice(0, MAX_QUERY_LENGTH),
    categories: pickKnown(params.getAll('category'), PRODUCT_CATEGORIES),
    sellers: canonicalSellers(params.getAll('seller')),
    minPrice,
    maxPrice,
    inStock: params.get('inStock') === 'true',
    sort: isOneOf(sort, PRODUCT_SORTS) ? sort : DEFAULT_SORT,
    page: page !== null && PAGE_PATTERN.test(page) ? Number(page) : 1,
  };
}

/**
 * Writes the catalogue state as query params, leaving defaults out so URLs stay short and one
 * view has one URL. Multi-value filters repeat their key (`category=bags&category=home`).
 */
export function catalogueQueryParams(query: CatalogueQuery): Params {
  const params: Params = {};
  if (query.q) {
    params['q'] = query.q;
  }
  if (query.categories.length > 0) {
    params['category'] = [...query.categories];
  }
  if (query.sellers.length > 0) {
    params['seller'] = [...query.sellers];
  }
  if (query.minPrice !== null) {
    params['minPrice'] = String(query.minPrice);
  }
  if (query.maxPrice !== null) {
    params['maxPrice'] = String(query.maxPrice);
  }
  if (query.inStock) {
    params['inStock'] = 'true';
  }
  if (query.sort !== DEFAULT_SORT) {
    params['sort'] = query.sort;
  }
  if (query.page > 1) {
    params['page'] = String(query.page);
  }
  return params;
}

/**
The filters and sort a shopper changed, applied on top of the current state.
*/
export type CatalogueChange = Partial<Omit<CatalogueQuery, 'page'>>;

/**
 * Applies a change of search, filter or sort. Always goes back to page 1, because the old page number means nothing in the new result set. Values are put in canonical order.
 */
export function applyChange(query: CatalogueQuery, change: CatalogueChange): CatalogueQuery {
  const next = { ...query, ...change, page: 1 };
  return {
    ...next,
    categories: pickKnown(next.categories, PRODUCT_CATEGORIES),
    sellers: canonicalSellers(next.sellers),
  };
}

/**
 * Keeps only sellers the API lists. `known` is `null` until `GET /sellers` has answered: every
 * well-formed id is kept until then, so a shared link to a new partner is not cut short.
 */
export function restrictSellers(
  query: CatalogueQuery,
  known: ReadonlySet<string> | null
): CatalogueQuery {
  return !known || query.sellers.every((seller) => known.has(seller))
    ? query
    : { ...query, sellers: query.sellers.filter((seller) => known.has(seller)) };
}

/**
The same filters with every refinement removed; the search text and sort stay.
*/
export function clearFilters(query: CatalogueQuery): CatalogueQuery {
  return applyChange(query, {
    categories: [],
    sellers: [],
    minPrice: null,
    maxPrice: null,
    inStock: false,
  });
}

/**
How many refinements are on (shown on the mobile "Filters" button).
*/
export function activeFilterCount(query: CatalogueQuery): number {
  const priceRange = query.minPrice === null && query.maxPrice === null ? 0 : 1;
  return query.categories.length + query.sellers.length + priceRange + (query.inStock ? 1 : 0);
}

/**
The API request for this state. Sort is always explicit so the API's default cannot drift.
*/
export function toProductSearch(query: CatalogueQuery): ProductSearch {
  return {
    q: query.q || undefined,
    categories: query.categories,
    sellers: query.sellers,
    minPrice: query.minPrice,
    maxPrice: query.maxPrice,
    inStock: query.inStock,
    sort: query.sort,
    page: query.page,
    pageSize: PRODUCT_PAGE_SIZE,
  };
}

/**
Value equality, so re-navigating to the same URL does not refetch.
*/
export function sameQuery(a: CatalogueQuery, b: CatalogueQuery): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

/**
True when two states differ only in their page number.
*/
export function differsOnlyInPage(a: CatalogueQuery, b: CatalogueQuery): boolean {
  return a.page !== b.page && sameQuery({ ...a, page: 1 }, { ...b, page: 1 });
}

/**
Parses a typed or URL price; `null` for empty or malformed input.
*/
export function parsePrice(value: string | null | undefined): number | null {
  const trimmed = value?.trim().replace(/^\$/, '') ?? '';
  return PRICE_PATTERN.test(trimmed) ? Number(trimmed) : null;
}

/**
 * Page links to show: the first, the last and two either side of the current page, with `null`
 * where numbers are skipped: 1 … 4 5 [6] 7 8 … 20. A gap of a single page shows that page instead.
 */
export function pageWindow(current: number, total: number): (number | null)[] {
  const shown = new Set([1, total]);
  for (let page = current - 2; page <= current + 2; page++) {
    shown.add(page);
  }
  const sorted = [...shown].filter((page) => page >= 1 && page <= total).sort((a, b) => a - b);
  const pages: (number | null)[] = [];
  let previous = 0;
  for (const page of sorted) {
    if (page - previous === 2) {
      pages.push(previous + 1);
    } else if (page - previous > 2) {
      pages.push(null);
    }
    pages.push(page);
    previous = page;
  }
  return pages;
}

/**
Well-formed seller ids, de-duplicated and sorted, so one selection has one URL.
*/
function canonicalSellers(values: readonly string[]): ProductSource[] {
  return [...new Set(values.filter((value) => SELLER_PATTERN.test(value)))].sort();
}

/**
Known values only, de-duplicated, in the canonical order of `known`.
*/
function pickKnown<T extends string>(values: readonly string[], known: readonly T[]): T[] {
  const wanted = new Set(values);
  return known.filter((value) => wanted.has(value));
}

function isOneOf<T extends string>(value: string | null, known: readonly T[]): value is T {
  return value !== null && (known as readonly string[]).includes(value);
}
