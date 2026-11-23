import {
  ApplicationConfig,
  DEFAULT_CURRENCY_CODE,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import {
  provideHttpClient,
  withFetch,
  withInterceptors,
  withXsrfConfiguration,
} from '@angular/common/http';
import {
  TitleStrategy,
  provideRouter,
  withComponentInputBinding,
  withInMemoryScrolling,
} from '@angular/router';
import { MAT_FORM_FIELD_DEFAULT_OPTIONS, MatFormFieldDefaultOptions } from '@angular/material/form-field';
import { routes } from './app.routes';
import { loadSessionOnStartup } from './core/api/session.api';
import { signedOutInterceptor } from './core/api/signed-out.interceptor';
import { provideAppIcons } from './core/icons/app-icons';
import { PageTitleStrategy } from './core/routing/page-title-strategy';
import { provideRouteHeadingFocus } from './core/routing/route-heading-focus';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // Zoneless is the v22 default (no zone.js installed); stated here so it is explicit.
    provideZonelessChangeDetection(),
    provideRouter(
      routes,
      withComponentInputBinding(),
      withInMemoryScrolling({ scrollPositionRestoration: 'enabled' })
    ),
    { provide: TitleStrategy, useClass: PageTitleStrategy },
    provideRouteHeadingFocus(),
    // Lumen.Api issues the XSRF-TOKEN cookie; Angular echoes it on mutating same-origin requests.
    // A 401 on a signed-in call means the session ended elsewhere: sign in again.
    provideHttpClient(
      withFetch(),
      withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }),
      withInterceptors([signedOutInterceptor])
    ),
    // GET /api/v1/auth/session sets that cookie, so load it before anything can POST.
    provideAppInitializer(loadSessionOnStartup),
    { provide: DEFAULT_CURRENCY_CODE, useValue: 'USD' },
    provideAppIcons(),
    // One field style for the shop and the admin pages.
    {
      provide: MAT_FORM_FIELD_DEFAULT_OPTIONS,
      useValue: { appearance: 'outline' } satisfies MatFormFieldDefaultOptions,
    },
  ],
};
