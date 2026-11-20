/**
 * A `returnUrl` query param that is safe to navigate to after sign-in: an in-app path only
 * ("/checkout/address"), never another origin ("//evil.example", "https://...", "/\\evil"), so
 * the sign-in page cannot be used as an open redirect. Anything else falls back to `fallback`.
 */
export function safeReturnUrl(value: string | null | undefined, fallback = '/'): string {
  if (!value || !value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\')) {
    return fallback;
  }
  // Control characters (tabs, newlines) are stripped by URL parsers and can hide a "//".
  if ([...value].some((char) => char.charCodeAt(0) < 0x20 || char.charCodeAt(0) === 0x7f)) {
    return fallback;
  }
  // Signing in again from the sign-in or register page would loop.
  if (/^\/(sign-in|register)(?:[/?#]|$)/.test(value)) {
    return fallback;
  }
  return value;
}

/** The query params for a "Sign in" link from `currentUrl`, so the shopper comes back after. */
export function signInQueryParams(currentUrl: string): Record<string, string> {
  const returnUrl = safeReturnUrl(currentUrl, '');
  return returnUrl && returnUrl !== '/' ? { returnUrl } : {};
}
