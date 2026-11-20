import { Component, computed, input, output } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButton } from '@angular/material/button';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';

/**
 * The one-time code step's form: the code field, why the code was refused, the time left and Verify.
 * The page owns the form and the countdown, checks the code and sends it.
 */
@Component({
  selector: 'app-one-time-code-form',
  imports: [ReactiveFormsModule, MatButton, MatFormField, MatHint, MatIcon, MatInput, MatLabel],
  templateUrl: './one-time-code-form.html',
  styleUrl: './one-time-code-form.scss',
})
export class OneTimeCodeForm {
  readonly form = input.required<FormGroup<{ code: FormControl<string> }>>();

  /**
  The mock gateway's code, shown because no text message is sent.
  */
  readonly testCode = input<string | null>(null);

  /**
  Why the code was refused; empty while it was not.
  */
  readonly error = input('');

  /**
  The time left, as m:ss.
  */
  readonly countdown = input.required<string>();

  readonly submitting = input(false);

  readonly verify = output();

  /**
  The error first, so a screen reader reads why the code was refused when the field has focus.
  */
  protected readonly describedBy = computed(() =>
    [this.error() ? 'code-error' : '', this.testCode() ? 'code-test' : '', 'code-timer']
      .filter(Boolean)
      .join(' ')
  );
}
