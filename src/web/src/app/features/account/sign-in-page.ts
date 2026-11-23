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
import { RouterLink } from '@angular/router';
import { NgIcon } from '@ng-icons/core';
import { finalize } from 'rxjs';
import { HlmButtonImports } from '@spartan-ng/helm/button';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import {
  RATE_LIMITED_PROBLEM,
  httpStatusOf,
  problemMessage,
  problemTypeOf,
} from '../../core/api/problem-details';
import { SessionApi } from '../../core/api/session.api';
import { SIGNED_OUT_REASON } from '../../core/api/signed-out.interceptor';
import { safeReturnUrl } from '../../core/routing/return-url';
import { ErrorSummary, FormProblem } from '../../shared/forms/error-summary';
import { AfterSignIn } from './after-sign-in';

/**
Sign in (`/sign-in?returnUrl=`): email and password; required to check out.
*/
@Component({
  selector: 'app-sign-in-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    NgIcon,
    HlmButtonImports,
    HlmInputImports,
    HlmLabelImports,
    ErrorSummary,
  ],
  templateUrl: './sign-in-page.html',
  styleUrls: ['./account-form.scss', './sign-in-page.scss'],
})
export class SignInPage {
  private readonly sessionApi = inject(SessionApi);

  private readonly afterSignIn = inject(AfterSignIn);

  private readonly injector = inject(Injector);

  private readonly summary = viewChild(ErrorSummary);

  /**
  Where to go after signing in (query param); only in-app paths are followed.
  */
  readonly returnUrl = input<string>();

  /**
  Why the shopper is here: `signed-out` when the session ended under them (`signedOutInterceptor`).
  */
  readonly reason = input<string>();

  protected readonly session = this.sessionApi.session;

  protected readonly signedOut = computed(() => this.reason() === SIGNED_OUT_REASON);

  protected readonly forCheckout = computed(() =>
    safeReturnUrl(this.returnUrl()).startsWith('/checkout')
  );

  /**
  Carries the return URL to "Create an account" and "Continue".
  */
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
    password: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });

  protected readonly showPassword = signal(false);

  protected readonly submitting = signal(false);

  protected readonly problems = signal<readonly FormProblem[]>([]);

  /**
  Wrong credentials, lockout or a failed request: about the whole form, not one field.
  */
  protected readonly failure = signal<{ title: string; detail: string } | null>(null);

  private showProblems(): void {
    const problems: FormProblem[] = [];
    if (this.form.controls.email.invalid) {
      problems.push({ fieldId: 'sign-in-email', message: this.emailError() });
    }
    if (this.form.controls.password.invalid) {
      problems.push({ fieldId: 'sign-in-password', message: 'Enter your password' });
    }
    this.problems.set(problems);
    afterNextRender({ write: () => this.summary()?.focus() }, { injector: this.injector });
  }

  private failed(error: unknown): void {
    const status = httpStatusOf(error);
    if (status === 401) {
      this.failure.set({
        title: 'Email or password is incorrect',
        detail: 'Check them and try again. Passwords are case-sensitive.',
      });
    } else if (status === 429 && problemTypeOf(error) === RATE_LIMITED_PROBLEM) {
      // The API's rate limit (too many sign-ins from this network), not a lockout of the account.
      this.failure.set({
        title: 'Too many attempts',
        detail: problemMessage(error, 'Wait a minute, then try again.'),
      });
    } else if (status === 429) {
      // ASP.NET Identity lockout: 5 failed attempts lock the account for a few minutes.
      this.failure.set({
        title: 'Too many failed attempts',
        detail: `${problemMessage(
          error,
          'The account is locked for a few minutes.'
        )} Wait a few minutes, then try again.`,
      });
    } else {
      this.failure.set({
        title: 'We couldn’t sign you in',
        detail: problemMessage(error, 'Check your connection and try again.'),
      });
    }
  }

  /**
  A field's error shows once it is left or the form is submitted (which touches every field).
  */
  protected shows(field: 'email' | 'password'): boolean {
    const control = this.form.controls[field];
    return control.invalid && control.touched;
  }

  protected emailError(): string {
    const control = this.form.controls.email;
    return control.hasError('required')
      ? 'Enter your email address'
      : 'Enter a valid email address, like name@example.com';
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
      .complete(this.sessionApi.signIn({ email: email.trim(), password }), this.returnUrl())
      .pipe(finalize(() => this.submitting.set(false)))
      .subscribe({ error: (error: unknown) => this.failed(error) });
  }
}
