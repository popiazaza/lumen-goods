import { LiveAnnouncer } from '@angular/cdk/a11y';
import { CurrencyPipe } from '@angular/common';
import { Component, DestroyRef, computed, inject, linkedSignal, signal } from '@angular/core';
import { rxResource, takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButton } from '@angular/material/button';
import { MatOption } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
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
import { switchMap, tap } from 'rxjs';
import {
  AdminProduct,
  AdminProductsApi,
  ProductStatusFilter,
} from '../../core/api/admin-products.api';
import { PagedResult } from '../../core/api/paged-result';
import { httpStatusOf, isAccessDenied, problemMessage } from '../../core/api/problem-details';
import { PRODUCT_CATEGORIES } from '../../core/api/products.api';
import { categoryLabel } from '../../shared/products/product-labels';
import { registerOrderIcons } from '../orders/order-icons';
import { StatusView } from '../orders/order-labels';
import { StatusBadge } from '../orders/status-badge';
import { AdminAccessDenied } from './admin-access-denied';
import {
  ADMIN_PAGE_SIZES,
  AdminProductsQuery,
  adminProductsQueryParams,
  hasProductFilters,
  parseAdminProductsQuery,
  withFilters,
} from './admin-list-query';
import { openConfirmDialog } from './confirm-dialog';

const ACTIVE: StatusView = { tone: 'success', icon: 'visibility', text: 'Active' };
const INACTIVE: StatusView = { tone: 'neutral', icon: 'visibility_off', text: 'Inactive' };

/**
Admin: Lumen Goods' own products (`/admin/products`), searchable and filterable via the URL.
*/
@Component({
  selector: 'app-admin-products-page',
  imports: [
    CurrencyPipe,
    ReactiveFormsModule,
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
  templateUrl: './admin-products-page.html',
  styleUrl: './admin-list-page.scss',
})
export class AdminProductsPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(AdminProductsApi);
  private readonly dialog = inject(MatDialog);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly destroyRef = inject(DestroyRef);
  private readonly queryParamMap = toSignal(this.route.queryParamMap, { requireSync: true });

  protected readonly query = computed(() => parseAdminProductsQuery(this.queryParamMap()), {
    equal: (a, b) => JSON.stringify(a) === JSON.stringify(b),
  });
  protected readonly products = rxResource({
    params: () => this.query(),
    stream: ({ params }) => this.api.list(params),
  });
  /**
  The last page that loaded: stays on screen (dimmed) while the next one loads.
  */
  protected readonly result = linkedSignal<
    PagedResult<AdminProduct> | undefined,
    PagedResult<AdminProduct> | undefined
  >({
    source: () => (this.products.hasValue() ? this.products.value() : undefined),
    computation: (next, previous) => next ?? previous?.value,
  });

  /**
  An action can also come back 401/403 (session expired, role removed).
  */
  private readonly deniedStatus = signal<number | undefined>(undefined);
  protected readonly accessStatus = computed(() => {
    const error = this.products.error();
    return isAccessDenied(error) ? httpStatusOf(error) : this.deniedStatus();
  });
  protected readonly isError = computed(() => this.products.status() === 'error');
  protected readonly errorMessage = computed(() =>
    httpStatusOf(this.products.error()) === 400
      ? problemMessage(this.products.error(), 'These filters are not valid.')
      : 'We couldn’t load the products. Check your connection and try again.'
  );
  protected readonly hasFilters = computed(() => hasProductFilters(this.query()));

  /**
  The search box: what is typed until it is submitted, then the URL's value again.
  */
  protected readonly search = new FormControl('', { nonNullable: true });
  /**
  The product whose activation is being changed.
  */
  protected readonly busyId = signal<string | null>(null);
  protected readonly actionMessage = signal<{ text: string; failed: boolean } | null>(null);

  protected readonly categories = PRODUCT_CATEGORIES.map((value) => ({
    value,
    label: categoryLabel(value),
  }));
  protected readonly statusOptions: { value: ProductStatusFilter; label: string }[] = [
    { value: 'all', label: 'Active and inactive' },
    { value: 'active', label: 'Active' },
    { value: 'inactive', label: 'Inactive' },
  ];
  protected readonly pageSizes = [...ADMIN_PAGE_SIZES];
  protected readonly columns = ['name', 'category', 'price', 'stock', 'status', 'actions'];
  protected readonly categoryLabel = categoryLabel;

  constructor() {
    registerOrderIcons();
    this.route.queryParamMap
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.search.setValue(this.query().q));
  }

  protected statusOf(product: AdminProduct): StatusView {
    return product.isActive ? ACTIVE : INACTIVE;
  }

  protected submitSearch(event: Event): void {
    event.preventDefault();
    this.navigate(withFilters(this.query(), { q: this.search.value.trim() }), 'manual');
  }

  protected setStatus(status: ProductStatusFilter): void {
    this.navigate(withFilters(this.query(), { status }), 'manual');
  }

  protected setCategory(value: string): void {
    const category = PRODUCT_CATEGORIES.find((candidate) => candidate === value) ?? null;
    this.navigate(withFilters(this.query(), { category }), 'manual');
  }

  protected clearFilters(): void {
    this.navigate(withFilters(this.query(), { q: '', status: 'all', category: null }), 'manual');
  }

  protected changePage(event: PageEvent): void {
    const { pageSize } = event;
    const page = pageSize === this.query().pageSize ? event.pageIndex + 1 : 1;
    this.navigate({ ...this.query(), page, pageSize }, 'auto');
  }

  /**
   * Deactivating hides the product from the shop; it is never deleted, because orders reference it
   * (BR-ADM-01). Both directions ask first.
   */
  protected toggleActive(product: AdminProduct): void {
    if (this.busyId()) {
      return;
    }
    const deactivate = product.isActive;
    openConfirmDialog(this.dialog, {
      title: deactivate ? `Deactivate ${product.name}?` : `Reactivate ${product.name}?`,
      message: deactivate
        ? 'It disappears from the shop and can no longer be added to a cart. Past orders keep it. You can reactivate it at any time.'
        : 'It appears in the shop again and can be bought while it is in stock.',
      confirmText: deactivate ? 'Deactivate' : 'Reactivate',
      cancelText: deactivate ? 'Keep active' : 'Keep inactive',
    })
      .pipe(
        tap(() => {
          this.busyId.set(product.id);
          this.actionMessage.set(null);
        }),
        switchMap(() => this.api[deactivate ? 'deactivate' : 'reactivate'](product.id)),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe({
        next: (updated) => {
          this.busyId.set(null);
          const text = `${updated.name} is now ${updated.isActive ? 'active' : 'inactive'}.`;
          this.actionMessage.set({ text, failed: false });
          void this.announcer.announce(text, 'polite');
          this.products.reload();
        },
        error: (error: unknown) => {
          this.busyId.set(null);
          if (isAccessDenied(error)) {
            this.deniedStatus.set(httpStatusOf(error));
            return;
          }
          const text = `Couldn’t ${deactivate ? 'deactivate' : 'reactivate'} ${product.name}: ${problemMessage(error, 'the request failed. Try again.')}`;
          this.actionMessage.set({ text, failed: true });
          void this.announcer.announce(text, 'assertive');
        },
      });
  }

  /**
  Filters keep the scroll position (focus stays on the control); paging goes to the top.
  */
  private navigate(query: AdminProductsQuery, scroll: 'manual' | 'auto'): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: adminProductsQueryParams(query),
      ...(scroll === 'manual' && { scroll: 'manual' as const }),
    });
  }
}
