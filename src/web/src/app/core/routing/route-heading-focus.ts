import { DOCUMENT } from '@angular/common';
import {
  EnvironmentProviders,
  Injector,
  afterNextRender,
  inject,
  provideEnvironmentInitializer,
} from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';
import { filter } from 'rxjs';

/**
 * After a client-side navigation to a new path, moves focus to the page's `<h1>` so keyboard
 * and screen-reader users start at the new content instead of the old link (WCAG 2.4.3).
 * Skips the first page load and query-only changes (catalogue filters must keep focus).
 */
export const provideRouteHeadingFocus = (): EnvironmentProviders =>
  provideEnvironmentInitializer(() => {
    const router = inject(Router);
    const document = inject(DOCUMENT);
    const injector = inject(Injector);
    let previousPath: string | undefined;

    router.events
      .pipe(filter((event): event is NavigationEnd => event instanceof NavigationEnd))
      .subscribe((event) => {
        const [path] = event.urlAfterRedirects.split(/[?#]/u, 1);
        const isFirstNavigation = previousPath === undefined;
        const isPathChanged = path !== previousPath;
        previousPath = path;
        if (isFirstNavigation || !isPathChanged) {
          return;
        }
        afterNextRender({ write: () => focusMainHeading(document) }, { injector });
      });
  });

const focusMainHeading = (document: Document): void => {
  const target =
    document.querySelector<HTMLElement>('main h1') ?? document.querySelector<HTMLElement>('main');
  if (!target) {
    return;
  }
  if (!target.hasAttribute('tabindex')) {
    target.setAttribute('tabindex', '-1');
  }
  // The router's scroll restoration owns the scroll position; focus must not fight it.
  target.focus({ preventScroll: true });
};
