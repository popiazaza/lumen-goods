# Lumen Goods web

The storefront and admin UI: Angular 22.3, Angular Material 22.3 (M3 theme), zoneless, Vitest.

```bash
npm ci
npm start        # ng serve on http://localhost:4200; /api and /media proxy to the API on 127.0.0.1:5080
npm test         # unit tests, single run
npm run lint     # Ultracite: ESLint (incl. template accessibility rules), Prettier, Stylelint
npm run fix      # Ultracite autofix and formatting
npm run build    # production build
```

In Docker (`docker compose up`), nginx serves the build on port 8080 and proxies `/api` and `/media` to the `api` service, so the SPA and the API share one origin (needed for the HttpOnly session cookie). `ng serve` keeps that: the proxy forwards to `dotnet run` or to the api container of `compose.dev.yaml` (root README, "Changing it live"). It targets 127.0.0.1, not localhost, because Node may resolve localhost to `::1` and compose publishes on IPv4.

The code is linted and formatted by [Ultracite](https://www.ultracite.ai/) 8 (ESLint flavour); `npm run fix` fixes what it can, and the pre-push hook and CI run `npm run lint`.

`npm ci` warns `ERESOLVE overriding peer dependency` twice: Ultracite 8.3.1 pins `eslint-plugin-github` 6.1.2, which depends on `eslint-plugin-import` and `eslint-plugin-jsx-a11y`, and even their latest versions declare ESLint only up to 9. They run on ESLint 10 (`npm run lint` passes), so the warning is harmless until those plugins widen their peer ranges.

Layout: `src/app/core/` holds app-wide pieces (API base URL, cart state, layout, routing helpers); `src/app/features/<area>/` holds the pages. Conventions are in `AGENTS.md`.
