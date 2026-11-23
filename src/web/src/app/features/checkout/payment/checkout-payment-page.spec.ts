import { LiveAnnouncer } from '@angular/cdk/a11y';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { OrderDetailDto } from '../../../core/api/order.models';
import {
  PAYMENT_IN_PROGRESS_PROBLEM,
  PaymentStatusDto,
  SAME_PAYMENT_ONLY_PROBLEM,
} from '../../../core/api/payments.api';
import { CheckoutStore } from '../../../core/checkout/checkout-store';
import { provideAppIcons } from '../../../core/icons/app-icons';
import { US_ADDRESS, aCart, aPaymentStatus, aWallet } from '../../../testing/fixtures';
import { anOrder } from '../../../testing/order-fixtures';
import { CheckoutPaymentPage, PAYMENT_POLL_INTERVAL_MS } from './checkout-payment-page';

@Component({ selector: 'app-stub-page', template: '<h1>Confirmation</h1>' })
class StubPage {}

const ORDER = 'LG-7K3M9QX2';
const ORDER_URL = `/api/v1/orders/${ORDER}`;
const CURRENT_URL = `${ORDER_URL}/payments/current`;
const NOT_FOUND = { status: 404, statusText: 'Not Found' };

const url = () => TestBed.inject(Router).url;

/**
A card payment waiting for its one-time code, as the server answers it.
*/
const needsCode = (overrides: Partial<PaymentStatusDto> = {}) =>
  aPaymentStatus({
    status: 'requires_action',
    cardLast4: '0034',
    needsOneTimeCode: true,
    oneTimeCodeExpiresUtc: '2026-11-20T08:05:00Z',
    testOneTimeCode: '123456',
    serverTimeUtc: '2026-11-20T08:00:00Z',
    ...overrides,
  });

