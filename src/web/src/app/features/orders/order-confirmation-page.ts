import { CurrencyPipe } from '@angular/common';
import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { Subscription, catchError, map, of, repeat, takeWhile } from 'rxjs';
import { OrdersApi } from '../../core/api/orders.api';
import {
  OWN_SOURCE,
  OrderDetailDto,
  OrderLineStatus,
  cardBrandName,
} from '../../core/api/order.models';
import { httpStatusOf } from '../../core/api/problem-details';
import { isOrderStillChanging } from './poll-while-updating';

/**
How often the page re-reads the order while a partner decides or a refund is on its way.
*/
export const CONFIRMATION_POLL_MS = 5000;
/**
Stop polling after this long; the order page keeps the history.
*/
export const CONFIRMATION_POLL_LIMIT_MS = 10 * 60_000;

const hasPaymentFailed = (order: OrderDetailDto): boolean =>
  order.status === 'awaiting_payment' && order.payment?.status === 'failed';

/**
 * Besides what the API counts as updating (`isOrderStillChanging`, which covers a payment being
 * confirmed), keep reading while the order waits on something only the server's jobs finish: a
 * Failed payment the card provider has yet to settle (BR-PAY-05), or an order that can no longer
 * be paid and is about to be closed. Otherwise the page would stop on a state that is not the end.
 */
const isStillChanging = (order: OrderDetailDto): boolean =>
  isOrderStillChanging(order) ||
  hasPaymentFailed(order) ||
  (order.status === 'awaiting_payment' && !order.canPay);

type ConfirmationState = 'loading' | 'loaded' | 'not-found' | 'signed-out' | 'error';

/**
What a first read that failed means for the page: no such order, no session, or a fault.
*/
const failedState = (error: unknown): ConfirmationState => {
  const status = httpStatusOf(error);
  if (status === 404) {
    return 'not-found';
  }
  return status === 401 ? 'signed-out' : 'error';
};

const LINE_ICONS: Readonly<Record<OrderLineStatus, string>> = {
  pending: 'schedule',
  awaiting_partner: 'schedule',
  needs_attention: 'warning',
  confirmed: 'check_circle',
  rejected: 'cancel',
};

/**
 * Order confirmation (`/orders/:id/confirmation`): the order number, items by seller, what was
 * charged (and refunded), and what happens next. Partner lines confirm later (BR-ORD-01) and a
 * line they cannot supply is refunded later still, so the page re-reads the order while it can
 * change on its own (`isStillChanging`).
 */
@Component({
  selector: 'app-order-confirmation-page',
  imports: [CurrencyPipe, RouterLink, MatButton, MatIcon, MatProgressBar],
  templateUrl: './order-confirmation-page.html',
  styleUrl: './order-confirmation-page.scss',
})
export class OrderConfirmationPage {
  private readonly api = inject(OrdersApi);

  private readonly destroyRef = inject(DestroyRef);

  private polling?: Subscription;

  /**
  The order number, bound from the `:id` route parameter.
  */
  readonly id = input.required<string>();

  protected readonly order = signal<OrderDetailDto | null>(null);

  protected readonly state = signal<ConfirmationState>('loading');

  protected readonly lineIcons = LINE_ICONS;

  protected readonly isPaid = computed(() => {
    const status = this.order()?.status;
    return (
      !!status &&
      status !== 'awaiting_payment' &&
      status !== 'payment_processing' &&
      status !== 'expired'
    );
  });

  /**
   * The card provider has yet to say how the payment ended: still running, or Failed and waiting
   * for the gateway's status (BR-PAY-05), when the card may or may not have been charged.
   */
  protected readonly confirming = computed(() => {
    const order = this.order();
    return (
      !!order &&
      !this.isPaid() &&
      (order.status === 'payment_processing' || hasPaymentFailed(order))
    );
  });

