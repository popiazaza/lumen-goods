import { LiveAnnouncer } from '@angular/cdk/a11y';
import {
  Component,
  ElementRef,
  Injector,
  TemplateRef,
  afterNextRender,
  computed,
  effect,
  inject,
  linkedSignal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { MatBottomSheet, MatBottomSheetRef } from '@angular/material/bottom-sheet';
import { MatButton } from '@angular/material/button';
import { MatChip, MatChipRemove, MatChipSet } from '@angular/material/chips';
import { MatOption } from '@angular/material/core';
import { MatFormField, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatSelect } from '@angular/material/select';
import { ActivatedRoute, Params, Router, RouterLink } from '@angular/router';
import { httpStatusOf, problemMessage } from '../../core/api/problem-details';
import { PRODUCT_SORTS, ProductPage, ProductsApi } from '../../core/api/products.api';
import { Seller } from '../../core/api/sellers.api';
import { LastCatalogueQuery } from '../../core/catalogue/last-catalogue-query';
import { SellerDirectory } from '../../core/catalogue/seller-directory';
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
  differsOnlyInPage,
  pageWindow,
  parseCatalogueQuery,
  restrictSellers,
  sameQuery,
  toProductSearch,
} from './catalogue-query';
import { ProductCard } from './product-card';

/** A removable chip for one applied refinement. */
interface FilterChip {
  key: string;
  label: string;
  removal: CatalogueChange;
}

const usd = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' });
const count = new Intl.NumberFormat('en-US');

/** Placeholder cards shown on the first load. */
const SKELETON_CARDS = Array.from({ length: 8 }, (_, index) => index);

/**
 * The catalogue (`/`). The URL's query string is the single source of truth: the page derives its
 * state from it and every control navigates with the new state, so Back/Forward and shared links
 * reproduce the view.
 */
