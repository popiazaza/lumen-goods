import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from './api-base-url';
import { OrderDetailDto, OrderSummaryDto } from './order.models';
import { PagedResult, toQueryParams } from './paged-result';

// Contract: Lumen.Api/Orders/OrderEndpoints.cs; the types are in `order.models.ts`.

/** Orders per history page. */
export const ORDER_PAGE_SIZE = 10;

/**
 * `GET /api/v1/orders` and `GET /api/v1/orders/{number}`: the signed-in customer's own orders.
 * Checkout's payment step and the confirmation read the order here too.
 */
@Service()
export class OrdersApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);

  /** Newest first. 401 when signed out. */
  list(page: number, pageSize = ORDER_PAGE_SIZE): Observable<PagedResult<OrderSummaryDto>> {
    return this.http.get<PagedResult<OrderSummaryDto>>(`${this.baseUrl}/orders`, {
      params: toQueryParams({ page, pageSize }),
    });
  }

  /** 404 for an unknown number and for another customer's order; 401 when signed out. */
  get(number: string): Observable<OrderDetailDto> {
    return this.http.get<OrderDetailDto>(`${this.baseUrl}/orders/${encodeURIComponent(number)}`);
  }
}
