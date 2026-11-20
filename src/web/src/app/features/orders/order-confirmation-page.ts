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

/** How often the page re-reads the order while a partner decides (`isUpdating`). */
export const CONFIRMATION_POLL_MS = 5000;
/** Stop polling after this long; the order page keeps the history. */
export const CONFIRMATION_POLL_LIMIT_MS = 10 * 60_000;

type Read = { order: OrderDetailDto } | { error: unknown };

const LINE_ICONS: Readonly<Record<OrderLineStatus, string>> = {
  pending: 'schedule',
  awaiting_partner: 'schedule',
  needs_attention: 'warning',
  confirmed: 'check_circle',
  rejected: 'cancel',
};

/**
 * Order confirmation (`/orders/:id/confirmation`): the order number, items by seller, what was
 * charged (and refunded), and what happens next. Partner lines confirm later (BR-ORD-01), so the
 * page re-reads the order while the API says it is still updating.
 */
@Component({
  selector: 'app-order-confirmation-page',
  imports: [CurrencyPipe, RouterLink, MatButton, MatIcon],
  templateUrl: './order-confirmation-page.html',
  styleUrl: './order-confirmation-page.scss',
})
export class OrderConfirmationPage {
  /** The order number, bound from the `:id` route parameter. */
  readonly id = input.required<string>();

  private readonly api = inject(OrdersApi);
  private readonly destroyRef = inject(DestroyRef);
  private polling?: Subscription;

  protected readonly order = signal<OrderDetailDto | null>(null);
  protected readonly state = signal<'loading' | 'loaded' | 'not-found' | 'error'>('loading');
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

  /** One `<h1>` for every state, so the focus the router puts on it survives the load. */
  protected readonly heading = computed(() => {
    const order = this.order();
    if (this.state() === 'not-found') {
      return 'Order not found';
    }
    if (!order || this.isPaid()) {
      return `Order ${this.id()} confirmed`;
    }
    return order.status === 'expired'
      ? `Order ${order.number} was not paid`
      : `Order ${order.number} is awaiting payment`;
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

  /** "What happens next", one entry per shipment, from the line statuses the API reports. */
  protected readonly nextSteps = computed(() =>
    (this.order()?.shipments ?? []).map((shipment) => {
      const statuses = shipment.lines.map((line) => line.status);
      let text: string;
      if (statuses.some((status) => status === 'awaiting_partner' || status === 'pending')) {
        text = `Waiting for ${shipment.sellerName} to confirm. We’ll update this page and your order; if they can’t supply an item, we refund it automatically.`;
      } else if (statuses.some((status) => status === 'needs_attention')) {
        text = `${shipment.sellerName} hasn’t answered yet. Our team is following up.`;
      } else if (statuses.every((status) => status === 'rejected')) {
        text = `${shipment.sellerName} couldn’t supply these items, so we refunded them.`;
      } else if (shipment.source === OWN_SOURCE) {
        text = 'We’re packing your Lumen Goods items.';
      } else {
        text = `${shipment.sellerName} confirmed your items and will ship them.`;
      }
      return { source: shipment.source, sellerName: shipment.sellerName, text };
    }),
  );

  constructor() {
    effect(() => {
      const number = this.id();
      untracked(() => this.load(number));
    });
  }

  protected retry(): void {
    this.load(this.id());
  }

  private load(number: string): void {
    this.polling?.unsubscribe();
    this.order.set(null);
    this.state.set('loading');
    this.polling = this.api
      .get(number)
      .pipe(
        map((order): Read => ({ order })),
        catchError((error: unknown) => of<Read>({ error })),
        // Read again a while after each answer (never two requests at once), for a limited time.
        repeat({
          count: CONFIRMATION_POLL_LIMIT_MS / CONFIRMATION_POLL_MS,
          delay: CONFIRMATION_POLL_MS,
        }),
        // Stop when nothing can change any more, or when the first read already failed. A later
        // failed re-read keeps the last answer on screen and tries again.
        takeWhile(
          (read) => ('order' in read ? read.order.isUpdating : this.order() !== null),
          true,
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((read) => {
        if ('order' in read) {
          this.order.set(read.order);
          this.state.set('loaded');
        } else if (!this.order()) {
          this.state.set(httpStatusOf(read.error) === 404 ? 'not-found' : 'error');
        }
      });
  }
}
