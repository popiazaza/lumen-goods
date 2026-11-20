import { LiveAnnouncer } from '@angular/cdk/a11y';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { OrderDetailDto, OrderLineDto, RefundStatus } from '../../core/api/order.models';
import { provideAppIcons } from '../../core/icons/app-icons';
import { itemAt } from '../../testing/items';
import {
  SOCKS_LINE,
  aCardPayment,
  aSettledOrderDetail,
  anOrderDetail,
  anOrderLine,
} from '../../testing/order-fixtures';
import { ORDER_POLL_INTERVAL_MS, OrderDetailPage } from './order-detail-page';

const NUMBER = 'LG-7K3M9QX2';
const URL = `/api/v1/orders/${NUMBER}`;

const text = (root: ParentNode, selector: string) =>
  root.querySelector(selector)?.textContent?.replaceAll(/\s+/gu, ' ').trim();
/**
`<dl>` rows as "label value".
*/
const rows = (root: ParentNode, selector: string) =>
  [...root.querySelectorAll(selector)].map((element) =>
    [...element.children].map((child) => child.textContent?.trim()).join(' ')
  );
const row = (root: ParentNode, selector: string) => rows(root, selector)[0];

const oneLineOrder = (line: Partial<OrderLineDto>): OrderDetailDto => {
  const order = anOrderDetail({ isUpdating: false });
  return {
    ...order,
    shipments: [{ ...itemAt(order.shipments, 1), lines: [{ ...SOCKS_LINE, ...line }] }],
  };
};

