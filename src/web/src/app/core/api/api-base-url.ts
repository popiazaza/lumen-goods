import { InjectionToken } from '@angular/core';

/**
 * Base path of the Lumen API. Always same-origin: nginx (Docker) and the dev-server proxy
 * forward it to Lumen.Api, so cookies and the XSRF token work without CORS.
 */
export const API_BASE_URL = /* @__PURE__ */ new InjectionToken<string>('API_BASE_URL', {
  factory: () => '/api/v1',
});
