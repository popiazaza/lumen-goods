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

@Component({ template: '<h1>Confirmation</h1>' })
class StubPage {}

const ORDER = 'LG-7K3M9QX2';
const ORDER_URL = `/api/v1/orders/${ORDER}`;
const CURRENT_URL = `${ORDER_URL}/payments/current`;
const NOT_FOUND = { status: 404, statusText: 'Not Found' };

describe('CheckoutPaymentPage', () => {
  let fixture: ComponentFixture<CheckoutPaymentPage>;
  let http: HttpTestingController;
  let announcer: LiveAnnouncer;

  const host = () => fixture.nativeElement as HTMLElement;
  const text = (selector: string) =>
    host().querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();
  const input = (id: string) =>
    host().querySelector<HTMLInputElement>(`#${id}`) as HTMLInputElement;
  const url = () => TestBed.inject(Router).url;

  /** Change detection plus pending timers and promises (the page polls on timers). */
  async function settle(ms = 0): Promise<void> {
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(ms);
    fixture.detectChanges();
  }

  /**
   * Opens the page as after a refresh: the order (`null`: the read answers 404), its current
   * payment and the wallet.
   */
  async function open(
    payment: PaymentStatusDto | null,
    { order = anOrder(), balance = 2000 }: { order?: OrderDetailDto | null; balance?: number } = {},
  ): Promise<void> {
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
  }

  function type(id: string, value: string): void {
    const field = input(id);
    field.value = value;
    field.dispatchEvent(new Event('input'));
  }

  function fillCard(number = '4111111111111111'): void {
    type('card-number', number);
    type('card-name', 'Ada Lovelace');
    type('card-expiry', '0829');
    type('card-cvc', '123');
  }

  async function pay(): Promise<void> {
    host().querySelector<HTMLButtonElement>('#pay-button')?.click();
    await settle();
  }

  /** Answers the next status poll. */
  async function poll(payment: PaymentStatusDto): Promise<void> {
    await settle(PAYMENT_POLL_INTERVAL_MS);
    http.expectOne(CURRENT_URL).flush(payment);
    await settle();
  }

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

      expect(text('.order-line')).toContain(`Order ${ORDER} · Total $67.46`);
      expect(text('#wallet-note')).toBe('Wallet balance: $2,000.00');
      const wallet = host().querySelector<HTMLInputElement>(
        'mat-radio-button[value="wallet"] input',
      );
      expect(wallet?.disabled).toBe(false);
      expect(text('#pay-button')).toBe('Pay $67.46');
    });

    it('disables the wallet, saying why, when the balance does not cover the total', async () => {
      await open(null, { balance: 42.1 });

      const wallet = host().querySelector<HTMLInputElement>(
        'mat-radio-button[value="wallet"] input',
      );
      expect(wallet?.disabled).toBe(true);
      expect(wallet?.getAttribute('aria-describedby')).toContain('wallet-note');
      expect(text('#wallet-note')).toBe(
        'Wallet balance: $42.10 · Not enough to pay $67.46, so pay by card.',
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

      expect(text('#closed-heading')).toBe('This order can’t be paid');
      expect(text('.state p')).toBe(`We couldn’t find order ${ORDER} in your account.`);
    });

    it('pays from the wallet without card details', async () => {
      await open(null);
      host().querySelector<HTMLInputElement>('mat-radio-button[value="wallet"] input')?.click();
      await settle();
      expect(host().querySelector('#card-number')).toBeNull();

      await pay();

      const request = http.expectOne(`${ORDER_URL}/payments`);
      expect(request.request.body).toEqual({ method: 'wallet', card: null });
      request.flush(aPaymentStatus({ method: 'wallet', status: 'processing' }));
      await settle();
      expect(text('#processing-heading')).toBe('Confirming your payment…');
    });
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
        [...host().querySelectorAll('app-error-summary li')].map((li) => li.textContent?.trim()),
      ).toEqual([
        'Enter a valid card number: check it for typos',
        'Enter the expiry date as MM/YY, like 08/29',
        'Enter the 3-digit security code',
      ]);
    });

    it('sends digits only, the expiry as month and year, and never an amount', async () => {
      await open(null);
      fillCard('4111 1111 1111 1111');
      await settle();
      expect(input('card-expiry').value).toBe('08/29');

      await pay();

      const request = http.expectOne(`${ORDER_URL}/payments`);
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({
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
        { status: 400, statusText: 'Bad Request' },
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

      expect(text('#processing-heading')).toBe('Confirming your payment…');
      expect(text('.processing [role="status"] p')).toBe(
        'This can take up to 20 seconds. Keep this page open; refreshing it won’t charge you twice.',
      );
      expect(host().querySelector('#pay-button')).toBeNull();

      await poll(aPaymentStatus({ status: 'processing' }));
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
      await open(aPaymentStatus({ status: 'processing' }));

      expect(text('#processing-heading')).toBe('Confirming your payment…');
      expect(host().querySelector('#card-number')).toBeNull();

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

      await poll(aPaymentStatus({ status: 'processing' }));
      expect(text('#processing-heading')).toBe('Confirming your payment…');
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
        { status: 409, statusText: 'Conflict' },
      );
      await settle();

      expect(text('.banner.in-progress p')).toBe(
        'A payment is already in progress. We’re showing it here; you won’t be charged twice.',
      );
      expect(text('#processing-heading')).toBe('Confirming your payment…');
      await poll(aPaymentStatus({ status: 'processing' }));
    });
  });

  describe('declines', () => {
    async function declineWith(payment: Partial<PaymentStatusDto>): Promise<void> {
      await open(null);
      fillCard('4242000000000018');
      await pay();
      http.expectOne(`${ORDER_URL}/payments`).flush(
        aPaymentStatus({
          status: 'declined',
          retryMode: 'any_method',
          canRetry: true,
          ...payment,
        }),
      );
      await settle();
    }

    it('names insufficient funds, says nothing was charged, how many attempts are left, and keeps the card', async () => {
      await declineWith({ declineMessage: 'Insufficient funds', attemptsLeft: 4 });

      expect(text('.outcome-title')).toBe('Insufficient funds');
      expect(text('.outcome .outcome-title + p')).toBe(
        'Payment declined. You have not been charged. Try another card or pay from your wallet. 4 attempts left.',
      );
      expect(document.activeElement).toBe(host().querySelector('.outcome'));
      expect(input('card-number').value).toBe('4242 0000 0000 0018');
      expect(input('card-name').value).toBe('Ada Lovelace');

      host().querySelector<HTMLButtonElement>('.try-again')?.click();
      await settle();
      expect(document.activeElement).toBe(input('card-number'));
      expect(host().querySelector('.outcome')).toBeNull();
    });

    it('shows only the generic decline for a stolen card', async () => {
      await declineWith({ declineMessage: 'Your card was declined', attemptsLeft: 1 });

      expect(text('.outcome-title')).toBe('Your card was declined');
      expect(text('.outcome')).not.toMatch(/stolen/i);
      expect(text('.outcome')).toContain('1 attempt left.');
    });

    it('allows only the same card payment again when the gateway could not settle a failed one', async () => {
      await declineWith({ status: 'failed', retryMode: 'same_payment_only', declineMessage: null });

      expect(text('.outcome-title')).toBe('We couldn’t confirm your payment');
      const wallet = host().querySelector<HTMLInputElement>(
        'mat-radio-button[value="wallet"] input',
      );
      expect(wallet?.disabled).toBe(true);
      expect(text('#wallet-note')).toBe(
        'Only the same card payment can be tried again, so the wallet can’t be used for this attempt.',
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
        }),
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
        { status: 409, statusText: 'Conflict' },
      );
      await settle();

      expect(text('.outcome-title')).toBe('Try the same card again');
      expect(text('.outcome .outcome-title + p')).toBe(
        'We could not confirm your payment with Visa ending 1111. Try again with that card; you will not be charged twice.',
      );
      expect(text('.outcome')).not.toContain('You have not been charged');
    });

    it('offers the card after a wallet decline, with the wallet balance read again', async () => {
      await open(null);
      host().querySelector<HTMLInputElement>('mat-radio-button[value="wallet"] input')?.click();
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
        }),
      );
      await settle();
      http.expectOne('/api/v1/wallet').flush(aWallet(10));
      await settle();

      expect(text('.outcome-title')).toBe('Insufficient funds');
      expect(text('#wallet-note')).toBe(
        'Wallet balance: $10.00 · Not enough to pay $67.46, so pay by card.',
      );
      expect(host().querySelector('#card-number')).toBeTruthy();
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
        'This order ran out of payment attempts, so it was cancelled. You have not been charged.',
      );
    });
  });

  describe('one-time code', () => {
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

      expect(text('#code-timer')).toBe('Code expires in 5:00');
      expect(host().querySelector('#code-timer')?.getAttribute('role')).toBe('timer');

      await settle(4 * 60_000 + 1000);
      expect(text('#code-timer')).toBe('Code expires in 0:59');
      expect(announcer.announce).toHaveBeenCalledWith('The code expires in 1 minute.', 'polite');

      await settle(60_000);
      expect(host().querySelector('#one-time-code')).toBeNull();
      expect(text('.state .banner.error p')).toBe(
        'The code has expired. You have not been charged.',
      );
      expect(announcer.announce).toHaveBeenCalledWith('The code has expired.', 'assertive');
      expect(
        vi
          .mocked(announcer.announce)
          .mock.calls.filter(([m]) => m === 'The code expires in 1 minute.'),
      ).toHaveLength(1);

      host().querySelector<HTMLButtonElement>('.state .banner.error button')?.click();
      await settle();
      http.expectOne(CURRENT_URL).flush(
        aPaymentStatus({
          status: 'expired',
          retryMode: 'any_method',
          canRetry: true,
          attemptsLeft: 4,
        }),
      );
      await settle();
      expect(text('.outcome-title')).toBe('The code has expired');
      expect(host().querySelector('#card-number')).toBeTruthy();
    });

    it('sends a pasted code without spaces and goes to the confirmation when approved', async () => {
      await open(needsCode());

      type('one-time-code', '123 456');
      host().querySelector<HTMLButtonElement>('.code-form button[type="submit"]')?.click();
      await settle();
      const request = http.expectOne(`${CURRENT_URL}/one-time-code`);
      expect(request.request.body).toEqual({ code: '123456' });
      request.flush(aPaymentStatus({ status: 'succeeded', orderStatus: 'paid' }));
      http.expectOne('/api/v1/cart').flush(aCart([]));
      await settle();

      expect(url()).toBe(`/orders/${ORDER}/confirmation`);
    });

    it('shows the API’s reason when it refuses the code itself (400), staying on the code', async () => {
      await open(needsCode());

      type('one-time-code', '1234');
      host().querySelector<HTMLButtonElement>('.code-form button[type="submit"]')?.click();
      await settle();
      http.expectOne(`${CURRENT_URL}/one-time-code`).flush(
        {
          type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
          title: 'One or more validation errors occurred.',
          status: 400,
          errors: { code: ['Enter the code.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle();

      expect(text('.field-error')).toBe('Enter the code.');
      expect(input('one-time-code')).toBeTruthy();
    });

    it('declines a wrong code and lets the shopper pay again', async () => {
      await open(needsCode());

      type('one-time-code', '654321');
      host().querySelector<HTMLButtonElement>('.code-form button[type="submit"]')?.click();
      await settle();
      http.expectOne(`${CURRENT_URL}/one-time-code`).flush(
        aPaymentStatus({
          status: 'declined',
          declineMessage: 'The code is not correct',
          retryMode: 'any_method',
          canRetry: true,
          attemptsLeft: 4,
        }),
      );
      await settle();

      expect(text('.outcome-title')).toBe('The code is not correct');
      expect(host().querySelector('#card-number')).toBeTruthy();
    });
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
