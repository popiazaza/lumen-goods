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
import {
  AdminOrder,
  LINE_NOT_AWAITING_DECISION_PROBLEM,
  PARTNER_CALL_IN_PROGRESS_PROBLEM,
} from '../../core/api/admin-orders.api';
import { OrderDetailDto, RefundStatus } from '../../core/api/order.models';
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

/**
The Kestrel line was accepted a day ago and never decided (BR-ORD-04).
*/
const needingAttention = (): AdminOrder => {
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
};

/**
 * What the API answers after the admin rejected the Kestrel line: refunded, fee included. The
 * refund is `pending` right after the decision and `succeeded` once the refund job paid it back.
 */
const rejected = (refundStatus: RefundStatus = 'pending'): AdminOrder => {
  const before = needingAttention();
  const isPaidBack = refundStatus === 'succeeded';
  const order: OrderDetailDto = {
    ...before.order,
    status: 'partially_refunded',
    statusText: isPaidBack ? 'Partially refunded' : 'Partial refund pending',
    isUpdating: refundStatus === 'pending',
    shipments: [
      before.order.shipments[0],
      {
        ...before.order.shipments[1],
        feeRefunded: true,
        lines: [
          {
            ...SOCKS_LINE,
            status: 'rejected',
            statusText: isPaidBack
              ? 'Not supplied, refunded $22.51'
              : 'Not supplied, refund pending',
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
        status: refundStatus,
        reason: 'Rejected by an admin: Kestrel support says it is lost',
        lineIds: [SOCKS_LINE.id],
        shippingAmount: 4.99,
        createdUtc: '2026-11-21T09:00:00Z',
        completedUtc: isPaidBack ? '2026-11-21T09:00:05Z' : null,
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
};

const text = (root: ParentNode | null | undefined, selector: string) =>
  root?.querySelector(selector)?.textContent?.replaceAll(/\s+/gu, ' ').trim();
const texts = (root: ParentNode, selector: string) =>
  [...root.querySelectorAll(selector)].map((element) =>
    element.textContent?.replaceAll(/\s+/gu, ' ').trim()
  );
const dialog = () => document.querySelector<HTMLElement>('app-confirm-dialog');

describe('AdminOrderDetailPage', () => {
  let fixture: ComponentFixture<AdminOrderDetailPage>;
  let http: HttpTestingController;
  let announcer: LiveAnnouncer;

  const host = () => fixture.nativeElement as HTMLElement;
  const section = (title: string) =>
    [...host().querySelectorAll<HTMLElement>('.section')].find(
      (element) => element.querySelector('h2')?.textContent?.trim() === title
    ) as HTMLElement;
  const button = (label: string) =>
    host().querySelector<HTMLButtonElement>(`button[aria-label="${CSS.escape(label)}"]`);

  const load = async (answer: AdminOrder | { status: number }): Promise<void> => {
    fixture = TestBed.createComponent(AdminOrderDetailPage);
    fixture.componentRef.setInput('id', NUMBER);
    fixture.detectChanges();
    const request = http.expectOne(URL);
    if ('order' in answer) {
      request.flush(answer);
    } else {
      request.flush(
        { title: 'Problem', status: answer.status },
        { status: answer.status, statusText: 'Error' }
      );
    }
    await fixture.whenStable();
  };

  /**
   * Clicks Confirm or Reject on the line, fills in the note and confirms in the dialog; returns the decision the page sends.
   */
  const decide = async (action: 'Confirm' | 'Reject', note: string): Promise<TestRequest> => {
    button(`${action} line 2, Merino Trail Socks`)?.click();
    await fixture.whenStable();
    const textarea = dialog()?.querySelector('textarea') as HTMLTextAreaElement;
    textarea.value = note;
    textarea.dispatchEvent(new Event('input'));
    (dialog()?.querySelector('button.confirm') as HTMLButtonElement).click();
    return vi.waitFor(() => http.expectOne(RESOLVE_URL));
  };

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

    const customer = texts(section('Customer'), 'dd');

    expect({
      email: customer[0],
      failedPaymentAttempts: customer[1],
      sellers: texts(section('Lines'), 'h3'),
      lineStatuses: texts(section('Lines'), 'tbody app-status-badge'),
    }).toStrictEqual({
      email: 'customer@lumengoods.test',
      failedPaymentAttempts: '1',
      sellers: ['Lumen Goods', 'Kestrel Outfitters'],
      lineStatuses: ['Confirmed', 'Awaiting Kestrel Outfitters'],
    });

    const events = [...section('Payment events').querySelectorAll(':scope .timeline > li')];

    expect(events.map((event) => text(event, 'app-status-badge'))).toStrictEqual([
      'Payment started',
      'Declined',
      'Gateway error',
      'Approved',
    ]);

    const [, declined, gatewayError] = events;

    expect({
      amount: text(declined, '.amount'),
      reason: texts(declined, 'p')[0],
      occurredAt: declined.querySelector('time')?.getAttribute('datetime'),
      paymentId: text(declined, 'code'),
    }).toStrictEqual({
      amount: '$75.46',
      reason: 'Card reported stolen',
      occurredAt: '2026-11-20T08:00:30Z',
      paymentId: 'payment-0',
    });
    expect(gatewayError.querySelector('.amount')).toBeNull();
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
      })
    );

    const [kestrel, harbor] = [...section('Partner orders').querySelectorAll('li')];

    expect({
      heading: text(kestrel, 'h3'),
      status: text(kestrel, 'app-status-badge'),
      referencePartnerIdAttempts: texts(kestrel, 'dd').slice(0, 3),
      lastError: kestrel.querySelector('.last-error'),
    }).toStrictEqual({
      heading: 'Line 2: Kestrel Outfitters',
      status: 'Accepted, awaiting decision',
      referencePartnerIdAttempts: ['LG-7K3M9QX2-2', 'KT-5521', '3'],
      lastError: null,
    });
    expect({
      status: text(harbor, 'app-status-badge'),
      referencePartnerIdAttempts: texts(harbor, 'dd').slice(0, 3),
      lastError: text(harbor, '.last-error'),
    }).toStrictEqual({
      status: 'Sent, no answer yet',
      referencePartnerIdAttempts: ['LG-7K3M9QX2-3', 'None yet', '4'],
      lastError: 'Last error: HTTP 503 from Harbor Supply Co.',
    });
  });

  it('shows refunds with their status, the lines and the shipping fee they pay back', async () => {
    await load(rejected('failed'));

    const refunds = host().querySelector(':scope app-order-summary-panel .refunds') as HTMLElement;

    expect(text(refunds, '.amount')).toBe('$22.51');
    // The admin sees what the customer does not: this attempt failed and is being retried.
    expect(text(refunds, 'app-status-badge')).toBe('Failed, will retry');
    expect(texts(refunds, '.breakdown dt')).toStrictEqual(['Merino Trail Socks', 'Shipping fee']);
    expect(texts(refunds, '.breakdown dd')).toStrictEqual(['$17.52', '$4.99']);
    expect(text(section('Partner orders'), 'app-status-badge')).toBe('Closed by an admin');
  });

  it('rejects a line that needs attention after confirmation, then shows the refreshed order', async () => {
    await load(needingAttention());

    expect({
      attention: text(host(), '.attention'),
      partnerOrderStatus: text(section('Partner orders'), 'app-status-badge'),
    }).toStrictEqual({
      attention: expect.stringContaining('1 line needs your decision.'),
      partnerOrderStatus: 'Needs attention',
    });

    const post = await decide('Reject', '  Kestrel support says it is lost ');

    expect({
      dialog: dialog(),
      method: post.request.method,
      body: post.request.body,
    }).toStrictEqual({
      dialog: null,
      method: 'POST',
      body: { decision: 'reject', note: 'Kestrel support says it is lost' },
    });

    post.flush(rejected());
    fixture.detectChanges();

    expect(texts(section('Lines'), 'tbody app-status-badge')).toStrictEqual([
      'Confirmed',
      'Not supplied, refund pending',
    ]);

    // The refund is on its way: the page reads the order again until it is paid back.
    await vi.waitFor(() => http.expectOne(URL).flush(rejected('succeeded')));
    await fixture.whenStable();

    expect({
      lineStatuses: texts(section('Lines'), 'tbody app-status-badge'),
      rejectButton: button('Reject line 2, Merino Trail Socks'),
      attention: host().querySelector('.attention'),
    }).toStrictEqual({
      lineStatuses: ['Confirmed', 'Not supplied, refunded $22.51'],
      rejectButton: null,
      attention: null,
    });
    expect(announcer.announce).toHaveBeenCalledWith(
      'Merino Trail Socks rejected. The customer is refunded for it.',
      'polite'
    );
  });

  it('asks before confirming, and does nothing when the admin keeps waiting', async () => {
    await load(needingAttention());

    button('Confirm line 2, Merino Trail Socks')?.click();
    await fixture.whenStable();

    expect({
      title: text(dialog(), 'h2'),
      alertDialog: dialog()?.closest('[role="alertdialog"]'),
      // The safe choice has focus first.
      focused: document.activeElement?.textContent?.trim(),
    }).toStrictEqual({
      title: 'Confirm Merino Trail Socks?',
      alertDialog: expect.any(Element),
      focused: 'Keep waiting',
    });

    (dialog()?.querySelector('button.cancel') as HTMLButtonElement).click();
    await fixture.whenStable();
    http.expectNone(RESOLVE_URL);

    expect(dialog()).toBeNull();

    const post = await decide('Confirm', '');

    expect(post.request.body).toStrictEqual({ decision: 'confirm', note: null });

    // The API's answer: the line is confirmed and its partner order closed; nothing left to wait for.
    const before = needingAttention();
    post.flush({
      ...before,
      order: {
        ...before.order,
        shipments: [
          before.order.shipments[0],
          {
            ...before.order.shipments[1],
            lines: [
              { ...SOCKS_LINE, status: 'confirmed', statusText: 'Confirmed by Kestrel Outfitters' },
            ],
          },
        ],
      },
      partnerOrders: [aPartnerOrder({ status: 'closed_by_admin', nextAttemptUtc: null })],
    });
    await fixture.whenStable();

    expect(texts(section('Lines'), 'tbody app-status-badge')[1]).toBe(
      'Confirmed by Kestrel Outfitters'
    );

    // Nothing can change on its own any more, so the page does not read the order again.
    http.expectNone(URL);
  });

  it('reloads the order when the line no longer needs attention (409)', async () => {
    await load(needingAttention());

    const post = await decide('Confirm', '');
    post.flush(
      {
        type: LINE_NOT_AWAITING_DECISION_PROBLEM,
        title: 'This line does not need a decision',
        detail: 'Line 2 is not delayed (any more); only a delayed line can be resolved.',
      },
      { status: 409, statusText: 'Conflict' }
    );
    await vi.waitFor(() => http.expectOne(URL).flush(rejected('succeeded')));
    await fixture.whenStable();

    expect(text(host(), '.action-error')).toBe(
      'Couldn’t save the decision for Merino Trail Socks: Line 2 is not delayed (any more); only a delayed line can be resolved.'
    );
    expect(texts(section('Lines'), 'tbody app-status-badge')[1]).toBe(
      'Not supplied, refunded $22.51'
    );
  });

  it('asks to try again in a minute while a call to the partner is in flight (409)', async () => {
    await load(needingAttention());

    const post = await decide('Reject', '');
    post.flush(
      {
        type: PARTNER_CALL_IN_PROGRESS_PROBLEM,
        title: 'A call to the partner is in progress',
        detail: 'A send to Kestrel Outfitters for this line is in progress; try again in a minute.',
      },
      { status: 409, statusText: 'Conflict' }
    );
    await fixture.whenStable();

    const message =
      'Couldn’t save the decision for Merino Trail Socks: Lumen is talking to Kestrel Outfitters about this item right now. Try again in a minute.';

    expect(text(host(), '.action-error')).toBe(message);
    expect(announcer.announce).toHaveBeenCalledWith(message, 'assertive');

    // Nothing changed on the server, so the line still waits and can be decided again.
    http.expectNone(URL);
    const rejectButton = button('Reject line 2, Merino Trail Socks') as HTMLButtonElement;

    expect(rejectButton.disabled).toBeFalsy();
  });

  it('warns when a partner may hold the order without Lumen knowing (outcome unknown)', async () => {
    const order = needingAttention();
    await load({
      ...order,
      partnerOrders: [
        aPartnerOrder({
          status: 'submitted',
          needsAttention: true,
          outcomeUnknown: true,
          acceptedUtc: null,
          nextAttemptUtc: null,
          lastError: 'Timed out after 5 s',
        }),
      ],
    });

    const partner = section('Partner orders');

    expect(text(partner, 'app-status-badge')).toBe('Needs attention: outcome unknown');
    expect(text(partner, '.last-error')).toContain('Outcome unknown:');
    expect(text(partner, '.last-error')).toContain(
      'Check with Kestrel Outfitters before rejecting'
    );

    button('Reject line 2, Merino Trail Socks')?.click();
    await fixture.whenStable();

    expect(text(dialog(), '.message')).toContain('Check with Kestrel Outfitters first.');

    (dialog()?.querySelector('button.cancel') as HTMLButtonElement).click();
    await fixture.whenStable();
  });

  it('says plainly when there is no such order', async () => {
    await load({ status: 404 });

    expect(text(host(), '.state strong')).toBe(`There is no order ${NUMBER}.`);
    expect(host().querySelector(':scope .state a')?.getAttribute('href')).toBe('/admin/orders');
  });

  it.each([401, 403])('asks the user to sign in as an admin on %d', async (status) => {
    await load({ status });

    expect(text(host(), 'app-admin-access-denied strong')).toBe(
      'Sign in as an admin to see this page.'
    );
    expect(host().querySelector('.section')).toBeNull();
  });
});
