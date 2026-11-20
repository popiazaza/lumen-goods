import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { noop } from 'rxjs';
import { aCart, aCartLine } from '../../testing/fixtures';
import { CART_CHANGED_PROBLEM, CartDto } from '../api/cart.api';
import { CartStateService } from './cart-state.service';

describe('CartStateService', () => {
  let cart: CartStateService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    cart = TestBed.inject(CartStateService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('loads the cart once and takes the badge count from the server', () => {
    expect(cart.status()).toBe('idle');

    cart.ensureLoaded();
    cart.ensureLoaded();

    expect(cart.status()).toBe('loading');

    http.expectOne('/api/v1/cart').flush(aCart([aCartLine({ quantity: 3 })]));

    expect(cart.status()).toBe('loaded');
    expect(cart.itemCount()).toBe(3);
    expect(cart.isEmpty()).toBeFalsy();
  });

  it('POSTs an add and shows the cart the server answers with (which may cap the quantity)', () => {
    cart.add('harbor-1', 4).subscribe();

    const request = http.expectOne('/api/v1/cart/lines');

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toStrictEqual({ productId: 'harbor-1', quantity: 4 });

    request.flush(aCart([aCartLine({ quantity: 2 })]));

    expect(cart.itemCount()).toBe(2);
  });

  it('re-reads the cart and adds once more after a sign-in elsewhere merged the guest cart (409)', () => {
    let answer: CartDto | undefined;
    cart.add('harbor-1', 1).subscribe((value) => (answer = value));

    http.expectOne('/api/v1/cart/lines').flush(
      {
        type: CART_CHANGED_PROBLEM,
        title: 'Your cart was updated; reload it',
        status: 409,
      },
      { status: 409, statusText: 'Conflict' }
    );
    http.expectOne('/api/v1/cart').flush(aCart([aCartLine({ id: 'saved', quantity: 2 })]));
    http.expectOne('/api/v1/cart/lines').flush(aCart([aCartLine({ id: 'saved', quantity: 3 })]));

    expect(answer?.itemCount).toBe(3);
    expect(cart.itemCount()).toBe(3);
  });

  it('passes other refusals through without retrying', () => {
    let failed: unknown;
    cart.add('harbor-1', 1).subscribe({ error: (error: unknown) => (failed = error) });

    http
      .expectOne('/api/v1/cart/lines')
      .flush(
        { type: 'urn:lumen:problem:cart-quantity-limit', status: 422 },
        { status: 422, statusText: 'Unprocessable Entity' }
      );

    expect(failed).toBeInstanceOf(HttpErrorResponse);

    http.expectNone('/api/v1/cart');
  });

  it('sends quantity changes, removals and acknowledgements to the line', () => {
    cart.setQuantity('line 1', 3).subscribe();
    const put = http.expectOne('/api/v1/cart/lines/line%201');

    expect({ method: put.request.method, body: put.request.body }).toStrictEqual({
      method: 'PUT',
      body: { quantity: 3 },
    });

    put.flush(aCart([aCartLine({ quantity: 3 })]));

    cart.acknowledge('line-1').subscribe();
    const ack = http.expectOne('/api/v1/cart/lines/line-1/acknowledge');

    expect(ack.request.method).toBe('POST');

    ack.flush(aCart([aCartLine()]));

    cart.remove('line-1').subscribe();
    const remove = http.expectOne('/api/v1/cart/lines/line-1');

    expect(remove.request.method).toBe('DELETE');

    remove.flush(aCart([]));

    expect(cart.itemCount()).toBe(0);
    expect(cart.isEmpty()).toBeTruthy();
  });

  it('ignores an answer that arrives after a newer one', () => {
    cart.reload().subscribe();
    cart.add('harbor-1', 1).subscribe();
    const [reload, add] = [
      http.expectOne((r) => r.method === 'GET'),
      http.expectOne((r) => r.method === 'POST'),
    ];

    add.flush(aCart([aCartLine({ quantity: 5 })]));
    reload.flush(aCart([aCartLine({ quantity: 4 })]));

    expect(cart.itemCount()).toBe(5);
  });

  it('keeps the last cart when a refresh fails, and reports an error only without one', () => {
    cart.reload().subscribe({ error: noop });
    http.expectOne('/api/v1/cart').flush('down', { status: 503, statusText: 'Unavailable' });

    expect(cart.status()).toBe('error');

    cart.reload().subscribe();
    http.expectOne('/api/v1/cart').flush(aCart([aCartLine()]));
    cart.reload().subscribe({ error: noop });
    http.expectOne('/api/v1/cart').flush('down', { status: 503, statusText: 'Unavailable' });

    expect(cart.status()).toBe('loaded');
    expect(cart.itemCount()).toBe(1);
  });

  it.each([0, -1, 1.5, NaN])('rejects quantity %d without calling the API', (quantity) => {
    expect(() => cart.add('harbor-1', quantity)).toThrow(RangeError);
  });

  it('needs a product id', () => {
    expect(() => cart.add('', 1)).toThrow('A product id is required to add to the cart.');
  });
});
