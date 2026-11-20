import { safeReturnUrl, signInQueryParams } from './return-url';

describe('safeReturnUrl', () => {
  it.each(['/checkout/address', '/products/p-1?q=tote', '/orders/LG-1#top', '/'])(
    'follows the in-app path %j',
    (url) => {
      expect(safeReturnUrl(url)).toBe(url);
    },
  );

  it.each([
    'https://evil.example/',
    '//evil.example',
    '/\\evil.example',
    'javascript:alert(1)',
    '/\t/evil.example',
    'checkout',
    '/sign-in?returnUrl=/cart',
    '/register',
    '',
    null,
    undefined,
  ])('falls back for %j', (url) => {
    expect(safeReturnUrl(url, '/fallback')).toBe('/fallback');
  });

  it('builds "Sign in" link params that come back to the current page', () => {
    expect(signInQueryParams('/cart')).toEqual({ returnUrl: '/cart' });
    expect(signInQueryParams('/')).toEqual({});
    expect(signInQueryParams('/sign-in?returnUrl=%2Fcart')).toEqual({});
  });
});
