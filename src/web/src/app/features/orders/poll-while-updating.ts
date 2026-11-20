import { EMPTY, Observable, catchError, expand, of, switchMap, timer } from 'rxjs';
import { OrderDetailDto } from '../../core/api/order.models';

/**
 * Loads once, then loads again every `intervalMs` while `keepsChanging` says the latest answer can
 * still change on its own, and completes as soon as it cannot. Unsubscribing (leaving the page)
 * stops it.
 *
 * Only the first load can fail: a failed refresh keeps the last answer on screen and tries again at the next tick, so a network blip does not replace the order with an error page.
 */
export function pollWhileUpdating<T>(
  load: () => Observable<T>,
  intervalMs: number,
  keepsChanging: (latest: T) => boolean
): Observable<T> {
  return load().pipe(
    expand((latest) =>
      keepsChanging(latest)
        ? timer(intervalMs).pipe(switchMap(() => load().pipe(catchError(() => of(latest)))))
        : EMPTY
    )
  );
}

/**
 * The order can still change without the customer doing anything, so a page showing it re-reads
 * it. The API's `isUpdating` covers a payment being confirmed, a line awaiting its partner and a
 * refund being sent (`pending`). A `failed` refund is retried by the refund job (back to pending,
 * same refund id) and the line still reads "Not supplied, refund pending", but `isUpdating` is
 * false between retries; so the page also keeps reading until every refund is paid back.
 */
export function orderKeepsChanging(order: OrderDetailDto): boolean {
  return order.isUpdating || hasRefundOnItsWay(order);
}

/**
A refund recorded but not paid back yet (pending, or failed and waiting for its retry).
*/
export function hasRefundOnItsWay(order: OrderDetailDto): boolean {
  return order.refunds.some((refund) => refund.status !== 'succeeded');
}
