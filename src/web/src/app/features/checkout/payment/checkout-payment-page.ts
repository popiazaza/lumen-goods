import { LiveAnnouncer } from '@angular/cdk/a11y';
import { CurrencyPipe, DOCUMENT, DatePipe } from '@angular/common';
import {
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource, takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { MatButton } from '@angular/material/button';
import { MatError, MatFormField, MatHint, MatLabel, MatSuffix } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatRadioButton, MatRadioGroup } from '@angular/material/radio';
import { Router, RouterLink } from '@angular/router';
import {
  Subscription,
  catchError,
  exhaustMap,
  filter,
  finalize,
  forkJoin,
  interval,
  map,
  of,
  takeWhile,
  throwError,
  timer,
} from 'rxjs';
import { CheckoutApi } from '../../../core/api/checkout.api';
import {
  OrderDetailDto,
  OrderStatus,
  PaymentMethod,
  PaymentStatus,
  cardBrandName,
} from '../../../core/api/order.models';
import {
  ORDER_NOT_PAYABLE_PROBLEM,
  PAYMENT_IN_PROGRESS_PROBLEM,
  PayRequest,
  PaymentStatusDto,
  PaymentsApi,
  paymentInProgressOf,
  paymentProblemTypeOf,
} from '../../../core/api/payments.api';
import { httpStatusOf, problemMessage, problemOf } from '../../../core/api/problem-details';
import { WalletApi } from '../../../core/api/wallet.api';
import { CartStateService } from '../../../core/cart/cart-state.service';
import { CheckoutStore } from '../../../core/checkout/checkout-store';
import { ErrorSummary, FormProblem } from '../../../shared/forms/error-summary';
import { CheckoutSteps } from '../checkout-steps';
import {
  caretAfterFormat,
  cardNumberValidator,
  cvcLength,
  detectBrand,
  digitsOf,
  expiryValidator,
  formatCardNumber,
  formatExpiry,
  parseExpiry,
} from './card';

/** How often the page asks for the payment's status while the gateway works (BR-PAY-04). */
export const PAYMENT_POLL_INTERVAL_MS = 1500;
/** After this long, the waiting message says the payment is taking longer than usual. */
export const SLOW_PAYMENT_NOTICE_MS = 25_000;
/** Announce the code's remaining time once at this point, then at expiry (never every second). */
export const CODE_WARNING_MS = 60_000;

/** Statuses during which the charge is still running: keep polling. `error` is retried once by the server. */
const IN_PROGRESS: ReadonlySet<PaymentStatus> = new Set(['created', 'processing', 'error']);
const PAID_ORDER: ReadonlySet<OrderStatus> = new Set([
  'paid',
  'completed',
  'partially_refunded',
  'refunded',
]);

type Phase = 'loading' | 'load-error' | 'choose' | 'processing' | 'code' | 'paid' | 'closed';

/** The last attempt's outcome, shown above the form (and kept until the next attempt). */
interface Outcome {
  title: string;
  detail: string;
}

type CardField = 'number' | 'name' | 'expiry' | 'cvc';

const FIELD_IDS: Readonly<Record<CardField, string>> = {
  number: 'card-number',
  name: 'card-name',
  expiry: 'card-expiry',
  cvc: 'card-cvc',
};

/** The API's validation keys (`card.number`, `number`, `holderName`, ...) to the form's fields. */
function cardFieldOf(key: string): CardField | undefined {
  const name = key.toLowerCase().replace(/^card\./, '');
  if (name === 'number' || name === 'expiry' || name === 'cvc') {
    return name;
  }
  if (name === 'name' || name === 'holdername') {
    return 'name';
  }
  if (name === 'expirymonth' || name === 'expiryyear') {
    return 'expiry';
  }
  return undefined;
}

/**
 * Checkout step 4 (`/checkout/payment?order=`): pay the placed order by card or wallet.
 *
 * The page is a view of the server's payment, not of its own requests: on load (and after a
 * refresh) it reads the current payment and resumes where it was, polling while the gateway works,
 * so a slow card (BR-PAY-04) is never shown as failed and never paid twice. The card number and
 * CVC stay in this form only; the API keeps brand and last 4 digits.
 */
