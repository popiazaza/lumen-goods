import { ParamMap, Params } from '@angular/router';
import {
  ADMIN_ORDER_STATUS_FILTERS,
  AdminOrderStatusFilter,
} from '../../core/api/admin-orders.api';
import { ProductStatusFilter } from '../../core/api/admin-products.api';
import { PRODUCT_CATEGORIES, ProductCategory } from '../../core/api/products.api';

// The admin lists keep their filters and page in the URL, like the catalogue: the page parses the query string into one of these, controls navigate with a changed copy, and Back/Forward and shared links reproduce the view. Defaults are left out of the URL so each view has one link.

export const ADMIN_PAGE_SIZES = [24, 48, 96] as const;
export const ADMIN_DEFAULT_PAGE_SIZE = 24;

export interface AdminOrdersQuery {
  /**
  Any of these (`status` repeated in the URL); empty means every status.
  */
  statuses: AdminOrderStatusFilter[];
  /**
  `YYYY-MM-DD` in the admin's time zone: placed on or after this day.
  */
  from: string | null;
  /**
  `YYYY-MM-DD` in the admin's time zone: placed on or before this day.
  */
  to: string | null;
  page: number;
  pageSize: number;
}

export interface AdminProductsQuery {
  q: string;
  status: ProductStatusFilter;
  category: ProductCategory | null;
  page: number;
  pageSize: number;
}

export const parseAdminOrdersQuery = (params: ParamMap): AdminOrdersQuery => ({
  statuses: pickMany(params.getAll('status'), ADMIN_ORDER_STATUS_FILTERS),
  from: parseDay(params.get('from')),
  to: parseDay(params.get('to')),
  ...parsePaging(params),
});

export const adminOrdersQueryParams = (query: AdminOrdersQuery): Params => ({
  status: query.statuses.length > 0 ? query.statuses : null,
  from: query.from,
  to: query.to,
  ...pagingParams(query),
});

export const hasOrderFilters = (query: AdminOrdersQuery): boolean =>
  query.statuses.length > 0 || query.from !== null || query.to !== null;

const PRODUCT_STATUSES: readonly ProductStatusFilter[] = ['all', 'active', 'inactive'];

export const parseAdminProductsQuery = (params: ParamMap): AdminProductsQuery => ({
  q: (params.get('q') ?? '').trim(),
  status: pickOne(params.get('status'), PRODUCT_STATUSES) ?? 'all',
  category: pickOne(params.get('category'), PRODUCT_CATEGORIES),
  ...parsePaging(params),
});

export const adminProductsQueryParams = (query: AdminProductsQuery): Params => ({
  q: query.q || null,
  status: query.status === 'all' ? null : query.status,
  category: query.category,
  ...pagingParams(query),
});

export const hasProductFilters = (query: AdminProductsQuery): boolean =>
  query.q !== '' || query.status !== 'all' || query.category !== null;

/**
A filter change starts again at page 1; a page change keeps the filters.
*/
export const withFilters = <T extends { page: number }>(query: T, change: Partial<T>): T => ({
  ...query,
  ...change,
  page: 1,
});

/**
Known values only, de-duplicated, in the order of `allowed`, so one selection has one URL.
*/
const pickMany = <T extends string>(values: readonly string[], allowed: readonly T[]): T[] =>
  allowed.filter((candidate) => values.includes(candidate));

const pickOne = <T extends string>(value: string | null, allowed: readonly T[]): T | null =>
  allowed.find((candidate) => candidate === value) ?? null;

/**
A real calendar day as `YYYY-MM-DD`, else `null`.
*/
const parseDay = (value: string | null): string | null => {
  if (!value || !/^\d{4}-\d{2}-\d{2}$/u.test(value)) {
    return null;
  }
  const date = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(date.getTime()) && date.toISOString().startsWith(value) ? value : null;
};

const parsePaging = (params: ParamMap): { page: number; pageSize: number } => {
  const page = Number(params.get('page'));
  const pageSize = Number(params.get('pageSize'));
  return {
    page: Number.isSafeInteger(page) && page >= 1 ? page : 1,
    pageSize: (ADMIN_PAGE_SIZES as readonly number[]).includes(pageSize)
      ? pageSize
      : ADMIN_DEFAULT_PAGE_SIZE,
  };
};

const pagingParams = (query: { page: number; pageSize: number }): Params => ({
  page: query.page > 1 ? query.page : null,
  pageSize: query.pageSize === ADMIN_DEFAULT_PAGE_SIZE ? null : query.pageSize,
});
