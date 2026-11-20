import { Component, inject } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButton } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogActions,
  MatDialogConfig,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatError, MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { Observable, filter } from 'rxjs';

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
 * Confirmation for a serious admin action. An `alertdialog` that focuses the safe choice first and
 * returns focus to the button that opened it.
 */
@Component({
  selector: 'app-confirm-dialog',
  imports: [
    ReactiveFormsModule,
    MatButton,
    MatDialogActions,
    MatDialogContent,
    MatDialogTitle,
    MatError,
    MatFormField,
    MatHint,
    MatInput,
    MatLabel,
  ],
  templateUrl: './confirm-dialog.html',
  styleUrl: './confirm-dialog.scss',
})
export class ConfirmDialog {
  private readonly dialogRef =
    inject<MatDialogRef<ConfirmDialog, ConfirmDialogResult>>(MatDialogRef);
  protected readonly data = inject<ConfirmDialogData>(MAT_DIALOG_DATA);

  protected readonly noteMaxLength = NOTE_MAX_LENGTH;
  protected readonly note = new FormControl('', {
    nonNullable: true,
    validators: [Validators.maxLength(NOTE_MAX_LENGTH)],
  });

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
  dialog: MatDialog,
  data: ConfirmDialogData
): Observable<ConfirmDialogResult> => {
  const config: MatDialogConfig<ConfirmDialogData> = {
    data,
    role: 'alertdialog',
    autoFocus: '.cancel',
    restoreFocus: true,
    width: '32rem',
    maxWidth: 'calc(100vw - 32px)',
  };
  return dialog
    .open<ConfirmDialog, ConfirmDialogData, ConfirmDialogResult>(ConfirmDialog, config)
    .afterClosed()
    .pipe(filter((answer): answer is ConfirmDialogResult => typeof answer?.note === 'string'));
};
