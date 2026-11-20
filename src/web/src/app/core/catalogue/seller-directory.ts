import { Service, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { Seller, SellersApi } from '../api/sellers.api';

/**
 * Shown only until `GET /sellers` answers (or if it fails), so a partner product is never shown
 * without its shop's name. The API's names always win.
 */
const FALLBACK_SELLERS: readonly Seller[] = [
  { source: 'lumen', name: 'Lumen Goods' },
  { source: 'harbor', name: 'Harbor Supply Co.' },
  { source: 'kestrel', name: 'Kestrel Outfitters' },
];

/** The shops in the catalogue and their display names ("Sold by Harbor Supply Co."). Loaded once. */
@Service()
export class SellerDirectory {
  private readonly api = inject(SellersApi);
  private readonly loaded = rxResource({ stream: () => this.api.list() });

  /** Sellers in the API's order; the fallback list until it has answered. */
  readonly sellers = computed<readonly Seller[]>(() =>
    this.loaded.hasValue() && this.loaded.value().length > 0
      ? this.loaded.value()
      : FALLBACK_SELLERS,
  );

  private readonly names = computed(
    () => new Map(this.sellers().map((seller) => [seller.source, seller.name])),
  );

  /** Display name for a product source; the raw source id if the API does not know it. */
  nameOf(source: string): string {
    return this.names().get(source) ?? source;
  }
}
