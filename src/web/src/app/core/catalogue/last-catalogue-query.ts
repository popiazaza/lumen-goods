import { Service, signal } from '@angular/core';
import { Params } from '@angular/router';

/**
 * The catalogue's most recent query params, so "Back to the shop" links on other pages (the
 * product breadcrumb) return to the same search, filters and page. In-memory only: a reload or a
 * shared product link starts from the unfiltered shop.
 */
@Service()
export class LastCatalogueQuery {
  private readonly current = signal<Params>({});

  readonly params = this.current.asReadonly();

  remember(params: Params): void {
    this.current.set(params);
  }
}
