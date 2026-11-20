import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from './api-base-url';
import { CardBrand, OrderStatus, PaymentMethod, PaymentStatus } from './order.models';
import { httpErrorOf } from './problem-details';

/** The card form as the API takes it. Never stored or logged in the client either. */
export interface CardDetails {
  number: string;
  name: string;
  expiryMonth: number;
  /** Two digits (28) or four (2028). */
  expiryYear: number;
  cvc: string;
}

/** One method per order (BR-PAY-06); the amount is always the order total, never sent. */
export type PayRequest = { method: 'card'; card: CardDetails } | { method: 'wallet'; card: null };

/**
 * What the payment page may offer next: a new attempt with either method; only a Try again of the
 * same card payment (the gateway could not say whether a failed payment charged, BR-PAY-05); or
 * nothing.
 */
export type PaymentRetryMode = 'any_method' | 'same_payment_only' | 'none';

/** The order's current payment, as the payment page shows and polls it. */
export interface PaymentStatusDto {
  paymentId: string;
  orderNumber: string;
  method: PaymentMethod;
  status: PaymentStatus;
  amount: number;
  cardBrand: CardBrand | null;
  cardLast4: string | null;
  /** Show the code field (requires_action and the code has not expired). */
  needsOneTimeCode: boolean;
  /** Server time the code expires, for the countdown. */
  oneTimeCodeExpiresUtc: string | null;
  /** The mock's code, shown because no SMS is sent. */
  testOneTimeCode: string | null;
  /** Customer wording (BR-PAY-07): "Insufficient funds", or "Your card was declined". */
  declineMessage: string | null;
  attemptsLeft: number;
  /** Shorthand for `retryMode !== 'none'`. */
  canRetry: boolean;
  retryMode: PaymentRetryMode;
  orderStatus: OrderStatus;
  holdExpiresUtc: string;
  /** The server clock when this was produced: count down from it, not from the browser's clock. */
  serverTimeUtc: string;
}

/** 409 from Pay or the code step: a payment is already running (BR-PAY-01); the body carries it. */
export const PAYMENT_IN_PROGRESS_PROBLEM = 'urn:lumen:problem:payment-in-progress';

export interface PaymentInProgressProblem {
  type: typeof PAYMENT_IN_PROGRESS_PROBLEM;
  title: string;
  detail: string;
  /** The running payment, to show instead of an error. */
  payment: PaymentStatusDto;
}

/** The running payment behind a 409 payment-in-progress, if that is what the error was. */
export function paymentInProgressOf(error: unknown): PaymentInProgressProblem | undefined {
  const body = httpErrorOf(error)?.error as Partial<PaymentInProgressProblem> | null | undefined;
  return body?.type === PAYMENT_IN_PROGRESS_PROBLEM && body.payment
    ? (body as PaymentInProgressProblem)
    : undefined;
}
/** 409 from Pay: already paid, expired, or out of attempts. */
export const ORDER_NOT_PAYABLE_PROBLEM = 'urn:lumen:problem:order-not-payable';

/** The problem `type` of a failed payment call, if the API sent one. */
export function paymentProblemTypeOf(error: unknown): string | undefined {
  const body: unknown = httpErrorOf(error)?.error;
  return body && typeof body === 'object' && 'type' in body && typeof body.type === 'string'
    ? body.type
    : undefined;
}

/**
 * Payments for one order (signed-in customer, own orders only):
 * `POST /orders/{number}/payments`, `GET /orders/{number}/payments/current` (404 before any
 * payment) and `POST /orders/{number}/payments/current/one-time-code`.
 */
@Service()
export class PaymentsApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);

  /** 202 with the payment's status: the charge runs on the server, independent of this request. */
  pay(orderNumber: string, request: PayRequest): Observable<PaymentStatusDto> {
    return this.http.post<PaymentStatusDto>(`${this.paymentsUrl(orderNumber)}`, request);
  }

  current(orderNumber: string): Observable<PaymentStatusDto> {
    return this.http.get<PaymentStatusDto>(`${this.paymentsUrl(orderNumber)}/current`);
  }

  submitOneTimeCode(orderNumber: string, code: string): Observable<PaymentStatusDto> {
    return this.http.post<PaymentStatusDto>(
      `${this.paymentsUrl(orderNumber)}/current/one-time-code`,
      { code },
    );
  }

  private paymentsUrl(orderNumber: string): string {
    return `${this.baseUrl}/orders/${encodeURIComponent(orderNumber)}/payments`;
  }
}
