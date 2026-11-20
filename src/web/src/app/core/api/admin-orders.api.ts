import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from './api-base-url';
import { OrderDetail, OrderStatus, PaymentMethod } from './orders.api';
import { PagedResult, toQueryParams } from './paged-result';

// Contract: Lumen.Api/Orders/AdminOrderContracts.cs (admin role; 401 signed out, 403 without it).

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
}

/** `GET /admin/orders/{number}`: the customer's view plus what only the admin sees. */
export interface AdminOrder {
  order: OrderDetail;
  customerEmail: string;
  failedPaymentAttempts: number;
  paymentEvents: PaymentEvent[];
  partnerOrders: PartnerOrder[];
}

/** An admin's answer for a line that needs attention (BR-ORD-04): it is coming, or it is refunded. */
export type LineDecision = 'confirm' | 'reject';

export interface AdminOrderFilter {
  /** Any of these; none means every status. */
  statuses?: readonly OrderStatus[];
  /** `YYYY-MM-DD`, when the order was placed. */
  from?: string | null;
  to?: string | null;
  page?: number;
  pageSize?: number;
}

/** `/api/v1/admin/orders`. */
@Service()
export class AdminOrdersApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);

  /** Newest first. An invalid filter answers 400 with ProblemDetails. */
  list(filter: AdminOrderFilter): Observable<PagedResult<AdminOrderSummary>> {
    return this.http.get<PagedResult<AdminOrderSummary>>(`${this.baseUrl}/admin/orders`, {
      params: toQueryParams({
        status: filter.statuses ?? [],
        from: filter.from,
        to: filter.to,
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
   * Answers with the updated order; 409 when the line no longer needs attention.
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
