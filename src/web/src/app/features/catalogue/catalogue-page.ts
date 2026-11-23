import { LiveAnnouncer } from '@angular/cdk/a11y';
import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  linkedSignal,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Params, Router, RouterLink } from '@angular/router';
import { NgIcon } from '@ng-icons/core';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmLabel } from '@spartan-ng/helm/label';
import { HlmNativeSelectImports } from '@spartan-ng/helm/native-select';
import { HlmProgressImports } from '@spartan-ng/helm/progress';
import { HlmSheet, HlmSheetImports } from '@spartan-ng/helm/sheet';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { httpStatusOf, problemMessage } from '../../core/api/problem-details';
import { PRODUCT_SORTS, ProductPage, ProductSort, ProductsApi } from '../../core/api/products.api';
import { Seller } from '../../core/api/sellers.api';
import { LastCatalogueQuery } from '../../core/catalogue/last-catalogue-query';
import { SellerDirectory } from '../../core/catalogue/seller-directory';
import { formatUsd } from '../../shared/format';
import { categoryLabel } from '../../shared/products/product-labels';
import { CatalogueFilters } from './catalogue-filters';
import {
  CatalogueChange,
  CatalogueQuery,
  SORT_LABELS,
  activeFilterCount,
  applyChange,
  catalogueQueryParams,
  clearFilters,
  isOnlyPageChange,
  pageWindow,
  parseCatalogueQuery,
  restrictSellers,
  isSameQuery,
  toProductSearch,
} from './catalogue-query';
import { ProductCard } from './product-card';

/**
A removable chip for one applied refinement.
*/
interface FilterChip {
  key: string;
  label: string;
  removal: CatalogueChange;
}

const count = /* @__PURE__ */ new Intl.NumberFormat('en-US');

/**
Placeholder cards shown on the first load.
*/
const SKELETON_CARDS = /* @__PURE__ */ Array.from({ length: 8 }, (_, index) => index);

const toSortOption = (value: ProductSort) => ({ value, label: SORT_LABELS[value] });

const isProductSort = (value: unknown): value is ProductSort =>
  (PRODUCT_SORTS as readonly unknown[]).includes(value);

/**
 * The catalogue (`/`). The URL's query string is the single source of truth: the page derives its
 * state from it and every control navigates with the new state, so Back/Forward and shared links
 * reproduce the view.
 */
@Component({
  selector: 'app-catalogue-page',
  imports: [
    RouterLink,
    NgIcon,
    HlmBadge,
    HlmButton,
    HlmLabel,
    HlmNativeSelectImports,
    HlmProgressImports,
    HlmSheetImports,
    HlmSkeleton,
    CatalogueFilters,
    ProductCard,
  ],
  templateUrl: './catalogue-page.html',
  styleUrl: './catalogue-page.scss',
})
export class CataloguePage {
  private readonly route = inject(ActivatedRoute);

  private readonly router = inject(Router);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  private readonly injector = inject(Injector);

  private readonly productsApi = inject(ProductsApi);

  private readonly announcer = inject(LiveAnnouncer);

  private readonly lastQuery = inject(LastCatalogueQuery);

  private readonly sellerDirectory = inject(SellerDirectory);

  private readonly resultsHeading = viewChild.required<ElementRef<HTMLElement>>('resultsHeading');

  private readonly filtersSheet = viewChild.required<HlmSheet>('filtersSheet');

  /**
  After a chip's removal: the chip whose remove button takes focus once the removed one is gone.
  */
  private readonly chipFocus = signal<{ removed: string; next: string } | null>(null);

  private readonly queryParamMap = toSignal(this.route.queryParamMap, { requireSync: true });

  /**
  The state in the URL, parsed and cleaned; sellers checked against `GET /sellers` once loaded.
  */
  protected readonly sellersFailed = this.sellerDirectory.failed;

  protected readonly query = computed(
    () =>
      restrictSellers(
        parseCatalogueQuery(this.queryParamMap()),
        this.sellerDirectory.knownSources()
      ),
    { equal: isSameQuery }
  );

