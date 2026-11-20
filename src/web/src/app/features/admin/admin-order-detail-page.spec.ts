import { LiveAnnouncer } from '@angular/cdk/a11y';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter } from '@angular/router';
import { AdminOrder } from '../../core/api/admin-orders.api';
import { OrderDetailDto } from '../../core/api/order.models';
import { provideAppIcons } from '../../core/icons/app-icons';
import {
  SOCKS_LINE,
  aPartnerOrder,
  anAdminOrder,
  anOrderDetail,
} from '../../testing/order-fixtures';
import { AdminOrderDetailPage } from './admin-order-detail-page';

const NUMBER = 'LG-7K3M9QX2';
const URL = `/api/v1/admin/orders/${NUMBER}`;
const RESOLVE_URL = `${URL}/lines/${SOCKS_LINE.id}/resolve`;

/** The Kestrel line was accepted a day ago and never decided (BR-ORD-04). */
function needingAttention(): AdminOrder {
  const order = anOrderDetail({ isUpdating: false });
  return anAdminOrder({
    order: {
      ...order,
      shipments: [
        order.shipments[0],
        {
          ...order.shipments[1],
          lines: [{ ...SOCKS_LINE, status: 'needs_attention', statusText: 'Delayed' }],
        },
      ],
    },
    partnerOrders: [aPartnerOrder({ needsAttention: true, nextAttemptUtc: null })],
  });
}

