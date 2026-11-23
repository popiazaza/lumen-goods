import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { OrderDetailDto, RefundStatus } from '../../core/api/order.models';
import { provideAppIcons } from '../../core/icons/app-icons';
import { itemAt } from '../../testing/items';
import { aCardPayment, anOrder, anOrderLine } from '../../testing/order-fixtures';
import { CONFIRMATION_POLL_MS, OrderConfirmationPage } from './order-confirmation-page';

const ORDER_URL = '/api/v1/orders/LG-7K3M9QX2';

const aPaidOrder = (overrides: Partial<OrderDetailDto> = {}): OrderDetailDto => {
  const order = anOrder();
  return anOrder({
    status: 'paid',
    canPay: false,
    isUpdating: true,
    payment: aCardPayment({ amount: 67.46 }),
    shipments: [
      {
        ...itemAt(order.shipments, 0),
        lines: [anOrderLine({ status: 'confirmed', statusText: 'Confirmed' })],
      },
      {
        ...itemAt(order.shipments, 1),
        lines: [
          anOrderLine({
            id: 'order-line-2',
            name: 'Merino Trail Socks',
            quantity: 1,
            subtotal: 16.22,
            status: 'awaiting_partner',
            statusText: 'Awaiting Kestrel Outfitters',
          }),
        ],
      },
    ],
    ...overrides,
  });
};

