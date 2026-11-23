import { HttpErrorResponse } from '@angular/common/http';

/**
 * A 429 from one of the API's rate limits (sign-in, registration, Place order, payments, cart
 * changes). Its `detail` says how long to wait. The sign-in lockout is also a 429, but has no such
 * type.
 */
export const RATE_LIMITED_PROBLEM = 'urn:lumen:problem:rate-limited';

/**
RFC 9457 error body returned by Lumen.Api for every 4xx and 5xx.
*/
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  errors?: Record<string, string[]>;
}

/**
 * The `HttpErrorResponse` behind an error, if there is one. Resources may hand back the response
 * itself or wrap it (`error.cause`), so both are checked.
 */
export const httpErrorOf = (error: unknown): HttpErrorResponse | undefined => {
  if (error instanceof HttpErrorResponse) {
    return error;
  }
  // eslint-disable-next-line unicorn/prefer-error-is-error -- Error.isError (ES2026) is outside Angular 22's browser baseline
  if (error instanceof Error && error.cause instanceof HttpErrorResponse) {
    return error.cause;
  }
  return undefined;
};

/**
HTTP status of a failed call, or `undefined` for non-HTTP errors (0 means the network failed).
*/
export const httpStatusOf = (error: unknown): number | undefined => httpErrorOf(error)?.status;

/**
True for 401 (signed out) and 403 (signed in without the right role).
*/
export const isAccessDenied = (error: unknown): boolean => {
  const status = httpStatusOf(error);
  return status === 401 || status === 403;
};

/**
The body of a failed call when it is a JSON object (not text, and not empty).
*/
const objectBodyOf = (error: unknown): object | undefined => {
  const body: unknown = httpErrorOf(error)?.error;
  return typeof body === 'object' && body !== null ? body : undefined;
};

/**
The ProblemDetails body of a failed call, if the API sent one.
*/
export const problemOf = (error: unknown): ProblemDetails | undefined => {
  const body = objectBodyOf(error);
  return body && ('title' in body || 'detail' in body) ? (body as ProblemDetails) : undefined;
};

/**
 * The problem `type` of a failed call, if the API sent one. The API gives several answers the same
 * status (409 has four meanings across checkout and payments), so pages compare this with a
 * `urn:lumen:problem:...` constant. Never test it for presence: ASP.NET fills in an RFC link as
 * the type of every plain problem.
 */
export const problemTypeOf = (error: unknown): string | undefined => {
  const body = objectBodyOf(error);
  return body && 'type' in body && typeof body.type === 'string' ? body.type : undefined;
};

/**
 * The messages of a 400 validation problem, keyed as the API sent them (`shippingAddress.postalCode`,
 * `card.number`, ...); empty when the error is not one.
 */
export const validationErrorsOf = (error: unknown): Readonly<Record<string, string[]>> =>
  httpStatusOf(error) === 400 ? (problemOf(error)?.errors ?? {}) : {};

/**
Human-readable text for a failed call: the API's `detail` or `title`, else the fallback.
*/
export const problemMessage = (error: unknown, fallback: string): string => {
  const problem = problemOf(error);
  return problem?.detail?.trim() || problem?.title?.trim() || fallback;
};
