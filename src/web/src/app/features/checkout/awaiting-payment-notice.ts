import { DatePipe } from '@angular/common';
import { Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { OrdersApi } from '../../core/api/orders.api';
import { SessionApi } from '../../core/api/session.api';
import { CheckoutStore } from '../../core/checkout/checkout-store';

/**
 * "Order LG-… is waiting for payment", with the way back to the payment step. Shown on the cart
 * and the review step while this checkout's placed order can still be paid: that order holds its
 * items, so they can read as out of stock there, and the shopper needs to know why and where to go.
 * Whether it can be paid is the API's answer (`canPay`, or a one-time code still awaited); the
 * notice stays hidden while the order is read, and if the read fails.
 */
@Component({
  selector: 'app-awaiting-payment-notice',
  imports: [DatePipe, RouterLink, MatButton, MatIcon],
  templateUrl: './awaiting-payment-notice.html',
  styleUrl: './awaiting-payment-notice.scss',
})
export class AwaitingPaymentNotice {
  private readonly store = inject(CheckoutStore);

  private readonly orders = inject(OrdersApi);

  private readonly sessionApi = inject(SessionApi);

  /**
  Signed out, the order can't be read (the checkout waits for the same customer to sign in again).
  */
  private readonly order = rxResource({
    params: () => (this.sessionApi.isAuthenticated() ? this.store.orderNumber() : null) ?? undefined,
    stream: ({ params }) => this.orders.get(params),
  });

  protected readonly awaiting = computed(() => {
    const order = this.order.hasValue() ? this.order.value() : undefined;
    return order && (order.canPay || order.awaitingOneTimeCode) ? order : null;
  });
}
