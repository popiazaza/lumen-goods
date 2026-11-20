import { EMPTY, Observable, catchError, expand, of, switchMap, timer } from 'rxjs';

/**
 * Loads once, then loads again every `intervalMs` while the latest answer says it is still
 * `isUpdating` (BR-ORD-02: a payment being confirmed or a line awaiting its partner), and completes
 * as soon as it is not. Unsubscribing (leaving the page) stops it.
 *
 * Only the first load can fail: a failed refresh keeps the last answer on screen and tries again at
 * the next tick, so a network blip does not replace the order with an error page.
 */
export function pollWhileUpdating<T extends { isUpdating: boolean }>(
  load: () => Observable<T>,
  intervalMs: number,
): Observable<T> {
  return load().pipe(
    expand((latest) =>
      latest.isUpdating
        ? timer(intervalMs).pipe(switchMap(() => load().pipe(catchError(() => of(latest)))))
        : EMPTY,
    ),
  );
}
