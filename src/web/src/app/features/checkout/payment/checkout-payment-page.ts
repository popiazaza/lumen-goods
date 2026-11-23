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
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatRadioButton, MatRadioGroup } from '@angular/material/radio';
import { Router, RouterLink } from '@angular/router';
import {
  Observable,
  Subscription,
  catchError,
  exhaustMap,
  filter,
  finalize,
  forkJoin,
  interval,
  map,
  noop,
  of,
  takeWhile,
  throwError,
  timer,
} from 'rxjs';
import {
  OrderDetailDto,
  OrderStatus,
  PaymentMethod,
  PaymentStatus,
} from '../../../core/api/order.models';
import { OrdersApi } from '../../../core/api/orders.api';
import {
  ORDER_NOT_PAYABLE_PROBLEM,
  PAYMENT_IN_PROGRESS_PROBLEM,
  PayRequest,
  PaymentStatusDto,
  PaymentsApi,
  SAME_PAYMENT_ONLY_PROBLEM,
  paymentInProgressOf,
} from '../../../core/api/payments.api';
import {
  httpStatusOf,
  problemMessage,
  problemOf,
  problemTypeOf,
  validationErrorsOf,
} from '../../../core/api/problem-details';
import { WalletApi } from '../../../core/api/wallet.api';
import { CartStateService } from '../../../core/cart/cart-state.service';
import { CheckoutStore } from '../../../core/checkout/checkout-store';
import { signInQueryParams } from '../../../core/routing/return-url';
import { ErrorSummary, FormProblem } from '../../../shared/forms/error-summary';
import { CheckoutSteps } from '../checkout-steps';
import {
  cardNumberValidator,
  cvcLength,
  detectBrand,
  digitsOf,
  expiryValidator,
  parseExpiry,
} from './card';
import { CardDetails } from './card-details';
import {
  CARD_FIELDS,
  CARD_FIELD_IDS,
  CardField,
  CardFieldMessages,
  CardForm,
  cardFieldError,
} from './card-form';
import { OneTimeCodeForm } from './one-time-code-form';
import { WalletNote } from './wallet-note';

/**
How often the page asks for the payment's status while the gateway works (BR-PAY-04).
*/
export const PAYMENT_POLL_INTERVAL_MS = 1500;
/**
After this long, the waiting message says the payment is taking longer than usual.
*/
export const SLOW_PAYMENT_NOTICE_MS = 25_000;
/**
Announce the code's remaining time once at this point, then at expiry (never every second).
*/
export const CODE_WARNING_MS = 60_000;
/**
 * The range of code lengths the server may be configured with (`Payments:OneTimeCode`, 4 to 8 digits).
 * The API exposes the length only through the test code it shows: with one, the page asks for exactly
 * that many digits (a wrong length would only be refused by the gateway and use up a payment attempt);
 * without one, any length in this range is sent, so a configured length is never blocked here.
 */
export const CODE_LENGTH_RANGE = { min: 4, max: 8 } as const;

/**
Statuses during which the charge is still running: keep polling. `error` is retried once by the server.
*/
const IN_PROGRESS: ReadonlySet<PaymentStatus> = /* @__PURE__ */ new Set([
  'created',
  'processing',
  'error',
]);

/**
 * A Failed payment that nothing can try again: the card provider has not said whether it charged
 * the card, so the API keeps the order awaiting payment and settles it later as Succeeded or
 * Declined (BR-PAY-05). Until then the page can only wait for that answer.
 */
const isAwaitingSettlement = (payment: PaymentStatusDto): boolean =>
  payment.status === 'failed' && payment.retryMode === 'none';

/**
The server will move this payment on by itself: keep polling.
*/
const isStillChanging = (payment: PaymentStatusDto): boolean =>
  IN_PROGRESS.has(payment.status) || isAwaitingSettlement(payment);

const PAID_ORDER: ReadonlySet<OrderStatus> = /* @__PURE__ */ new Set([
  'paid',
  'completed',
  'partially_refunded',
  'refunded',
]);