@Component({
  selector: 'app-checkout-payment-page',
  imports: [
    CurrencyPipe,
    DatePipe,
    ReactiveFormsModule,
    RouterLink,
    MatButton,
    MatError,
    MatFormField,
    MatHint,
    MatIcon,
    MatInput,
    MatLabel,
    MatProgressBar,
    MatRadioButton,
    MatRadioGroup,
    MatSuffix,
    CheckoutSteps,
    ErrorSummary,
  ],
  templateUrl: './checkout-payment-page.html',
  styleUrls: ['../checkout.scss', './checkout-payment-page.scss'],
})
export class CheckoutPaymentPage {
  /** `?order=` (bound by the router); otherwise the order this checkout placed. */
  readonly order = input<string>();

  private readonly checkoutApi = inject(CheckoutApi);
  private readonly payments = inject(PaymentsApi);
  private readonly walletApi = inject(WalletApi);
  private readonly store = inject(CheckoutStore);
  private readonly cart = inject(CartStateService);
  private readonly router = inject(Router);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);
  private readonly summary = viewChild(ErrorSummary);
  private readonly phaseHeading = viewChild<ElementRef<HTMLElement>>('phaseHeading');
  private readonly outcomeBanner = viewChild<ElementRef<HTMLElement>>('outcomeBanner');
  private polling?: Subscription;

  protected readonly orderNumber = computed(() => this.order() || this.store.orderNumber() || '');
  protected readonly orderDetail = signal<OrderDetailDto | null>(null);
  protected readonly payment = signal<PaymentStatusDto | null>(null);
  protected readonly phase = signal<Phase>('loading');
  protected readonly loadError = signal('');
  protected readonly closedReason = signal('');
  protected readonly outcome = signal<Outcome | null>(null);
  protected readonly submitting = signal(false);
  protected readonly problems = signal<readonly FormProblem[]>([]);
  protected readonly serverErrors = signal<Partial<Record<CardField, string>>>({});

  protected readonly total = computed(
    () => this.orderDetail()?.totals.total ?? this.payment()?.amount ?? null,
  );
  protected readonly holdExpires = computed(
    () => this.payment()?.holdExpiresUtc ?? this.orderDetail()?.holdExpiresUtc ?? null,
  );

  protected readonly wallet = rxResource({ stream: () => this.walletApi.get() });
  protected readonly walletBalance = computed(() =>
    this.wallet.hasValue() ? this.wallet.value().balance : null,
  );
  /** The wallet pays only if it covers the whole total (BR-PAY-06); no split with a card. */
  protected readonly walletCovers = computed(() => {
    const balance = this.walletBalance();
    const total = this.total();
    return balance !== null && total !== null && balance >= total;
  });

  /** Ticks every second for the code countdown and the waiting time. */
  protected readonly now = toSignal(interval(1000).pipe(map(() => Date.now())), {
    initialValue: Date.now(),
  });
  private readonly waitingSince = signal(0);
  protected readonly waitedSeconds = computed(() =>
    Math.max(0, Math.floor((this.now() - this.waitingSince()) / 1000)),
  );
  protected readonly slow = computed(
    () => this.now() - this.waitingSince() >= SLOW_PAYMENT_NOTICE_MS,
  );

  /** Server clock minus browser clock, from the last payment status: the code expires on the server's time. */
  private readonly clockOffset = signal(0);
  protected readonly codeRemainingMs = computed(() => {
    const expires = this.payment()?.oneTimeCodeExpiresUtc;
    return expires ? Math.max(0, Date.parse(expires) - (this.now() + this.clockOffset())) : 0;
  });
  protected readonly codeExpired = computed(
    () => this.phase() === 'code' && this.codeRemainingMs() <= 0,
  );
  protected readonly codeCountdown = computed(() => {
    const seconds = Math.ceil(this.codeRemainingMs() / 1000);
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  });
  protected readonly testCode = computed(() => this.payment()?.testOneTimeCode ?? '123456');

  /**
   * After a Failed payment the gateway could not settle, only that same card payment may be tried
   * again (BR-PAY-05): its idempotency key makes a second charge impossible. No wallet, no new card.
   */
  protected readonly samePaymentOnly = computed(
    () => this.phase() === 'choose' && this.payment()?.retryMode === 'same_payment_only',
  );
  protected readonly walletUsable = computed(() => this.walletCovers() && !this.samePaymentOnly());
  /** "A payment is already in progress": another tab or a double click started one first. */
  protected readonly inProgressNotice = signal('');

  protected readonly method = new FormControl<PaymentMethod>('card', { nonNullable: true });
  protected readonly chosenMethod = toSignal(this.method.valueChanges, {
    initialValue: this.method.value,
  });

  protected readonly cardForm = new FormGroup({
    number: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, cardNumberValidator],
    }),
    name: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(100)],
    }),
    expiry: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, expiryValidator()],
    }),
    cvc: new FormControl('', {
      nonNullable: true,
      validators: [
        Validators.required,
        (control: AbstractControl<string>) => this.checkCvc(control),
      ],
    }),
  });
  private readonly cardNumber = toSignal(this.cardForm.controls.number.valueChanges, {
    initialValue: '',
  });
  protected readonly brand = computed(() => detectBrand(digitsOf(this.cardNumber())));
  protected readonly brandName = computed(() => {
    const brand = this.brand();
    return brand && brand !== 'other' ? cardBrandName(brand) : '';
  });
  protected readonly cvcDigits = computed(() => cvcLength(this.brand()));

  protected readonly codeForm = new FormGroup({
    code: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, (control: AbstractControl<string>) => validCode(control)],
    }),
  });
  protected readonly codeError = signal('');
  private announcedWarning = false;
  private announcedExpiry = false;

  constructor() {
    effect(() => {
      const number = this.orderNumber();
      untracked(() => this.load(number));
    });

    // The CVC's length depends on the brand.
    this.cardForm.controls.number.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.cardForm.controls.cvc.updateValueAndValidity({ emitEvent: false }));

    // Countdown: one announcement at a minute left and one at expiry, never every second.
    effect(() => {
      if (this.phase() !== 'code') {
        return;
      }
      const remaining = this.codeRemainingMs();
      untracked(() => {
        if (remaining <= 0 && !this.announcedExpiry) {
          this.announcedExpiry = true;
          void this.announcer.announce('The code has expired.', 'assertive');
        } else if (remaining > 0 && remaining <= CODE_WARNING_MS && !this.announcedWarning) {
          this.announcedWarning = true;
          void this.announcer.announce('The code expires in 1 minute.', 'polite');
        }
      });
    });
  }

  // ---- Loading and following the payment ------------------------------------------------------

  protected retryLoad(): void {
    this.load(this.orderNumber());
  }

  private load(number: string): void {
    this.stopPolling();
    if (!number) {
      this.close('There is no order to pay. Review your order and place it first.');
      return;
    }
    this.phase.set('loading');
    forkJoin({
      order: this.checkoutApi.order(number),
      payment: this.payments
        .current(number)
        .pipe(
          catchError((error: unknown) =>
            httpStatusOf(error) === 404 ? of(null) : throwError(() => error),
          ),
        ),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ order, payment }) => {
          this.orderDetail.set(order);
          this.show(payment, order);
        },
        error: (error: unknown) => {
          if (httpStatusOf(error) === 404) {
            this.close(`We couldn’t find order ${number} in your account.`);
            return;
          }
          this.loadError.set(
            problemMessage(
              error,
              'We couldn’t load your order. Check your connection and try again.',
            ),
          );
          this.phase.set('load-error');
        },
      });
  }

  /** Moves the page to whatever the server says the payment is doing now. */
  private show(payment: PaymentStatusDto | null, order = this.orderDetail()): void {
    this.payment.set(payment);
    const serverTime = Date.parse(payment?.serverTimeUtc ?? '');
    if (!Number.isNaN(serverTime)) {
      this.clockOffset.set(serverTime - Date.now());
    }
    const orderStatus = payment?.orderStatus ?? order?.status;
    if (payment?.status === 'succeeded' || (orderStatus && PAID_ORDER.has(orderStatus))) {
      this.paid();
      return;
    }
    if (orderStatus === 'expired') {
      this.close(
        payment && payment.attemptsLeft === 0
          ? 'This order ran out of payment attempts, so it was cancelled. You have not been charged.'
          : 'The 15-minute hold on this order’s prices ended before it was paid, so it was cancelled. You have not been charged.',
      );
      return;
    }
    if (!payment) {
      this.enter('choose');
      return;
    }
    if (IN_PROGRESS.has(payment.status)) {
      this.enter('processing');
      this.startPolling(payment.orderNumber);
      return;
    }
    if (payment.status === 'requires_action') {
      if (payment.needsOneTimeCode) {
        this.codeForm.reset();
        this.codeError.set('');
        this.announcedWarning = false;
        this.announcedExpiry = false;
        this.enter('code');
      } else {
        this.enter('processing');
        this.startPolling(payment.orderNumber);
      }
      return;
    }
    // declined | failed | expired: tell the shopper, keep what they typed, let them try again.
    this.inProgressNotice.set('');
    this.outcome.set(outcomeOf(payment));
    if (payment.retryMode === 'none') {
      this.close(
        payment.attemptsLeft === 0
          ? 'This order ran out of payment attempts, so it was cancelled. You have not been charged.'
          : 'This order can no longer be paid. You have not been charged.',
      );
      return;
    }
    if (payment.retryMode === 'same_payment_only') {
      this.method.setValue('card');
    }
    this.enter('choose');
    afterNextRender(
      { write: () => this.outcomeBanner()?.nativeElement.focus() },
      { injector: this.injector },
    );
  }

  private enter(phase: Phase): void {
    const previous = this.phase();
    this.phase.set(phase);
    if (phase === 'processing' && previous !== 'processing') {
      this.waitingSince.set(Date.now());
    }
    // A new state replaces the content: start reading at its heading (not on the first load).
    if (previous !== phase && previous !== 'loading' && phase !== 'choose') {
      afterNextRender(
        { write: () => this.phaseHeading()?.nativeElement.focus() },
        { injector: this.injector },
      );
    }
  }

  /** Polls `GET .../payments/current` until the payment leaves created/processing/error. */
  private startPolling(number: string): void {
    this.stopPolling();
    this.polling = timer(PAYMENT_POLL_INTERVAL_MS, PAYMENT_POLL_INTERVAL_MS)
      .pipe(
        // A dropped request is not an answer: keep waiting, never show "failed" for it.
        exhaustMap(() => this.payments.current(number).pipe(catchError(() => of(null)))),
        filter((payment): payment is PaymentStatusDto => payment !== null),
        takeWhile((payment) => IN_PROGRESS.has(payment.status), true),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((payment) => {
        if (IN_PROGRESS.has(payment.status)) {
          this.payment.set(payment);
        } else {
          this.show(payment);
        }
      });
  }

  private stopPolling(): void {
    this.polling?.unsubscribe();
    this.polling = undefined;
  }

  private paid(): void {
    this.stopPolling();
    this.phase.set('paid');
    const number = this.payment()?.orderNumber ?? this.orderDetail()?.number ?? this.orderNumber();
    this.store.reset();
    this.cart.reload().subscribe({ error: () => undefined });
    void this.announcer.announce('Payment confirmed.', 'polite');
    // Replace this URL so Back cannot show a payable form for a paid order.
    void this.router.navigate(['/orders', number, 'confirmation'], { replaceUrl: true });
  }

  private close(reason: string): void {
    this.stopPolling();
    this.closedReason.set(reason);
    this.enter('closed');
    if (this.orderNumber() && this.orderNumber() === this.store.orderNumber()) {
      // The next checkout must create a new order.
      this.store.reset();
    }
  }

  // ---- Paying ---------------------------------------------------------------------------------

  protected pay(): void {
    if (this.submitting() || this.phase() !== 'choose') {
      return;
    }
    const method = this.method.value;
    let request: PayRequest;
    if (method === 'wallet') {
      if (!this.walletUsable()) {
        return;
      }
      request = { method: 'wallet', card: null };
    } else {
      this.serverErrors.set({});
      if (this.cardForm.invalid) {
        this.cardForm.markAllAsTouched();
        this.showProblems();
        return;
      }
      const value = this.cardForm.getRawValue();
      const expiry = parseExpiry(value.expiry);
      if (!expiry) {
        return;
      }
      request = {
        method: 'card',
        card: {
          number: digitsOf(value.number),
          name: value.name.trim(),
          expiryMonth: expiry.month,
          expiryYear: expiry.year,
          cvc: value.cvc.trim(),
        },
      };
    }
    this.problems.set([]);
    this.outcome.set(null);
    this.inProgressNotice.set('');
    this.submitting.set(true);
    const number = this.orderNumber();
    this.payments
      .pay(number, request)
      .pipe(
        finalize(() => this.submitting.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (payment) => this.show(payment),
        error: (error: unknown) => this.payFailed(number, error),
      });
  }

  /** Focuses the form again after a decline; everything typed is still there. */
  protected tryAgain(): void {
    this.outcome.set(null);
    this.enter('choose');
    afterNextRender(
      {
        write: () => {
          const id = this.method.value === 'card' ? FIELD_IDS.number : 'pay-button';
          this.document.getElementById(id)?.focus();
        },
      },
      { injector: this.injector },
    );
  }

  private payFailed(number: string, error: unknown): void {
    if (this.followRunningPayment(error)) {
      return;
    }
    const type = paymentProblemTypeOf(error);
    if (type === PAYMENT_IN_PROGRESS_PROBLEM || type === ORDER_NOT_PAYABLE_PROBLEM) {
      this.load(number);
      return;
    }
    const errors = problemOf(error)?.errors;
    if (httpStatusOf(error) === 400 && errors) {
      const mapped: Partial<Record<CardField, string>> = {};
      for (const [key, messages] of Object.entries(errors)) {
        const field = cardFieldOf(key);
        if (field && messages.length) {
          mapped[field] = messages.join(' ');
          this.cardForm.controls[field].setErrors({ server: true });
        }
      }
      if (Object.keys(mapped).length > 0) {
        this.serverErrors.set(mapped);
        this.cardForm.markAllAsTouched();
        this.showProblems();
        return;
      }
    }
    this.outcome.set({
      title: 'We couldn’t start the payment',
      detail: `${problemMessage(error, 'Check your connection and try again.')} You have not been charged.`,
    });
    afterNextRender(
      { write: () => this.outcomeBanner()?.nativeElement.focus() },
      { injector: this.injector },
    );
  }

  /**
   * A 409 payment-in-progress carries the running payment (BR-PAY-01): show it (processing or the
   * code step) with a note, instead of an error. Nothing new was charged.
   */
  private followRunningPayment(error: unknown): boolean {
    const running = paymentInProgressOf(error);
    if (!running) {
      return false;
    }
    const message = running.title || 'A payment is already in progress';
    this.inProgressNotice.set(`${message}. We’re showing it here; you won’t be charged twice.`);
    void this.announcer.announce(message, 'polite');
    this.show(running.payment);
    return true;
  }

  // ---- One-time code --------------------------------------------------------------------------

  protected submitCode(): void {
    if (this.submitting() || this.codeExpired()) {
      return;
    }
    const control = this.codeForm.controls.code;
    if (control.invalid) {
      control.markAsTouched();
      this.codeError.set(
        control.hasError('required') ? 'Enter the 6-digit code' : 'The code must be 6 digits',
      );
      return;
    }
    this.codeError.set('');
    this.submitting.set(true);
    this.payments
      .submitOneTimeCode(this.orderNumber(), digitsOf(control.value))
      .pipe(
        finalize(() => this.submitting.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (payment) => this.show(payment),
        error: (error: unknown) => {
          if (this.followRunningPayment(error)) {
            return;
          }
          if (paymentProblemTypeOf(error) || httpStatusOf(error) === 409) {
            this.load(this.orderNumber());
            return;
          }
          this.codeError.set(problemMessage(error, 'We couldn’t check the code. Try again.'));
        },
      });
  }

  /** After the code ran out: back to the form; the next attempt sends a new code. */
  protected codeExpiredTryAgain(): void {
    this.outcome.set({
      title: 'The code has expired',
      detail: 'You have not been charged. Pay again and we will send a new code.',
    });
    this.payments
      .current(this.orderNumber())
      .pipe(
        catchError(() => of(null)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((payment) => {
        if (payment && payment.status !== 'requires_action') {
          this.show(payment);
        } else {
          this.enter('choose');
        }
      });
  }

  // ---- Card form ------------------------------------------------------------------------------

  /** Spaces the number as it is typed (4-4-4-4, or 4-6-5 for Amex) and keeps the caret in place. */
  protected formatNumber(event: Event): void {
    const input = event.target as HTMLInputElement;
    const raw = input.value;
    const caret = input.selectionStart ?? raw.length;
    const formatted = formatCardNumber(raw);
    if (formatted !== raw) {
      this.cardForm.controls.number.setValue(formatted);
      const position = caretAfterFormat(raw, caret, formatted);
      input.setSelectionRange?.(position, position);
    }
  }

  protected formatExpiryInput(event: Event): void {
    const input = event.target as HTMLInputElement;
    const deleting = (event as InputEvent).inputType?.startsWith('delete') ?? false;
    const formatted = formatExpiry(input.value, deleting);
    if (formatted !== input.value) {
      this.cardForm.controls.expiry.setValue(formatted);
    }
  }

  protected errorFor(field: CardField): string {
    const control = this.cardForm.controls[field];
    const server = this.serverErrors()[field];
    if (control.hasError('server') && server) {
      return server;
    }
    switch (field) {
      case 'number':
        return control.hasError('required')
          ? 'Enter your card number'
          : 'Enter a valid card number: check it for typos';
      case 'name':
        return control.hasError('required')
          ? 'Enter the name on the card'
          : 'The name must be 100 characters or fewer';
      case 'expiry':
        if (control.hasError('required')) {
          return 'Enter the expiry date';
        }
        return control.hasError('expired')
          ? 'This card has expired'
          : 'Enter the expiry date as MM/YY, like 08/29';
      case 'cvc':
        return control.hasError('required')
          ? 'Enter the security code'
          : `Enter the ${this.cvcDigits()}-digit security code`;
    }
  }

  private checkCvc(control: AbstractControl<string>): ValidationErrors | null {
    const value = control.value?.trim() ?? '';
    if (!value) {
      return null;
    }
    const length = cvcLength(detectBrand(digitsOf(this.cardForm?.controls.number.value)));
    return new RegExp(`^\\d{${length}}$`).test(value) ? null : { cvc: true };
  }

  private showProblems(): void {
    const fields: CardField[] = ['number', 'name', 'expiry', 'cvc'];
    this.problems.set(
      fields
        .filter((field) => this.cardForm.controls[field].invalid)
        .map((field) => ({ fieldId: FIELD_IDS[field], message: this.errorFor(field) })),
    );
    afterNextRender({ write: () => this.summary()?.focus() }, { injector: this.injector });
  }
}

/** 4 to 8 digits once spaces and dashes are taken out (a pasted "123 456" is fine). */
function validCode(control: AbstractControl<string>): ValidationErrors | null {
  const raw = control.value ?? '';
  if (!raw.trim()) {
    return null;
  }
  return /^[\d\s-]+$/.test(raw) && /^\d{4,8}$/.test(digitsOf(raw)) ? null : { code: true };
}

/** What to tell the shopper after an attempt that did not go through (BR-PAY-07 wording from the API). */
function outcomeOf(payment: PaymentStatusDto): Outcome {
  const attempts =
    payment.attemptsLeft === 1 ? '1 attempt left' : `${payment.attemptsLeft} attempts left`;
  switch (payment.status) {
    case 'failed':
      return {
        title: 'We couldn’t confirm your payment',
        detail:
          payment.retryMode === 'same_payment_only'
            ? `The card provider did not answer, so we can’t tell yet whether the card was charged. Enter the same card and try again: it continues the same payment, so you can’t be charged twice. ${attempts}.`
            : `The card provider did not answer. You have not been charged twice: trying again continues the same payment. ${attempts}.`,
      };
    case 'expired':
      return {
        title: 'The code has expired',
        detail: `You have not been charged. Pay again and we will send a new code. ${attempts}.`,
      };
    default:
      return {
        title: payment.declineMessage ?? 'Your card was declined',
        detail: `Payment declined. You have not been charged. ${
          payment.method === 'card'
            ? 'Try another card or pay from your wallet'
            : 'Try paying by card'
        }. ${attempts}.`,
      };
  }
}
