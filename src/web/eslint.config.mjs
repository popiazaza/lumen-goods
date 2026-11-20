import angular from 'ultracite/eslint/angular';
import core from 'ultracite/eslint/core';
import vitest from 'ultracite/eslint/vitest';

// Project settings on top of Ultracite. Each rule changed here says why: either this project's
// own convention (the selector prefix) or a preset rule that conflicts with Angular itself.
export default [
  ...core,
  ...angular,
  ...vitest,
  {
    // Angular's build cache holds generated JavaScript.
    ignores: ['.angular/**'],
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
            '@angular/common/http': ['provideHttpClient', 'withFetch', 'withXsrfConfiguration'],
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
            './checkout-step.guard': ['checkoutStepGuard'],
            './core/icons/app-icons': ['provideAppIcons'],
            './core/routing/route-heading-focus': ['provideRouteHeadingFocus'],
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
    files: ['**/*.spec.ts'],
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
    },
  },
];
