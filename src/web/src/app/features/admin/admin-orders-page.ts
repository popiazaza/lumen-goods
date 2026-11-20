import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, inject, linkedSignal } from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatOption } from '@angular/material/core';
import { MatFormField, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatPaginator, PageEvent } from '@angular/material/paginator';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatSelect } from '@angular/material/select';
import {
  MatCell,
  MatCellDef,
  MatColumnDef,
  MatHeaderCell,
  MatHeaderCellDef,
  MatHeaderRow,
  MatHeaderRowDef,
  MatRow,
  MatRowDef,
  MatTable,
} from '@angular/material/table';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  ADMIN_ORDER_STATUS_FILTERS,
  AdminOrderStatusFilter,
  AdminOrderSummary,
  AdminOrdersApi,
} from '../../core/api/admin-orders.api';
import { PagedResult } from '../../core/api/paged-result';
import { httpStatusOf, isAccessDenied, problemMessage } from '../../core/api/problem-details';
import { registerOrderIcons } from '../orders/order-icons';
import { ADMIN_ORDER_STATUS_LABELS, StatusView, orderStatusView } from '../orders/order-labels';
import { StatusBadge } from '../orders/status-badge';
import { AdminAccessDenied } from './admin-access-denied';
import {
  ADMIN_PAGE_SIZES,
  AdminOrdersQuery,
  adminOrdersQueryParams,
  hasOrderFilters,
  parseAdminOrdersQuery,
  withFilters,
} from './admin-list-query';

const NEEDS_ATTENTION: StatusView = { tone: 'warning', icon: 'warning', text: 'Needs attention' };

const STATUS_FILTER_LABELS: Readonly<Record<AdminOrderStatusFilter, string>> = {
  ...ADMIN_ORDER_STATUS_LABELS,
  // Not an order status: orders with a line waiting for an admin decision (BR-ORD-04).
  needs_attention: 'Needs attention',
};

const toStatusOption = (value: AdminOrderStatusFilter) => ({
  value,
  label: STATUS_FILTER_LABELS[value],
});

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
    MatButton,
    MatCell,
    MatCellDef,
    MatColumnDef,
    MatFormField,
    MatHeaderCell,
    MatHeaderCellDef,
    MatHeaderRow,
    MatHeaderRowDef,
    MatIcon,
    MatInput,
    MatLabel,
    MatOption,
    MatPaginator,
    MatProgressBar,
    MatRow,
    MatRowDef,
    MatSelect,
    MatTable,
    AdminAccessDenied,
    StatusBadge,
  ],
  templateUrl: './admin-orders-page.html',
  styleUrl: './admin-list-page.scss',
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

  protected readonly columns = ['number', 'placed', 'customer', 'sellers', 'total', 'status'];

  protected readonly needsAttention = NEEDS_ATTENTION;

  protected readonly statusOf = summaryStatusView;

  constructor() {
    registerOrderIcons();
  }

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

  protected changePage(event: PageEvent): void {
    const { pageSize } = event;
    const page = pageSize === this.query().pageSize ? event.pageIndex + 1 : 1;
    this.navigate({ ...this.query(), page, pageSize }, 'auto');
  }
}
