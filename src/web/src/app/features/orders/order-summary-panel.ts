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
  template: `
    @let o = order();
    <mat-card appearance="outlined" class="section">
      <h2>Summary</h2>
      <dl class="pairs totals">
        <div>
          <dt>Items</dt>
          <dd>{{ o.totals.subtotal | currency }}</dd>
        </div>
        <div>
          <dt>Shipping</dt>
          <dd>{{ o.totals.shipping | currency }}</dd>
        </div>
        <div>
          <dt>Tax</dt>
          <dd>{{ o.totals.tax | currency }}</dd>
        </div>
        <div class="total">
          <dt>Total charged</dt>
          <dd>{{ o.totals.total | currency }}</dd>
        </div>
        @if (o.totals.refunded > 0) {
          <div class="refunded">
            <dt>Refunded</dt>
            <dd>−{{ o.totals.refunded | currency }}</dd>
          </div>
          <div class="total">
            <dt>Net paid</dt>
            <dd>{{ o.totals.netPaid | currency }}</dd>
          </div>
        }
      </dl>
    </mat-card>

    <mat-card appearance="outlined" class="section">
      <h2>Payment</h2>
      @if (o.payment; as payment) {
        <p class="method">
          <mat-icon [svgIcon]="payment.method === 'wallet' ? 'wallet' : 'credit_card'" />
          <span>{{ methodLabel() }}</span>
        </p>
        <app-status-badge [status]="paymentStatus(payment.status)" />
        @if (payment.completedUtc) {
          <p class="muted">
            <time [attr.datetime]="payment.completedUtc">{{
              payment.completedUtc | date: 'medium'
            }}</time>
          </p>
        }
      } @else {
        <p>Not paid yet.</p>
        @if (o.canPay) {
          <p class="muted">
            The prices and stock are held until
            <time [attr.datetime]="o.holdExpiresUtc">{{
              o.holdExpiresUtc | date: 'shortTime'
            }}</time>.
          </p>
        }
      }
    </mat-card>

    @if (o.refunds.length > 0) {
      <mat-card appearance="outlined" class="section">
        <h2>Refunds</h2>
        <ul class="refunds">
          @for (refund of o.refunds; track refund.id) {
            <li>
              <div class="refund-head">
                <span class="amount">{{ refund.amount | currency }}</span>
                <app-status-badge [status]="refundStatus(refund.status, admin())" />
              </div>
              <p>{{ refund.reason }}</p>
              <dl class="pairs breakdown">
                @for (line of refundedLines(refund); track line.id) {
                  <div>
                    <dt>{{ line.name }}</dt>
                    <dd>{{ line.refundedAmount | currency }}</dd>
                  </div>
                }
                @if (refund.shippingAmount > 0) {
                  <div>
                    <dt>Shipping fee</dt>
                    <dd>{{ refund.shippingAmount | currency }}</dd>
                  </div>
                }
              </dl>
              <p class="muted">
                <time [attr.datetime]="refund.createdUtc">{{
                  refund.createdUtc | date: 'medium'
                }}</time>
                @if (o.payment) {
                  · to {{ methodLabel() }}
                }
              </p>
            </li>
          }
        </ul>
      </mat-card>
    }

    <mat-card appearance="outlined" class="section">
      <h2>Shipping to</h2>
      <address>
        {{ o.shippingAddress.name }}<br />
        {{ o.shippingAddress.line1 }}<br />
        @if (o.shippingAddress.line2) {
          {{ o.shippingAddress.line2 }}<br />
        }
        {{ o.shippingAddress.city }}, {{ o.shippingAddress.region }}
        {{ o.shippingAddress.postalCode }}<br />
        {{ o.shippingAddress.country }}
      </address>
    </mat-card>
  `,
  styleUrl: './order-summary-panel.scss',
})
export class OrderSummaryPanel {
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

  private readonly linesById = computed(
    () =>
      new Map(
        this.order()
          .shipments.flatMap((shipment) => shipment.lines)
          .map((line) => [line.id, line])
      )
  );

  /**
  The lines a refund pays back, each with the amount the API refunded for it.
  */
  protected refundedLines(refund: RefundDto): OrderLineDto[] {
    const lines = this.linesById();
    return refund.lineIds.flatMap((id) => lines.get(id) ?? []);
  }
}
