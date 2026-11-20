import { Component, computed, input } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ControlContainer, FormGroupDirective, ReactiveFormsModule } from '@angular/forms';
import { MatError, MatFormField, MatHint, MatLabel, MatSuffix } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { merge, switchMap } from 'rxjs';
import { cardBrandName } from '../../../core/api/order.models';
import {
  caretAfterFormat,
  cvcLength,
  detectBrand,
  digitsOf,
  formatCardNumber,
  formatExpiry,
} from './card';
import { CARD_FIELDS, CardFieldMessages, CardForm, cardFieldErrors } from './card-form';
import type { Signal } from '@angular/core';

/**
 * The card fields of the payment form: the number (spaced as it is typed, with its brand), the
 * name, the expiry date and the CVC, each with its error under it. The page owns the form: it
 * checks and sends it, and lists the same errors in its error summary.
 */
@Component({
  selector: 'app-card-details',
  imports: [ReactiveFormsModule, MatError, MatFormField, MatHint, MatInput, MatLabel, MatSuffix],
  templateUrl: './card-details.html',
  styleUrl: './card-details.scss',
  // The fields join the page's own <form [formGroup]>, so submitting it shows their errors (Material reads
  // the nearest form directive's submitted state); a second [formGroup] here would never be submitted.
  viewProviders: [{ provide: ControlContainer, useExisting: FormGroupDirective }],
})
export class CardDetails {
  /**
   * The latest event of the form or of one of its fields. The page changes the form as well (Pay
   * marks every field touched, the API's errors are set on their fields), and the fields must show
   * that, not only what their own input changed. Set in the constructor: it reads `form`, an input
   * declared after it.
   */
  private readonly formEvent: Signal<unknown>;

  readonly form = input.required<CardForm>();

  /**
  The API's messages for the fields it refused, shown instead of the form's own.
  */
  readonly serverErrors = input<CardFieldMessages>({});

  /**
  What the fields show: the brand, the CVC's length and each invalid field's message.
  */
  protected readonly view = computed(() => {
    // Read only so this is worked out again on every event of the form or of a field.
    this.formEvent();
    const form = this.form();
    const brand = detectBrand(digitsOf(form.controls.number.value));
    const cvcDigits = cvcLength(brand);
    return {
      brandName: brand && brand !== 'other' ? cardBrandName(brand) : '',
      cvcHint: `${cvcDigits} digits on the ${cvcDigits === 4 ? 'front' : 'back'}`,
      errors: cardFieldErrors(form, this.serverErrors()),
    };
  });

  constructor() {
    this.formEvent = toSignal(
      toObservable(this.form).pipe(
        // `markAllAsTouched()` on a form that is already touched only tells the fields.
        switchMap((form) =>
          merge(form.events, ...CARD_FIELDS.map((field) => form.controls[field].events))
        )
      )
    );
  }

  /**
  Spaces the number as it is typed (4-4-4-4, or 4-6-5 for Amex) and keeps the caret in place.
  */
  protected formatNumber(event: Event): void {
    const field = event.target as HTMLInputElement;
    const raw = field.value;
    const caret = field.selectionStart ?? raw.length;
    const formatted = formatCardNumber(raw);
    if (formatted === raw) {
      return;
    }
    this.form().controls.number.setValue(formatted);
    const position = caretAfterFormat(raw, caret, formatted);
    field.setSelectionRange?.(position, position);
  }

  protected formatExpiryInput(event: Event): void {
    const field = event.target as HTMLInputElement;
    const isDeleting = (event as InputEvent).inputType?.startsWith('delete') ?? false;
    const formatted = formatExpiry(field.value, isDeleting);
    if (formatted !== field.value) {
      this.form().controls.expiry.setValue(formatted);
    }
  }
}
