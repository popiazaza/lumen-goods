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
import { AdminOrdersApi, LineDecision } from '../../core/api/admin-orders.api';
import { OrderDetailDto, OrderLineDto } from '../../core/api/order.models';
import { httpStatusOf, isAccessDenied, problemMessage } from '../../core/api/problem-details';
import { registerOrderIcons } from '../orders/order-icons';
import { StatusView, lineStatusView, orderStatusView, shippingLabel } from '../orders/order-labels';
import { OrderSummaryPanel } from '../orders/order-summary-panel';
import { StatusBadge } from '../orders/status-badge';
import { AdminAccessDenied } from './admin-access-denied';
import { partnerOrderView, paymentEventView } from './admin-order-labels';
import { ConfirmDialogData, openConfirmDialog } from './confirm-dialog';

/**
 * Admin: one order (`/admin/orders/:id`, the order number): what the customer sees plus the
 * payment timeline with the real decline reasons, each partner order, and the Confirm/Reject
 * decision for lines that need attention (BR-ORD-04).
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
    stream: ({ params: number }) => this.api.get(number),
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
    openConfirmDialog(this.dialog, confirmation(line, sellerName, decision))
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
          this.order.set(updated);
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
          const message = `Couldn’t save the decision for ${line.name}: ${problemMessage(error, 'the request failed. Try again.')}`;
          this.actionError.set(message);
          void this.announcer.announce(message, 'assertive');
          if (httpStatusOf(error) === 409) {
            this.order.reload();
          }
        },
      });
  }
}

function confirmation(
  line: OrderLineDto,
  sellerName: string,
  decision: LineDecision,
): ConfirmDialogData {
  const note = {
    noteLabel: 'Note (optional)',
    noteHint: 'Kept with the decision, e.g. what the partner’s support said.',
  };
  return decision === 'confirm'
    ? {
        title: `Confirm ${line.name}?`,
        message: `${sellerName} accepted this item but has not confirmed it. Confirm it only if you know it will be supplied: the customer sees it as confirmed and Lumen stops asking ${sellerName}.`,
        confirmText: 'Confirm item',
        cancelText: 'Keep waiting',
        ...note,
      }
    : {
        title: `Reject ${line.name}?`,
        message: `The customer is refunded for this item and its tax, plus the ${sellerName} shipping fee if nothing else from ${sellerName} remains. Lumen stops asking ${sellerName}. This cannot be undone.`,
        confirmText: 'Reject and refund',
        cancelText: 'Keep waiting',
        ...note,
      };
}
