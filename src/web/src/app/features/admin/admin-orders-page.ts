import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, inject, linkedSignal } from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NgIcon } from '@ng-icons/core';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmLabel } from '@spartan-ng/helm/label';
import { HlmProgressImports } from '@spartan-ng/helm/progress';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { HlmTableImports } from '@spartan-ng/helm/table';
import {
  ADMIN_ORDER_STATUS_FILTERS,
  AdminOrderStatusFilter,
  AdminOrderSummary,
  AdminOrdersApi,
} from '../../core/api/admin-orders.api';
import { PagedResult } from '../../core/api/paged-result';
import { httpStatusOf, isAccessDenied, problemMessage } from '../../core/api/problem-details';
import { provideOrderIcons } from '../orders/order-icons';
import { ADMIN_ORDER_STATUS_LABELS, StatusView, orderStatusView } from '../orders/order-labels';
import { StatusBadge } from '../orders/status-badge';
import { AdminAccessDenied } from './admin-access-denied';
import { AdminPageChange, AdminPaginator } from './admin-paginator';
import {
  ADMIN_PAGE_SIZES,
  AdminOrdersQuery,
  adminOrdersQueryParams,
  hasOrderFilters,
  parseAdminOrdersQuery,
  withFilters,
} from './admin-list-query';

const NEEDS_ATTENTION: StatusView = {
  tone: 'warning',
  icon: 'lucideTriangleAlert',
  text: 'Needs attention',
};

const STATUS_FILTER_LABELS: Readonly<Record<AdminOrderStatusFilter, string>> = {
  ...ADMIN_ORDER_STATUS_LABELS,
  // Not an order status: orders with a line waiting for an admin decision (BR-ORD-04).
  needs_attention: 'Needs attention',
};

const toStatusOption = (value: AdminOrderStatusFilter) => ({
  value,
  label: STATUS_FILTER_LABELS[value],
});

/**
The select's text for one status or the chosen ones ("Paid, in progress, Refunded").
*/
const statusFilterText = (value: unknown): string =>
  (Array.isArray(value) ? value : [value])
    .map((status) => STATUS_FILTER_LABELS[status as AdminOrderStatusFilter] ?? String(status))
    .join(', ');

const summaryStatusView = (order: AdminOrderSummary): StatusView =>
  orderStatusView(order.status, order.statusText);

/**
Admin: every order, filtered by status and placed date, paged; the URL holds the view.
*/
@Component({
  selector: 'app-admin-orders-page',
  imports: [
    CurrencyPipe,
    DatePipe,
    RouterLink,
    NgIcon,
    HlmButton,
    HlmInput,
    HlmLabel,
    HlmProgressImports,
    HlmSelectImports,
    HlmTableImports,
    AdminPaginator,
    AdminAccessDenied,
    StatusBadge,
  ],
  templateUrl: './admin-orders-page.html',
  styleUrl: './admin-list-page.scss',
  providers: [provideOrderIcons()],
})
export class AdminOrdersPage {
  private readonly route = inject(ActivatedRoute);

  private readonly router = inject(Router);

  private readonly api = inject(AdminOrdersApi);

  private readonly queryParamMap = toSignal(this.route.queryParamMap, { requireSync: true });

  protected readonly query = computed(() => parseAdminOrdersQuery(this.queryParamMap()), {
    equal: (a, b) => JSON.stringify(a) === JSON.stringify(b),
  });

  protected readonly orders = rxResource({
    params: () => this.query(),
    stream: ({ params }) => this.api.list(params),
  });

  /**
  The last page that loaded: stays on screen (dimmed) while the next one loads.
  */
  protected readonly result = linkedSignal<
    PagedResult<AdminOrderSummary> | undefined,
    PagedResult<AdminOrderSummary> | undefined
  >({
    source: () => (this.orders.hasValue() ? this.orders.value() : undefined),
    computation: (next, previous) => next ?? previous?.value,
  });

  protected readonly accessStatus = computed(() => {
    const error = this.orders.error();
    return isAccessDenied(error) ? httpStatusOf(error) : undefined;
  });

  protected readonly isError = computed(() => this.orders.status() === 'error');

  protected readonly errorMessage = computed(() =>
    httpStatusOf(this.orders.error()) === 400
      ? problemMessage(this.orders.error(), 'These filters are not valid.')
      : 'We couldn’t load the orders. Check your connection and try again.'
  );

  protected readonly hasFilters = computed(() => hasOrderFilters(this.query()));

  protected readonly statusOptions = ADMIN_ORDER_STATUS_FILTERS.map(toStatusOption);

  protected readonly pageSizes = [...ADMIN_PAGE_SIZES];

  protected readonly needsAttention = NEEDS_ATTENTION;

  protected readonly statusText = statusFilterText;

  protected readonly statusOf = summaryStatusView;

  /**
  Filters keep the scroll position (focus stays on the control); paging goes to the top.
  */
  private navigate(query: AdminOrdersQuery, scroll: 'manual' | 'auto'): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: adminOrdersQueryParams(query),
      ...(scroll === 'manual' && { scroll: 'manual' as const }),
    });
  }

  protected setStatuses(values: readonly string[]): void {
    const statuses = ADMIN_ORDER_STATUS_FILTERS.filter((status) => values.includes(status));
    this.navigate(withFilters(this.query(), { statuses }), 'manual');
  }

  protected setDay(which: 'from' | 'to', event: Event): void {
    const value = (event.target as HTMLInputElement).value || null;
    this.navigate(withFilters(this.query(), { [which]: value }), 'manual');
  }

  protected clearFilters(): void {
    this.navigate(withFilters(this.query(), { statuses: [], from: null, to: null }), 'manual');
  }

  protected changePage(event: AdminPageChange): void {
    const { pageSize } = event;
    const page = pageSize === this.query().pageSize ? event.pageIndex + 1 : 1;
    this.navigate({ ...this.query(), page, pageSize }, 'auto');
  }
}
