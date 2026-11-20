const isControlCharacter = (char: string): boolean => {
  const code = char.codePointAt(0);
  return code !== undefined && (code < 0x20 || code === 0x7f);
};

/**
 * A `returnUrl` query param that is safe to navigate to after sign-in: an in-app path only
 * ("/checkout/address"), never another origin ("//evil.example", "https://...", "/\\evil"), so
 * the sign-in page cannot be used as an open redirect. Anything else falls back to `fallback`.
 */
export const safeReturnUrl = (value: string | null | undefined, fallback = '/'): string => {
  if (!value || !value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\')) {
    return fallback;
  }
  // Control characters (tabs, newlines) are stripped by URL parsers and can hide a "//".
  if ([...value].some((char) => isControlCharacter(char))) {
    return fallback;
  }
  // Signing in again from the sign-in or register page would loop.
  if (/^\/(?:sign-in|register)(?:[/?#]|$)/u.test(value)) {
    return fallback;
  }
  return value;
};

/**
The query params for a "Sign in" link from `currentUrl`, so the shopper comes back after.
*/
export const signInQueryParams = (currentUrl: string): Record<string, string> => {
  const returnUrl = safeReturnUrl(currentUrl, '');
  return returnUrl && returnUrl !== '/' ? { returnUrl } : {};
};
