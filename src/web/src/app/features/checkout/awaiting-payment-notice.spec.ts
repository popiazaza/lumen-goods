import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { OrderDetailDto } from '../../core/api/order.models';
import { ANONYMOUS_SESSION, Session, SessionApi } from '../../core/api/session.api';
import { CheckoutStore } from '../../core/checkout/checkout-store';
import { provideAppIcons } from '../../core/icons/app-icons';
import { CUSTOMER_SESSION } from '../../testing/fixtures';
import { anOrderDetail } from '../../testing/order-fixtures';
import { AwaitingPaymentNotice } from './awaiting-payment-notice';

const AWAITING = anOrderDetail({
  number: 'LG-WAIT1',
  status: 'awaiting_payment',
  statusText: 'Awaiting payment',
  paidUtc: null,
  payment: null,
  canPay: true,
});

describe('AwaitingPaymentNotice', () => {
  let fixture: ComponentFixture<AwaitingPaymentNotice>;
  let http: HttpTestingController;

  const host = () => fixture.nativeElement as HTMLElement;
  const text = () => host().textContent?.replaceAll(/\s+/gu, ' ').trim() ?? '';

  /**
  Signs in as `session`, notes that this checkout placed `orderNumber`, and renders the notice.
  */
  const render = async (session: Session, orderNumber: string | null): Promise<void> => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideAppIcons()],
    });
    http = TestBed.inject(HttpTestingController);
    const ready = TestBed.inject(SessionApi).ready();
    http.expectOne('/api/v1/auth/session').flush(session);
    await ready;
    if (orderNumber) {
      TestBed.inject(CheckoutStore).setOrderNumber(orderNumber);
    }
    fixture = TestBed.createComponent(AwaitingPaymentNotice);
    fixture.detectChanges();
  };

  const answer = async (order: OrderDetailDto): Promise<void> => {
    await vi.waitFor(() => http.expectOne(`/api/v1/orders/${order.number}`).flush(order));
    await fixture.whenStable();
  };

  afterEach(() => {
    http.verify();
    sessionStorage.clear();
  });

  it('says this checkout’s order is waiting for payment and links to its payment step', async () => {
    await render(CUSTOMER_SESSION, 'LG-WAIT1');
    await answer(AWAITING);

    expect(text()).toContain('Order LG-WAIT1 is waiting for payment');
    expect(text()).toContain('Its items are held for that order');
    const link = host().querySelector('a');

    expect(link?.textContent?.trim()).toBe('Continue to payment');
    expect(link?.getAttribute('href')).toBe('/checkout/payment?order=LG-WAIT1');
    expect(host().querySelector('[role="status"]')).not.toBeNull();
  });

  it('stays hidden once the API says the order can no longer be paid', async () => {
    await render(CUSTOMER_SESSION, 'LG-WAIT1');
    await answer({ ...AWAITING, status: 'expired', statusText: 'Expired', canPay: false });

    expect(text()).toBe('');
  });

  it('asks nothing when no order was placed, or nobody is signed in', async () => {
    await render(CUSTOMER_SESSION, null);
    await fixture.whenStable();

    expect(text()).toBe('');

    TestBed.resetTestingModule();
    await render(ANONYMOUS_SESSION, 'LG-WAIT1');
    await fixture.whenStable();

    expect(text()).toBe('');
  });
});
