import {
  OrderLineStatus,
  OrderStatus,
  PaymentStatus,
  PaymentSummaryDto,
  RefundStatus,
  ShippingMethod,
  cardBrandName,
} from '../../core/api/order.models';

// How statuses look. The words for order and line statuses come from the API (`statusText`); the
// client only picks a tone and an icon, so a badge never relies on colour alone (WCAG 1.4.1).

export type StatusTone = 'success' | 'info' | 'warning' | 'error' | 'neutral';

/** What a status badge shows. */
export interface StatusView {
  tone: StatusTone;
  icon: string;
  text: string;
}

const LINE_LOOKS: Readonly<Record<OrderLineStatus, Omit<StatusView, 'text'>>> = {
  pending: { tone: 'neutral', icon: 'schedule' },
  awaiting_partner: { tone: 'info', icon: 'schedule' },
  needs_attention: { tone: 'warning', icon: 'warning' },
  confirmed: { tone: 'success', icon: 'check_circle' },
  rejected: { tone: 'neutral', icon: 'cancel' },
};

const ORDER_LOOKS: Readonly<Record<OrderStatus, Omit<StatusView, 'text'>>> = {
  awaiting_payment: { tone: 'warning', icon: 'schedule' },
  payment_processing: { tone: 'info', icon: 'schedule' },
  paid: { tone: 'info', icon: 'schedule' },
  completed: { tone: 'success', icon: 'check_circle' },
  partially_refunded: { tone: 'neutral', icon: 'undo' },
  refunded: { tone: 'neutral', icon: 'undo' },
  expired: { tone: 'neutral', icon: 'block' },
};

const FALLBACK_LOOK: Omit<StatusView, 'text'> = { tone: 'neutral', icon: 'info' };

/** BR-ORD-02 badge for one line: "Awaiting Kestrel Outfitters", "Not supplied, refunded $40.03". */
export function lineStatusView(status: OrderLineStatus, statusText: string): StatusView {
  return { ...(LINE_LOOKS[status] ?? FALLBACK_LOOK), text: statusText || humanise(status) };
}

export function orderStatusView(status: OrderStatus, statusText: string): StatusView {
  return { ...(ORDER_LOOKS[status] ?? FALLBACK_LOOK), text: statusText || humanise(status) };
}

/** Admin wording for the orders filter (the customer wording calls `paid` "Processing"). */
export const ADMIN_ORDER_STATUS_LABELS: Readonly<Record<OrderStatus, string>> = {
  awaiting_payment: 'Awaiting payment',
  payment_processing: 'Payment in progress',
  paid: 'Paid, in progress',
  completed: 'Completed',
  partially_refunded: 'Partially refunded',
  refunded: 'Refunded',
  expired: 'Expired',
};

const SHIPPING_LABELS: Readonly<Record<ShippingMethod, string>> = {
  standard: 'Standard shipping',
  express: 'Express shipping',
};

export function shippingLabel(method: ShippingMethod): string {
  return SHIPPING_LABELS[method] ?? humanise(method);
}

/** "Visa ending 1111" or "Lumen wallet". */
export function paymentMethodLabel(payment: PaymentSummaryDto): string {
  if (payment.method === 'wallet') {
    return 'Lumen wallet';
  }
  const brand = cardBrandName(payment.cardBrand);
  return payment.cardLast4 ? `${brand} ending ${payment.cardLast4}` : brand;
}

const PAYMENT_STATUS_LOOKS: Readonly<Record<PaymentStatus, StatusView>> = {
  created: { tone: 'info', icon: 'schedule', text: 'Being confirmed' },
  processing: { tone: 'info', icon: 'schedule', text: 'Being confirmed' },
  requires_action: { tone: 'warning', icon: 'lock', text: 'Waiting for a one-time code' },
  succeeded: { tone: 'success', icon: 'check_circle', text: 'Paid' },
  declined: { tone: 'error', icon: 'cancel', text: 'Declined' },
  error: { tone: 'info', icon: 'schedule', text: 'Being confirmed' },
  failed: { tone: 'error', icon: 'error', text: 'Failed' },
  expired: { tone: 'neutral', icon: 'block', text: 'Expired' },
};

export function paymentStatusView(status: PaymentStatus): StatusView {
  return PAYMENT_STATUS_LOOKS[status] ?? { ...FALLBACK_LOOK, text: humanise(status) };
}

const REFUND_LOOKS: Readonly<Record<RefundStatus, StatusView>> = {
  pending: { tone: 'info', icon: 'schedule', text: 'Pending' },
  succeeded: { tone: 'success', icon: 'check_circle', text: 'Refunded' },
  failed: { tone: 'error', icon: 'error', text: 'Failed, will retry' },
};

export function refundStatusView(status: RefundStatus): StatusView {
  return REFUND_LOOKS[status] ?? { ...FALLBACK_LOOK, text: humanise(status) };
}

/** `needs_attention` -> "Needs attention": a readable fallback for a value this client does not know. */
export function humanise(value: string): string {
  const text = value.replace(/_/g, ' ').trim();
  return text.charAt(0).toUpperCase() + text.slice(1);
}
