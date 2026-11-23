import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { MatCard } from '@angular/material/card';
import { MatIcon } from '@angular/material/icon';
import { OrderDetailDto, OrderLineDto, OrderStatus, RefundDto } from '../../core/api/order.models';
import { paymentMethodLabel, paymentStatusView, refundStatusView } from './order-labels';
import { StatusBadge } from './status-badge';

/**
Statuses in which nothing has been charged (yet), as on the confirmation page.
*/
const NOT_CHARGED: ReadonlySet<OrderStatus> = /* @__PURE__ */ new Set([
  'awaiting_payment',
  'payment_processing',
  'expired',
]);

/**
 * Totals (with refunds and net paid), payment, refunds and the shipping address of an order, as
 * the API computed them. Shared by the customer's and the admin's order pages.
 */
@Component({
  selector: 'app-order-summary-panel',
  imports: [CurrencyPipe, DatePipe, MatCard, MatIcon, StatusBadge],
  templateUrl: './order-summary-panel.html',
  styleUrl: './order-summary-panel.scss',
})
export class OrderSummaryPanel {
  private readonly linesById = computed(
    () =>
      new Map(
        this.order()
          .shipments.flatMap((shipment) => shipment.lines)
          .map((line) => [line.id, line])
      )
  );

  readonly order = input.required<OrderDetailDto>();

  /**
  The admin order page: refunds show their real status (a failed one says so).
  */
  readonly admin = input(false);

  /**
  The total says "charged" only once the order is paid.
  */
  protected readonly isPaid = computed(() => !NOT_CHARGED.has(this.order().status));

  protected readonly methodLabel = computed(() => {
    const { payment } = this.order();
    return payment ? paymentMethodLabel(payment) : '';
  });

  /**
   * `totals.refunded` counts every refund recorded, so it says which of them are still on their
   * way back (pending, or failed and being retried): the refunds' own statuses, from the API.
   */
  protected readonly refundedLabel = computed(() => {
    const { refunds } = this.order();
    const paidBack = refunds.filter((refund) => refund.status === 'succeeded').length;
    if (paidBack === refunds.length) {
      return 'Refunded';
    }
    // Admins see a failed refund as failed, as in the refunds list below; customers see it pending.
    if (this.admin() && refunds.some((refund) => refund.status === 'failed')) {
      return paidBack === 0 ? 'Refund failed' : 'Refunded (some failed)';
    }
    return paidBack === 0 ? 'Refund pending' : 'Refunded (some pending)';
  });

  protected readonly paymentStatus = paymentStatusView;

  protected readonly refundStatus = refundStatusView;

  /**
  The lines a refund pays back, each with the amount the API refunded for it.
  */
  protected refundedLines(refund: RefundDto): OrderLineDto[] {
    const lines = this.linesById();
    return refund.lineIds.flatMap((id) => lines.get(id) ?? []);
  }
}
