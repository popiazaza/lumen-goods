import { Injector, inject } from '@angular/core';
import { CanDeactivateFn } from '@angular/router';
import { defaultIfEmpty, firstValueFrom, map } from 'rxjs';

/**
A page with a form that leaving would throw away.
*/
export interface HasUnsavedChanges {
  hasUnsavedChanges: () => boolean;
}

/**
 * Asks before leaving a form with unsaved edits (Cancel, a section tab, a header link, Back). The
 * safe choice, staying, has focus; saving marks the form clean, so a save never asks.
 *
 * `app.routes.ts` imports the admin routes eagerly, so the dialog is loaded only when it is needed:
 * a static import would put Material's dialog into every shopper's start-up bundle.
 */
export const unsavedChangesGuard: CanDeactivateFn<HasUnsavedChanges> = async (page) => {
  if (!page.hasUnsavedChanges()) {
    return true;
  }
  const injector = inject(Injector);
  const [{ MatDialog }, { openConfirmDialog }] = await Promise.all([
    import('@angular/material/dialog'),
    import('./confirm-dialog'),
  ]);
  return firstValueFrom(
    openConfirmDialog(injector.get(MatDialog), {
      title: 'Discard your changes?',
      message: 'You have edits that are not saved. Leaving this page throws them away.',
      confirmText: 'Discard changes',
      cancelText: 'Keep editing',
    }).pipe(
      map(() => true),
      defaultIfEmpty(false)
    )
  );
};
