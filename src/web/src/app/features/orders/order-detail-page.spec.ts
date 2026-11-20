import { LiveAnnouncer } from '@angular/cdk/a11y';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { OrderDetail, OrderLine } from '../../core/api/orders.api';
import { provideAppIcons } from '../../core/icons/app-icons';
import {
  SOCKS_LINE,
  aSettledOrderDetail,
  anOrderDetail,
  anOrderLine,
} from '../../testing/order-fixtures';
import { ORDER_POLL_INTERVAL_MS, OrderDetailPage } from './order-detail-page';

const NUMBER = 'LG-7K3M9QX2';
const URL = `/api/v1/orders/${NUMBER}`;

describe('OrderDetailPage', () => {
  let fixture: ComponentFixture<OrderDetailPage>;
  let http: HttpTestingController;
  let announcer: LiveAnnouncer;

  const host = () => fixture.nativeElement as HTMLElement;
  const text = (root: ParentNode, selector: string) =>
    root.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();
  /** `<dl>` rows as "label value". */
  const rows = (root: ParentNode, selector: string) =>
    [...root.querySelectorAll(selector)].map((element) =>
      [...element.children].map((child) => child.textContent?.trim()).join(' '),
    );
  const row = (root: ParentNode, selector: string) => rows(root, selector)[0];
  const shipment = (seller: string) =>
    [...host().querySelectorAll<HTMLElement>('.shipment')].find(
      (element) => element.querySelector('h3')?.textContent?.trim() === seller,
    ) as HTMLElement;
  const lineBadges = () =>
    [...host().querySelectorAll<HTMLElement>('.line app-status-badge')].map((badge) => ({
      text: badge.textContent?.trim(),
      icon: badge.querySelector('mat-icon')?.getAttribute('data-mat-icon-name'),
      tone: [...badge.classList].find((name) => name.startsWith('tone-')),
    }));

  /** Lets the resource take the flushed answer (a microtask), then renders. */
  async function settle(): Promise<void> {
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
  }

  function open(): void {
    fixture = TestBed.createComponent(OrderDetailPage);
    fixture.componentRef.setInput('id', NUMBER);
    fixture.detectChanges();
  }

  async function show(order: OrderDetail | { status: number }): Promise<void> {
    open();
    const request = http.expectOne(URL);
    if ('number' in order) {
      request.flush(order);
    } else {
      request.flush(
        { title: 'Problem', status: order.status },
        { status: order.status, statusText: 'Error' },
      );
    }
    await fixture.whenStable();
  }

  function oneLineOrder(line: Partial<OrderLine>): OrderDetail {
    const order = anOrderDetail({ isUpdating: false });
    return {
      ...order,
      shipments: [{ ...order.shipments[1], lines: [{ ...SOCKS_LINE, ...line }] }],
    };
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

    expect(lineBadges()).toEqual([
      { text: line.statusText || 'Needs attention', icon: line.icon, tone: line.tone },
    ]);
  });

  it('groups lines by seller with each shipment method and fee', async () => {
    await show(anOrderDetail({ isUpdating: false }));

    expect(text(shipment('Lumen Goods'), '.shipping')).toBe('Express shipping, $12.99');
    expect(text(shipment('Lumen Goods'), '.line .name')).toBe('Enamel Camp Mug');
    expect(text(shipment('Lumen Goods'), '.line .muted')).toBe('2 × $18.50');
    expect(text(shipment('Lumen Goods'), '.line-total')).toBe('$37.00');
    expect(text(shipment('Kestrel Outfitters'), '.shipping')).toBe('Standard shipping, $4.99');
    expect(text(shipment('Kestrel Outfitters'), '.line .name')).toBe('Merino Trail Socks');
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
        order.shipments[0],
        {
          ...order.shipments[1],
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
    expect(text(kestrel, '.shipping')).toBe('Standard shipping, $4.99 (refunded)');
    expect(kestrel.querySelector('.shipping s')?.textContent).toBe('$4.99');
    expect(text(kestrel, '.line-total s')).toBe('Refunded: $16.22');
    expect(text(host(), '.notice')).toContain('1 item couldn’t be supplied.');

    const summary = host().querySelector('app-order-summary-panel') as HTMLElement;
    expect(row(summary, '.refunded')).toBe('Refunded −$22.51');
    expect(row(summary, '.refunded + .total')).toBe('Net paid $52.95');
    // The line and the shipping fee it paid back, as separate amounts.
    expect(rows(summary, '.breakdown > div')).toEqual([
      'Merino Trail Socks $17.52',
      'Shipping fee $4.99',
    ]);
    expect(text(summary, '.refunds li')).toContain('$22.51');
    expect(text(summary, '.refunds li app-status-badge')).toBe('Refunded');
    expect(text(summary, '.refunds li')).toContain('to Visa ending 1111');
  });

  it('summarises the payment: card brand and last 4, or the wallet', async () => {
    await show(aSettledOrderDetail());
    const summary = () => host().querySelector('app-order-summary-panel') as HTMLElement;
    expect(text(summary(), '.method')).toBe('Visa ending 1111');
    expect(row(summary(), '.total')).toBe('Total charged $75.46');
    // Nothing was refunded, so no refund or net paid rows.
    expect(summary().querySelector('.refunded')).toBeNull();

    fixture.destroy();
    const order = aSettledOrderDetail();
    await show({
      ...order,
      payment: { ...order.payment!, method: 'wallet', cardBrand: null, cardLast4: null },
    });
    expect(text(summary(), '.method')).toBe('Lumen wallet');
  });

  it('refreshes every few seconds while a line is waiting, then stops (BR-ORD-02)', async () => {
    open();
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'setTimeout', 'clearTimeout'] });
    http.expectOne(URL).flush(anOrderDetail());
    await settle();
    expect(text(host(), '.updating')).toBe(
      'Waiting for Kestrel Outfitters to confirm. This page updates by itself.',
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

    expect(lineBadges().map((badge) => badge.text)).toEqual([
      'Confirmed',
      'Confirmed by Kestrel Outfitters',
    ]);
    expect(text(host(), '.updating')).toBe('');
    expect(announcer.announce).toHaveBeenCalledWith(
      'Merino Trail Socks: Confirmed by Kestrel Outfitters',
      'polite',
    );

    // isUpdating is false: no more requests, however long the page stays open.
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
    expect(host().querySelector('.state a')?.getAttribute('href')).toBe('/orders');
    expect(host().querySelector('[role="alert"]')).toBeNull();
  });

  it('asks a signed-out customer to sign in', async () => {
    await show({ status: 401 });

    expect(text(host(), '.state strong')).toBe('Sign in to see this order.');
    expect(host().querySelector('.state a')?.getAttribute('href')).toBe(
      `/sign-in?returnUrl=%2Forders%2F${NUMBER}`,
    );
  });

  it('offers Retry when the order fails to load', async () => {
    await show({ status: 500 });

    (host().querySelector('[role="alert"] button') as HTMLButtonElement).click();
    await vi.waitFor(() => http.expectOne(URL).flush(aSettledOrderDetail()));
    await fixture.whenStable();

    expect(lineBadges()).toHaveLength(2);
  });

  it('lists a line image when there is one', async () => {
    const order = aSettledOrderDetail();
    await show({
      ...order,
      shipments: [
        { ...order.shipments[0], lines: [anOrderLine({ imageUrl: '/media/own/mug.png' })] },
      ],
    });

    expect(host().querySelector('.line img')?.getAttribute('src')).toBe('/media/own/mug.png');
  });
});
