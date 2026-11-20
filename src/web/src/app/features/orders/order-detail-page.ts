import { LiveAnnouncer } from '@angular/cdk/a11y';
import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatCard } from '@angular/material/card';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { OrderDetailDto, OrderLineDto } from '../../core/api/order.models';
import { OrdersApi } from '../../core/api/orders.api';
import { httpStatusOf } from '../../core/api/problem-details';
import { ProductImage } from '../../shared/products/product-image';
import { registerOrderIcons } from './order-icons';
import {
  StatusView,
  lineStatusView,
  orderStatusView,
  paymentMethodLabel,
  shippingLabel,
} from './order-labels';
import { OrderSummaryPanel } from './order-summary-panel';
import { hasRefundOnItsWay, orderKeepsChanging, pollWhileUpdating } from './poll-while-updating';
import { StatusBadge } from './status-badge';

/** How often the page re-reads an order that is still updating. */
export const ORDER_POLL_INTERVAL_MS = 5000;

/**
 * One of the customer's orders (`/orders/:id`, the order number): every line with its seller and
 * its own status (BR-ORD-02), shipments, refunds (BR-ORD-03), totals and payment. Re-reads the
 * order every few seconds while something can still change on its own (a payment, a partner, a
 * refund on its way back), then stops.
 */
@Component({
  selector: 'app-order-detail-page',
  imports: [
    CurrencyPipe,
    DatePipe,
    RouterLink,
    MatButton,
    MatCard,
    MatIcon,
    MatProgressBar,
    OrderSummaryPanel,
    ProductImage,
    StatusBadge,
  ],
  templateUrl: './order-detail-page.html',
  styleUrl: './order-detail-page.scss',
})
export class OrderDetailPage {
  /** The order number, bound from the `:id` route parameter. */
  readonly id = input.required<string>();

  private readonly api = inject(OrdersApi);
  private readonly announcer = inject(LiveAnnouncer);

  protected readonly order = rxResource({
    params: () => this.id(),
    stream: ({ params: number }) =>
      pollWhileUpdating(() => this.api.get(number), ORDER_POLL_INTERVAL_MS, orderKeepsChanging),
  });
  protected readonly detail = computed(() =>
    this.order.hasValue() ? this.order.value() : undefined,
  );
  protected readonly errorStatus = computed(() =>
    this.order.status() === 'error' ? (httpStatusOf(this.order.error()) ?? -1) : undefined,
  );

  protected readonly lines = computed(() =>
    (this.detail()?.shipments ?? []).flatMap((shipment) => shipment.lines),
  );
  protected readonly rejectedCount = computed(
    () => this.lines().filter((line) => line.status === 'rejected').length,
  );

  /** What the page is waiting for while it refreshes itself. */
  protected readonly waitingText = computed(() => {
    const order = this.detail();
    if (!order || !orderKeepsChanging(order)) {
      return '';
    }
    const reasons: string[] = [];
    if (order.status === 'payment_processing') {
      reasons.push('Waiting for your payment to be confirmed.');
    }
    const shops = order.shipments
      .filter((shipment) => shipment.lines.some((line) => line.status === 'awaiting_partner'))
      .map((shipment) => shipment.sellerName);
    if (shops.length > 0) {
      reasons.push(`Waiting for ${joinNames(shops)} to confirm.`);
    }
    if (hasRefundOnItsWay(order)) {
      reasons.push(
        order.payment
          ? `Your refund is on its way back to your ${paymentMethodLabel(order.payment)}.`
          : 'Your refund is on its way back.',
      );
    }
    if (reasons.length === 0) {
      reasons.push('Your order is being updated.');
    }
    return `${reasons.join(' ')} This page updates by itself.`;
  });

  protected readonly shippingLabel = shippingLabel;

  constructor() {
    registerOrderIcons();
    this.announceLineChanges();
  }

  protected orderStatus(order: OrderDetailDto): StatusView {
    return orderStatusView(order.status, order.statusText);
  }

  protected lineStatus(line: OrderLineDto): StatusView {
    return lineStatusView(line.status, line.statusText);
  }

  /** A refresh that changes a line's status is read out, e.g. "Merino Trail Socks: Confirmed". */
  private announceLineChanges(): void {
    let previous: ReadonlyMap<string, string> | undefined;
    effect(() => {
      const lines = this.lines();
      if (lines.length === 0) {
        return;
      }
      const before = previous;
      previous = new Map(lines.map((line) => [line.id, line.statusText]));
      const changed = before
        ? lines.filter((line) => before.has(line.id) && before.get(line.id) !== line.statusText)
        : [];
      if (changed.length > 0) {
        void this.announcer.announce(
          changed.map((line) => `${line.name}: ${line.statusText}`).join('. '),
          'polite',
        );
      }
    });
  }
}

/** "A", "A and B", "A, B and C". */
export function joinNames(names: readonly string[]): string {
  if (names.length <= 1) {
    return names[0] ?? '';
  }
  return `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`;
}
