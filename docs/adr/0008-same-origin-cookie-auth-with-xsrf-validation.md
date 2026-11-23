# 0008 Same-origin cookie auth with explicit XSRF validation

## Status

Accepted, 2026-11-20

## Context

Customers and admins sign in with email and password from the Angular SPA; guest carts merge on sign-in; admin pages need a role.
Reviewers run the system on `http://localhost:8080`, without TLS.
Injected script must not be able to read the session, and another site must not be able to use it (CSRF).

## Considered options

- Bearer tokens held by the SPA (Identity's `MapIdentityApi` tokens, or JWTs we issue ourselves).
- A backend-for-frontend with an external OpenID Connect provider.
- Identity cookies on one origin, relying on `SameSite=Strict` alone.
- Identity cookies on one origin, plus antiforgery tokens validated on every state-changing call (chosen).

## Decision

- ASP.NET Core Identity with a cookie session (`lumen_session`): HttpOnly, `SameSite=Strict`, `Secure` always except in the local compose and development settings (`SameAsRequest`, for plain HTTP), 7 days sliding. The API answers 401 and 403 instead of redirecting. Lockout after 5 failed attempts. Admin endpoints require the admin role policy.
- One origin: nginx serves the SPA and proxies `/api` and `/media` to the API (the Angular dev server proxies the same paths), so there is no CORS configuration at all.
- CSRF: minimal APIs validate antiforgery tokens only for form posts, so an endpoint filter on the whole `/api/v1` group validates `X-XSRF-TOKEN` on every call except GET, HEAD and OPTIONS. `GET /auth/session` puts the token in a readable `XSRF-TOKEN` cookie; sign-in, register and sign-out issue a new one, because tokens are bound to the user. Angular's `HttpClient` echoes it. `SameSite=Strict` is defence in depth, not the defence.
- Demo accounts are seeded only when `Seed:Enabled` is set (compose and development; off in the base configuration), and compose publishes ports on 127.0.0.1 only, because the README lists their passwords.

Why: for a browser app on one origin, Microsoft's guidance and the IETF best practice for browser apps (RFC 10017) prefer a cookie that script cannot read over a token it can. Identity's bearer tokens are not standard JWTs, and issuing our own would mean running a token server.

## Consequences

- Good: injected script cannot take the session away, only act within the open page; the CSP and Angular's sanitisation narrow that further.
- Good: integration tests prove that a call without the header is rejected and that a token from before sign-in is refused after it.
- Good: Data Protection keys are kept on the `api-keys` volume (`DataProtection:KeysPath`), so recreating the API container keeps everyone signed in.
- Bad: GET endpoints skip the check, so no GET may ever change state. That is a convention, and reviews have to hold it.
- Bad: the cookie is a self-contained ticket, so revoking a session waits for Identity's periodic security-stamp check.
- Bad: a second replica needs a shared key ring.
- Bad: no `__Host-` cookie prefix, because it requires `Secure` and the demo runs on HTTP; production behind TLS should add it.
- Later: mobile apps or third-party clients get an OpenID Connect provider and bearer tokens of their own, with the web staying on cookies or moving to a BFF. Passkeys, built into Identity since .NET 10, can be added on top.

## More information

- Code: [IdentityServiceCollectionExtensions.cs](../../src/Lumen.Api/Identity/IdentityServiceCollectionExtensions.cs), [AuthEndpoints.cs](../../src/Lumen.Api/Identity/AuthEndpoints.cs), [AntiforgeryValidationFilter.cs](../../src/Lumen.Api/Infrastructure/Security/AntiforgeryValidationFilter.cs), [Program.cs](../../src/Lumen.Api/Program.cs) (filter on the route group), [SeedOptions.cs](../../src/Lumen.Api/Infrastructure/Data/SeedOptions.cs)
- Web: [nginx.conf](../../src/web/nginx.conf), [proxy.conf.json](../../src/web/proxy.conf.json), [app.config.ts](../../src/web/src/app/app.config.ts); tests: [AuthTests.cs](../../tests/Lumen.IntegrationTests/Identity/AuthTests.cs)
- Microsoft Learn, Prevent CSRF attacks in ASP.NET Core: <https://learn.microsoft.com/aspnet/core/security/anti-request-forgery>; Identity for SPAs: <https://learn.microsoft.com/aspnet/core/security/authentication/identity-api-authorization>
- RFC 10017, OAuth 2.0 for Browser-Based Applications: <https://datatracker.ietf.org/doc/rfc10017/>
