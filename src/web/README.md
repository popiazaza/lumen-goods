# Lumen Goods web

The storefront and admin UI: Angular 22.3, Angular Material 3, zoneless, Vitest.

```bash
npm ci
npm start        # ng serve on http://localhost:4200; /api and /media proxy to the API on http://localhost:5080
npm test         # unit tests, single run
npm run lint     # angular-eslint, including template accessibility rules
npm run build    # production build
```

In Docker (`docker compose up`), nginx serves the build on port 8080 and proxies `/api` and `/media` to the
`api` service, so the SPA and the API share one origin (needed for the HttpOnly session cookie).

Layout: `src/app/core/` holds app-wide pieces (API base URL, cart state, layout, routing helpers);
`src/app/features/<area>/` holds the pages. Conventions are in `AGENTS.md`.
