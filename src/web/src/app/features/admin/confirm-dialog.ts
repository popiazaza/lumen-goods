import { Component, inject } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { BrnDialogRef, injectBrnDialogContext } from '@spartan-ng/brain/dialog';
import { Observable, filter } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import {
  HlmDialogDescription,
  HlmDialogFooter,
  HlmDialogHeader,
  HlmDialogService,
  HlmDialogTitle,
} from '@spartan-ng/helm/dialog';
import { HlmLabel } from '@spartan-ng/helm/label';
import { HlmTextarea } from '@spartan-ng/helm/textarea';

export interface ConfirmDialogData {
  title: string;
  /**
  Says exactly what will happen.
  */
  message: string;
  /**
  A verb: "Deactivate product", "Reject and refund".
  */
  confirmText: string;
  /**
  The way out: "Keep active", "Keep waiting".
  */
  cancelText: string;
  /**
  Shows an optional note field with this label, returned with the answer.
  */
  noteLabel?: string;
  noteHint?: string;
}

/**
What the admin confirmed with (the note is '' without a note field).
*/
export interface ConfirmDialogResult {
  note: string;
}

export const NOTE_MAX_LENGTH = 500;

/**
 * Confirmation for a serious admin action. An `alertdialog`, named by its title and described by
 * its message, that focuses the safe choice first and returns focus to the button that opened it.
 */
@Component({
  selector: 'app-confirm-dialog',
  imports: [
    ReactiveFormsModule,
    HlmButton,
    HlmDialogDescription,
    HlmDialogFooter,
    HlmDialogHeader,
    HlmDialogTitle,
    HlmLabel,
    HlmTextarea,
  ],
  templateUrl: './confirm-dialog.html',
  styleUrl: './confirm-dialog.scss',
})
export class ConfirmDialog {
  private readonly dialogRef = inject<BrnDialogRef<ConfirmDialogResult>>(BrnDialogRef);
  protected readonly data = injectBrnDialogContext<ConfirmDialogData>();

  protected readonly noteMaxLength = NOTE_MAX_LENGTH;
  protected readonly note = new FormControl('', {
    nonNullable: true,
    validators: [Validators.maxLength(NOTE_MAX_LENGTH)],
  });

  /**
  The note's error shows once it was touched, as on the app's other forms.
  */
  protected noteError(): boolean {
    return this.note.invalid && this.note.touched;
  }

  protected confirm(): void {
    if (this.note.invalid) {
      this.note.markAsTouched();
      return;
    }
    this.dialogRef.close({ note: this.note.value.trim() });
  }

  protected cancel(): void {
    this.dialogRef.close();
  }
}

/**
 * Opens the confirmation. Emits once if the admin confirms; completes without a value if they
 * back out (the safe button, Escape or a click outside).
 */
export const openConfirmDialog = (
  dialog: HlmDialogService,
  data: ConfirmDialogData
): Observable<ConfirmDialogResult> =>
  dialog
    .open<ConfirmDialogResult | undefined, ConfirmDialogData>(ConfirmDialog, {
      context: data,
      role: 'alertdialog',
      autoFocus: '.cancel',
      restoreFocus: true,
      showCloseButton: false,
      contentClass: 'sm:max-w-lg',
    })
    .closed$.pipe(
      filter((answer): answer is ConfirmDialogResult => typeof answer?.note === 'string')
    );
