import { inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { CanDeactivateFn } from '@angular/router';
import { defaultIfEmpty, map } from 'rxjs';
import { openConfirmDialog } from './confirm-dialog';

/**
A page with a form that leaving would throw away.
*/
export interface HasUnsavedChanges {
  hasUnsavedChanges: () => boolean;
}

/**
 * Asks before leaving a form with unsaved edits (Cancel, a section tab, a header link, Back). The
 * safe choice, staying, has focus; saving marks the form clean, so a save never asks.
 */
export const unsavedChangesGuard: CanDeactivateFn<HasUnsavedChanges> = (page) => {
  if (!page.hasUnsavedChanges()) {
    return true;
  }
  return openConfirmDialog(inject(MatDialog), {
    title: 'Discard your changes?',
    message: 'You have edits that are not saved. Leaving this page throws them away.',
    confirmText: 'Discard changes',
    cancelText: 'Keep editing',
  }).pipe(
    map(() => true),
    defaultIfEmpty(false)
  );
};
