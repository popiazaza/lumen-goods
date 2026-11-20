import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { OrderDetailDto } from '../../core/api/order.models';
import { provideAppIcons } from '../../core/icons/app-icons';
import { anOrder, anOrderLine } from '../../testing/fixtures';
import { CONFIRMATION_POLL_MS, OrderConfirmationPage } from './order-confirmation-page';

const ORDER_URL = '/api/v1/orders/LG-7K3M9QX2';

function aPaidOrder(overrides: Partial<OrderDetailDto> = {}): OrderDetailDto {
  const order = anOrder();
  return anOrder({
    status: 'paid',
    statusText: 'Processing',
    paidUtc: '2026-11-20T08:01:10Z',
    canPay: false,
    isUpdating: true,
    payment: {
      paymentId: 'payment-1',
      method: 'card',
      status: 'succeeded',
      amount: 67.46,
      cardBrand: 'visa',
      cardLast4: '1111',
      completedUtc: '2026-11-20T08:01:10Z',
    },
    shipments: [
      {
        ...order.shipments[0],
        lines: [anOrderLine({ status: 'confirmed', statusText: 'Confirmed' })],
      },
      {
        ...order.shipments[1],
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
}

describe('OrderConfirmationPage', () => {
  let fixture: ComponentFixture<OrderConfirmationPage>;
  let http: HttpTestingController;

  const host = () => fixture.nativeElement as HTMLElement;
  const text = (selector: string) =>
    host().querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();
  const all = (selector: string) =>
    [...host().querySelectorAll(selector)].map((element) =>
      element.textContent?.replace(/\s+/g, ' ').trim(),
    );

  async function open(order: OrderDetailDto): Promise<void> {
    fixture = TestBed.createComponent(OrderConfirmationPage);
    fixture.componentRef.setInput('id', 'LG-7K3M9QX2');
    fixture.detectChanges();
    http.expectOne(ORDER_URL).flush(order);
    await fixture.whenStable();
  }

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

    expect(text('h1')).toBe('Order LG-7K3M9QX2 confirmed');
    expect(text('.number')).toBe('LG-7K3M9QX2');
    expect(text('.thanks .muted')).toBe('Paid by Visa ending 1111: $67.46');
    expect(all('section.group h3')).toEqual(['Sold by Lumen Goods', 'Sold by Kestrel Outfitters']);
    expect(all('.lines .status')).toEqual(['Confirmed', 'Awaiting Kestrel Outfitters']);
    expect(all('.totals dt')).toEqual(['Items', 'Shipping', 'Sales tax', 'Total']);
    expect(all('.totals dd')).toEqual(['$53.22', '$9.98', '$4.26', '$67.46']);
    expect(host().querySelector('.actions a')?.getAttribute('href')).toBe('/orders/LG-7K3M9QX2');
  });

  it('says what happens next: Lumen packs, and the partner still has to confirm', async () => {
    await open(aPaidOrder({ isUpdating: false }));

    expect(all('.next li')).toEqual([
      'We’re packing your Lumen Goods items.',
      'Waiting for Kestrel Outfitters to confirm. We’ll update this page and your order; if they can’t supply an item, we refund it automatically.',
    ]);
  });

  it('shows refunds against what was charged', async () => {
    await open(
      aPaidOrder({
        status: 'partially_refunded',
        isUpdating: false,
        totals: {
          subtotal: 53.22,
          shipping: 9.98,
          tax: 4.26,
          total: 67.46,
          refunded: 22.51,
          netPaid: 44.95,
        },
        refunds: [
          {
            id: 'refund-1',
            amount: 22.51,
            status: 'succeeded',
            reason: 'Kestrel Outfitters could not supply Merino Trail Socks',
            lineIds: ['order-line-2'],
            shippingAmount: 4.99,
            createdUtc: '2026-11-20T08:05:00Z',
            completedUtc: '2026-11-20T08:05:02Z',
          },
        ],
      }),
    );

    expect(all('.totals dt').slice(4)).toEqual(['Refunded', 'You paid']);
    expect(all('.totals dd').slice(4)).toEqual(['−$22.51', '$44.95']);
    expect(text('.refund-line')).toBe(
      'Refund of $22.51: Kestrel Outfitters could not supply Merino Trail Socks (done)',
    );
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
    confirmed.shipments[1].lines[0] = {
      ...confirmed.shipments[1].lines[0],
      status: 'confirmed',
      statusText: 'Confirmed by Kestrel Outfitters',
    };
    http.expectOne(ORDER_URL).flush(confirmed);
    fixture.detectChanges();

    expect(all('.lines .status')).toEqual(['Confirmed', 'Confirmed by Kestrel Outfitters']);
    expect(all('.next li')[1]).toBe('Kestrel Outfitters confirmed your items and will ship them.');
    await vi.advanceTimersByTimeAsync(CONFIRMATION_POLL_MS * 3);
    http.expectNone(ORDER_URL);
  });

  it('offers Pay now for an order that is not paid yet', async () => {
    await open(anOrder());

    expect(text('h1')).toBe('Order LG-7K3M9QX2 is awaiting payment');
    expect(host().querySelector('a[href^="/checkout/payment"]')?.getAttribute('href')).toBe(
      '/checkout/payment?order=LG-7K3M9QX2',
    );
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
