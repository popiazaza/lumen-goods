# AGENTS.md (src/web)

Angular 22.3 storefront and admin for Lumen Goods. The root `AGENTS.md` applies; this file adds web rules.

## Commands

```bash
npm start        # ng serve on :4200; /api and /media proxy to Lumen.Api on :5080 (proxy.conf.json)
npm run build    # production build -> dist/lumen-web/browser
npm test         # Vitest, runs once (npm run test:watch to watch)
npm run lint     # angular-eslint, including the template accessibility rules
docker build -t lumen-web:dev .   # nginx on :8080; proxies /api and /media to http://api:8080
```

## Layout

- `src/app/core/`: app-wide code: `api/` (typed API services), `cart/`, `layout/` (header, footer), `routing/` (titles, focus), `icons/`.
- `src/app/features/<area>/`: catalogue, products, cart, account, checkout, orders, admin. Pages are `<name>-page.ts`, lazy via `loadComponent`; child routes in `<area>.routes.ts`.
- Theme: `src/styles.scss` (M3 `mat.theme`, `--lg-*` tokens); palette in `src/styles/_theme-colors.scss` (generated).

## Angular conventions

- Standalone components; never write `standalone: true`. OnPush is the v22 default: don't set `changeDetection`.
- Zoneless (no zone.js): anything a template reads must be a signal or come from one.
- State: `signal`, `computed`, `linkedSignal`; `set`/`update`, never mutate. Services expose read-only signals (`asReadonly()`) and change them through methods.
- DI with `inject()`, never constructor parameters. App-wide services: `@Service()` or `@Injectable({ providedIn: 'root' })`.
- `input()`, `input.required()`, `output()`, `model()`; route params arrive as inputs (`withComponentInputBinding`).
- Templates: `@if`, `@for` (always `track`), `@switch`; `class`/`style` bindings, no `ngClass`/`ngStyle`/`CommonModule`.
- Forms: typed Reactive Forms (`FormGroup`, `FormControl` with `nonNullable`). Not template-driven forms.
- Host listeners and bindings go in the `host` object, not `@HostListener`/`@HostBinding`.
- UI: Angular Material and CDK components first. Style with `--mat-sys-*`/`--lg-*` tokens and `mat.*-overrides()`, never Material's internal classes. Icons are inline SVGs in `core/icons/app-icons.ts` (no icon font, no third-party requests).

## API access

- Base path `/api/v1` (the `API_BASE_URL` token), always same-origin and relative. Never hard-code a host.
- One typed service per API resource in `src/app/core/api/` (e.g. `products.api.ts` exporting `ProductsApi`) built on `HttpClient`. Components never call `HttpClient` directly.
- Give components signals (`httpResource`, `toSignal`) and render explicit loading, empty and error states.
- Errors are RFC 9457 `ProblemDetails`: show `title`/`detail`, map `errors` onto form fields.
- XSRF: the API sets the `XSRF-TOKEN` cookie and `HttpClient` returns it as `X-XSRF-TOKEN`. Don't work around it.

## No business logic in the client

- The server prices everything: line prices, subtotal, shipping, tax, totals, EUR to USD, refunds. Show the amounts the API returns (`currency` pipe, USD); never add, round or convert money in TypeScript.
- Stock, availability, price-change notices, order and line statuses come from the API; the client only renders them.
- Client-side validation is for UX only; the API validates again and its answer wins.
- `CartStateService` mirrors the server cart for the header badge; it never calculates totals.

## Accessibility (WCAG 2.2 AA)

- One `<h1>` per page. The router sets the title from the route `title` ("Your cart — Lumen Goods") and focuses the `<h1>` after navigation.
- Every control has a visible label (`<label for>` or `mat-label`); icon-only buttons get an `aria-label`.
- Announce status changes (cart count, result count, payment progress) with `LiveAnnouncer` or `role="status"`.
- Targets at least 24 px (44 px on phones), no horizontal scroll at 320 px, visible focus, never colour alone (icon and text).
- Never block paste or autofill; use `autocomplete` tokens on sign-in, address and card fields.
- `npm run lint` must pass. Fix template a11y findings; don't disable the rules.

## Tests

- Vitest via `ng test`, `*.spec.ts` next to the code. Prefer Material harnesses and `RouterTestingHarness` to DOM internals.
