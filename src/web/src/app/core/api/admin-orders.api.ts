import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from './api-base-url';
import { ORDER_STATUSES, OrderDetailDto, OrderStatus, PaymentMethod } from './order.models';
import { PagedResult, toQueryParams } from './paged-result';

// Contract: Lumen.Api/Orders/AdminOrderContracts.cs and AdminOrderEndpoints.cs (admin role; 401
// signed out, 403 without it).

/** 409 from resolve: the line was decided meanwhile, or never needed a decision. Reload. */
export const LINE_NOT_AWAITING_DECISION_PROBLEM = 'urn:lumen:problem:line-not-awaiting-decision';

/**
 * 409 from resolve: a send to (or poll of) the partner for this line is on its way, and a
 * decision now could cross its answer (BR-ORD-04). Nothing changed; try again in a minute.
 */
export const PARTNER_CALL_IN_PROGRESS_PROBLEM = 'urn:lumen:problem:partner-call-in-progress';

/** The list's `status` filter: an order status, or orders with a line waiting for a decision. */
export type AdminOrderStatusFilter = OrderStatus | 'needs_attention';

export const ADMIN_ORDER_STATUS_FILTERS: readonly AdminOrderStatusFilter[] = [
  ...ORDER_STATUSES,
  'needs_attention',
];

/** One row of the admin orders list, newest first. */
export interface AdminOrderSummary {
  number: string;
  status: OrderStatus;
  statusText: string;
  createdUtc: string;
  customerEmail: string;
  total: number;
  refundedTotal: number;
  sellerNames: string[];
  /** The latest payment's method; `null` before any payment. */
  paymentMethod: PaymentMethod | null;
  /** A line waits for an admin decision (BR-ORD-04). */
  needsAttention: boolean;
}

export type PaymentEventType =
  | 'started'
  | 'sent_to_gateway'
  | 'one_time_code_requested'
  | 'approved'
  | 'declined'
  | 'gateway_error'
  | 'failed'
  | 'expired'
  | 'refunded'
  | 'refund_failed';

/** One entry of the payment timeline, oldest first. */
export interface PaymentEvent {
  occurredUtc: string;
  paymentId: string;
  type: PaymentEventType;
  amount: number | null;
  /** Admin wording: the real decline reason ("Card reported stolen"), the gateway error, which retry. */
  detail: string | null;
}

export type PartnerOrderStatus =
  'pending' | 'submitted' | 'accepted' | 'confirmed' | 'rejected' | 'given_up' | 'closed_by_admin';

/** The order Lumen placed with a partner for one line (BR-ORD-01, BR-ORD-04). */
export interface PartnerOrder {
  id: string;
  orderLineId: string;
  lineNumber: number;
  source: string;
  sellerName: string;
  /** Lumen's reference, reused on every resend. */
  externalRef: string;
  /** The partner's order id or ticket, once it has one. */
  partnerOrderId: string | null;
  status: PartnerOrderStatus;
  reason: string | null;
  attempts: number;
  nextAttemptUtc: string | null;
  lastError: string | null;
  submittedUtc: string | null;
  acceptedUtc: string | null;
  decidedUtc: string | null;
  needsAttention: boolean;
  /**
   * A send may have reached the partner without Lumen learning the result (BR-ORD-04): the partner
   * may hold the order, so it is not refunded on its own. Check with the partner before rejecting.
   */
  outcomeUnknown: boolean;
}

/** `GET /admin/orders/{number}`: the customer's view plus what only the admin sees. */
export interface AdminOrder {
  order: OrderDetailDto;
  customerEmail: string;
  failedPaymentAttempts: number;
  paymentEvents: PaymentEvent[];
  partnerOrders: PartnerOrder[];
}

/** An admin's answer for a line that needs attention (BR-ORD-04): it is coming, or it is refunded. */
export type LineDecision = 'confirm' | 'reject';

export interface AdminOrderFilter {
  /** Any of these; none means every status. */
  statuses?: readonly AdminOrderStatusFilter[];
  /** `YYYY-MM-DD` in the admin's time zone: placed on or after this day. */
  from?: string | null;
  /** `YYYY-MM-DD` in the admin's time zone: placed on or before this day. */
  to?: string | null;
  page?: number;
  pageSize?: number;
}

/** `/api/v1/admin/orders`. */
@Service()
export class AdminOrdersApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);

  /**
   * Newest first. An invalid filter answers 400 with ProblemDetails.
   *
   * The API reads a bare `YYYY-MM-DD` as a UTC day, but the page shows when orders were placed in
   * the admin's own time; so the days go out as the instants of local midnight (`from` inclusive,
   * `to` the next midnight, exclusive), which the API also accepts.
   */
  list(filter: AdminOrderFilter): Observable<PagedResult<AdminOrderSummary>> {
    return this.http.get<PagedResult<AdminOrderSummary>>(`${this.baseUrl}/admin/orders`, {
      params: toQueryParams({
        status: filter.statuses ?? [],
        from: localMidnightUtc(filter.from, 0),
        to: localMidnightUtc(filter.to, 1),
        page: filter.page,
        pageSize: filter.pageSize,
      }),
    });
  }

  /** 404 for an unknown number. */
  get(number: string): Observable<AdminOrder> {
    return this.http.get<AdminOrder>(`${this.baseUrl}/admin/orders/${encodeURIComponent(number)}`);
  }

  /**
   * Confirms or rejects a line that needs attention; a rejected line is refunded (BR-ORD-03).
   * Answers with the updated order. 409 `LINE_NOT_AWAITING_DECISION_PROBLEM` when the line no
   * longer needs a decision, `PARTNER_CALL_IN_PROGRESS_PROBLEM` while a partner call for it is on
   * its way; 400 for a note over 500 characters.
   */
  resolveLine(
    number: string,
    lineId: string,
    decision: LineDecision,
    note: string | null,
  ): Observable<AdminOrder> {
    return this.http.post<AdminOrder>(
      `${this.baseUrl}/admin/orders/${encodeURIComponent(number)}/lines/${encodeURIComponent(lineId)}/resolve`,
      { decision, note: note?.trim() || null },
    );
  }
}

/**
 * `YYYY-MM-DD` plus `addDays`, at local midnight, as a UTC ISO instant ("2026-11-19T17:00:00.000Z"
 * for 2026-11-20 in UTC+7); `null` for no day.
 */
function localMidnightUtc(day: string | null | undefined, addDays: number): string | null {
  const parts = day?.match(/^(\d{4})-(\d{2})-(\d{2})$/);
  if (!parts) {
    return null;
  }
  const [, year, month, date] = parts.map(Number);
  return new Date(year, month - 1, date + addDays).toISOString();
}
