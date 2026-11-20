import { LiveAnnouncer } from '@angular/cdk/a11y';
import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatCard } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { switchMap, tap } from 'rxjs';
import {
  AdminOrder,
  AdminOrdersApi,
  LineDecision,
  PARTNER_CALL_IN_PROGRESS_PROBLEM,
  PartnerOrder,
} from '../../core/api/admin-orders.api';
import { OrderDetailDto, OrderLineDto } from '../../core/api/order.models';
import {
  httpStatusOf,
  isAccessDenied,
  problemMessage,
  problemTypeOf,
} from '../../core/api/problem-details';
import { registerOrderIcons } from '../orders/order-icons';
import { StatusView, lineStatusView, orderStatusView, shippingLabel } from '../orders/order-labels';
import { OrderSummaryPanel } from '../orders/order-summary-panel';
import { orderKeepsChanging, pollWhileUpdating } from '../orders/poll-while-updating';
import { StatusBadge } from '../orders/status-badge';
import { AdminAccessDenied } from './admin-access-denied';
import { partnerOrderView, paymentEventView } from './admin-order-labels';
import { ConfirmDialogData, openConfirmDialog } from './confirm-dialog';

/** How often the page re-reads an order that can still change on its own. */
export const ADMIN_ORDER_POLL_INTERVAL_MS = 5000;

/**
 * Admin: one order (`/admin/orders/:id`, the order number): what the customer sees plus the
 * payment timeline with the real decline reasons, each partner order, and the Confirm/Reject
 * decision for lines that need attention (BR-ORD-04). Like the customer's page, it re-reads the
 * order while a partner, a payment or a refund can still change it.
 */
@Component({
  selector: 'app-admin-order-detail-page',
  imports: [
    CurrencyPipe,
    DatePipe,
    RouterLink,
    MatButton,
    MatCard,
    MatIcon,
    MatProgressBar,
    AdminAccessDenied,
    OrderSummaryPanel,
    StatusBadge,
  ],
  templateUrl: './admin-order-detail-page.html',
  styleUrl: './admin-order-detail-page.scss',
})
export class AdminOrderDetailPage {
  /** The order number, bound from the `:id` route parameter. */
  readonly id = input.required<string>();

  private readonly api = inject(AdminOrdersApi);
  private readonly dialog = inject(MatDialog);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly order = rxResource({
    params: () => this.id(),
    stream: ({ params: number }) =>
      pollWhileUpdating(() => this.api.get(number), ADMIN_ORDER_POLL_INTERVAL_MS, (admin) =>
        orderKeepsChanging(admin.order),
      ),
  });
  protected readonly detail = computed(() =>
    this.order.hasValue() ? this.order.value() : undefined,
  );

  /** A decision can also come back 401/403 (session expired, role removed). */
  private readonly deniedStatus = signal<number | undefined>(undefined);
  protected readonly accessStatus = computed(() => {
    const error = this.order.error();
    return isAccessDenied(error) ? httpStatusOf(error) : this.deniedStatus();
  });
  protected readonly errorStatus = computed(() =>
    this.order.status() === 'error' ? (httpStatusOf(this.order.error()) ?? -1) : undefined,
  );

  protected readonly attentionCount = computed(
    () =>
      (this.detail()?.order.shipments ?? [])
        .flatMap((shipment) => shipment.lines)
        .filter((line) => line.status === 'needs_attention').length,
  );
  /** The line whose decision is being saved. */
  protected readonly savingLine = signal<string | null>(null);
  protected readonly actionError = signal('');

  protected readonly shippingLabel = shippingLabel;
  protected readonly eventView = paymentEventView;
  protected readonly partnerOrderView = partnerOrderView;

  constructor() {
    registerOrderIcons();
  }

  protected orderStatus(order: OrderDetailDto): StatusView {
    return orderStatusView(order.status, order.statusText);
  }

  protected lineStatus(line: OrderLineDto): StatusView {
    return lineStatusView(line.status, line.statusText);
  }

  /**
   * Asks for confirmation, then saves the decision. The answer is the updated order, which
   * replaces the page's copy; if the line no longer needs attention (409), the order is reloaded.
   */
  protected resolve(line: OrderLineDto, sellerName: string, decision: LineDecision): void {
    if (this.savingLine()) {
      return;
    }
    const number = this.id();
    const partnerOrder = this.detail()?.partnerOrders.find((p) => p.orderLineId === line.id);
    openConfirmDialog(this.dialog, confirmation(line, sellerName, decision, partnerOrder))
      .pipe(
        tap(() => {
          this.savingLine.set(line.id);
          this.actionError.set('');
        }),
        switchMap((answer) => this.api.resolveLine(number, line.id, decision, answer.note)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (updated) => {
          this.savingLine.set(null);
          this.show(updated);
          void this.announcer.announce(
            decision === 'confirm'
              ? `${line.name} confirmed.`
              : `${line.name} rejected. The customer is refunded for it.`,
            'polite',
          );
        },
        error: (error: unknown) => {
          this.savingLine.set(null);
          if (isAccessDenied(error)) {
            this.deniedStatus.set(httpStatusOf(error));
            return;
          }
          // A partner call in flight changed nothing: the line still waits, so keep the page and
          // say when to come back. Any other 409 means the line was decided meanwhile.
          const busy = problemTypeOf(error) === PARTNER_CALL_IN_PROGRESS_PROBLEM;
          const message = busy
            ? `Couldn’t save the decision for ${line.name}: Lumen is talking to ${sellerName} about this item right now. Try again in a minute.`
            : `Couldn’t save the decision for ${line.name}: ${problemMessage(error, 'the request failed. Try again.')}`;
          this.actionError.set(message);
          void this.announcer.announce(message, 'assertive');
          if (httpStatusOf(error) === 409 && !busy) {
            this.order.reload();
          }
        },
      });
  }

  /**
   * Shows the order the decision answered with. Setting it stops the resource's stream, so if a
   * refund (or anything else) is still on its way, reading again restarts the polling.
   */
  private show(updated: AdminOrder): void {
    this.order.set(updated);
    if (orderKeepsChanging(updated.order)) {
      this.order.reload();
    }
  }
}

function confirmation(
  line: OrderLineDto,
  sellerName: string,
  decision: LineDecision,
  partnerOrder: PartnerOrder | undefined,
): ConfirmDialogData {
  const note = {
    noteLabel: 'Note (optional)',
    noteHint: 'Kept with the decision, e.g. what the partner’s support said.',
  };
  // BR-ORD-04: a send whose answer never came back may have reached the partner.
  const where = partnerOrder?.outcomeUnknown
    ? `Lumen sent this item to ${sellerName} but never learned whether ${sellerName} took it, so ${sellerName} may hold the order. Check with ${sellerName} first.`
    : `${sellerName} accepted this item but has not confirmed it.`;
  return decision === 'confirm'
    ? {
        title: `Confirm ${line.name}?`,
        message: `${where} Confirm it only if you know it will be supplied: the customer sees it as confirmed and Lumen stops asking ${sellerName}.`,
        confirmText: 'Confirm item',
        cancelText: 'Keep waiting',
        ...note,
      }
    : {
        title: `Reject ${line.name}?`,
        message: `${partnerOrder?.outcomeUnknown ? `${where} ` : ''}The customer is refunded for this item and its tax, plus the ${sellerName} shipping fee if nothing else from ${sellerName} remains. Lumen stops asking ${sellerName}. This cannot be undone.`,
        confirmText: 'Reject and refund',
        cancelText: 'Keep waiting',
        ...note,
      };
}
