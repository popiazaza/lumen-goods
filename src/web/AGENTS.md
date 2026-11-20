# AGENTS.md (src/web)

Angular 22.3 storefront and admin for Lumen Goods. The root `AGENTS.md` applies; this file adds web rules.

## Commands

```bash
npm start        # ng serve on :4200; /api and /media proxy to Lumen.Api on :5080 (proxy.conf.json)
npm run build    # production build -> dist/lumen-web/browser
npm test         # Vitest, runs once (npm run test:watch to watch)
npm run lint     # ultracite check: ESLint (Ultracite + angular-eslint, incl. template a11y), Prettier, Stylelint
npm run fix      # ultracite fix: formats and applies the safe autofixes
docker build -t lumen-web:dev .   # nginx on :8080; proxies /api and /media to http://api:8080
```

## Layout

- `src/app/core/`: app-wide code: `api/` (typed API services), `cart/`, `catalogue/` (seller names, last catalogue query), `layout/` (header, footer), `routing/` (titles, focus), `icons/`.
- `src/app/features/<area>/`: catalogue, products, cart, account, checkout, orders, admin. Pages are `<name>-page.ts`, lazy via `loadComponent`; child routes in `<area>.routes.ts`.
- `src/app/shared/`: presentational pieces used by more than one feature (price, stock badge, product image).
- `src/app/testing/`: test data builders for specs; never imported by app code.
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
- Give components signals (`rxResource` over the service's Observable, `httpResource`, `toSignal`) and render explicit loading, empty and error states. `resource.value()` throws in the error state: read it behind `hasValue()`.
- The catalogue's URL is its state: `features/catalogue/catalogue-query.ts` parses and writes the query params; controls navigate, they never hold filter state of their own. Filter and sort navigations pass `scroll: 'manual'` so the page does not jump; paging keeps the router's scroll-to-top.
- Partners are API configuration: the seller list, valid `seller` values and names come from `GET /sellers` (`SellerDirectory`). Never hard-code partner ids in the client.
- Errors are RFC 9457 `ProblemDetails`: show `title`/`detail`, map `errors` onto form fields.
- XSRF: `SessionApi` (`session.api.ts`) loads `GET /api/v1/auth/session` at start-up, which sets the `XSRF-TOKEN` cookie; `HttpClient` echoes it as `X-XSRF-TOKEN` on every POST/PUT/PATCH/DELETE. Read the user from `SessionApi.session()`.

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
- CSP: nginx sends a strict policy and `autoCsp` hashes the bootstrap scripts. No inline scripts, `eval`, inline event attributes or third-party hosts.

## Lint

- The code is linted and formatted by [Ultracite](https://www.ultracite.ai/) (ESLint, Prettier and Stylelint presets; project rules in `eslint.config.mjs`). `npm run fix` fixes what it can; review its diff, then fix the rest by hand. Every rule changed in `eslint.config.mjs` or disabled on a line says why; add one only for a real conflict with Angular, the browser baseline or the API, never to make the check pass.

## Tests

- Vitest via `ng test`, `*.spec.ts` next to the code. Prefer Material harnesses and `RouterTestingHarness` to DOM internals.
- Mock the API with `provideHttpClientTesting()` and `HttpTestingController`. A pending request keeps the app unstable, so `fixture.whenStable()` (and harness actions, which call it) wait for it: flush first, or wait for the URL/request with `vi.waitFor`.

The generic guidance below comes from `ultracite init`; where it differs, the rules above win.

# Ultracite Code Standards

This project uses **Ultracite**, a zero-config preset that enforces strict code quality standards through automated formatting and linting.

## Quick Reference

- **Format code**: `npx ultracite fix`
- **Check for issues**: `npx ultracite check`
- **Diagnose setup**: `npx ultracite doctor`

ESLint + Prettier + Stylelint (the underlying engine) provides robust linting and formatting. Most issues are automatically fixable.

---

## Core Principles

Write code that is **accessible, performant, type-safe, and maintainable**. Focus on clarity and explicit intent over brevity.

### Type Safety & Explicitness

- Use explicit types for function parameters and return values when they enhance clarity
- Prefer `unknown` over `any` when the type is genuinely unknown
- Use const assertions (`as const`) for immutable values and literal types
- Leverage TypeScript's type narrowing instead of type assertions
- Use meaningful variable names instead of magic numbers - extract constants with descriptive names

### Modern JavaScript/TypeScript

- Write functions as arrow functions assigned to `const`, and define them above where they're used
- Prefer `for...of` loops over `.forEach()` and indexed `for` loops
- Use optional chaining (`?.`) and nullish coalescing (`??`) for safer property access
- Prefer template literals over string concatenation
- Use destructuring for object and array assignments
- Use `const` by default, `let` only when reassignment is needed, never `var`

### Async & Promises

- Always `await` promises in async functions - don't forget to use the return value
- Use `async/await` syntax instead of promise chains for better readability
- Handle errors appropriately in async code with try-catch blocks
- Don't use async functions as Promise executors

### React & JSX

- Use function components over class components
- Call hooks at the top level only, never conditionally
- When a hook dependency rule fires, first check whether the effect is needed, and whether adding the dependency changes when it runs
- Use the `key` prop for elements in iterables (prefer unique IDs over array indices)
- Nest children between opening and closing tags instead of passing as props
- Don't define components inside other components
- In JSX, work out a multi-way condition in a variable or helper above the `return`, not in a chain of ternaries

### Accessibility

- Use semantic HTML first; add ARIA only when no native element fits
- Provide meaningful alt text for images
- Use proper heading hierarchy
- Add labels for form inputs
- Make anything clickable a `<button>` or a link, so it works from the keyboard; don't add key handlers to a `<div>`
- Use semantic elements (`<button>`, `<nav>`, etc.) instead of divs with roles

### Error Handling & Debugging

- Remove `console.log`, `debugger`, and `alert` statements from production code
- Throw `Error` objects with descriptive messages, not strings or other values
- Use `try-catch` blocks meaningfully - don't catch errors just to rethrow them
- Prefer early returns over nested conditionals for error cases

### Code Organization

- Keep functions focused and under reasonable cognitive complexity limits
- Extract complex conditions into well-named boolean variables
- Use early returns to reduce nesting, and put braces around every `if` body, including a one-line guard
- Use a single ternary for a choice between two values. For three or more outcomes, use early returns or a lookup; never nest ternaries
- Group related code together and separate concerns
- Avoid import cycles; move shared code into a module both sides import
- Don't leave TODO or FIXME comments in new code. For an existing one, do the task, delete it if it's done, or leave it; never reword it to get past the linter

### Security

- Add `rel="noopener noreferrer"` when using `target="_blank"` on links
- Avoid `dangerouslySetInnerHTML` unless absolutely necessary
- Don't use `eval()` or assign directly to `document.cookie`
- Validate and sanitize user input

### Performance

- Avoid spread syntax in accumulators within loops
- Use top-level regex literals instead of creating them in loops
- Avoid barrel files (index files that only re-export other modules) inside an app; a package's entry point is fine
- Use proper image components (e.g., Next.js `<Image>`) over `<img>` tags

### Framework-Specific Guidance

**Next.js:**

- Use Next.js `<Image>` component for images
- Use `next/head` or App Router metadata API for head elements
- Use Server Components for async data fetching instead of async Client Components

**React 19+:**

- Use ref as a prop instead of `React.forwardRef`

**Solid/Svelte/Vue/Qwik:**

- Use `class` and `for` attributes (not `className` or `htmlFor`)

---

## Testing

- Write assertions inside `it()` or `test()` blocks
- Avoid done callbacks in async tests - use async/await instead
- Don't use `.only` or `.skip` in committed code
- Keep test suites reasonably flat - avoid excessive `describe` nesting

## When ESLint + Prettier + Stylelint Can't Help

ESLint + Prettier + Stylelint will catch most mechanical issues automatically. Focus your attention on:

1. **Business logic correctness** - ESLint + Prettier + Stylelint can't validate your algorithms
2. **Meaningful naming** - Use descriptive names for functions, variables, and types
3. **Architecture decisions** - Component structure, data flow, and API design
4. **Edge cases** - Handle boundary conditions and error states
5. **User experience** - Accessibility, performance, and usability considerations
6. **Documentation** - Add comments for complex logic, but prefer self-documenting code

---

Most formatting and common issues are automatically fixed by ESLint + Prettier + Stylelint. Run `npx ultracite fix` before committing to ensure compliance.