  /**
   * The card issuer asked for a one-time code (`requires_action`): nothing happens until the
   * customer enters it, so the page says so rather than "Confirming your payment".
   */
  protected readonly awaitingCode = computed(
    () => this.confirming() && this.order()?.payment?.status === 'requires_action'
  );

  /**
   * One `<h1>` for every state, so the focus the router puts on it survives the load. It says
   * "confirmed" only once the API says the order is paid, never while loading or after an error.
   */
  protected readonly heading = computed(() => {
    const order = this.order();
    if (!order) {
      return this.state() === 'not-found' ? 'Order not found' : `Order ${this.id()}`;
    }
    if (this.isPaid()) {
      return `Order ${order.number} confirmed`;
    }
    if (this.awaitingCode()) {
      return `Order ${order.number} is waiting for your one-time code`;
    }
    if (this.confirming()) {
      return `Confirming payment for order ${order.number}`;
    }
    return order.status === 'awaiting_payment' && order.canPay
      ? `Order ${order.number} is awaiting payment`
      : `Order ${order.number} was not paid`;
  });

  protected readonly paymentText = computed(() => {
    const payment = this.order()?.payment;
    if (!payment || payment.status !== 'succeeded') {
      return '';
    }
    return payment.method === 'wallet'
      ? 'Paid from your Lumen Goods wallet'
      : `Paid by ${cardBrandName(payment.cardBrand)} ending ${payment.cardLast4}`;
  });

  /**
  "What happens next", one entry per shipment, from the line statuses the API reports.
  */
  protected readonly nextSteps = computed(() =>
    (this.order()?.shipments ?? []).map((shipment) => {
      const statuses = shipment.lines.map((line) => line.status);
      let text: string;
      if (statuses.some((status) => status === 'awaiting_partner' || status === 'pending')) {
        text = `Waiting for ${shipment.sellerName} to confirm. We’ll update this page and your order; if they can’t supply an item, we refund it automatically.`;
      } else if (statuses.includes('needs_attention')) {
        text = `${shipment.sellerName} hasn’t answered yet. Our team is following up.`;
      } else if (statuses.every((status) => status === 'rejected')) {
        // The refund may still be on its way: each line's status says whether it is paid back.
        text = `${shipment.sellerName} couldn’t supply these items, so you don’t pay for them. The refund goes back to how you paid.`;
      } else if (shipment.source === OWN_SOURCE) {
        text = 'We’re packing your Lumen Goods items.';
      } else {
        text = `${shipment.sellerName} confirmed your items and will ship them.`;
      }
      return { source: shipment.source, sellerName: shipment.sellerName, text };
    })
  );

  constructor() {
    effect(() => {
      const number = this.id();
      untracked(() => this.load(number));
    });
  }

  private load(number: string): void {
    type Read = { order: OrderDetailDto } | { error: unknown };
    this.polling?.unsubscribe();
    this.order.set(null);
    this.state.set('loading');
    this.polling = this.api
      .get(number)
      .pipe(
        map((order): Read => ({ order })),
        // eslint-disable-next-line promise/prefer-await-to-callbacks -- RxJS catchError operator, not a Node-style callback
        catchError((error: unknown) => of<Read>({ error })),
        // Read again a while after each answer (never two requests at once), for a limited time.
        repeat({
          count: CONFIRMATION_POLL_LIMIT_MS / CONFIRMATION_POLL_MS,
          delay: CONFIRMATION_POLL_MS,
        }),
        // Stop when nothing can change any more, or when the first read already failed. A later
        // failed re-read keeps the last answer on screen and tries again.
        takeWhile(
          (read) => ('order' in read ? isStillChanging(read.order) : this.order() !== null),
          true
        ),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe((read) => {
        if ('order' in read) {
          this.order.set(read.order);
          this.state.set('loaded');
        } else if (!this.order()) {
          this.state.set(failedState(read.error));
        }
      });
  }

  protected retry(): void {
    this.load(this.id());
  }
}
