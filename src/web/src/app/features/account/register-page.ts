import {
  Component,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatError, MatFormField, MatHint, MatLabel, MatSuffix } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { problemMessage, problemOf } from '../../core/api/problem-details';
import { SessionApi } from '../../core/api/session.api';
import { safeReturnUrl } from '../../core/routing/return-url';
import { ErrorSummary, FormProblem } from '../../shared/forms/error-summary';
import { AfterSignIn } from './after-sign-in';
import { PASSWORD_HINT, passwordRulesValidator } from './password-rules';

/**
Create an account (`/register?returnUrl=`): email and password, then signed in.
*/
@Component({
  selector: 'app-register-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatButton,
    MatError,
    MatFormField,
    MatHint,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatSuffix,
    ErrorSummary,
  ],
  templateUrl: './register-page.html',
  styleUrl: './account-form.scss',
})
export class RegisterPage {
  private readonly sessionApi = inject(SessionApi);

  private readonly afterSignIn = inject(AfterSignIn);

  private readonly injector = inject(Injector);

  private readonly summary = viewChild(ErrorSummary);

  readonly returnUrl = input<string>();

  protected readonly session = this.sessionApi.session;

  protected readonly passwordHint = PASSWORD_HINT;

  protected readonly returnParams = computed(() => {
    const returnUrl = safeReturnUrl(this.returnUrl(), '');
    return returnUrl ? { returnUrl } : {};
  });

  protected readonly continueUrl = computed(() => safeReturnUrl(this.returnUrl()));

  protected readonly form = new FormGroup({
    email: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.email],
    }),
    password: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, passwordRulesValidator],
    }),
  });

  protected readonly showPassword = signal(false);

  protected readonly submitting = signal(false);

  protected readonly problems = signal<readonly FormProblem[]>([]);

  protected readonly failure = signal<string | null>(null);

  /**
  Messages from the API's validation problem, shown under their fields.
  */
  protected readonly serverErrors = signal<{ email: string | null; password: string | null }>({
    email: null,
    password: null,
  });

  private showProblems(): void {
    const problems: FormProblem[] = [];
    if (this.form.controls.email.invalid) {
      problems.push({ fieldId: 'register-email', message: this.emailError() });
    }
    if (this.form.controls.password.invalid) {
      problems.push({ fieldId: 'register-password', message: this.passwordError() });
    }
    this.problems.set(problems);
    afterNextRender({ write: () => this.summary()?.focus() }, { injector: this.injector });
  }

  /**
  A 400 validation problem lands on its fields (keys `Email`, `Password`); anything else on top.
  */
  private failed(error: unknown): void {
    const errors = problemOf(error)?.errors;
    const pick = (key: string) =>
      Object.entries(errors ?? {})
        .find(([name]) => name.toLowerCase() === key)?.[1]
        ?.join(' ') ?? null;
    const email = pick('email');
    const password = pick('password');
    if (email || password) {
      this.serverErrors.set({ email, password });
      if (email) {
        this.form.controls.email.setErrors({ server: true });
      }
      if (password) {
        this.form.controls.password.setErrors({ server: true });
      }
      this.form.markAllAsTouched();
      this.showProblems();
      return;
    }
    this.failure.set(problemMessage(error, 'We couldn’t create your account. Try again.'));
  }

  protected emailError(): string {
    const control = this.form.controls.email;
    if (control.hasError('server')) {
      return this.serverErrors().email ?? 'Check your email address';
    }
    return control.hasError('required')
      ? 'Enter your email address'
      : 'Enter a valid email address, like name@example.com';
  }

  protected passwordError(): string {
    const control = this.form.controls.password;
    if (control.hasError('server')) {
      return this.serverErrors().password ?? 'Choose a different password';
    }
    if (control.hasError('required')) {
      return 'Enter a password';
    }
    const missing: unknown = control.getError('passwordRules');
    return Array.isArray(missing) ? missing.join(' ') : 'Choose a different password';
  }

  protected submit(): void {
    if (this.submitting()) {
      return;
    }
    this.failure.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.showProblems();
      return;
    }
    this.problems.set([]);
    this.submitting.set(true);
    const { email, password } = this.form.getRawValue();
    this.afterSignIn
      .complete(this.sessionApi.register({ email: email.trim(), password }), this.returnUrl())
      .pipe(finalize(() => this.submitting.set(false)))
      .subscribe({ error: (error: unknown) => this.failed(error) });
  }
}