describe('CheckoutPaymentPage', () => {
  let fixture: ComponentFixture<CheckoutPaymentPage>;
  let http: HttpTestingController;
  let announcer: LiveAnnouncer;

  const host = () => fixture.nativeElement as HTMLElement;
  const text = (selector: string) =>
    host().querySelector(selector)?.textContent?.replaceAll(/\s+/gu, ' ').trim();
  const input = (id: string) =>
    host().querySelector<HTMLInputElement>(`#${id}`) as HTMLInputElement;
  const walletInput = () =>
    host().querySelector<HTMLInputElement>(
      ':scope mat-radio-button[value="wallet"] input'
    ) as HTMLInputElement;
  /**
  Every announcement of this message, with its politeness.
  */
  const announcementsOf = (message: string) =>
    vi.mocked(announcer.announce).mock.calls.filter(([announced]) => announced === message);

  /**
  Change detection plus pending timers and promises (the page polls on timers).
  */
  const settle = async (ms = 0): Promise<void> => {
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(ms);
    fixture.detectChanges();
  };

  /**
   * Opens the page as after a refresh: the order (`null`: the read answers 404), its current
   * payment and the wallet.
   */
  const open = async (
    payment: PaymentStatusDto | null,
    { order = anOrder(), balance = 2000 }: { order?: OrderDetailDto | null; balance?: number } = {}
  ): Promise<void> => {
    fixture = TestBed.createComponent(CheckoutPaymentPage);
    fixture.componentRef.setInput('order', ORDER);
    await settle();
    if (order) {
      http.expectOne(ORDER_URL).flush(order);
    } else {
      http.expectOne(ORDER_URL).flush({ title: 'Not Found' }, NOT_FOUND);
    }
    if (payment) {
      http.expectOne(CURRENT_URL).flush(payment);
    } else {
      http.expectOne(CURRENT_URL).flush({ title: 'Not Found' }, NOT_FOUND);
    }
    http.expectOne('/api/v1/wallet').flush(aWallet(balance));
    await settle();
  };

  const type = (id: string, value: string): void => {
    const field = input(id);
    field.value = value;
    field.dispatchEvent(new Event('input'));
  };

  const fillCard = (number = '4111111111111111'): void => {
    type('card-number', number);
    type('card-name', 'Ada Lovelace');
    type('card-expiry', '0829');
    type('card-cvc', '123');
  };

  /**
  Types a code, presses Verify and gives the code's error, if any.
  */
  const verify = async (code: string): Promise<string | undefined> => {
    type('one-time-code', code);
    host().querySelector<HTMLButtonElement>(':scope .code-form button[type="submit"]')?.click();
    await settle();
    return text('.field-error');
  };

  const pay = async (): Promise<void> => {
    host().querySelector<HTMLButtonElement>('#pay-button')?.click();
    await settle();
  };

  /**
  Answers the next status poll.
  */
  const poll = async (payment: PaymentStatusDto): Promise<void> => {
    await settle(PAYMENT_POLL_INTERVAL_MS);
    http.expectOne(CURRENT_URL).flush(payment);
    await settle();
  };

  /**
  Pays by card and gets a decline (or another final answer) back.
  */
  const declineWith = async (
    payment: Partial<PaymentStatusDto>,
    { balance = 2000 }: { balance?: number } = {}
  ): Promise<void> => {
    await open(null, { balance });
    fillCard('4242000000000018');
    await pay();
    http.expectOne(`${ORDER_URL}/payments`).flush(
      aPaymentStatus({
        status: 'declined',
        retryMode: 'any_method',
        canRetry: true,
        ...payment,
      })
    );
    await settle();
  };

  beforeEach(() => {
    vi.useFakeTimers({
      now: Date.parse('2026-11-20T08:00:00Z'),
      toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'],
    });
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'orders/:id/confirmation', component: StubPage }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    announcer = TestBed.inject(LiveAnnouncer);
    vi.spyOn(announcer, 'announce').mockResolvedValue(undefined);
  });

  afterEach(() => {
    http.verify();
    fixture?.destroy();
    vi.useRealTimers();
    vi.restoreAllMocks();
    sessionStorage.clear();
  });

  describe('choosing how to pay', () => {
    it('offers card and wallet with the balance, and the Pay button names the total', async () => {
      await open(null);

      // A space before the dot: the hold sits in its own span, and Angular drops a bare line break.
      expect(text('.order-line')).toContain(`Order ${ORDER} · Total $67.46 · Prices held until `);
      expect(text('#wallet-note')).toBe('Wallet balance: $2,000.00');
      expect(walletInput().disabled).toBeFalsy();
      expect(text('#pay-button')).toBe('Pay $67.46');
    });

    it('disables the wallet, saying why, when the balance does not cover the total', async () => {
      await open(null, { balance: 42.1 });

      const wallet = walletInput();

      expect(wallet.disabled).toBeTruthy();
      expect(wallet.getAttribute('aria-describedby')).toContain('wallet-note');
      expect(text('#wallet-note')).toBe(
        'Wallet balance: $42.10 · Not enough to pay $67.46, so pay by card.'
      );
    });

    it('opens on Place order’s answer instead of reading the order again', async () => {
      TestBed.inject(CheckoutStore).orderPlaced(anOrder());
      fixture = TestBed.createComponent(CheckoutPaymentPage);
      fixture.componentRef.setInput('order', ORDER);
      await settle();

      http.expectNone(ORDER_URL);
      http.expectOne(CURRENT_URL).flush({ title: 'No payment yet' }, NOT_FOUND);
      http.expectOne('/api/v1/wallet').flush(aWallet());
      await settle();

      expect(text('#pay-button')).toBe('Pay $67.46');
    });

    it('says so when neither the order nor a payment of it can be found', async () => {
      await open(null, { order: null });

      expect(text('#closed-heading')).toBe('Order not found');
      expect(text('.state p')).toBe(`We couldn’t find order ${ORDER} in your account.`);
    });

    it('mentions how long card payments take only while the card is chosen', async () => {
      await open(null);

      expect(text('.pay-note')).toBe(
        'Card payments usually take a few seconds but can take up to 20. You pay exactly the order total.'
      );

      walletInput().click();
      await settle();

      expect(text('.pay-note')).toBe('You pay exactly the order total.');
    });

    it('pays from the wallet without card details', async () => {
      await open(null);
      walletInput().click();
      await settle();

      expect(host().querySelector('#card-number')).toBeNull();

      await pay();

      const request = http.expectOne(`${ORDER_URL}/payments`);

      expect(request.request.body).toStrictEqual({ method: 'wallet', card: null });

      request.flush(aPaymentStatus({ method: 'wallet', status: 'processing' }));
      await settle();

      expect(text('#processing-heading')).toBe('Confirming your payment…');
    });
  });

  it('says why and switches to the card when the chosen wallet no longer covers the total', async () => {
    await open(null);
    walletInput().click();
    await settle();
    // The balance is read again (as after a wallet decline) and is now too low.
    (fixture.componentInstance as unknown as { wallet: { reload: () => void } }).wallet.reload();
    await settle();
    http.expectOne('/api/v1/wallet').flush(aWallet(10));
    await settle();

    await pay();

    http.expectNone(`${ORDER_URL}/payments`);

    expect(text('.outcome-title')).toBe('The wallet can’t pay this order');
    expect(text('.outcome .outcome-title + p')).toBe(
      'Your wallet balance doesn’t cover the order total, so pay by card.'
    );
    expect(document.activeElement).toBe(host().querySelector('.outcome'));
    expect(host().querySelector('#card-number')).toBeTruthy();
  });

  describe('card form', () => {
    it('spaces the number as typed, names the brand and checks Luhn before sending', async () => {
      await open(null);

      type('card-number', '4111111111111112');
      await settle();

      expect(input('card-number').value).toBe('4111 1111 1111 1112');
      expect(text('.brand')).toBe('Visa');

      type('card-name', 'Ada Lovelace');
      type('card-expiry', '1/2');
      type('card-cvc', '12');
      await pay();

      http.expectNone(`${ORDER_URL}/payments`);

      expect(
        [...host().querySelectorAll(':scope app-error-summary li')].map((li) =>
          li.textContent?.trim()
        )
      ).toStrictEqual([
        'Enter a valid card number: check it for typos',
        'Enter the expiry date as MM/YY, like 08/29',
        'Enter the 3-digit security code',
      ]);
    });

    it('drops each problem from the summary as its field is corrected, then the summary', async () => {
      await open(null);
      type('card-number', '4111111111111112');
      type('card-name', 'Ada Lovelace');
      await pay();
      const listed = () =>
        [...host().querySelectorAll(':scope app-error-summary li')].map((li) =>
          li.textContent?.trim()
        );

      expect(listed()).toStrictEqual([
        'Enter a valid card number: check it for typos',
        'Enter the expiry date',
        'Enter the security code',
      ]);

      type('card-expiry', '08/2');
      await settle();

      // Still wrong, so still listed, and worded as at Pay: the alert region must not interrupt
      // someone typing. The field's own error shows what is wrong now.
      expect(listed()).toStrictEqual([
        'Enter a valid card number: check it for typos',
        'Enter the expiry date',
        'Enter the security code',
      ]);

      type('card-number', '4111111111111111');
      type('card-expiry', '0829');
      await settle();

      expect(listed()).toStrictEqual(['Enter the security code']);

      type('card-cvc', '123');
      await settle();

      expect(host().querySelector('app-error-summary')).toBeNull();

      http.expectNone(`${ORDER_URL}/payments`);
    });

    it('sends digits only, the expiry as month and year, and never an amount', async () => {
      await open(null);
      fillCard('4111 1111 1111 1111');
      await settle();

      expect(input('card-expiry').value).toBe('08/29');

      await pay();

      const request = http.expectOne(`${ORDER_URL}/payments`);

      expect(request.request.method).toBe('POST');
      expect(request.request.body).toStrictEqual({
        method: 'card',
        card: {
          number: '4111111111111111',
          name: 'Ada Lovelace',
          expiryMonth: 8,
          expiryYear: 2029,
          cvc: '123',
        },
      });

      request.flush(aPaymentStatus({ status: 'succeeded', orderStatus: 'paid' }));
      http.expectOne('/api/v1/cart').flush(aCart([]));
      await settle();

      expect(url()).toBe(`/orders/${ORDER}/confirmation`);
    });

    it.each([
      [0, 'Unknown Error'],
      [502, 'Bad Gateway'],
      [504, 'Gateway Timeout'],
    ])(
      'after a lost answer (%i) reads the payment again and shows what the server says',
      async (status, statusText) => {
        await open(null);
        fillCard('4242000000000042');

        await pay();
        http.expectOne(`${ORDER_URL}/payments`).flush(null, { status, statusText });
        await settle();
        // The charge did start before the answer was lost.
        http.expectOne(ORDER_URL).flush(anOrder());
        http.expectOne(CURRENT_URL).flush(aPaymentStatus({ status: 'processing' }));
        await settle();

        expect(text('#processing-heading')).toBe('Confirming your payment…');
        expect(host().textContent).not.toContain('not been charged');
        expect(document.activeElement).toBe(host().querySelector('#processing-heading'));

        await poll(aPaymentStatus({ status: 'processing' }));
      }
    );

    it('after a lost answer, offers to pay again when the server shows no payment', async () => {
      await open(null);
      fillCard();

      await pay();
      http
        .expectOne(`${ORDER_URL}/payments`)
        .flush(null, { status: 502, statusText: 'Bad Gateway' });
      await settle();
      http.expectOne(ORDER_URL).flush(anOrder());
      http.expectOne(CURRENT_URL).flush({ title: 'Not Found' }, NOT_FOUND);
      await settle();

      expect(text('.outcome-title')).toBe('We couldn’t reach the payment service');
      expect(text('.outcome')).not.toContain('not been charged');
      expect(document.activeElement).toBe(host().querySelector('.outcome'));
      expect(input('card-number').value).toBe('4111 1111 1111 1111');
    });

    it('says nothing was charged when the API refused the request (4xx)', async () => {
      await open(null);
      fillCard();

      await pay();
      http
        .expectOne(`${ORDER_URL}/payments`)
        .flush(
          { title: 'Too many requests', status: 429, detail: 'Wait a minute, then try again.' },
          { status: 429, statusText: 'Too Many Requests' }
        );
      await settle();

      expect(text('.outcome-title')).toBe('We couldn’t start the payment');
      expect(text('.outcome .outcome-title + p')).toBe(
        'Wait a minute, then try again. You have not been charged.'
      );
    });

    it('puts the API’s card errors on their fields', async () => {
      await open(null);
      fillCard();

      await pay();
      http.expectOne(`${ORDER_URL}/payments`).flush(
        {
          title: 'Validation failed',
          status: 400,
          errors: { 'card.expiry': ['This card has expired.'] },
        },
        { status: 400, statusText: 'Bad Request' }
      );
      await settle();

      expect(text('app-error-summary li')).toBe('This card has expired.');
    });
  });

  describe('slow payment (processing)', () => {
    it('shows "Confirming your payment…", polls, and stops when the payment is final', async () => {
      await open(null);
      fillCard('4242000000000042');
      await pay();
      http.expectOne(`${ORDER_URL}/payments`).flush(aPaymentStatus({ status: 'processing' }));
      await settle();

      expect({
        heading: text('#processing-heading'),
        waited: text('.waited'),
      }).toStrictEqual({ heading: 'Confirming your payment…', waited: 'Waiting 0 s' });
      expect(text('.processing [role="status"] p')).toBe(
        'This can take up to 20 seconds. Keep this page open; refreshing it won’t charge you twice.'
      );
      expect(host().querySelector('#pay-button')).toBeNull();

      await poll(aPaymentStatus({ status: 'processing' }));

      expect(text('.waited')).toBe('Waiting 1 s');

      await poll(aPaymentStatus({ status: 'error' }));
      await poll(aPaymentStatus({ status: 'succeeded', orderStatus: 'paid' }));
      http.expectOne('/api/v1/cart').flush(aCart([]));
      await settle();

      expect(url()).toBe(`/orders/${ORDER}/confirmation`);

      await settle(PAYMENT_POLL_INTERVAL_MS * 3);
      http.expectNone(CURRENT_URL);
    });

    it('resumes polling after a reload instead of showing the form again', async () => {
      TestBed.inject(CheckoutStore).setAddress(US_ADDRESS);
      TestBed.inject(CheckoutStore).setOrderNumber(ORDER);
      await open(aPaymentStatus({ status: 'processing' }));

      expect(text('#processing-heading')).toBe('Confirming your payment…');
      expect(host().querySelector('#card-number')).toBeNull();
      // The status has no start time, so a count from this page load would be wrong.
      expect(text('.waited')).toBe('Still confirming…');

      await poll(aPaymentStatus({ status: 'succeeded', orderStatus: 'paid' }));
      http.expectOne('/api/v1/cart').flush(aCart([]));
      await settle();

      expect(url()).toBe(`/orders/${ORDER}/confirmation`);
      // The checkout is over: the next one starts fresh.
      expect(TestBed.inject(CheckoutStore).address()).toBeNull();
    });

    it('resumes from the payment alone when the order itself cannot be read', async () => {
      await open(aPaymentStatus({ status: 'processing' }), { order: null });

      expect(text('#processing-heading')).toBe('Confirming your payment…');
      expect(text('.order-line')).toContain(`Order ${ORDER} · Total $67.46`);

      await poll(aPaymentStatus({ status: 'succeeded', orderStatus: 'paid' }));
      http.expectOne('/api/v1/cart').flush(aCart([]));
      await settle();

      expect(url()).toBe(`/orders/${ORDER}/confirmation`);
    });

    it('keeps waiting through a dropped status request', async () => {
      await open(aPaymentStatus({ status: 'processing' }));

      await settle(PAYMENT_POLL_INTERVAL_MS);
      http.expectOne(CURRENT_URL).flush('down', { status: 502, statusText: 'Bad Gateway' });
      await settle();

      expect(text('#processing-heading')).toBe('Confirming your payment…');

      await settle(PAYMENT_POLL_INTERVAL_MS);
      http.expectOne(CURRENT_URL).error(new ProgressEvent('error'));
      await settle();

      expect(text('#processing-heading')).toBe('Confirming your payment…');

      await poll(aPaymentStatus({ status: 'processing' }));

      expect(text('#processing-heading')).toBe('Confirming your payment…');
    });

    it('stops polling when signed out (401) and offers to sign in and come back', async () => {
      await open(aPaymentStatus({ status: 'processing' }));

      await settle(PAYMENT_POLL_INTERVAL_MS);
      http
        .expectOne(CURRENT_URL)
        .flush({ title: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });
      await settle();

      const returnUrl = encodeURIComponent(`/checkout/payment?order=${ORDER}`);

      expect(text('#signed-out-heading')).toBe('Sign in to see your payment');
      expect(document.activeElement).toBe(host().querySelector('#signed-out-heading'));
      expect(host().querySelector(':scope .state a')?.getAttribute('href')).toBe(
        `/sign-in?returnUrl=${returnUrl}`
      );

      await settle(PAYMENT_POLL_INTERVAL_MS * 3);
      http.expectNone(CURRENT_URL);
    });

    it('stops polling when the order is not found (404)', async () => {
      await open(aPaymentStatus({ status: 'processing' }));

      await settle(PAYMENT_POLL_INTERVAL_MS);
      http.expectOne(CURRENT_URL).flush({ title: 'Not Found' }, NOT_FOUND);
      await settle();

      expect(text('#closed-heading')).toBe('Order not found');
      expect(text('.state p')).toBe(`We couldn’t find order ${ORDER} in your account.`);

      await settle(PAYMENT_POLL_INTERVAL_MS * 3);
      http.expectNone(CURRENT_URL);
    });

    it('stops polling with the API’s reason on another refusal (403)', async () => {
      await open(aPaymentStatus({ status: 'processing' }));

      await settle(PAYMENT_POLL_INTERVAL_MS);
      http
        .expectOne(CURRENT_URL)
        .flush({ title: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });
      await settle();

      expect(text('.banner.error p')).toBe('Forbidden');

      await settle(PAYMENT_POLL_INTERVAL_MS * 3);
      http.expectNone(CURRENT_URL);
    });

    it('shows the running payment when another tab already started one', async () => {
      await open(null);
      fillCard();

      await pay();
      http.expectOne(`${ORDER_URL}/payments`).flush(
        {
          type: PAYMENT_IN_PROGRESS_PROBLEM,
          title: 'A payment is already in progress',
          status: 409,
          detail: 'Your card payment is still being confirmed.',
          payment: aPaymentStatus({ status: 'processing' }),
        },
        { status: 409, statusText: 'Conflict' }
      );
      await settle();

      expect(text('.banner.in-progress p')).toBe(
        'A payment is already in progress. We’re showing it here; you won’t be charged twice.'
      );
      expect(text('#processing-heading')).toBe('Confirming your payment…');
      expect(text('.waited')).toBe('Still confirming…');

      await poll(aPaymentStatus({ status: 'processing' }));
    });
  });

  describe('declines', () => {
    it('names insufficient funds, says nothing was charged, how many attempts are left, and keeps the card', async () => {
      await declineWith({ declineMessage: 'Insufficient funds', attemptsLeft: 4 });

      expect({
        title: text('.outcome-title'),
        detail: text('.outcome .outcome-title + p'),
      }).toStrictEqual({
        title: 'Insufficient funds',
        detail:
          'Payment declined. You have not been charged. Try another card or pay from your wallet. 4 attempts left.',
      });
      expect(document.activeElement).toBe(host().querySelector('.outcome'));
      expect({
        number: input('card-number').value,
        name: input('card-name').value,
        cvc: input('card-cvc').value,
      }).toStrictEqual({
        number: '4242 0000 0000 0018',
        name: 'Ada Lovelace',
        // Asked again for the next attempt; not left in the form.
        cvc: '',
      });

      host().querySelector<HTMLButtonElement>('.try-again')?.click();
      await settle();

      expect(document.activeElement).toBe(input('card-number'));
      expect(host().querySelector('.outcome')).toBeNull();
    });

    it('suggests only another card when the wallet does not cover the total', async () => {
      await declineWith({ declineMessage: 'Insufficient funds', attemptsLeft: 4 }, { balance: 10 });

      expect(text('.outcome .outcome-title + p')).toBe(
        'Payment declined. You have not been charged. Try another card. 4 attempts left.'
      );
    });

    it('shows only the generic decline for a stolen card', async () => {
      await declineWith({ declineMessage: 'Your card was declined', attemptsLeft: 1 });

      expect(text('.outcome-title')).toBe('Your card was declined');
      expect(text('.outcome')).not.toMatch(/stolen/iu);
      expect(text('.outcome')).toContain('1 attempt left.');
    });

    it('allows only the same card payment again when the gateway could not settle a failed one', async () => {
      await declineWith({ status: 'failed', retryMode: 'same_payment_only', declineMessage: null });

      expect(text('.outcome-title')).toBe('We couldn’t confirm your payment');
      expect(walletInput().disabled).toBeTruthy();
      expect(text('#wallet-note')).toBe(
        'Only the same card payment can be tried again, so the wallet can’t be used for this attempt.'
      );
    });

    it('keeps the API’s words when only the same card may continue a failed payment (409)', async () => {
      await open(
        aPaymentStatus({
          status: 'failed',
          retryMode: 'same_payment_only',
          canRetry: true,
          attemptsLeft: 4,
          orderStatus: 'awaiting_payment',
        })
      );
      fillCard('5555555555554444');

      await pay();
      http.expectOne(`${ORDER_URL}/payments`).flush(
        {
          type: SAME_PAYMENT_ONLY_PROBLEM,
          title: 'Try the same card again',
          status: 409,
          detail:
            'We could not confirm your payment with Visa ending 1111. Try again with that card; you will not be charged twice.',
        },
        { status: 409, statusText: 'Conflict' }
      );
      await settle();

      expect(text('.outcome-title')).toBe('Try the same card again');
      expect(text('.outcome .outcome-title + p')).toBe(
        'We could not confirm your payment with Visa ending 1111. Try again with that card; you will not be charged twice.'
      );
      expect(text('.outcome')).not.toContain('You have not been charged');
    });

    it('offers the card after a wallet decline, with the wallet balance read again', async () => {
      await open(null);
      walletInput().click();
      await settle();

      await pay();
      http.expectOne(`${ORDER_URL}/payments`).flush(
        aPaymentStatus({
          method: 'wallet',
          status: 'declined',
          cardBrand: null,
          cardLast4: null,
          declineMessage: 'Insufficient funds',
          retryMode: 'any_method',
          canRetry: true,
          attemptsLeft: 4,
          orderStatus: 'awaiting_payment',
        })
      );
      await settle();
      http.expectOne('/api/v1/wallet').flush(aWallet(10));
      await settle();

      expect(text('.outcome-title')).toBe('Insufficient funds');
      expect(text('#wallet-note')).toBe(
        'Wallet balance: $10.00 · Not enough to pay $67.46, so pay by card.'
      );
      expect(host().querySelector('#card-number')).toBeTruthy();
    });

    it('keeps confirming a failed payment nothing can retry, never saying cancelled or not charged', async () => {
      TestBed.inject(CheckoutStore).setOrderNumber(ORDER);
      const unsettled = aPaymentStatus({
        status: 'failed',
        retryMode: 'none',
        canRetry: false,
        attemptsLeft: 0,
        orderStatus: 'awaiting_payment',
        declineMessage:
          'We could not confirm your payment. Try again with the same card; you will not be charged twice.',
      });
      await declineWith(unsettled);

      expect({
        heading: text('#processing-heading'),
        status: text('.processing [role="status"]'),
        ordersLink: host().querySelector(':scope .processing a[href="/orders"]'),
      }).toStrictEqual({
        heading: 'Confirming your payment…',
        status: expect.stringContaining('we’re confirming with them whether your card was charged'),
        ordersLink: expect.any(HTMLAnchorElement),
      });
      expect(text('.processing')).not.toMatch(/not been charged|cancelled|can’t be paid/iu);
      // The checkout is not over: the order may still turn out paid.
      expect(TestBed.inject(CheckoutStore).orderNumber()).toBe(ORDER);

      await poll(unsettled);

      expect(text('#processing-heading')).toBe('Confirming your payment…');

      await poll(aPaymentStatus({ status: 'succeeded', orderStatus: 'paid' }));
      http.expectOne('/api/v1/cart').flush(aCart([]));
      await settle();

      expect(url()).toBe(`/orders/${ORDER}/confirmation`);
    });

    it('says not charged once the card provider settles a failed payment as never charged', async () => {
      await open(
        aPaymentStatus({
          status: 'failed',
          retryMode: 'none',
          attemptsLeft: 0,
          orderStatus: 'awaiting_payment',
        })
      );

      expect(text('#processing-heading')).toBe('Confirming your payment…');

      await poll(
        aPaymentStatus({
          status: 'declined',
          retryMode: 'none',
          attemptsLeft: 0,
          orderStatus: 'expired',
          declineMessage: 'Your card was declined',
        })
      );

      expect(text('.state p')).toBe(
        'This order ran out of payment attempts, so it was cancelled. You have not been charged.'
      );
    });

    it('closes the order when no attempt is left', async () => {
      await declineWith({
        retryMode: 'none',
        canRetry: false,
        attemptsLeft: 0,
        orderStatus: 'expired',
      });

      expect(text('#closed-heading')).toBe('This order can’t be paid');
      expect(text('.state p')).toBe(
        'This order ran out of payment attempts, so it was cancelled. You have not been charged.'
      );
    });
  });

  describe('one-time code', () => {
    it('asks for the code in one numeric field with the test code shown', async () => {
      await open(needsCode());

      const field = input('one-time-code');

      expect(field.getAttribute('inputmode')).toBe('numeric');
      expect(field.getAttribute('autocomplete')).toBe('one-time-code');
      expect(field.type).toBe('text');
      expect(field.getAttribute('maxlength')).toBeNull();
      expect(text('#code-test')).toBe('Test code: 123456 (no text message is sent)');
    });

    it('counts down on the server’s clock, warns once at a minute, and expires', async () => {
      // The browser clock is 2 minutes behind the server's.
      vi.setSystemTime(Date.parse('2026-11-20T07:58:00Z'));
      await open(needsCode());

      expect({
        timer: text('#code-timer'),
        role: host().querySelector('#code-timer')?.getAttribute('role'),
      }).toStrictEqual({ timer: 'Code expires in 5:00', role: 'timer' });

      await settle(4 * 60_000 + 1000);

      expect({
        timer: text('#code-timer'),
        warnings: announcementsOf('The code expires in 1 minute.'),
      }).toStrictEqual({
        timer: 'Code expires in 0:59',
        warnings: [['The code expires in 1 minute.', 'polite']],
      });

      await settle(60_000);

      // The one-minute warning is still the only one.
      expect({
        codeField: host().querySelector('#one-time-code'),
        message: text('.state .banner.error p'),
        warnings: announcementsOf('The code expires in 1 minute.'),
        expired: announcementsOf('The code has expired.'),
      }).toStrictEqual({
        codeField: null,
        message: 'The code has expired. You have not been charged.',
        warnings: [['The code expires in 1 minute.', 'polite']],
        expired: [['The code has expired.', 'assertive']],
      });

      host().querySelector<HTMLButtonElement>(':scope .state .banner.error button')?.click();
      await settle();
      http.expectOne(CURRENT_URL).flush(
        aPaymentStatus({
          status: 'expired',
          retryMode: 'any_method',
          canRetry: true,
          attemptsLeft: 4,
        })
      );
      await settle();

      expect({
        title: text('.outcome-title'),
        tryAgain: host().querySelector(':scope .outcome button'),
        cardField: host().querySelector('#card-number'),
      }).toStrictEqual({
        title: 'The code has expired',
        tryAgain: null,
        cardField: expect.any(HTMLInputElement),
      });
    });

    it('sends a pasted code without spaces and goes to the confirmation when approved', async () => {
      await open(needsCode());

      type('one-time-code', '123 456');
      host().querySelector<HTMLButtonElement>(':scope .code-form button[type="submit"]')?.click();
      await settle();
      const request = http.expectOne(`${CURRENT_URL}/one-time-code`);

      expect(request.request.body).toStrictEqual({ code: '123456' });

      request.flush(aPaymentStatus({ status: 'succeeded', orderStatus: 'paid' }));
      http.expectOne('/api/v1/cart').flush(aCart([]));
      await settle();

      expect(url()).toBe(`/orders/${ORDER}/confirmation`);
    });

    it('shows the API’s reason when it refuses the code itself (400), staying on the code', async () => {
      await open(needsCode());

      type('one-time-code', '000000');
      host().querySelector<HTMLButtonElement>(':scope .code-form button[type="submit"]')?.click();
      await settle();
      http.expectOne(`${CURRENT_URL}/one-time-code`).flush(
        {
          type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
          title: 'One or more validation errors occurred.',
          status: 400,
          errors: { code: ['Enter the code.'] },
        },
        { status: 400, statusText: 'Bad Request' }
      );
      await settle();

      expect(text('.field-error')).toBe('Enter the code.');
      expect(input('one-time-code').getAttribute('aria-describedby')?.split(' ', 1)[0]).toBe(
        'code-error'
      );
    });

    it('never sends a code of the wrong length, which would use up an attempt', async () => {
      await open(needsCode());

      expect({
        empty: await verify(''),
        short: await verify('12345'),
        long: await verify('1234567'),
      }).toStrictEqual({
        empty: 'Enter the 6-digit code',
        short: 'The code must be 6 digits',
        long: 'The code must be 6 digits',
      });

      http.expectNone(`${CURRENT_URL}/one-time-code`);
    });

    it('asks for as many digits as the gateway’s code has', async () => {
      await open(needsCode({ testOneTimeCode: '2468' }));

      type('one-time-code', '123456');
      host().querySelector<HTMLButtonElement>(':scope .code-form button[type="submit"]')?.click();
      await settle();

      expect(text('.field-error')).toBe('The code must be 4 digits');

      http.expectNone(`${CURRENT_URL}/one-time-code`);
    });

    it('focuses the outcome when Try again after expiry finds the code step still open', async () => {
      vi.setSystemTime(Date.parse('2026-11-20T08:00:00Z'));
      await open(needsCode());
      await settle(5 * 60_000);

      const tryAgain = () =>
        host().querySelector<HTMLButtonElement>(':scope .state .banner.error button');
      tryAgain()?.click();
      await settle();

      expect({
        label: tryAgain()?.textContent?.trim(),
        disabled: tryAgain()?.getAttribute('aria-disabled'),
      }).toStrictEqual({ label: 'Starting again…', disabled: 'true' });

      // A second click while the first is under way reads nothing more (one request below).
      tryAgain()?.click();
      await settle();
      // The server has not closed the code yet.
      http.expectOne(CURRENT_URL).flush(needsCode({ needsOneTimeCode: false }));
      await settle();

      expect(text('.outcome-title')).toBe('The code has expired');
      expect(host().querySelector(':scope .outcome button')).toBeNull();
      expect(document.activeElement).toBe(host().querySelector('.outcome'));
    });

    it('declines a wrong code and lets the shopper pay again', async () => {
      await open(needsCode());

      type('one-time-code', '654321');
      host().querySelector<HTMLButtonElement>(':scope .code-form button[type="submit"]')?.click();
      await settle();
      http.expectOne(`${CURRENT_URL}/one-time-code`).flush(
        aPaymentStatus({
          status: 'declined',
          declineMessage: 'The code is not correct',
          retryMode: 'any_method',
          canRetry: true,
          attemptsLeft: 4,
        })
      );
      await settle();

      expect(text('.outcome-title')).toBe('The code is not correct');
      expect(host().querySelector('#card-number')).toBeTruthy();
    });
  });

  describe('opened without ?order= (the stepper’s Payment link)', () => {
    let store: CheckoutStore;

    /**
    Opens the page on the order this checkout placed, as the stepper does.
    */
    const openFromCheckout = async (): Promise<void> => {
      store = TestBed.inject(CheckoutStore);
      store.setAddress(US_ADDRESS);
      store.setShippingMethod('standard');
      store.orderPlaced(anOrder());
      fixture = TestBed.createComponent(CheckoutPaymentPage);
      await settle();
      http.expectOne(CURRENT_URL).flush({ title: 'No payment yet' }, NOT_FOUND);
      http.expectOne('/api/v1/wallet').flush(aWallet());
      await settle();
    };

    it('pays the checkout’s order, ends the checkout and never flashes "can’t be paid"', async () => {
      await openFromCheckout();

      expect(text('.order-line')).toContain(`Order ${ORDER} · Total $67.46`);

      fillCard();

      await pay();
      http
        .expectOne(`${ORDER_URL}/payments`)
        .flush(aPaymentStatus({ status: 'succeeded', orderStatus: 'paid' }));
      await settle();

      // The reset checkout must not reload the page with no order.
      expect(host().querySelector('#closed-heading')).toBeNull();
      expect(text('p[role="status"]')).toBe('Payment confirmed. Opening your order confirmation…');
      expect(store.orderNumber()).toBeNull();

      http.expectOne('/api/v1/cart').flush(aCart([]));
      await settle();

      expect(url()).toBe(`/orders/${ORDER}/confirmation`);
    });

    it('keeps the reason the order closed after the checkout resets', async () => {
      await openFromCheckout();
      fillCard('4242000000000018');

      await pay();
      http.expectOne(`${ORDER_URL}/payments`).flush(
        aPaymentStatus({
          status: 'declined',
          declineMessage: 'Insufficient funds',
          retryMode: 'none',
          attemptsLeft: 0,
          orderStatus: 'expired',
        })
      );
      await settle();

      expect(store.orderNumber()).toBeNull();
      expect(text('.state p')).toBe(
        'This order ran out of payment attempts, so it was cancelled. You have not been charged.'
      );
    });
  });

  it('leaves the checkout in progress alone when paying another order through ?order=', async () => {
    const store = TestBed.inject(CheckoutStore);
    store.setAddress(US_ADDRESS);
    store.setOrderNumber('LG-OTHER1');

    await open(aPaymentStatus({ status: 'succeeded', orderStatus: 'paid' }));
    http.expectOne('/api/v1/cart').flush(aCart([]));
    await settle();

    expect(url()).toBe(`/orders/${ORDER}/confirmation`);
    expect(store.orderNumber()).toBe('LG-OTHER1');
    expect(store.address()).toStrictEqual(US_ADDRESS);
  });

  it('drops an unfinished load when the order in the URL changes, so the old order never shows', async () => {
    fixture = TestBed.createComponent(CheckoutPaymentPage);
    fixture.componentRef.setInput('order', 'LG-OLD00001');
    await settle();
    const oldOrder = http.expectOne('/api/v1/orders/LG-OLD00001');
    const oldPayment = http.expectOne('/api/v1/orders/LG-OLD00001/payments/current');

    fixture.componentRef.setInput('order', ORDER);
    await settle();

    expect({ order: oldOrder.cancelled, payment: oldPayment.cancelled }).toStrictEqual({
      order: true,
      payment: true,
    });

    http.expectOne(ORDER_URL).flush(anOrder());
    http.expectOne(CURRENT_URL).flush({ title: 'Not Found' }, NOT_FOUND);
    http.expectOne('/api/v1/wallet').flush(aWallet());
    await settle();

    expect(text('.order-line')).toContain(`Order ${ORDER}`);
  });

  it('goes straight to the confirmation for an order that is already paid', async () => {
    await open(aPaymentStatus({ status: 'succeeded', orderStatus: 'paid' }), {
      order: anOrder({ status: 'paid', canPay: false }),
    });
    http.expectOne('/api/v1/cart').flush(aCart([]));
    await settle();

    expect(url()).toBe(`/orders/${ORDER}/confirmation`);
  });
});
