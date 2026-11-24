import { Component, computed, inject, input } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ControlContainer, FormGroupDirective, ReactiveFormsModule } from '@angular/forms';
import { merge, switchMap } from 'rxjs';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmLabel } from '@spartan-ng/helm/label';
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
  imports: [ReactiveFormsModule, HlmInput, HlmLabel],
  templateUrl: './card-details.html',
  styleUrl: './card-details.scss',
  // The fields join the page's own <form [formGroup]>, so submitting it shows their errors (`view`
  // reads that form's submitted state); a second [formGroup] here would never be submitted.
  viewProviders: [{ provide: ControlContainer, useExisting: FormGroupDirective }],
})
export class CardDetails {
  /**
  The page's form, whose submit shows every field's error.
  */
  private readonly formDirective = inject(FormGroupDirective, { optional: true });

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
  What the fields show: the brand, the CVC's length and each invalid field's message, shown once
  the field is left or the form submitted (as Material's form fields did).
  */
  protected readonly view = computed(() => {
    // Read only so this is worked out again on every event of the form or of a field.
    this.formEvent();
    const form = this.form();
    const brand = detectBrand(digitsOf(form.controls.number.value));
    const cvcDigits = cvcLength(brand);
    const errors = cardFieldErrors(form, this.serverErrors());
    const isSubmitted = this.formDirective?.submitted ?? false;
    return {
      brandName: brand && brand !== 'other' ? cardBrandName(brand) : '',
      cvcHint: `${cvcDigits} digits on the ${cvcDigits === 4 ? 'front' : 'back'}`,
      shown: Object.fromEntries(
        CARD_FIELDS.filter(
          (field) => errors[field] && (form.controls[field].touched || isSubmitted)
        ).map((field) => [field, errors[field]])
      ) as CardFieldMessages,
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