describe('OrderConfirmationPage', () => {
  let fixture: ComponentFixture<OrderConfirmationPage>;
  let http: HttpTestingController;

  const host = () => fixture.nativeElement as HTMLElement;
  const text = (selector: string) =>
    host().querySelector(selector)?.textContent?.replaceAll(/\s+/gu, ' ').trim();
  const all = (selector: string) =>
    [...host().querySelectorAll(selector)].map((element) =>
      element.textContent?.replaceAll(/\s+/gu, ' ').trim()
    );

  const open = async (order: OrderDetailDto): Promise<void> => {
    fixture = TestBed.createComponent(OrderConfirmationPage);
    fixture.componentRef.setInput('id', 'LG-7K3M9QX2');
    fixture.detectChanges();
    http.expectOne(ORDER_URL).flush(order);
    await fixture.whenStable();
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    fixture?.destroy();
    vi.useRealTimers();
  });

  it('shows the order number, items by seller, amounts charged and how it was paid', async () => {
    await open(aPaidOrder({ isUpdating: false }));

    expect({
      heading: text('h1'),
      number: text('.number'),
      payment: text('.thanks .muted'),
    }).toStrictEqual({
      heading: 'Order LG-7K3M9QX2 confirmed',
      number: 'LG-7K3M9QX2',
      payment: 'Paid by Visa ending 1111: $67.46',
    });
    expect(all('section.group h3')).toStrictEqual([
      'Sold by Lumen Goods',
      'Sold by Kestrel Outfitters',
    ]);
    expect(all('.lines .status')).toStrictEqual(['Confirmed', 'Awaiting Kestrel Outfitters']);
    expect({ labels: all('.totals dt'), amounts: all('.totals dd') }).toStrictEqual({
      labels: ['Subtotal', 'Shipping', 'Tax', 'Total charged'],
      amounts: ['$53.22', '$9.98', '$4.26', '$67.46'],
    });
    expect(host().querySelector(':scope .actions a')?.getAttribute('href')).toBe(
      '/orders/LG-7K3M9QX2'
    );
  });

  it('says what happens next: Lumen packs, and the partner still has to confirm', async () => {
    await open(aPaidOrder({ isUpdating: false }));

    expect(all('.next li')).toStrictEqual([
      'We’re packing your Lumen Goods items.',
      'Waiting for Kestrel Outfitters to confirm. We’ll update this page and your order; if they can’t supply an item, we refund it automatically.',
    ]);
  });

  it('shows a refunded line and the refund against what was charged', async () => {
    const order = aPaidOrder();
    const kestrel = itemAt(order.shipments, 1);
    await open(
      aPaidOrder({
        status: 'partially_refunded',
        isUpdating: false,
        shipments: [
          itemAt(order.shipments, 0),
          {
            ...kestrel,
            feeRefunded: true,
            lines: [
              {
                ...itemAt(kestrel.lines, 0),
                status: 'rejected',
                statusText: 'Not supplied, refunded $17.52',
              },
            ],
          },
        ],
        totals: {
          subtotal: 53.22,
          shipping: 9.98,
          tax: 4.26,
          total: 67.46,
          refunded: 22.51,
          netPaid: 44.95,
        },
      })
    );

    expect(all('.lines .status')[1]).toBe('Not supplied, refunded $17.52');
    expect(all('.group-head .muted')[1]).toBe('Standard shipping: $4.99 (refunded)');
    expect(all('.totals dt').slice(4)).toStrictEqual(['Refunded', 'Net paid']);
    expect(all('.totals dd').slice(4)).toStrictEqual(['−$22.51', '$44.95']);
    expect(all('.next li')[1]).toBe(
      'Kestrel Outfitters couldn’t supply these items, so you don’t pay for them. The refund goes back to how you paid.'
    );
  });

  it('keeps re-reading while a refund is on its way, until it is paid back', async () => {
    const paid = aPaidOrder();
    const kestrel = itemAt(paid.shipments, 1);
    const socks = itemAt(kestrel.lines, 0);
    /**
    Kestrel rejected the socks; the refund is recorded with this status.
    */
    const refunding = (status: RefundStatus, statusText: string): OrderDetailDto =>
      aPaidOrder({
        status: 'partially_refunded',
        // The API counts only a pending refund as updating; a failed one waits for its retry.
        isUpdating: status === 'pending',
        shipments: [
          itemAt(paid.shipments, 0),
          {
            ...kestrel,
            feeRefunded: true,
            lines: [{ ...socks, status: 'rejected', statusText }],
          },
        ],
        refunds: [
          {
            status,
            id: 'refund-1',
            amount: 22.51,
            reason: 'Not supplied by Kestrel Outfitters',
            lineIds: [socks.id],
            shippingAmount: 4.99,
            lines: [{ lineId: socks.id, subtotal: 16.22, tax: 1.3, amount: 17.52 }],
            taxAmount: 1.3,
            createdUtc: '2026-11-20T08:05:00Z',
            completedUtc: status === 'succeeded' ? '2026-11-20T08:06:00Z' : null,
          },
        ],
      });
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    fixture = TestBed.createComponent(OrderConfirmationPage);
    fixture.componentRef.setInput('id', 'LG-7K3M9QX2');
    fixture.detectChanges();
    http.expectOne(ORDER_URL).flush(refunding('pending', 'Not supplied, refund pending'));
    fixture.detectChanges();

    expect(all('.lines .status')[1]).toBe('Not supplied, refund pending');

    await vi.advanceTimersByTimeAsync(CONFIRMATION_POLL_MS);
    http.expectOne(ORDER_URL).flush(refunding('failed', 'Not supplied, refund pending'));
    await vi.advanceTimersByTimeAsync(CONFIRMATION_POLL_MS);
    http.expectOne(ORDER_URL).flush(refunding('succeeded', 'Not supplied, refunded $22.51'));
    fixture.detectChanges();

    expect(all('.lines .status')[1]).toBe('Not supplied, refunded $22.51');

    await vi.advanceTimersByTimeAsync(CONFIRMATION_POLL_MS * 3);
    http.expectNone(ORDER_URL);
  });

  it('re-reads the order while a partner decides, and stops when nothing can change', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    // Not whenStable(): with fake timers, change detection runs on detectChanges() only.
    fixture = TestBed.createComponent(OrderConfirmationPage);
    fixture.componentRef.setInput('id', 'LG-7K3M9QX2');
    fixture.detectChanges();
    http.expectOne(ORDER_URL).flush(aPaidOrder());
    fixture.detectChanges();

    await vi.advanceTimersByTimeAsync(CONFIRMATION_POLL_MS);
    const confirmed = aPaidOrder({ isUpdating: false });
    const kestrelLines = itemAt(confirmed.shipments, 1).lines;
    kestrelLines[0] = {
      ...itemAt(kestrelLines, 0),
      status: 'confirmed',
      statusText: 'Confirmed by Kestrel Outfitters',
    };
    http.expectOne(ORDER_URL).flush(confirmed);
    fixture.detectChanges();

    expect(all('.lines .status')).toStrictEqual(['Confirmed', 'Confirmed by Kestrel Outfitters']);
    expect(all('.next li')[1]).toBe('Kestrel Outfitters confirmed your items and will ship them.');

    await vi.advanceTimersByTimeAsync(CONFIRMATION_POLL_MS * 3);
    http.expectNone(ORDER_URL);
  });

  it('offers Pay now for an order that is not paid yet', async () => {
    await open(anOrder());

    expect(text('h1')).toBe('Order LG-7K3M9QX2 is awaiting payment');
    expect(host().querySelector('a[href^="/checkout/payment"]')?.getAttribute('href')).toBe(
      '/checkout/payment?order=LG-7K3M9QX2'
    );
    // Nothing charged yet: "Total", as on the order page.
    expect(all('.totals dt').at(-1)).toBe('Total');
  });

  it('says the payment is being confirmed while it runs, and refreshes until it is paid', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    fixture = TestBed.createComponent(OrderConfirmationPage);
    fixture.componentRef.setInput('id', 'LG-7K3M9QX2');
    fixture.detectChanges();
    http.expectOne(ORDER_URL).flush(
      anOrder({
        status: 'payment_processing',
        canPay: false,
        isUpdating: true,
        payment: aCardPayment({ status: 'processing', completedUtc: null }),
      })
    );
    fixture.detectChanges();

    expect(text('h1')).toBe('Confirming payment for order LG-7K3M9QX2');
    expect(text('.confirming')).toContain('We’re confirming your payment with the card provider.');
    expect(host().textContent).not.toMatch(/cancelled|not been charged/u);

    await vi.advanceTimersByTimeAsync(CONFIRMATION_POLL_MS);
    http.expectOne(ORDER_URL).flush(aPaidOrder({ isUpdating: false }));
    fixture.detectChanges();

    expect(text('h1')).toBe('Order LG-7K3M9QX2 confirmed');
  });

  it('says the order waits for the customer’s one-time code, and links to where it goes', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    fixture = TestBed.createComponent(OrderConfirmationPage);
    fixture.componentRef.setInput('id', 'LG-7K3M9QX2');
    fixture.detectChanges();
    http.expectOne(ORDER_URL).flush(
      anOrder({
        status: 'payment_processing',
        statusText: 'Waiting for your one-time code',
        canPay: false,
        isUpdating: true,
        payment: aCardPayment({ status: 'requires_action', cardLast4: '0034', completedUtc: null }),
      })
    );
    fixture.detectChanges();

    expect({
      heading: text('h1'),
      status: text('.confirming p'),
      link: host().querySelector('a[href^="/checkout/payment"]')?.getAttribute('href'),
    }).toStrictEqual({
      heading: 'Order LG-7K3M9QX2 is waiting for your one-time code',
      status: 'Waiting for your one-time code',
      link: '/checkout/payment?order=LG-7K3M9QX2',
    });
    expect(host().textContent).not.toContain('Confirming your payment');

    await vi.advanceTimersByTimeAsync(CONFIRMATION_POLL_MS);
    http.expectOne(ORDER_URL).flush(aPaidOrder({ isUpdating: false }));
  });

  it('keeps refreshing a failed payment the card provider has yet to settle', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    fixture = TestBed.createComponent(OrderConfirmationPage);
    fixture.componentRef.setInput('id', 'LG-7K3M9QX2');
    fixture.detectChanges();
    const unsettled = anOrder({
      canPay: false,
      isUpdating: false,
      payment: aCardPayment({ status: 'failed', completedUtc: null }),
    });
    http.expectOne(ORDER_URL).flush(unsettled);
    fixture.detectChanges();

    expect(text('h1')).toBe('Confirming payment for order LG-7K3M9QX2');
    expect(host().textContent).not.toMatch(/cancelled|not been charged/u);

    await vi.advanceTimersByTimeAsync(CONFIRMATION_POLL_MS);
    http.expectOne(ORDER_URL).flush(unsettled);
  });

  it('says cancelled and not charged only for an expired order', async () => {
    await open(anOrder({ status: 'expired', canPay: false }));

    expect(text('h1')).toBe('Order LG-7K3M9QX2 was not paid');
    expect(host().textContent).toContain(
      'This order was not paid, so it was cancelled. You have not been charged.'
    );
  });

  it('never says confirmed while loading or after an error', async () => {
    fixture = TestBed.createComponent(OrderConfirmationPage);
    fixture.componentRef.setInput('id', 'LG-7K3M9QX2');
    fixture.detectChanges();

    expect(text('h1')).toBe('Order LG-7K3M9QX2');

    http.expectOne(ORDER_URL).flush('down', { status: 503, statusText: 'Service Unavailable' });
    await fixture.whenStable();

    expect(text('[role="alert"]')).toContain('We couldn’t load your order.');
    expect(text('h1')).toBe('Order LG-7K3M9QX2');
  });

  it('asks a signed-out visitor to sign in, and comes back here after', async () => {
    fixture = TestBed.createComponent(OrderConfirmationPage);
    fixture.componentRef.setInput('id', 'LG-7K3M9QX2');
    fixture.detectChanges();
    http
      .expectOne(ORDER_URL)
      .flush({ title: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });
    await fixture.whenStable();

    expect({
      heading: text('h1'),
      alert: host().querySelector('[role="alert"]'),
      message: text('p'),
      signIn: host().querySelector('a[href^="/sign-in"]')?.getAttribute('href'),
    }).toStrictEqual({
      heading: 'Order LG-7K3M9QX2',
      alert: null,
      message: 'Sign in to see this order.',
      signIn: '/sign-in?returnUrl=%2Forders%2FLG-7K3M9QX2%2Fconfirmation',
    });
  });

  it('says so when the order is not in the account', async () => {
    fixture = TestBed.createComponent(OrderConfirmationPage);
    fixture.componentRef.setInput('id', 'LG-7K3M9QX2');
    fixture.detectChanges();
    http
      .expectOne(ORDER_URL)
      .flush({ title: 'Not Found' }, { status: 404, statusText: 'Not Found' });
    await fixture.whenStable();

    expect(text('h1')).toBe('Order not found');
  });
});