describe('OrderDetailPage', () => {
  let fixture: ComponentFixture<OrderDetailPage>;
  let http: HttpTestingController;
  let announcer: LiveAnnouncer;

  const host = () => fixture.nativeElement as HTMLElement;
  const summary = () => host().querySelector('app-order-summary-panel') as HTMLElement;
  const shipment = (seller: string) =>
    [...host().querySelectorAll<HTMLElement>('.shipment')].find(
      (element) => element.querySelector('h3')?.textContent?.trim() === seller
    ) as HTMLElement;
  const lineBadges = () =>
    [...host().querySelectorAll<HTMLElement>(':scope .line app-status-badge')].map((badge) => ({
      text: badge.textContent?.trim(),
      icon: badge.querySelector('mat-icon')?.getAttribute('data-mat-icon-name'),
      tone: [...badge.classList].find((name) => name.startsWith('tone-')),
    }));
  /**
  The Kestrel line's status, the refund's badge and the updating note, while a refund is paid back.
  */
  const refundProgress = () => ({
    line: itemAt(lineBadges(), 1).text,
    refund: text(summary(), '.refunds li app-status-badge'),
    updating: text(host(), '.updating'),
  });

  /**
  Lets the resource take the flushed answer (a microtask), then renders.
  */
  const settle = async (): Promise<void> => {
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
  };

  const open = (): void => {
    fixture = TestBed.createComponent(OrderDetailPage);
    fixture.componentRef.setInput('id', NUMBER);
    fixture.detectChanges();
  };

  const show = async (order: OrderDetailDto | { status: number }): Promise<void> => {
    open();
    const request = http.expectOne(URL);
    if ('number' in order) {
      request.flush(order);
    } else {
      request.flush(
        { title: 'Problem', status: order.status },
        { status: order.status, statusText: 'Error' }
      );
    }
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
    announcer = TestBed.inject(LiveAnnouncer);
    vi.spyOn(announcer, 'announce').mockResolvedValue(undefined);
  });

  afterEach(() => {
    http.verify();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  // BR-ORD-02: each line's own status, as icon and text (never colour alone).
  it.each([
    { status: 'pending', statusText: 'Awaiting payment', icon: 'schedule', tone: 'tone-neutral' },
    {
      status: 'awaiting_partner',
      statusText: 'Awaiting Kestrel Outfitters',
      icon: 'schedule',
      tone: 'tone-info',
    },
    {
      status: 'confirmed',
      statusText: 'Confirmed by Kestrel Outfitters',
      icon: 'check_circle',
      tone: 'tone-success',
    },
    {
      status: 'rejected',
      statusText: 'Not supplied, refunded $40.03',
      icon: 'cancel',
      tone: 'tone-neutral',
    },
    { status: 'needs_attention', statusText: 'Delayed', icon: 'warning', tone: 'tone-warning' },
    { status: 'needs_attention', statusText: '', icon: 'warning', tone: 'tone-warning' },
  ] as const)('shows a $status line as "$statusText" with the $icon icon', async (line) => {
    await show(oneLineOrder({ status: line.status, statusText: line.statusText }));

    expect(lineBadges()).toStrictEqual([
      { text: line.statusText || 'Needs attention', icon: line.icon, tone: line.tone },
    ]);
  });

  it('groups lines by seller with each shipment method and fee', async () => {
    await show(anOrderDetail({ isUpdating: false }));

    const lumen = shipment('Lumen Goods');
    const kestrel = shipment('Kestrel Outfitters');

    expect({
      lumen: {
        shipping: text(lumen, '.shipping'),
        name: text(lumen, '.line .name'),
        quantity: text(lumen, '.line .muted'),
        lineTotal: text(lumen, '.line-total'),
      },
      kestrel: { shipping: text(kestrel, '.shipping'), name: text(kestrel, '.line .name') },
    }).toStrictEqual({
      lumen: {
        shipping: 'Express shipping, $12.99',
        name: 'Enamel Camp Mug',
        quantity: '2 × $18.50',
        lineTotal: '$37.00',
      },
      kestrel: { shipping: 'Standard shipping, $4.99', name: 'Merino Trail Socks' },
    });
    expect(text(host(), '.overview')).toContain('Processing');
  });

  it('shows a rejected line, its refunded shipping fee, the refund and the net paid', async () => {
    const order = anOrderDetail();
    await show({
      ...order,
      status: 'partially_refunded',
      statusText: 'Partially refunded',
      isUpdating: false,
      shipments: [
        itemAt(order.shipments, 0),
        {
          ...itemAt(order.shipments, 1),
          feeRefunded: true,
          lines: [
            {
              ...SOCKS_LINE,
              status: 'rejected',
              statusText: 'Not supplied, refunded $22.51',
              refundedAmount: 17.52,
            },
          ],
        },
      ],
      totals: { ...order.totals, refunded: 22.51, netPaid: 52.95 },
      refunds: [
        {
          id: 'refund-1',
          amount: 22.51,
          status: 'succeeded',
          reason: 'Kestrel Outfitters could not supply Merino Trail Socks',
          lineIds: [SOCKS_LINE.id],
          shippingAmount: 4.99,
          createdUtc: '2026-11-20T08:05:00Z',
          completedUtc: '2026-11-20T08:05:01Z',
        },
      ],
    });

    const kestrel = shipment('Kestrel Outfitters');

    expect({
      shipping: text(kestrel, '.shipping'),
      struckFee: kestrel.querySelector(':scope .shipping s')?.textContent,
      lineTotal: text(kestrel, '.line-total s'),
    }).toStrictEqual({
      shipping: 'Standard shipping, $4.99 (refunded)',
      struckFee: '$4.99',
      lineTotal: 'You don’t pay: $16.22',
    });
    expect(text(host(), '.notice')).toContain('1 item couldn’t be supplied.');
    expect({
      refunded: row(summary(), '.refunded'),
      netPaid: row(summary(), '.refunded + .total'),
      // The line and the shipping fee it paid back, as separate amounts.
      breakdown: rows(summary(), '.breakdown > div'),
      refundBadge: text(summary(), '.refunds li app-status-badge'),
    }).toStrictEqual({
      refunded: 'Refunded −$22.51',
      netPaid: 'Net paid $52.95',
      breakdown: ['Merino Trail Socks $17.52', 'Shipping fee $4.99'],
      refundBadge: 'Refunded',
    });

    const refund = text(summary(), '.refunds li');

    expect(refund).toContain('$22.51');
    expect(refund).toContain('to Visa ending 1111');
  });

  it('summarises the payment: card brand and last 4, or the wallet', async () => {
    await show(aSettledOrderDetail());

    expect(text(summary(), '.method')).toBe('Visa ending 1111');
    expect(row(summary(), '.total')).toBe('Total charged $75.46');
    // Nothing was refunded, so no refund or net paid rows.
    expect(summary().querySelector('.refunded')).toBeNull();

    fixture.destroy();
    await show(
      aSettledOrderDetail({
        payment: aCardPayment({ method: 'wallet', cardBrand: null, cardLast4: null }),
      })
    );

    expect(text(summary(), '.method')).toBe('Lumen wallet');
  });

  it('refreshes every few seconds while a line is waiting, then stops (BR-ORD-02)', async () => {
    open();
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'setTimeout', 'clearTimeout'] });
    http.expectOne(URL).flush(anOrderDetail());
    await settle();

    expect(text(host(), '.updating')).toBe(
      'Waiting for Kestrel Outfitters to confirm. This page updates by itself.'
    );

    // Not yet: the next read is one interval away.
    await vi.advanceTimersByTimeAsync(ORDER_POLL_INTERVAL_MS - 1);
    http.expectNone(URL);

    await vi.advanceTimersByTimeAsync(1);
    http.expectOne(URL).flush(anOrderDetail());
    fixture.detectChanges();

    expect(announcer.announce).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(ORDER_POLL_INTERVAL_MS);
    http.expectOne(URL).flush(aSettledOrderDetail());
    fixture.detectChanges();

    expect(lineBadges().map((badge) => badge.text)).toStrictEqual([
      'Confirmed',
      'Confirmed by Kestrel Outfitters',
    ]);
    expect(text(host(), '.updating')).toBe('');
    expect(announcer.announce).toHaveBeenCalledWith(
      'Merino Trail Socks: Confirmed by Kestrel Outfitters',
      'polite'
    );

    // isUpdating is false: no more requests, however long the page stays open.
    await vi.advanceTimersByTimeAsync(ORDER_POLL_INTERVAL_MS * 10);
    http.expectNone(URL);
  });

  it('refreshes while a refund is on its way back, failed retries included, until it is paid', async () => {
    const order = aSettledOrderDetail();
    /**
    Kestrel could not supply the socks; the refund for them has this status.
    */
    const refunding = (status: RefundStatus, statusText: string): OrderDetailDto => ({
      ...order,
      status: 'partially_refunded',
      statusText: status === 'succeeded' ? 'Partially refunded' : 'Partial refund pending',
      // The API counts only a pending refund as updating; a failed one waits for its retry.
      isUpdating: status === 'pending',
      shipments: [
        itemAt(order.shipments, 0),
        {
          ...itemAt(order.shipments, 1),
          feeRefunded: true,
          lines: [{ ...SOCKS_LINE, status: 'rejected', statusText, refundedAmount: 17.52 }],
        },
      ],
      totals: { ...order.totals, refunded: 22.51, netPaid: 52.95 },
      refunds: [
        {
          status,
          id: 'refund-1',
          amount: 22.51,
          reason: 'Not supplied by Kestrel Outfitters',
          lineIds: [SOCKS_LINE.id],
          shippingAmount: 4.99,
          createdUtc: '2026-11-20T08:05:00Z',
          completedUtc: status === 'succeeded' ? '2026-11-20T08:06:00Z' : null,
        },
      ],
    });

    open();
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'setTimeout', 'clearTimeout'] });
    http.expectOne(URL).flush(refunding('pending', 'Not supplied, refund pending'));
    await settle();

    expect(refundProgress()).toStrictEqual({
      line: 'Not supplied, refund pending',
      refund: 'Pending',
      updating:
        'Your refund is on its way back to your Visa ending 1111. This page updates by itself.',
    });

    // A failed attempt is retried by the API: still pending for the customer, still refreshing.
    await vi.advanceTimersByTimeAsync(ORDER_POLL_INTERVAL_MS);
    http.expectOne(URL).flush(refunding('failed', 'Not supplied, refund pending'));
    fixture.detectChanges();

    expect(refundProgress()).toStrictEqual({
      line: 'Not supplied, refund pending',
      refund: 'Pending',
      updating: expect.stringContaining('Your refund is on its way back'),
    });

    await vi.advanceTimersByTimeAsync(ORDER_POLL_INTERVAL_MS);
    http.expectOne(URL).flush(refunding('succeeded', 'Not supplied, refunded $22.51'));
    fixture.detectChanges();

    expect(refundProgress()).toStrictEqual({
      line: 'Not supplied, refunded $22.51',
      refund: 'Refunded',
      updating: '',
    });
    expect(announcer.announce).toHaveBeenCalledWith(
      'Merino Trail Socks: Not supplied, refunded $22.51',
      'polite'
    );

    await vi.advanceTimersByTimeAsync(ORDER_POLL_INTERVAL_MS * 10);
    http.expectNone(URL);
  });

  it('keeps showing the order when a refresh fails, and tries again', async () => {
    open();
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'setTimeout', 'clearTimeout'] });
    http.expectOne(URL).flush(anOrderDetail());
    fixture.detectChanges();

    await vi.advanceTimersByTimeAsync(ORDER_POLL_INTERVAL_MS);
    http.expectOne(URL).flush('down', { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();

    expect(text(host(), '.line .name')).toBe('Enamel Camp Mug');
    expect(host().querySelector('[role="alert"]')).toBeNull();

    await vi.advanceTimersByTimeAsync(ORDER_POLL_INTERVAL_MS);
    http.expectOne(URL).flush(aSettledOrderDetail());
    await vi.advanceTimersByTimeAsync(ORDER_POLL_INTERVAL_MS * 3);
    http.expectNone(URL);
  });

  it('stops refreshing when the customer leaves the page', async () => {
    open();
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'setTimeout', 'clearTimeout'] });
    http.expectOne(URL).flush(anOrderDetail());
    fixture.detectChanges();

    fixture.destroy();
    await vi.advanceTimersByTimeAsync(ORDER_POLL_INTERVAL_MS * 3);
    http.expectNone(URL);
  });

  it('says plainly when the order does not exist (or is someone else’s)', async () => {
    await show({ status: 404 });

    expect(text(host(), 'h1')).toBe(`Order ${NUMBER}`);
    expect(text(host(), '.state strong')).toBe(`We can’t find order ${NUMBER}.`);
    expect(host().querySelector(':scope .state a')?.getAttribute('href')).toBe('/orders');
    expect(host().querySelector('[role="alert"]')).toBeNull();
  });

  it('asks a signed-out customer to sign in', async () => {
    await show({ status: 401 });

    expect(text(host(), '.state strong')).toBe('Sign in to see this order.');
    expect(host().querySelector(':scope .state a')?.getAttribute('href')).toBe(
      `/sign-in?returnUrl=%2Forders%2F${NUMBER}`
    );
  });

  it('offers Retry when the order fails to load', async () => {
    await show({ status: 500 });

    (host().querySelector(':scope [role="alert"] button') as HTMLButtonElement).click();
    await vi.waitFor(() => http.expectOne(URL).flush(aSettledOrderDetail()));
    await fixture.whenStable();

    expect(lineBadges()).toHaveLength(2);
  });

  it('lists a line image when there is one', async () => {
    const order = aSettledOrderDetail();
    await show({
      ...order,
      shipments: [
        { ...itemAt(order.shipments, 0), lines: [anOrderLine({ imageUrl: '/media/own/mug.png' })] },
      ],
    });

    expect(host().querySelector(':scope .line img')?.getAttribute('src')).toBe(
      '/media/own/mug.png'
    );
  });
});
