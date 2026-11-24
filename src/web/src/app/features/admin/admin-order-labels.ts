import { PartnerOrder, PaymentEventType } from '../../core/api/admin-orders.api';
import { StatusView, humanise } from '../orders/order-labels';

const PAYMENT_EVENTS: Readonly<Record<PaymentEventType, StatusView>> = {
  started: { tone: 'neutral', icon: 'lucideCreditCard', text: 'Payment started' },
  sent_to_gateway: { tone: 'info', icon: 'lucideClock', text: 'Sent to gateway' },
  one_time_code_requested: { tone: 'info', icon: 'lucideLock', text: 'One-time code requested' },
  approved: { tone: 'success', icon: 'lucideCircleCheck', text: 'Approved' },
  declined: { tone: 'error', icon: 'lucideCircleX', text: 'Declined' },
  gateway_error: { tone: 'error', icon: 'lucideCircleAlert', text: 'Gateway error' },
  failed: { tone: 'error', icon: 'lucideCircleAlert', text: 'Failed' },
  expired: { tone: 'warning', icon: 'lucideBan', text: 'Code expired' },
  refunded: { tone: 'neutral', icon: 'lucideUndo2', text: 'Refunded' },
  refund_failed: { tone: 'error', icon: 'lucideCircleAlert', text: 'Refund failed' },
};

export const paymentEventView = (type: PaymentEventType): StatusView =>
  PAYMENT_EVENTS[type] ?? { tone: 'neutral', icon: 'lucideInfo', text: humanise(type) };

const PARTNER_ORDERS: Readonly<Record<PartnerOrder['status'], StatusView>> = {
  pending: { tone: 'neutral', icon: 'lucideClock', text: 'Not sent yet' },
  submitted: { tone: 'info', icon: 'lucideClock', text: 'Sent, no answer yet' },
  accepted: { tone: 'info', icon: 'lucideClock', text: 'Accepted, awaiting decision' },
  confirmed: { tone: 'success', icon: 'lucideCircleCheck', text: 'Confirmed' },
  rejected: { tone: 'neutral', icon: 'lucideCircleX', text: 'Rejected' },
  given_up: { tone: 'warning', icon: 'lucideBan', text: 'Given up' },
  closed_by_admin: { tone: 'neutral', icon: 'lucideLock', text: 'Closed by an admin' },
};

/**
A partner order that needs an admin says so first, whatever its stored status.
*/
export const partnerOrderView = (order: PartnerOrder): StatusView => {
  if (order.needsAttention) {
    return {
      tone: 'warning',
      icon: 'lucideTriangleAlert',
      text: order.outcomeUnknown ? 'Needs attention: outcome unknown' : 'Needs attention',
    };
  }
  return (
    PARTNER_ORDERS[order.status] ?? {
      tone: 'neutral',
      icon: 'lucideInfo',
      text: humanise(order.status),
    }
  );
};
