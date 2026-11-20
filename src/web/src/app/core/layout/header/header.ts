import { Component, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatBadge } from '@angular/material/badge';
import { MatIconButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import {
  IsActiveMatchOptions,
  NavigationEnd,
  Router,
  RouterLink,
  RouterLinkActive,
} from '@angular/router';
import { filter } from 'rxjs';
import { CartStateService } from '../../cart/cart-state.service';

@Component({
  selector: 'app-header',
  imports: [ReactiveFormsModule, RouterLink, RouterLinkActive, MatBadge, MatIconButton, MatIcon],
  templateUrl: './header.html',
  styleUrl: './header.scss',
  host: {
    '(document:keydown.escape)': 'closeMenu(true)',
  },
})
export class Header {
  private readonly router = inject(Router);
  private readonly cart = inject(CartStateService);
  private readonly menuToggle = viewChild.required('menuToggle', { read: ElementRef });

  /** "Shop" stays active while the catalogue's query string changes. */
  protected readonly shopLinkMatch: IsActiveMatchOptions = {
    paths: 'exact',
    queryParams: 'ignored',
    matrixParams: 'ignored',
    fragment: 'ignored',
  };

  protected readonly menuOpen = signal(false);
  protected readonly itemCount = this.cart.itemCount;
  protected readonly cartLabel = computed(() => {
    const count = this.itemCount();
    if (count === 0) {
      return 'Cart, empty';
    }
    return count === 1 ? 'Cart, 1 item' : `Cart, ${count} items`;
  });

  protected readonly searchForm = new FormGroup({
    q: new FormControl('', { nonNullable: true }),
  });

  constructor() {
    this.router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe((event) => {
        this.menuOpen.set(false);
        // Keep the field in sync with the URL so Back and shared links show the current search.
        const q = this.router.parseUrl(event.urlAfterRedirects).queryParamMap.get('q') ?? '';
        this.searchForm.controls.q.setValue(q, { emitEvent: false });
      });
  }

  protected toggleMenu(): void {
    this.menuOpen.update((open) => !open);
  }

  protected closeMenu(returnFocus = false): void {
    if (!this.menuOpen()) {
      return;
    }
    this.menuOpen.set(false);
    if (returnFocus) {
      this.menuToggle().nativeElement.focus();
    }
  }

  protected search(): void {
    const q = this.searchForm.controls.q.value.trim();
    // A new search starts from page 1 with no filters; the catalogue owns the rest of the query.
    void this.router.navigate(['/'], { queryParams: q ? { q } : {} });
  }
}
