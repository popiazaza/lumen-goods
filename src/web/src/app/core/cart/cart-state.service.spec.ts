import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { aCart, aCartLine } from '../../testing/fixtures';
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
    expect(cart.isEmpty()).toBe(false);
  });

  it('POSTs an add and shows the cart the server answers with (which may cap the quantity)', () => {
    cart.add('harbor-1', 4).subscribe();

    const request = http.expectOne('/api/v1/cart/lines');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ productId: 'harbor-1', quantity: 4 });
    request.flush(aCart([aCartLine({ quantity: 2 })]));

    expect(cart.itemCount()).toBe(2);
  });

  it('sends quantity changes, removals and acknowledgements to the line', () => {
    cart.setQuantity('line 1', 3).subscribe();
    const put = http.expectOne('/api/v1/cart/lines/line%201');
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ quantity: 3 });
    put.flush(aCart([aCartLine({ quantity: 3 })]));

    cart.acknowledge('line-1').subscribe();
    const ack = http.expectOne('/api/v1/cart/lines/line-1/acknowledge');
    expect(ack.request.method).toBe('POST');
    ack.flush(aCart());

    cart.remove('line-1').subscribe();
    const remove = http.expectOne('/api/v1/cart/lines/line-1');
    expect(remove.request.method).toBe('DELETE');
    remove.flush(aCart([]));

    expect(cart.itemCount()).toBe(0);
    expect(cart.isEmpty()).toBe(true);
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
    cart.reload().subscribe({ error: () => undefined });
    http.expectOne('/api/v1/cart').flush('down', { status: 503, statusText: 'Unavailable' });
    expect(cart.status()).toBe('error');

    cart.reload().subscribe();
    http.expectOne('/api/v1/cart').flush(aCart());
    cart.reload().subscribe({ error: () => undefined });
    http.expectOne('/api/v1/cart').flush('down', { status: 503, statusText: 'Unavailable' });

    expect(cart.status()).toBe('loaded');
    expect(cart.itemCount()).toBe(1);
  });

  it.each([0, -1, 1.5, Number.NaN])('rejects quantity %d without calling the API', (quantity) => {
    expect(() => cart.add('harbor-1', quantity)).toThrow(RangeError);
  });

  it('needs a product id', () => {
    expect(() => cart.add('', 1)).toThrow();
  });
});