  /**
   * Seller checkboxes: the API's list. Until it has loaded, the sellers already chosen in the URL, so they can still be unticked.
   */
  protected readonly sellerOptions = computed<readonly Seller[]>(() =>
    this.sellerDirectory.isLoaded()
      ? this.sellerDirectory.sellers()
      : this.query().sellers.map((source) => ({
          source,
          name: this.sellerDirectory.nameOf(source),
        }))
  );

  protected readonly results = rxResource({
    params: () => toProductSearch(this.query()),
    stream: ({ params }) => this.productsApi.search(params),
  });

  /**
  The last page that loaded: kept on screen (dimmed) while the next one loads.
  */
  protected readonly page = linkedSignal<ProductPage | undefined, ProductPage | undefined>({
    source: () => (this.results.hasValue() ? this.results.value() : undefined),
    computation: (next, previous) => next ?? previous?.value,
  });

  protected readonly sortOptions = PRODUCT_SORTS.map(toSortOption);

  protected readonly skeletonCards = SKELETON_CARDS;

  protected readonly filterCount = computed(() => activeFilterCount(this.query()));

  protected readonly isError = computed(() => this.results.status() === 'error');

  protected readonly isBadRequest = computed(() => httpStatusOf(this.results.error()) === 400);

  protected readonly errorMessage = computed(() =>
    problemMessage(this.results.error(), 'Some filters in this link are not valid.')
  );

  protected readonly countText = computed(() => {
    const page = this.page();
    if (page) {
      return page.totalCount === 1 ? '1 product' : `${count.format(page.totalCount)} products`;
    }
    return this.isError() ? 'Products' : 'Loading products';
  });

  /**
  "Showing 25–48 of 312".
  */
  protected readonly rangeText = computed(() => {
    const page = this.page();
    if (!page || page.items.length === 0) {
      return '';
    }
    const first = (page.page - 1) * page.pageSize + 1;
    const last = first + page.items.length - 1;
    return `Showing ${count.format(first)}–${count.format(last)} of ${count.format(page.totalCount)}`;
  });

  protected readonly pageLinks = computed(() => {
    const page = this.page();
    return page ? pageWindow(page.page, page.totalPages) : [];
  });

  protected readonly chips = computed<FilterChip[]>(() => {
    const query = this.query();
    const chips: FilterChip[] = query.categories.map((category) => ({
      key: `category:${category}`,
      label: categoryLabel(category),
      removal: { categories: query.categories.filter((c) => c !== category) },
    }));
    for (const source of query.sellers) {
      chips.push({
        key: `seller:${source}`,
        label: this.sellerDirectory.nameOf(source),
        removal: { sellers: query.sellers.filter((s) => s !== source) },
      });
    }
    if (query.minPrice !== null || query.maxPrice !== null) {
      chips.push({
        key: 'price',
        label: priceRangeLabel(query.minPrice, query.maxPrice),
        removal: { minPrice: null, maxPrice: null },
      });
    }
    if (query.inStock) {
      chips.push({ key: 'inStock', label: 'In stock only', removal: { inStock: false } });
    }
    return chips;
  });

  constructor() {
    // Product pages link back to this exact view.
    effect(() => this.lastQuery.remember(catalogueQueryParams(this.query())));

    this.announceResultCounts();
    this.focusResultsOnPageChange();
    this.focusNeighbourChip();
  }

