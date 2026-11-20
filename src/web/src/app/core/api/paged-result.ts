import { HttpParams } from '@angular/common/http';

/**
One page of a list (`PagedResult<T>` on the API). `page` is 1-based; past the end has no items.
*/
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

type QueryValue = string | number | null | undefined;

/**
 * `GET` query values; empty strings, `null` and `undefined` are left out so the API uses its
 * default. An array repeats its key: `status=paid&status=refunded`.
 */
export type QueryValues = Readonly<Record<string, QueryValue | readonly QueryValue[]>>;

export function toQueryParams(values: QueryValues): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(values)) {
    for (const item of Array.isArray(value) ? value : [value]) {
      const text = typeof item === 'string' ? item.trim() : item;
      if (text !== '' && text !== null && text !== undefined) {
        params = params.append(key, String(text));
      }
    }
  }
  return params;
}