/** What the API answers after the admin rejected the Kestrel line: refunded, fee included. */
function rejected(): AdminOrder {
  const before = needingAttention();
  const order: OrderDetailDto = {
    ...before.order,
    status: 'partially_refunded',
    statusText: 'Partially refunded',
    shipments: [
      before.order.shipments[0],
      {
        ...before.order.shipments[1],
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
    totals: { ...before.order.totals, refunded: 22.51, netPaid: 52.95 },
    refunds: [
      {
        id: 'refund-1',
        amount: 22.51,
        status: 'pending',
        reason: 'Rejected by an admin: Kestrel support says it is lost',
        lineIds: [SOCKS_LINE.id],
        shippingAmount: 4.99,
        createdUtc: '2026-11-21T09:00:00Z',
        completedUtc: null,
      },
    ],
  };
  return {
    ...before,
    order,
    partnerOrders: [
      aPartnerOrder({
        status: 'closed_by_admin',
        needsAttention: false,
        decidedUtc: '2026-11-21T09:00:00Z',
        nextAttemptUtc: null,
      }),
    ],
  };
}

describe('AdminOrderDetailPage', () => {
  let fixture: ComponentFixture<AdminOrderDetailPage>;
  let http: HttpTestingController;
  let announcer: LiveAnnouncer;

  const host = () => fixture.nativeElement as HTMLElement;
  const text = (root: ParentNode | null | undefined, selector: string) =>
    root?.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();
  const texts = (root: ParentNode, selector: string) =>
    [...root.querySelectorAll(selector)].map((element) =>
      element.textContent?.replace(/\s+/g, ' ').trim(),
    );
  const section = (title: string) =>
    [...host().querySelectorAll<HTMLElement>('.section')].find(
      (element) => element.querySelector('h2')?.textContent?.trim() === title,
    ) as HTMLElement;
  const button = (label: string) =>
    host().querySelector<HTMLButtonElement>(`button[aria-label="${label}"]`);
  const dialog = () => document.querySelector<HTMLElement>('app-confirm-dialog');

  async function load(answer: AdminOrder | { status: number }): Promise<void> {
    fixture = TestBed.createComponent(AdminOrderDetailPage);
    fixture.componentRef.setInput('id', NUMBER);
    fixture.detectChanges();
    const request = http.expectOne(URL);
    if ('order' in answer) {
      request.flush(answer);
    } else {
      request.flush(
        { title: 'Problem', status: answer.status },
        { status: answer.status, statusText: 'Error' },
      );
    }
    await fixture.whenStable();
  }

  /**
   * Clicks Confirm or Reject on the line, fills in the note and confirms in the dialog; returns
   * the decision the page sends.
   */
  async function decide(action: 'Confirm' | 'Reject', note: string): Promise<TestRequest> {
    button(`${action} line 2, Merino Trail Socks`)?.click();
    await fixture.whenStable();
    const textarea = dialog()?.querySelector('textarea') as HTMLTextAreaElement;
    textarea.value = note;
    textarea.dispatchEvent(new Event('input'));
    (dialog()?.querySelector('button.confirm') as HTMLButtonElement).click();
    return vi.waitFor(() => http.expectOne(RESOLVE_URL));
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
        // jsdom fires no animationend, so the dialog would never finish closing.
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    announcer = TestBed.inject(LiveAnnouncer);
    vi.spyOn(announcer, 'announce').mockResolvedValue(undefined);
  });

  afterEach(() => {
    http.verify();
    vi.restoreAllMocks();
  });

  it('shows the customer, the lines and the payment timeline with the real reasons', async () => {
    const order = anAdminOrder({
      failedPaymentAttempts: 1,
      paymentEvents: [
        ...anAdminOrder().paymentEvents.slice(0, 1),
        {
          occurredUtc: '2026-11-20T08:00:30Z',
          paymentId: 'payment-0',
          type: 'declined',
          amount: 75.46,
          detail: 'Card reported stolen',
        },
        ...anAdminOrder().paymentEvents.slice(1),
      ],
    });
    await load(order);

    expect(text(section('Customer'), 'dd')).toBe('customer@lumengoods.test');
    expect(texts(section('Customer'), 'dd')[1]).toBe('1');
    expect(texts(section('Lines'), 'h3')).toEqual(['Lumen Goods', 'Kestrel Outfitters']);
    expect(texts(section('Lines'), 'tbody app-status-badge')).toEqual([
      'Confirmed',
      'Awaiting Kestrel Outfitters',
    ]);

    const events = [...section('Payment events').querySelectorAll('.timeline > li')];
    expect(events.map((event) => text(event, 'app-status-badge'))).toEqual([
      'Payment started',
      'Declined',
      'Gateway error',
      'Approved',
    ]);
    expect(text(events[1], '.amount')).toBe('$75.46');
    expect(texts(events[1], 'p')[0]).toBe('Card reported stolen');
    expect(events[1].querySelector('time')?.getAttribute('datetime')).toBe('2026-11-20T08:00:30Z');
    expect(text(events[1], 'code')).toBe('payment-0');
    expect(events[2].querySelector('.amount')).toBeNull();
  });

  it('shows each partner order with reference, partner id, status, attempts and last error', async () => {
    await load(
      anAdminOrder({
        partnerOrders: [
          aPartnerOrder(),
          aPartnerOrder({
            id: 'partner-order-2',
            lineNumber: 3,
            source: 'harbor',
            sellerName: 'Harbor Supply Co.',
            externalRef: 'LG-7K3M9QX2-3',
            partnerOrderId: null,
            status: 'submitted',
            attempts: 4,
            lastError: 'HTTP 503 from Harbor Supply Co.',
            acceptedUtc: null,
          }),
        ],
      }),
    );

    const [kestrel, harbor] = [...section('Partner orders').querySelectorAll('li')];
    expect(text(kestrel, 'h3')).toBe('Line 2: Kestrel Outfitters');
    expect(text(kestrel, 'app-status-badge')).toBe('Accepted, awaiting decision');
    expect(texts(kestrel, 'dd').slice(0, 3)).toEqual(['LG-7K3M9QX2-2', 'KT-5521', '3']);
    expect(kestrel.querySelector('.last-error')).toBeNull();

    expect(text(harbor, 'app-status-badge')).toBe('Sent, no answer yet');
    expect(texts(harbor, 'dd').slice(0, 3)).toEqual(['LG-7K3M9QX2-3', 'None yet', '4']);
    expect(text(harbor, '.last-error')).toBe('Last error: HTTP 503 from Harbor Supply Co.');
  });

  it('shows refunds with their status, the lines and the shipping fee they pay back', async () => {
    await load(rejected());

    const refunds = host().querySelector('app-order-summary-panel .refunds') as HTMLElement;
    expect(text(refunds, '.amount')).toBe('$22.51');
    expect(text(refunds, 'app-status-badge')).toBe('Pending');
    expect(texts(refunds, '.breakdown dt')).toEqual(['Merino Trail Socks', 'Shipping fee']);
    expect(texts(refunds, '.breakdown dd')).toEqual(['$17.52', '$4.99']);
    expect(text(section('Partner orders'), 'app-status-badge')).toBe('Closed by an admin');
  });

  it('rejects a line that needs attention after confirmation, then shows the refreshed order', async () => {
    await load(needingAttention());
    expect(text(host(), '.attention')).toContain('1 line needs your decision.');
    expect(text(section('Partner orders'), 'app-status-badge')).toBe('Needs attention');

    const post = await decide('Reject', '  Kestrel support says it is lost ');
    expect(dialog()).toBeNull();
    expect(post.request.method).toBe('POST');
    expect(post.request.body).toEqual({
      decision: 'reject',
      note: 'Kestrel support says it is lost',
    });
    post.flush(rejected());
    await fixture.whenStable();

    expect(texts(section('Lines'), 'tbody app-status-badge')).toEqual([
      'Confirmed',
      'Not supplied, refunded $22.51',
    ]);
    expect(button('Reject line 2, Merino Trail Socks')).toBeNull();
    expect(host().querySelector('.attention')).toBeNull();
    expect(announcer.announce).toHaveBeenCalledWith(
      'Merino Trail Socks rejected. The customer is refunded for it.',
      'polite',
    );
  });

  it('asks before confirming, and does nothing when the admin keeps waiting', async () => {
    await load(needingAttention());

    button('Confirm line 2, Merino Trail Socks')?.click();
    await fixture.whenStable();
    expect(text(dialog(), 'h2')).toBe('Confirm Merino Trail Socks?');
    expect(dialog()?.closest('[role="alertdialog"]')).not.toBeNull();
    // The safe choice has focus first.
    expect(document.activeElement?.textContent?.trim()).toBe('Keep waiting');

    (dialog()?.querySelector('button.cancel') as HTMLButtonElement).click();
    await fixture.whenStable();
    http.expectNone(RESOLVE_URL);
    expect(dialog()).toBeNull();

    const post = await decide('Confirm', '');
    expect(post.request.body).toEqual({ decision: 'confirm', note: null });
    post.flush(anAdminOrder());
    await fixture.whenStable();
    expect(texts(section('Lines'), 'tbody app-status-badge')[1]).toBe(
      'Awaiting Kestrel Outfitters',
    );
  });

  it('reloads the order when the line no longer needs attention (409)', async () => {
    await load(needingAttention());

    (await decide('Confirm', '')).flush(
      { title: 'Conflict', detail: 'Line 2 does not need attention.' },
      { status: 409, statusText: 'Conflict' },
    );
    await vi.waitFor(() => http.expectOne(URL).flush(rejected()));
    await fixture.whenStable();

    expect(text(host(), '.action-error')).toBe(
      'Couldn’t save the decision for Merino Trail Socks: Line 2 does not need attention.',
    );
    expect(texts(section('Lines'), 'tbody app-status-badge')[1]).toBe(
      'Not supplied, refunded $22.51',
    );
  });

  it('says plainly when there is no such order', async () => {
    await load({ status: 404 });

    expect(text(host(), '.state strong')).toBe(`There is no order ${NUMBER}.`);
    expect(host().querySelector('.state a')?.getAttribute('href')).toBe('/admin/orders');
  });

  it.each([401, 403])('asks the user to sign in as an admin on %d', async (status) => {
    await load({ status });

    expect(text(host(), 'app-admin-access-denied strong')).toBe(
      'Sign in as an admin to see this page.',
    );
    expect(host().querySelector('.section')).toBeNull();
  });
});