@Component({
  selector: 'app-catalogue-page',
  imports: [
    RouterLink,
    MatButton,
    MatChip,
    MatChipRemove,
    MatChipSet,
    MatFormField,
    MatIcon,
    MatLabel,
    MatOption,
    MatProgressBar,
    MatSelect,
    CatalogueFilters,
    ProductCard,
  ],
  templateUrl: './catalogue-page.html',
  styleUrl: './catalogue-page.scss',
})
export class CataloguePage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly productsApi = inject(ProductsApi);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly bottomSheet = inject(MatBottomSheet);
  private readonly lastQuery = inject(LastCatalogueQuery);
  protected readonly sellerDirectory = inject(SellerDirectory);

  private readonly resultsHeading = viewChild.required<ElementRef<HTMLElement>>('resultsHeading');
  private readonly filtersSheet = viewChild.required<TemplateRef<unknown>>('filtersSheet');
  private sheetRef: MatBottomSheetRef | undefined;

  private readonly queryParamMap = toSignal(this.route.queryParamMap, { requireSync: true });

  /** The state in the URL, parsed and cleaned; sellers checked against `GET /sellers` once loaded. */
  protected readonly query = computed(
    () =>
      restrictSellers(
        parseCatalogueQuery(this.queryParamMap()),
        this.sellerDirectory.knownSources(),
      ),
    { equal: sameQuery },
  );

  /**
   * Seller checkboxes: the API's list. Until it has loaded, the sellers already chosen in the URL,
   * so they can still be unticked.
   */
  protected readonly sellerOptions = computed<readonly Seller[]>(() =>
    this.sellerDirectory.isLoaded()
      ? this.sellerDirectory.sellers()
      : this.query().sellers.map((source) => ({
          source,
          name: this.sellerDirectory.nameOf(source),
        })),
  );

  protected readonly results = rxResource({
    params: () => toProductSearch(this.query()),
    stream: ({ params }) => this.productsApi.search(params),
  });

  /** The last page that loaded: kept on screen (dimmed) while the next one loads. */
  protected readonly page = linkedSignal<ProductPage | undefined, ProductPage | undefined>({
    source: () => (this.results.hasValue() ? this.results.value() : undefined),
    computation: (next, previous) => next ?? previous?.value,
  });

  protected readonly sortOptions = PRODUCT_SORTS.map((value) => ({
    value,
    label: SORT_LABELS[value],
  }));
  protected readonly skeletonCards = SKELETON_CARDS;
  protected readonly filterCount = computed(() => activeFilterCount(this.query()));
  protected readonly isError = computed(() => this.results.status() === 'error');
  protected readonly isBadRequest = computed(() => httpStatusOf(this.results.error()) === 400);
  protected readonly errorMessage = computed(() =>
    problemMessage(this.results.error(), 'Some filters in this link are not valid.'),
  );

  protected readonly countText = computed(() => {
    const page = this.page();
    if (page) {
      return page.totalCount === 1 ? '1 product' : `${count.format(page.totalCount)} products`;
    }
    return this.isError() ? 'Products' : 'Loading products';
  });

  /** "Showing 25–48 of 312". */
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
  }

  /** Query params for a link to another page of the same results. */
  protected pageParams(page: number): Params {
    return catalogueQueryParams({ ...this.query(), page });
  }

  protected clearSearchParams(): Params {
    return catalogueQueryParams(applyChange(this.query(), { q: '' }));
  }

  protected apply(change: CatalogueChange): void {
    this.navigate(applyChange(this.query(), change));
  }

  protected removeChip(chip: FilterChip): void {
    // The chip set moves focus to a neighbouring chip; the last chip takes the row with it.
    if (this.chips().length === 1) {
      this.focusResultsHeadingAfterRender();
    }
    this.apply(chip.removal);
  }

  /** Clears every refinement. The buttons that call this disappear, so focus goes to the results. */
  protected clearAll(): void {
    this.focusResultsHeadingAfterRender();
    this.navigate(clearFilters(this.query()));
  }

  /** The sheet's "Clear all" stays (disabled) inside the sheet; focus restarts at its heading. */
  protected clearAllInSheet(heading: HTMLElement): void {
    if (this.filterCount() === 0) {
      return;
    }
    this.navigate(clearFilters(this.query()));
    // The CDK may have removed tabindex after its own autofocus moved on (it does for elements it
    // considers unfocusable); put it back so the heading can take focus.
    if (!heading.hasAttribute('tabindex')) {
      heading.setAttribute('tabindex', '-1');
    }
    heading.focus();
  }

  /** Retry also fetches the seller list again if that failed. */
  protected retry(): void {
    if (this.sellerDirectory.failed()) {
      this.sellerDirectory.reload();
    }
    this.results.reload();
  }

  protected openFilters(): void {
    this.sheetRef = this.bottomSheet.open(this.filtersSheet(), {
      ariaLabel: 'Filters',
      autoFocus: 'first-heading',
    });
  }

  protected closeFilters(): void {
    this.sheetRef?.dismiss();
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

  /** Screen readers hear the new count after each change, not on the first load (WCAG 4.1.3). */
  private announceResultCounts(): void {
    let isFirstLoad = true;
    effect(() => {
      if (this.results.status() !== 'resolved') {
        return;
      }
      const totalCount = this.results.value()?.totalCount ?? 0;
      if (isFirstLoad) {
        isFirstLoad = false;
        return;
      }
      const message =
        totalCount === 0
          ? 'No products match'
          : `${count.format(totalCount)} ${totalCount === 1 ? 'product' : 'products'}`;
      void this.announcer.announce(message, 'polite');
    });
  }

  /** Paging moves focus to the top of the new results instead of leaving it on a page link. */
  private focusResultsOnPageChange(): void {
    let previous: CatalogueQuery | undefined;
    effect(() => {
      const query = this.query();
      if (previous && differsOnlyInPage(previous, query)) {
        untracked(() => this.focusResultsHeadingAfterRender());
      }
      previous = query;
    });
  }

  private focusResultsHeadingAfterRender(): void {
    afterNextRender(
      { write: () => this.resultsHeading().nativeElement.focus() },
      {
        injector: this.injector,
      },
    );
  }
}

function priceRangeLabel(min: number | null, max: number | null): string {
  if (min !== null && max !== null) {
    return `${usd.format(min)} – ${usd.format(max)}`;
  }
  return min !== null ? `From ${usd.format(min)}` : `Up to ${usd.format(max ?? 0)}`;
}
