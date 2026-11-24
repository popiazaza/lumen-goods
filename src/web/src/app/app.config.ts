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
    // Not spartan's provideSpartanHlm(): it only sets the CDK overlay's `usePopover: false` (so a
    // dialog stays under the toaster), and importing that token pulls the whole CDK overlay
    // (about 50 kB) into the start-up bundle. Our overlays (the filters sheet, the admin dialog and
    // status list) load with their lazy pages and work in the browser's top layer; no toast shows
    // while one is open.
  ],
};