  /**
   * Filter and sort changes keep the scroll position: the router would otherwise jump to the top
   * and hide the control that has focus (WCAG 2.4.11). Page links scroll to the top as usual.
   */
  private navigate(query: CatalogueQuery): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: catalogueQueryParams(query),
      scroll: 'manual',
    });
  }

  /**
  Screen readers hear the new count after each change, not on the first load (WCAG 4.1.3).
  */
  private announceResultCounts(): void {
    let isFirstLoad = true;
    effect(() => {
      if (this.results.status() !== 'resolved') {
        return;
      }
      const totalCount = this.results.value()?.totalCount ?? 0;
      if (!isFirstLoad) {
        const message =
          totalCount === 0
            ? 'No products match'
            : `${count.format(totalCount)} ${totalCount === 1 ? 'product' : 'products'}`;
        void this.announcer.announce(message, 'polite');
      }
      isFirstLoad = false;
    });
  }

  /**
  Paging moves focus to the top of the new results instead of leaving it on a page link.
  */
  private focusResultsOnPageChange(): void {
    let previous: CatalogueQuery | undefined;
    effect(() => {
      const query = this.query();
      if (previous && isOnlyPageChange(previous, query)) {
        untracked(() => this.focusResultsHeadingAfterRender());
      }
      previous = query;
    });
  }

  private focusNeighbourChip(): void {
    effect(() => {
      const pending = this.chipFocus();
      if (!pending || this.chips().some((chip) => chip.key === pending.removed)) {
        return;
      }
      untracked(() => this.chipFocus.set(null));
      afterNextRender(
        {
          write: () =>
            this.host.nativeElement
              .querySelector<HTMLButtonElement>(
                `.chip-remove[data-chip="${CSS.escape(pending.next)}"]`
              )
              ?.focus(),
        },
        { injector: this.injector }
      );
    });
  }

  private focusResultsHeadingAfterRender(): void {
    afterNextRender(
      { write: () => this.resultsHeading().nativeElement.focus() },
      {
        injector: this.injector,
      }
    );
  }

  /**
  Query params for a link to another page of the same results.
  */
  protected sellerName(source: string): string {
    return this.sellerDirectory.nameOf(source);
  }

  protected pageParams(page: number): Params {
    return catalogueQueryParams({ ...this.query(), page });
  }

  protected clearSearchParams(): Params {
    return catalogueQueryParams(applyChange(this.query(), { q: '' }));
  }

  protected apply(change: CatalogueChange): void {
    this.navigate(applyChange(this.query(), change));
  }

  protected applySort(value: string | null | undefined): void {
    if (isProductSort(value)) {
      this.apply({ sort: value });
    }
  }

  /**
   * Focus moves to the chip that takes the removed one's place (or the one before it, at the end
   * of the row) once the new query has dropped it; the last chip takes the row with it, so focus
   * goes to the results.
   */
  protected removeChip(chip: FilterChip, index: number): void {
    const chips = this.chips();
    const neighbour = chips[index + 1] ?? chips[index - 1];
    if (neighbour) {
      this.chipFocus.set({ removed: chip.key, next: neighbour.key });
    } else {
      this.focusResultsHeadingAfterRender();
    }
    this.apply(chip.removal);
  }

  /**
  Clears every refinement. The buttons that call this disappear, so focus goes to the results.
  */
  protected clearAll(): void {
    this.focusResultsHeadingAfterRender();
    this.navigate(clearFilters(this.query()));
  }

  /**
  The sheet's "Clear all" stays (disabled) inside the sheet; focus restarts at its heading.
  */
  protected clearAllInSheet(heading: HTMLElement): void {
    if (this.filterCount() === 0) {
      return;
    }
    this.navigate(clearFilters(this.query()));
    // The CDK removes tabindex from a heading it autofocused once focus moves on; put it back so
    // the heading can take focus again.
    if (!heading.hasAttribute('tabindex')) {
      heading.setAttribute('tabindex', '-1');
    }
    heading.focus();
  }

  /**
  Retry also fetches the seller list again if that failed.
  */
  protected retry(): void {
    if (this.sellerDirectory.failed()) {
      this.sellerDirectory.reload();
    }
    this.results.reload();
  }

  protected closeFilters(): void {
    this.filtersSheet().close();
  }
}

const priceRangeLabel = (min: number | null, max: number | null): string => {
  if (min !== null && max !== null) {
    return `${formatUsd(min)} – ${formatUsd(max)}`;
  }
  return min === null ? `Up to ${formatUsd(max ?? 0)}` : `From ${formatUsd(min)}`;
};
