# Lumen Goods web

The storefront and admin UI: Angular 22.3, zoneless, [spartan/ui](https://www.spartan.ng/) (shadcn/ui for Angular) with Tailwind CSS 4 and Lucide icons, Vitest.

```bash
npm ci
npm start        # ng serve on http://localhost:4200; /api and /media proxy to the API on 127.0.0.1:5080
npm test         # unit tests, single run
npm run lint     # Ultracite: ESLint (incl. template accessibility rules), Prettier, Stylelint; type-checks e2e/
npm run fix      # Ultracite autofix and formatting
npm run build    # production build
```

## Browser tests

Playwright Test in `e2e/` drives the running compose stack (`docker compose up --build -d --wait` from the repository root) in Chromium, at desktop width and on a Pixel 7. A setup project registers a new customer and signs the seeded admin in once per run; the specs reuse those sessions from `e2e/.auth/` (git-ignored) and run one at a time.

```bash
npx playwright install chromium        # once
npm run e2e                            # every spec, against http://localhost:8080 (E2E_BASE_URL to change)
npm run e2e:ui                         # UI mode: pick, watch and step through tests
npx playwright test --grep @BR-PAY-03  # the specs of one business rule; @phone for those that also run on the phone
npx playwright show-report             # the HTML report of the last run
npx playwright show-trace test-results/<test>/trace.zip   # a trace (recorded on a retry, or with --trace on)
```

The price-change spec edits Harbor through `docker compose exec` and runs only when `E2E_COMPOSE=1` is set (CI sets it). Product images are not loaded: on a fresh stack partner images arrive within Harbor's image budget, and a page of them would hold the browser's six connections. To rerun specs with the saved sessions instead of registering again (the API allows 5 registrations per 10 minutes per IP), name their projects: `npx playwright test --project chromium --project phone --no-deps`.

In Docker (`docker compose up`), nginx serves the build on port 8080 and proxies `/api` and `/media` to the `api` service, so the SPA and the API share one origin (needed for the HttpOnly session cookie). `ng serve` keeps that: the proxy forwards to `dotnet run` or to the api container of `compose.dev.yaml` (root README, "Changing it live"). It targets 127.0.0.1, not localhost, because Node may resolve localhost to `::1` and compose publishes on IPv4.

The code is linted and formatted by [Ultracite](https://www.ultracite.ai/) 8 (ESLint flavour); `npm run fix` fixes what it can, and the pre-push hook and CI run `npm run lint`.

`npm ci` warns `ERESOLVE overriding peer dependency` twice: Ultracite 8.3.1 pins `eslint-plugin-github` 6.1.2, which depends on `eslint-plugin-import` and `eslint-plugin-jsx-a11y`, and even their latest versions declare ESLint only up to 9. They run on ESLint 10 (`npm run lint` passes), so the warning is harmless until those plugins widen their peer ranges.

Layout: `src/app/core/` holds app-wide pieces (API base URL, cart state, layout, routing helpers); `src/app/features/<area>/` holds the pages. Conventions are in `AGENTS.md`.