type Phase =
  'loading' | 'load-error' | 'signed-out' | 'choose' | 'processing' | 'code' | 'paid' | 'closed';

/**
The last attempt's outcome, shown above the form (and kept until the next attempt).
*/
interface Outcome {
  title: string;
  detail: string;
  /**
  The shopper already chose Try again (the expired code's), so the banner only reports.
  */
  hidesTryAgain?: boolean;
}

/**
The API's validation keys (`card.number`, `number`, `holderName`, ...) to the form's fields.
*/
const CARD_FIELD_KEYS: ReadonlyMap<string, CardField> = /* @__PURE__ */ new Map([
  ['number', 'number'],
  ['name', 'name'],
  ['holdername', 'name'],
  ['expiry', 'expiry'],
  ['expirymonth', 'expiry'],
  ['expiryyear', 'expiry'],
  ['cvc', 'cvc'],
]);

const cardFieldOf = (key: string): CardField | undefined =>
  CARD_FIELD_KEYS.get(key.toLowerCase().replace(/^card\./u, ''));

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
    MatIcon,
    MatProgressBar,
    MatRadioButton,
    MatRadioGroup,
    CardDetails,
    CheckoutSteps,
    ErrorSummary,
    OneTimeCodeForm,
    WalletNote,
  ],
  templateUrl: './checkout-payment-page.html',
  styleUrls: ['../checkout.scss', './checkout-payment-page.scss'],
})
export class CheckoutPaymentPage {
  private readonly orders = inject(OrdersApi);

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

  private polling: Subscription | null = null;

  /**
  The order and payment read under way: a newer load replaces it, so an older answer never shows.
  */
  private loading: Subscription | null = null;

  private readonly waitingSince = signal(0);

  /**
  Server clock minus browser clock, from the last payment status: the code expires on the server's time.
  */
  private readonly clockOffset = signal(0);

  private announcedWarning = false;

  private announcedExpiry = false;

  /**
   * The wait began with this page's own Pay, so the counter is the real time since the charge started. After a refresh or another tab's payment the status says nothing about when it started, so the page says "Still confirming…" instead of counting from 0.
   */
  private readonly waitCounted = signal(false);

  /**
  `?order=` (bound by the router); otherwise the order this checkout placed.
  */
  readonly order = input<string>();

  /**
   * The order this page pays. Without `?order=`, the checkout's order as the page opened (read
   * untracked): paying or closing resets the checkout, and the page must go on showing that order
   * and how it ended, not reload with no order ("This order can’t be paid" after paying).
   */
  protected readonly orderNumber = computed(
    () => this.order() || untracked(() => this.store.orderNumber()) || ''
  );

  protected readonly orderDetail = signal<OrderDetailDto | null>(null);

  protected readonly payment = signal<PaymentStatusDto | null>(null);

  protected readonly phase = signal<Phase>('loading');

  protected readonly loadError = signal('');

  protected readonly closedReason = signal('');

  protected readonly closedHeading = signal('');

  /**
  Sign in again and come back to this order's payment (the `?order=` survives the store).
  */
  protected readonly signInParams = computed(() =>
    signInQueryParams(
      this.router.serializeUrl(
        this.router.createUrlTree(['/checkout/payment'], {
          queryParams: { order: this.orderNumber() },
        })
      )
    )
  );

  protected readonly outcome = signal<Outcome | null>(null);

  protected readonly submitting = signal(false);

  protected readonly problems = signal<readonly FormProblem[]>([]);

  protected readonly serverErrors = signal<CardFieldMessages>({});

  protected readonly total = computed(
    () => this.orderDetail()?.totals.total ?? this.payment()?.amount ?? null
  );

  protected readonly holdExpires = computed(
    () => this.payment()?.holdExpiresUtc ?? this.orderDetail()?.holdExpiresUtc ?? null
  );

