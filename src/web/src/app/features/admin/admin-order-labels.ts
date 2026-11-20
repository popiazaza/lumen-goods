import { PartnerOrder, PaymentEventType } from '../../core/api/admin-orders.api';
import { StatusView, humanise } from '../orders/order-labels';

const PAYMENT_EVENTS: Readonly<Record<PaymentEventType, StatusView>> = {
  started: { tone: 'neutral', icon: 'credit_card', text: 'Payment started' },
  sent_to_gateway: { tone: 'info', icon: 'schedule', text: 'Sent to gateway' },
  one_time_code_requested: { tone: 'info', icon: 'lock', text: 'One-time code requested' },
  approved: { tone: 'success', icon: 'check_circle', text: 'Approved' },
  declined: { tone: 'error', icon: 'cancel', text: 'Declined' },
  gateway_error: { tone: 'error', icon: 'error', text: 'Gateway error' },
  failed: { tone: 'error', icon: 'error', text: 'Failed' },
  expired: { tone: 'warning', icon: 'block', text: 'Code expired' },
  refunded: { tone: 'neutral', icon: 'undo', text: 'Refunded' },
  refund_failed: { tone: 'error', icon: 'error', text: 'Refund failed' },
};

export const paymentEventView = (type: PaymentEventType): StatusView =>
  PAYMENT_EVENTS[type] ?? { tone: 'neutral', icon: 'info', text: humanise(type) };

const PARTNER_ORDERS: Readonly<Record<PartnerOrder['status'], StatusView>> = {
  pending: { tone: 'neutral', icon: 'schedule', text: 'Not sent yet' },
  submitted: { tone: 'info', icon: 'schedule', text: 'Sent, no answer yet' },
  accepted: { tone: 'info', icon: 'schedule', text: 'Accepted, awaiting decision' },
  confirmed: { tone: 'success', icon: 'check_circle', text: 'Confirmed' },
  rejected: { tone: 'neutral', icon: 'cancel', text: 'Rejected' },
  given_up: { tone: 'warning', icon: 'block', text: 'Given up' },
  closed_by_admin: { tone: 'neutral', icon: 'lock', text: 'Closed by an admin' },
};

/**
A partner order that needs an admin says so first, whatever its stored status.
*/
export const partnerOrderView = (order: PartnerOrder): StatusView => {
  if (order.needsAttention) {
    return {
      tone: 'warning',
      icon: 'warning',
      text: order.outcomeUnknown ? 'Needs attention: outcome unknown' : 'Needs attention',
    };
  }
  return (
    PARTNER_ORDERS[order.status] ?? { tone: 'neutral', icon: 'info', text: humanise(order.status) }
  );
};
