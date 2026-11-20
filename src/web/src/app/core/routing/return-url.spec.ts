import { safeReturnUrl, signInQueryParams } from './return-url';

describe('safeReturnUrl', () => {
  it.each(['/checkout/address', '/products/p-1?q=tote', '/orders/LG-1#top', '/'])(
    'follows the in-app path %j',
    (url) => {
      expect(safeReturnUrl(url)).toBe(url);
    }
  );

  it.each([
    'https://evil.example/',
    '//evil.example',
    '/\\evil.example',
    // eslint-disable-next-line no-script-url -- the attack input this test proves is rejected
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
    expect(signInQueryParams('/cart')).toStrictEqual({ returnUrl: '/cart' });
    expect(signInQueryParams('/')).toStrictEqual({});
    expect(signInQueryParams('/sign-in?returnUrl=%2Fcart')).toStrictEqual({});
  });
});