  protected readonly wallet = rxResource({ stream: () => this.walletApi.get() });

  protected readonly walletBalance = computed(() =>
    this.wallet.hasValue() ? this.wallet.value().balance : null
  );

  /**
  The wallet pays only if it covers the whole total (BR-PAY-06); no split with a card.
  */
  protected readonly walletCovers = computed(() => {
    const balance = this.walletBalance();
    const total = this.total();
    return balance !== null && total !== null && balance >= total;
  });

  /**
  Ticks every second for the code countdown and the waiting time.
  */
  protected readonly now = toSignal(interval(1000).pipe(map(() => Date.now())), {
    initialValue: Date.now(),
  });

  protected readonly waitedSeconds = computed(() =>
    Math.max(0, Math.floor((this.now() - this.waitingSince()) / 1000))
  );

  protected readonly waitText = computed(() =>
    this.waitCounted() ? `Waiting ${this.waitedSeconds()} s` : 'Still confirming…'
  );

  /**
  The expired code's Try again is reading the payment: one click starts one new attempt.
  */
  protected readonly restartingCode = signal(false);

  protected readonly slow = computed(
    () => this.now() - this.waitingSince() >= SLOW_PAYMENT_NOTICE_MS
  );

  /**
  Waiting for the card provider to say whether a Failed charge went through (BR-PAY-05).
  */
  protected readonly unsettled = computed(() => this.payment()?.status === 'failed');

  protected readonly codeRemainingMs = computed(() => {
    const expires = this.payment()?.oneTimeCodeExpiresUtc;
    return expires ? Math.max(0, Date.parse(expires) - (this.now() + this.clockOffset())) : 0;
  });

  protected readonly codeExpired = computed(
    () => this.phase() === 'code' && this.codeRemainingMs() <= 0
  );

  protected readonly codeCountdown = computed(() => {
    const seconds = Math.ceil(this.codeRemainingMs() / 1000);
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  });

  /**
  The mock gateway's code, which the API sends because no text message is; never guessed here.
  */
  protected readonly testCode = computed(() => this.payment()?.testOneTimeCode ?? null);

  /**
  How many digits the code has, when the API shows it (see `CODE_LENGTH_RANGE`); the messages and the check agree.
  */
  protected readonly codeLength = computed(() => this.testCode()?.length ?? null);

  /**
   * After a Failed payment the gateway could not settle, only that same card payment may be tried
   * again (BR-PAY-05): its idempotency key makes a second charge impossible. No wallet, no new card.
   */
  protected readonly samePaymentOnly = computed(
    () => this.phase() === 'choose' && this.payment()?.retryMode === 'same_payment_only'
  );

  protected readonly walletUsable = computed(() => this.walletCovers() && !this.samePaymentOnly());

  /**
  "A payment is already in progress": another tab or a double click started one first.
  */
  protected readonly inProgressNotice = signal('');

  protected readonly method = new FormControl<PaymentMethod>('card', { nonNullable: true });

  protected readonly chosenMethod = toSignal(this.method.valueChanges, {
    initialValue: this.method.value,
  });

  /**
  The card's timing is not the wallet's: the wallet pays at once.
  */
  protected readonly payNote = computed(() =>
    this.chosenMethod() === 'card'
      ? 'Card payments usually take a few seconds but can take up to 20. You pay exactly the order total.'
      : 'You pay exactly the order total.'
  );

