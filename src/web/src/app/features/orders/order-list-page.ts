import { CurrencyPipe, DatePipe } from '@angular/common';
import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  linkedSignal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatCard } from '@angular/material/card';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { ActivatedRoute, Params, RouterLink } from '@angular/router';
import { OrderSummaryDto } from '../../core/api/order.models';
import { OrdersApi } from '../../core/api/orders.api';
import { PagedResult } from '../../core/api/paged-result';
import { httpStatusOf } from '../../core/api/problem-details';
import { registerOrderIcons } from './order-icons';
import { StatusView, orderStatusView } from './order-labels';
import { StatusBadge } from './status-badge';

/**
A 1-based page number from the URL; anything else is page 1.
*/
export function parsePage(value: string | null): number {
  const page = Number(value);
  return Number.isInteger(page) && page >= 1 ? page : 1;
}

/**
Query params for a page link; page 1 has none, so it has one URL.
*/
export function pageQueryParams(page: number): Params {
  return { page: page > 1 ? page : null };
}

/**
The customer's order history (`/orders`), newest first, paged through `?page=`.
*/
@Component({
  selector: 'app-order-list-page',
  imports: [
    CurrencyPipe,
    DatePipe,
    RouterLink,
    MatButton,
    MatCard,
    MatIcon,
    MatProgressBar,
    StatusBadge,
  ],
  templateUrl: './order-list-page.html',
  styleUrl: './order-list-page.scss',
})
export class OrderListPage {
  private readonly api = inject(OrdersApi);
  private readonly injector = inject(Injector);
  private readonly queryParamMap = toSignal(inject(ActivatedRoute).queryParamMap, {
    requireSync: true,
  });
  private readonly countHeading = viewChild<ElementRef<HTMLElement>>('countHeading');

  protected readonly pageNumber = computed(() => parsePage(this.queryParamMap().get('page')));
  protected readonly orders = rxResource({
    params: () => this.pageNumber(),
    stream: ({ params: page }) => this.api.list(page),
  });
  /**
  The last page that loaded: stays on screen (dimmed) while the next one loads.
  */
  protected readonly result = linkedSignal<
    PagedResult<OrderSummaryDto> | undefined,
    PagedResult<OrderSummaryDto> | undefined
  >({
    source: () => (this.orders.hasValue() ? this.orders.value() : undefined),
    computation: (next, previous) => next ?? previous?.value,
  });
  protected readonly isError = computed(() => this.orders.status() === 'error');
  protected readonly signedOut = computed(() => httpStatusOf(this.orders.error()) === 401);

  protected readonly countText = computed(() => {
    const total = this.result()?.totalCount ?? 0;
    return total === 1 ? '1 order' : `${total.toLocaleString('en-US')} orders`;
  });

  protected readonly pageParams = pageQueryParams;

  constructor() {
    registerOrderIcons();
    // Paging is a query-only navigation, so the router leaves focus on the page link; start the
    // new page at its count instead (WCAG 2.4.3).
    let previous: number | undefined;
    effect(() => {
      const page = this.pageNumber();
      if (previous !== undefined && page !== previous) {
        untracked(() =>
          afterNextRender(
            { write: () => this.countHeading()?.nativeElement.focus() },
            { injector: this.injector }
          )
        );
      }
      previous = page;
    });
  }

  protected statusOf(order: OrderSummaryDto): StatusView {
    return orderStatusView(order.status, order.statusText);
  }
}
