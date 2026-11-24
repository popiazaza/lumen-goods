import {
  OrderLineStatus,
  OrderStatus,
  PaymentStatus,
  PaymentSummaryDto,
  RefundStatus,
  ShippingMethod,
  cardBrandName,
} from '../../core/api/order.models';
import { OrderIconName } from './order-icons';

// How statuses look. The words for order and line statuses come from the API (`statusText`); the
// client only picks a tone and an icon, so a badge never relies on colour alone (WCAG 1.4.1).

export type StatusTone = 'success' | 'info' | 'warning' | 'error' | 'neutral';

/**
What a status badge shows.
*/
export interface StatusView {
  tone: StatusTone;
  icon: OrderIconName;
  text: string;
}

const LINE_LOOKS: Readonly<Record<OrderLineStatus, Omit<StatusView, 'text'>>> = {
  pending: { tone: 'neutral', icon: 'lucideClock' },
  awaiting_partner: { tone: 'info', icon: 'lucideClock' },
  needs_attention: { tone: 'warning', icon: 'lucideTriangleAlert' },
  confirmed: { tone: 'success', icon: 'lucideCircleCheck' },
  rejected: { tone: 'neutral', icon: 'lucideCircleX' },
};

const ORDER_LOOKS: Readonly<Record<OrderStatus, Omit<StatusView, 'text'>>> = {
  awaiting_payment: { tone: 'warning', icon: 'lucideClock' },
  payment_processing: { tone: 'info', icon: 'lucideClock' },
  paid: { tone: 'info', icon: 'lucideClock' },
  completed: { tone: 'success', icon: 'lucideCircleCheck' },
  partially_refunded: { tone: 'neutral', icon: 'lucideUndo2' },
  refunded: { tone: 'neutral', icon: 'lucideUndo2' },
  expired: { tone: 'neutral', icon: 'lucideBan' },
};

const FALLBACK_LOOK: Omit<StatusView, 'text'> = { tone: 'neutral', icon: 'lucideInfo' };

/**
BR-ORD-02 badge for one line: "Awaiting Kestrel Outfitters", "Not supplied, refunded $40.03".
*/
export const lineStatusView = (status: OrderLineStatus, statusText: string): StatusView => ({
  ...(LINE_LOOKS[status] ?? FALLBACK_LOOK),
  text: statusText || humanise(status),
});

export const orderStatusView = (status: OrderStatus, statusText: string): StatusView => ({
  ...(ORDER_LOOKS[status] ?? FALLBACK_LOOK),
  text: statusText || humanise(status),
});

/**
Admin wording for the orders filter (the customer wording calls `paid` "Processing").
*/
export const ADMIN_ORDER_STATUS_LABELS: Readonly<Record<OrderStatus, string>> = {
  awaiting_payment: 'Awaiting payment',
  payment_processing: 'Payment in progress',
  paid: 'Paid, in progress',
  completed: 'Confirmed',
  partially_refunded: 'Partially refunded',
  refunded: 'Refunded',
  expired: 'Expired',
};

const SHIPPING_LABELS: Readonly<Record<ShippingMethod, string>> = {
  standard: 'Standard shipping',
  express: 'Express shipping',
};

export const shippingLabel = (method: ShippingMethod): string =>
  SHIPPING_LABELS[method] ?? humanise(method);

/**
"Visa ending 1111" or "Lumen wallet".
*/
export const paymentMethodLabel = (payment: PaymentSummaryDto): string => {
  if (payment.method === 'wallet') {
    return 'Lumen wallet';
  }
  const brand = cardBrandName(payment.cardBrand);
  return payment.cardLast4 ? `${brand} ending ${payment.cardLast4}` : brand;
};

const BEING_CONFIRMED: StatusView = { tone: 'info', icon: 'lucideClock', text: 'Being confirmed' };

const PAYMENT_STATUS_LOOKS: Readonly<Record<PaymentStatus, StatusView>> = {
  created: BEING_CONFIRMED,
  processing: BEING_CONFIRMED,
  requires_action: { tone: 'warning', icon: 'lucideLock', text: 'Waiting for a one-time code' },
  succeeded: { tone: 'success', icon: 'lucideCircleCheck', text: 'Paid' },
  declined: { tone: 'error', icon: 'lucideCircleX', text: 'Declined' },
  error: BEING_CONFIRMED,
  failed: { tone: 'error', icon: 'lucideCircleAlert', text: 'Failed' },
  expired: { tone: 'neutral', icon: 'lucideBan', text: 'Expired' },
};

export const paymentStatusView = (status: PaymentStatus): StatusView =>
  PAYMENT_STATUS_LOOKS[status] ?? { ...FALLBACK_LOOK, text: humanise(status) };

const REFUND_LOOKS: Readonly<Record<RefundStatus, StatusView>> = {
  pending: { tone: 'info', icon: 'lucideClock', text: 'Pending' },
  succeeded: { tone: 'success', icon: 'lucideCircleCheck', text: 'Refunded' },
  failed: { tone: 'error', icon: 'lucideCircleAlert', text: 'Failed, will retry' },
};

/**
 * The admin sees a failed refund as failed. The customer sees it as pending, like the API's line
 * wording ("Not supplied, refund pending"): it is retried until it is paid back, and nothing is
 * theirs to do.
 */
export const refundStatusView = (status: RefundStatus, isForAdmin = false): StatusView => {
  const shown = !isForAdmin && status === 'failed' ? 'pending' : status;
  return REFUND_LOOKS[shown] ?? { ...FALLBACK_LOOK, text: humanise(status) };
};

/**
`needs_attention` -> "Needs attention": a readable fallback for a value this client does not know.
*/
export const humanise = (value: string): string => {
  const text = value.replaceAll('_', ' ').trim();
  return text.charAt(0).toUpperCase() + text.slice(1);
};
