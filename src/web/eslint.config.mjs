import angular from 'ultracite/eslint/angular';
import core from 'ultracite/eslint/core';
import playwright from 'ultracite/eslint/playwright';
import vitest from 'ultracite/eslint/vitest';

// Project settings on top of Ultracite. Each rule changed here says why: either this project's
// own convention (the selector prefix) or a preset rule that conflicts with Angular, Playwright
// or the tooling itself.
export default [
  ...core,
  ...angular,
  // Both test presets match every `*.spec.ts`: Vitest owns the unit tests under src/, Playwright
  // the browser tests and their helpers under e2e/.
  ...vitest.map((config) => ({ ...config, files: ['src/**/*.spec.ts'] })),
  ...playwright.map((config) => ({ ...config, files: ['e2e/**/*.ts'] })),
  {
    files: ['eslint.config.mjs'],
    rules: {
      // Narrowing a preset to its folder with map() above is configuration, not a side effect.
      'unicorn/no-top-level-side-effects': [
        'error',
        { allow: { 'ultracite/eslint/playwright': ['map'], 'ultracite/eslint/vitest': ['map'] } },
      ],
    },
  },
  {
    // Angular's build cache holds generated JavaScript; Playwright writes its reports and traces.
    // libs/ui holds spartan/ui's helm components as its CLI generates them (spartan's `hlm`
    // selectors and `Hlm*` names, its own code style), kept that way so a regenerated component
    // diffs cleanly against ours. Prettier still formats them and the strict compiler and
    // template type-checker still check them.
    ignores: ['.angular/**', 'playwright-report/**', 'test-results/**', 'libs/ui/**'],
  },
  {
    files: ['**/*.ts'],
    rules: {
      // This project's selector prefix (angular.json "prefix": "app").
      '@angular-eslint/component-selector': [
        'error',
        { prefix: 'app', style: 'kebab-case', type: 'element' },
      ],
      '@angular-eslint/directive-selector': [
        'error',
        { prefix: 'app', style: 'camelCase', type: 'attribute' },
      ],
      // Angular's style guide (v20+) and `ng generate` name classes without a type suffix
      // (`CartPage`, not `CartPageComponent`).
      '@angular-eslint/component-class-suffix': 'off',
      // An Angular class is declared by its decorator; `@Component({...}) class Footer {}` is the
      // idiomatic template-only component.
      '@typescript-eslint/no-extraneous-class': ['error', { allowWithDecorator: true }],
      // Contradicts unicorn/consistent-class-member-order (private members first) and Angular's
      // inject-at-top: a public field derived from a private `inject()` field must come after it,
      // because fields initialise in order. Unicorn's rule orders by visibility; this one keeps
      // grouping by kind only.
      '@typescript-eslint/member-ordering': [
        'error',
        {
          default: [
            'signature',
            'call-signature',
            'field',
            'static-initialization',
            'constructor',
            ['get', 'set'],
            'method',
          ],
        },
      ],
      // Ultracite reports every call at module scope it can't classify, which in Angular is every
      // decorator, InjectionToken and provider function. These are declarations, not side effects.
      'unicorn/no-top-level-side-effects': [
        'error',
        {
          allow: {
            '@angular/common/http': [
              'provideHttpClient',
              'withFetch',
              'withInterceptors',
              'withXsrfConfiguration',
            ],
            '@angular/core': [
              'Component',
              'Directive',
              'Injectable',
              'InjectionToken',
              'Pipe',
              'Service',
              'provideAppInitializer',
              'provideBrowserGlobalErrorListeners',
              'provideZonelessChangeDetection',
            ],
            '@angular/router': [
              'provideRouter',
              'withComponentInputBinding',
              'withInMemoryScrolling',
            ],
            // A Playwright fixture declaration (e2e/support).
            '@playwright/test': ['test.extend'],
            './checkout-step.guard': ['checkoutStepGuard'],
            './core/icons/app-icons': ['provideAppIcons'],
            './core/routing/route-heading-focus': ['provideRouteHeadingFocus'],
            // The order and admin pages' own Lucide icons (orders/order-icons.ts).
            './order-icons': ['provideOrderIcons'],
            '../orders/order-icons': ['provideOrderIcons'],
          },
          checkUnknownCalls: true,
        },
      ],
      // Bundlers read `/* @__PURE__ */` only directly before the call it marks, which is also how
      // no-top-level-side-effects accepts a pure module-level `new Set(...)` or `new Intl...`.
      'no-inline-comments': ['error', { ignorePattern: '@__PURE__' }],
      // Without type information this rule reports an import when any overload is deprecated.
      // RxJS 7 deprecates single overloads (thisArg, resultSelector, scheduler) of map, switchMap,
      // tap, timer and subscribe, so every use was reported although none uses those overloads.
      // The type-aware @typescript-eslint/no-deprecated would check the overload actually called.
      'import-x/no-deprecated': 'off',
    },
  },
  {
    files: ['**/*.html'],
    rules: {
      // spartan/ui's checkbox and switch are components (a `role="checkbox"`/`"switch"` button
      // inside), labelled by the <label> around them; the rule only knows native controls.
      '@angular-eslint/template/label-has-associated-control': [
        'error',
        { controlComponents: ['hlm-checkbox', 'hlm-radio', 'hlm-switch'] },
      ],
    },
  },
  {
    files: ['src/**/*.spec.ts'],
    rules: {
      // Angular's unit-test builder runs `*.spec.ts`, and `ng generate` names specs that way.
      'vitest/consistent-test-filename': ['error', { pattern: '.*[.]spec[.]ts$' }],
      // A spec is named after the file it tests, so `app.config.ts` gets `app.config.spec.ts`.
      // The subject part is checked with the default pattern.
      'github/filenames-match-regex': ['error', '^[a-z0-9-]+([.][a-z0-9-]+)?[.]spec$'],
      // Angular's HttpTestingController asserts with expectOne/expectNone (they throw on a
      // mismatch), and specs share helpers named expect*. The rule only follows helpers written
      // as function declarations, which func-style forbids.
      'vitest/expect-expect': [
        'error',
        { assertFunctionNames: ['expect*', '*.expectOne', '*.expectNone'] },
      ],
      // With @playwright/test installed, sonarjs reads Vitest's global expect as Playwright's and
      // reports `toBe(67.46)`. Amounts arrive from the API as JSON numbers and are compared with
      // the same literal, so exact equality is the check.
      'sonarjs/no-floating-point-equality': 'off',
    },
  },
  {
    files: ['e2e/**/*.ts'],
    rules: {
      // As in the unit specs, shared checks are helpers named expect* (expectConfirmed).
      'playwright/expect-expect': ['error', { assertFunctionPatterns: ['^expect'] }],
      'playwright/prefer-ending-with-an-expect': ['error', { assertFunctionPatterns: ['^expect'] }],
      // A browser test follows one purchase across several pages and checks each step. Five is a
      // unit-test budget; splitting the flow would place and pay the same order again.
      'playwright/max-expects': ['error', { max: 12 }],
      // The price-change test needs Docker access to the stack and skips itself, with that reason,
      // when E2E_COMPOSE is not set.
      'playwright/no-skipped-test': ['error', { allowConditional: true }],
    },
  },
  {
    files: ['e2e/**/*.setup.ts'],
    rules: {
      // A setup test ends by saving the signed-in state for the specs, as Playwright's own
      // authentication guide does.
      'playwright/prefer-ending-with-an-expect': 'off',
    },
  },
];