  protected readonly cardForm: CardForm = new FormGroup({
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

  protected readonly codeForm = new FormGroup({
    code: new FormControl('', {
      nonNullable: true,
      validators: [
        Validators.required,
        (control: AbstractControl<string>) => validCode(control, untracked(this.codeLength)),
      ],
    }),
  });

  protected readonly codeError = signal('');

  constructor() {
    effect(() => {
      const number = this.orderNumber();
      untracked(() => this.load(number));
    });

    // The CVC's length depends on the brand.
    this.cardForm.controls.number.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.cardForm.controls.cvc.updateValueAndValidity({ emitEvent: false }));

    // The error summary lists only what is still wrong: a corrected field leaves it at once, and
    // the summary goes when none is left. It never grows or rewords an item before the next Pay, so
    // its alert region does not interrupt someone typing.
    this.cardForm.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.refreshProblems());

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

  /**
  Reads the order and its current payment and shows them; `then` runs once they are shown.
  */
  private load(number: string, then?: () => void): void {
    this.stopPolling();
    this.loading?.unsubscribe();
    if (!number) {
      this.close('There is no order to pay. Review your order and place it first.');
      return;
    }
    this.phase.set('loading');
    // The current payment is what the page follows (a refresh during a slow card resumes from
    // it). The order adds its total before the first payment: right after Place order that is
    // the answer it gave; otherwise the order is read, and its 404 only matters if the payment
    // read cannot find the order either.
    const placed = this.store.placedOrder();
    this.loading = forkJoin({
      order: placed?.number === number ? of(placed) : notFoundAsNull(this.orders.get(number)),
      payment: notFoundAsNull(this.payments.current(number)),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ order, payment }) => {
          if (!order && !payment) {
            this.notFound(number);
            return;
          }
          this.orderDetail.set(order);
          this.show(payment, order);
          then?.();
        },
        error: (error: unknown) => {
          if (httpStatusOf(error) === 401) {
            this.signedOut();
            return;
          }
          this.loadFailed(error);
        },
      });
  }

  private loadFailed(error: unknown): void {
    this.stopPolling();
    this.loadError.set(
      problemMessage(error, 'We couldn’t load your order. Check your connection and try again.')
    );
    this.phase.set('load-error');
  }

  private notFound(number: string): void {
    this.close(`We couldn’t find order ${number} in your account.`, 'Order not found');
  }

  /**
   * The session ended (401): the page can no longer read the payment, which carries on without it.
   * Signing in again comes back here, with the order in the URL.
   */
  private signedOut(): void {
    this.stopPolling();
    this.enter('signed-out');
  }

  /**
  A status read the server refused: stop, since asking again gets the same answer.
  */
  private pollFailed(error: unknown): void {
    const status = httpStatusOf(error);
    if (status === 401) {
      this.signedOut();
    } else if (status === 404) {
      this.notFound(this.orderNumber());
    } else {
      this.loadFailed(error);
    }
  }

  /**
  Moves the page to whatever the server says the payment is doing now.
  */
  private show(payment: PaymentStatusDto | null, order = this.orderDetail()): void {
    this.payment.set(payment);
    this.syncClock(payment);
    const orderStatus = payment?.orderStatus ?? order?.status;
    if (payment?.status === 'succeeded' || (orderStatus && PAID_ORDER.has(orderStatus))) {
      this.paid();
      return;
    }
    if (orderStatus === 'expired') {
      this.close(
        payment?.attemptsLeft === 0
          ? 'This order ran out of payment attempts, so it was cancelled. You have not been charged.'
          : 'The 15-minute hold on this order’s prices ended before it was paid, so it was cancelled. You have not been charged.'
      );
      return;
    }
    if (!payment) {
      this.enter('choose');
      return;
    }
    // A Failed payment nothing can retry is never "cancelled" or "not charged": the card may have
    // been charged, so the page waits with the shopper for the card provider's answer.
    if (isStillChanging(payment)) {
      this.enter('processing');
      this.startPolling(payment.orderNumber);
      return;
    }
    if (payment.status === 'requires_action') {
      this.showRequiredAction(payment);
      return;
    }
    this.showOutcome(payment);
  }

  /**
  Server clock minus browser clock, from the payment's `serverTimeUtc` when it has one.
  */
  private syncClock(payment: PaymentStatusDto | null): void {
    const serverTime = Date.parse(payment?.serverTimeUtc ?? '');
    if (!Number.isNaN(serverTime)) {
      this.clockOffset.set(serverTime - Date.now());
    }
  }

  /**
  `requires_action`: ask for the one-time code when the payment needs one, otherwise keep polling.
  */
  private showRequiredAction(payment: PaymentStatusDto): void {
    if (!payment.needsOneTimeCode) {
      this.enter('processing');
      this.startPolling(payment.orderNumber);
      return;
    }
    this.codeForm.reset();
    this.codeError.set('');
    this.announcedWarning = false;
    this.announcedExpiry = false;
    this.enter('code');
  }

  /**
  Declined, expired or failed with the same card: tell the shopper, keep what they typed, and let them try again when the server allows it.
  */
  private showOutcome(payment: PaymentStatusDto): void {
    this.inProgressNotice.set('');
    this.outcome.set(outcomeOf(payment, this.walletCovers()));
    // The security code is asked again for every attempt, as card forms do; the rest stays.
    this.cardForm.controls.cvc.reset();
    if (payment.method === 'wallet') {
      // A declined wallet payment means the balance moved since it was read (another tab), so
      // show the balance now and offer the card, which is what the decline message suggests.
      this.wallet.reload();
      this.method.setValue('card');
    }
    if (payment.retryMode === 'none') {
      this.close(
        payment.attemptsLeft === 0
          ? 'This order ran out of payment attempts, so it was cancelled. You have not been charged.'
          : 'This order can no longer be paid. You have not been charged.'
      );
      return;
    }
    if (payment.retryMode === 'same_payment_only') {
      this.method.setValue('card');
    }
    this.enter('choose');
    this.focusOutcome();
  }

  private enter(phase: Phase): void {
    const previous = this.phase();
    this.phase.set(phase);
    if (phase === 'processing' && previous !== 'processing') {
      this.waitingSince.set(Date.now());
      this.waitCounted.set(false);
    }
    // A new state replaces the content: start reading at its heading (not on the first load).
    if (previous !== phase && previous !== 'loading' && phase !== 'choose') {
      afterNextRender(
        { write: () => this.phaseHeading()?.nativeElement.focus() },
        { injector: this.injector }
      );
    }
  }

  /**
   * Polls `GET .../payments/current` until the payment is final or waits for the shopper. The
   * read also asks the API to settle the payment if it is due (BR-PAY-05).
   */
  private startPolling(number: string): void {
    this.stopPolling();
    this.polling = timer(PAYMENT_POLL_INTERVAL_MS, PAYMENT_POLL_INTERVAL_MS)
      .pipe(
        // A dropped request or a server error is not an answer: keep waiting, never show "failed"
        // for it. Any other error (401, 403, 404) would answer the same every time: stop.
        exhaustMap(() =>
          this.payments.current(number).pipe(
            // eslint-disable-next-line promise/prefer-await-to-callbacks -- RxJS catchError operator, not a Node-style callback
            catchError((error: unknown) =>
              isTransient(error) ? of(null) : throwError(() => error)
            )
          )
        ),
        filter((payment): payment is PaymentStatusDto => payment !== null),
        takeWhile(isStillChanging, true),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe({
        next: (payment) => {
          if (isStillChanging(payment)) {
            this.payment.set(payment);
          } else {
            this.show(payment);
          }
        },
        error: (error: unknown) => this.pollFailed(error),
      });
  }

  private stopPolling(): void {
    this.polling?.unsubscribe();
    this.polling = null;
  }

  private paid(): void {
    this.stopPolling();
    this.phase.set('paid');
    const number = this.payment()?.orderNumber ?? this.orderDetail()?.number ?? this.orderNumber();
    this.endCheckoutOf(number);
    this.cart.reload().subscribe({ error: noop });
    void this.announcer.announce('Payment confirmed.', 'polite');
    // Replace this URL so Back cannot show a payable form for a paid order.
    void this.router.navigate(['/orders', number, 'confirmation'], { replaceUrl: true });
  }

  private close(reason: string, heading = 'This order can’t be paid'): void {
    this.stopPolling();
    this.closedReason.set(reason);
    this.closedHeading.set(heading);
    this.enter('closed');
    this.endCheckoutOf(this.orderNumber());
  }

  /**
   * The checkout that placed this order is over, so the next one creates a new order. Paying or
   * closing another order through `?order=` leaves a checkout in progress alone.
   */
  private endCheckoutOf(number: string): void {
    if (number && number === this.store.orderNumber()) {
      this.store.reset();
    }
  }

  private walletUnusable(): void {
    let detail: string;
    if (this.samePaymentOnly()) {
      detail = 'Only the same card payment can be tried again, so pay with that card.';
    } else if (this.walletBalance() === null) {
      detail = 'We couldn’t load your wallet balance, so pay by card.';
    } else {
      detail = 'Your wallet balance doesn’t cover the order total, so pay by card.';
    }
    this.method.setValue('card');
    this.outcome.set({ title: 'The wallet can’t pay this order', detail });
    this.focusOutcome();
  }

  private payFailed(number: string, error: unknown): void {
    if (this.followRunningPayment(error)) {
      return;
    }
    const type = problemTypeOf(error);
    if (type === PAYMENT_IN_PROGRESS_PROBLEM || type === ORDER_NOT_PAYABLE_PROBLEM) {
      this.load(number);
      return;
    }
    if (type === SAME_PAYMENT_ONLY_PROBLEM) {
      // Not "you have not been charged": the gateway cannot tell yet whether the failed payment
      // did, which is exactly why only that same card may continue it.
      this.outcome.set({
        title: problemOf(error)?.title ?? 'Try the same card again',
        detail: problemMessage(error, 'Enter the same card as before to try that payment again.'),
      });
      this.method.setValue('card');
      this.focusOutcome();
      return;
    }
    const errors = validationErrorsOf(error);
    if (Object.keys(errors).length > 0) {
      const mapped: CardFieldMessages = {};
      for (const [key, messages] of Object.entries(errors)) {
        const field = cardFieldOf(key);
        if (!(field && messages.length)) {
          continue;
        }

        mapped[field] = messages.join(' ');
        this.cardForm.controls[field].setErrors({ server: true });
      }
      if (Object.keys(mapped).length > 0) {
        this.serverErrors.set(mapped);
        this.cardForm.markAllAsTouched();
        this.showProblems();
        return;
      }
    }
    if (httpStatusOf(error) === undefined || isTransient(error)) {
      // The request may have reached the API, and a charge started, before the answer was lost (a
      // dropped connection, or a 502/504 from the proxy): only the server knows. Read the payment
      // again and show that; paying again is safe, as one payment runs per order (BR-PAY-01).
      this.load(number, () => {
        if (this.phase() === 'choose' && !this.payment()) {
          this.outcome.set({
            title: 'We couldn’t reach the payment service',
            detail:
              'Your order shows no payment, so try again. You can’t be charged twice for this order.',
          });
        }
        this.focusOutcome();
      });
      return;
    }
    // Any other 4xx refused the request before a charge could start. That says nothing about a
    // Failed payment the card provider has yet to settle, so the claim is left out then.
    const notCharged = this.payment()?.status === 'failed' ? '' : ' You have not been charged.';
    this.outcome.set({
      title: 'We couldn’t start the payment',
      detail: `${problemMessage(error, 'Try again.')}${notCharged}`,
    });
    this.focusOutcome();
  }

  /**
  Focuses the outcome above the form, or the heading of the state that replaced the form.
  */
  private focusOutcome(): void {
    afterNextRender(
      { write: () => (this.outcomeBanner() ?? this.phaseHeading())?.nativeElement.focus() },
      { injector: this.injector }
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

  private checkCvc(control: AbstractControl<string>): ValidationErrors | null {
    const value = control.value?.trim() ?? '';
    if (!value) {
      return null;
    }
    const length = cvcLength(detectBrand(digitsOf(this.cardForm?.controls.number.value)));
    const pattern = new RegExp(`^\\d{${length}}$`, 'u');
    return pattern.test(value) ? null : { cvc: true };
  }

  private showProblems(): void {
    this.problems.set(this.problemsOf(CARD_FIELDS));
    afterNextRender({ write: () => this.summary()?.focus() }, { injector: this.injector });
  }

  /**
  The summary's fields that are still invalid, with their messages as they read now.
  */
  private refreshProblems(): void {
    const shown = this.problems();
    if (shown.length === 0) {
      return;
    }
    const stillWrong = shown.filter((problem) =>
      CARD_FIELDS.some(
        (field) =>
          CARD_FIELD_IDS[field] === problem.fieldId && this.cardForm.controls[field].invalid
      )
    );
    if (stillWrong.length !== shown.length) {
      this.problems.set(stillWrong);
    }
  }

  private problemsOf(fields: readonly CardField[]): FormProblem[] {
    const serverErrors = this.serverErrors();
    return fields
      .filter((field) => this.cardForm.controls[field].invalid)
      .map((field) => ({
        fieldId: CARD_FIELD_IDS[field],
        message: cardFieldError(this.cardForm, field, serverErrors),
      }));
  }

  // ---- Loading and following the payment ------------------------------------------------------

  protected retryLoad(): void {
    this.load(this.orderNumber());
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
        // The wallet was chosen, then stopped covering the total (its balance was read again
        // after a decline, or only the same card may continue a failed payment). Say why and
        // offer the card rather than ignore the click.
        this.walletUnusable();
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
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe({
        next: (payment) => {
          this.show(payment);
          // Pay started this charge just now: the wait can be counted.
          this.waitCounted.set(this.phase() === 'processing');
        },
        error: (error: unknown) => this.payFailed(number, error),
      });
  }

  /**
  Focuses the form again after a decline; everything typed is still there but the security code.
  */
  protected tryAgain(): void {
    this.outcome.set(null);
    this.enter('choose');
    afterNextRender(
      {
        write: () => {
          const id = this.method.value === 'card' ? CARD_FIELD_IDS.number : 'pay-button';
          this.document.querySelector<HTMLElement>(`#${CSS.escape(id)}`)?.focus();
        },
      },
      { injector: this.injector }
    );
  }

  // ---- One-time code --------------------------------------------------------------------------

  protected submitCode(): void {
    if (this.submitting() || this.codeExpired()) {
      return;
    }
    const control = this.codeForm.controls.code;
    if (control.invalid) {
      control.markAsTouched();
      const length = this.codeLength();
      const digits =
        length === null ? `${CODE_LENGTH_RANGE.min} to ${CODE_LENGTH_RANGE.max}` : String(length);
      const named = length === null ? 'the code' : `the ${length}-digit code`;
      this.codeError.set(
        control.hasError('required') ? `Enter ${named}` : `The code must be ${digits} digits`
      );
      return;
    }
    this.codeError.set('');
    this.submitting.set(true);
    this.payments
      .submitOneTimeCode(this.orderNumber(), digitsOf(control.value))
      .pipe(
        finalize(() => this.submitting.set(false)),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe({
        next: (payment) => this.show(payment),
        error: (error: unknown) => {
          if (this.followRunningPayment(error)) {
            return;
          }
          // Every ProblemDetails has a `type` (ASP.NET fills in an RFC link), so only a 409 (the
          // payment moved on) or a 404 (the order is gone) means re-reading the page's state.
          const status = httpStatusOf(error);
          if (status === 409 || status === 404) {
            this.load(this.orderNumber());
            return;
          }
          const codeErrors = validationErrorsOf(error)['code'] ?? [];
          this.codeError.set(
            codeErrors.join(' ') || problemMessage(error, 'We couldn’t check the code. Try again.')
          );
        },
      });
  }

  /**
  After the code ran out: back to the form; the next attempt sends a new code.
  */
  protected codeExpiredTryAgain(): void {
    if (this.restartingCode()) {
      return;
    }
    this.restartingCode.set(true);
    this.payments
      .current(this.orderNumber())
      .pipe(
        catchError(() => of(null)),
        finalize(() => this.restartingCode.set(false)),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe((payment) => {
        if (payment && payment.status !== 'requires_action') {
          this.show(payment);
        } else {
          this.outcome.set({
            title: 'The code has expired',
            detail: 'You have not been charged. Pay again and we will send a new code.',
          });
          // The code step that had focus is gone: start reading at the outcome above the form.
          this.enter('choose');
          this.focusOutcome();
        }
        // This click was the Try again: the form is open, so the banner above it only reports.
        const outcome = this.phase() === 'choose' ? this.outcome() : null;
        if (outcome) {
          this.outcome.set({ ...outcome, hidesTryAgain: true });
        }
      });
  }
}

/**
No answer (status 0) or a server error (5xx): the same request may well work in a moment.
*/
const isTransient = (error: unknown): boolean => {
  const status = httpStatusOf(error);
  return status === 0 || (status !== undefined && status >= 500);
};

/**
A read whose 404 is an answer (no such order, or no payment yet) rather than a failure.
*/
const notFoundAsNull = <T>(read: Observable<T>): Observable<T | null> =>
  read.pipe(
    // eslint-disable-next-line promise/prefer-await-to-callbacks -- RxJS catchError operator, not a Node-style callback
    catchError((error: unknown) =>
      httpStatusOf(error) === 404 ? of(null) : throwError(() => error)
    )
  );

const nextStepAfterDecline = (payment: PaymentStatusDto, doesWalletCover: boolean): string => {
  if (payment.method !== 'card') {
    return 'Try paying by card';
  }
  return doesWalletCover && payment.retryMode === 'any_method'
    ? 'Try another card or pay from your wallet'
    : 'Try another card';
};

const CODE_CHARACTERS = /^[\d\s-]+$/u;

/**
Exactly `length` digits once spaces and dashes are taken out (a pasted "123 456" is fine).
*/
const validCode = (
  control: AbstractControl<string>,
  length: number | null
): ValidationErrors | null => {
  const raw = control.value ?? '';
  if (!raw.trim()) {
    return null;
  }
  const count = digitsOf(raw).length;
  const isRightLength =
    length === null
      ? count >= CODE_LENGTH_RANGE.min && count <= CODE_LENGTH_RANGE.max
      : count === length;
  return isRightLength && CODE_CHARACTERS.test(raw) ? null : { code: true };
};

/**
 * What to tell the shopper after an attempt that did not go through (BR-PAY-07 wording from the API).
 * The wallet is suggested only when it can pay this order: it covers the total and any method may be tried again.
 */
const outcomeOf = (payment: PaymentStatusDto, doesWalletCover: boolean): Outcome => {
  const attempts =
    payment.attemptsLeft === 1 ? '1 attempt left' : `${payment.attemptsLeft} attempts left`;
  switch (payment.status) {
    // Never "not charged": only the card provider's later answer can say (BR-PAY-05).
    case 'failed': {
      return {
        title: 'We couldn’t confirm your payment',
        detail:
          payment.retryMode === 'same_payment_only'
            ? `The card provider did not answer, so we can’t tell yet whether the card was charged. Enter the same card and try again: it continues the same payment, so you can’t be charged twice. ${attempts}.`
            : `The card provider did not answer, so we can’t tell yet whether the card was charged. Paying again checks that payment with them first, so you can’t be charged twice. ${attempts}.`,
      };
    }
    case 'expired': {
      return {
        title: 'The code has expired',
        detail: `You have not been charged. Pay again and we will send a new code. ${attempts}.`,
      };
    }
    default: {
      return {
        title: payment.declineMessage ?? 'Your card was declined',
        detail: `Payment declined. You have not been charged. ${nextStepAfterDecline(payment, doesWalletCover)}. ${attempts}.`,
      };
    }
  }
};
