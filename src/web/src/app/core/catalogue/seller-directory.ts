import { Service, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { Seller, SellersApi } from '../api/sellers.api';

/**
 * Display names only, for the moment before `GET /sellers` answers (or if it fails), so a product
 * card never shows a bare id. They never decide which sellers exist or can be filtered on.
 */
const FALLBACK_NAMES: Readonly<Record<string, string>> = {
  lumen: 'Lumen Goods',
  harbor: 'Harbor Supply Co.',
  kestrel: 'Kestrel Outfitters',
};

/**
 * The shops in the catalogue, from `GET /sellers` (partners are configuration on the API, so the
 * list can grow without a client release). Loaded once per app session.
 */
@Service()
export class SellerDirectory {
  private readonly api = inject(SellersApi);
  private readonly loaded = rxResource({ stream: () => this.api.list() });

  /** True once the API has answered; until then every seller id in a URL is kept as it is. */
  readonly isLoaded = computed(() => this.loaded.hasValue());
  readonly failed = computed(() => this.loaded.status() === 'error');

  /** Sellers in the API's order; empty until it has answered. */
  readonly sellers = computed<readonly Seller[]>(() =>
    this.loaded.hasValue() ? this.loaded.value() : [],
  );

  /** The seller ids the API accepts, or `null` while unknown (accept any well-formed id). */
  readonly knownSources = computed<ReadonlySet<string> | null>(() =>
    this.isLoaded() ? new Set(this.sellers().map((seller) => seller.source)) : null,
  );

  private readonly names = computed(
    () => new Map(this.sellers().map((seller) => [seller.source, seller.name])),
  );

  /** Display name for a product source: the API's, else a built-in name, else the raw id. */
  nameOf(source: string): string {
    return this.names().get(source) ?? FALLBACK_NAMES[source] ?? source;
  }

  reload(): void {
    this.loaded.reload();
  }
}
