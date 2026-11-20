import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { MatCard } from '@angular/material/card';
import { MatIcon } from '@angular/material/icon';
import { OrderDetailDto, OrderLineDto, RefundDto } from '../../core/api/order.models';
import { paymentMethodLabel, paymentStatusView, refundStatusView } from './order-labels';
import { StatusBadge } from './status-badge';

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

  protected readonly methodLabel = computed(() => {
    const { payment } = this.order();
    return payment ? paymentMethodLabel(payment) : '';
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
