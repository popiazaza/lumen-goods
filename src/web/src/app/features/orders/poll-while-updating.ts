import { EMPTY, Observable, catchError, expand, of, switchMap, timer } from 'rxjs';
import { OrderDetailDto } from '../../core/api/order.models';

/**
 * Loads once, then loads again every `intervalMs` while `isStillChanging` says the latest answer can
 * still change on its own, and completes as soon as it cannot. Unsubscribing (leaving the page)
 * stops it.
 *
 * Only the first load can fail: a failed refresh keeps the last answer on screen and tries again at the next tick, so a network blip does not replace the order with an error page.
 */
export const pollWhileUpdating = <T>(
  load: () => Observable<T>,
  intervalMs: number,
  isStillChanging: (latest: T) => boolean
): Observable<T> =>
  load().pipe(
    expand((latest) =>
      isStillChanging(latest)
        ? timer(intervalMs).pipe(switchMap(() => load().pipe(catchError(() => of(latest)))))
        : EMPTY
    )
  );

/**
 * The order can still change without the customer doing anything, so a page showing it re-reads
 * it. The API's `isUpdating` covers a payment being confirmed, a line awaiting its partner and a
 * refund being sent (`pending`). A `failed` refund is retried by the refund job (back to pending,
 * same refund id) and the line still reads "Not supplied, refund pending", but `isUpdating` is
 * false between retries; so the page also keeps reading until every refund is paid back.
 */
export const isOrderStillChanging = (order: OrderDetailDto): boolean =>
  order.isUpdating || hasRefundOnItsWay(order);

/**
A refund recorded but not paid back yet (pending, or failed and waiting for its retry).
*/
export const hasRefundOnItsWay = (order: OrderDetailDto): boolean =>
  order.refunds.some((refund) => refund.status !== 'succeeded');
