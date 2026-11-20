import { HttpClient } from '@angular/common/http';
import { Service, computed, inject, signal } from '@angular/core';
import { Observable, firstValueFrom, tap } from 'rxjs';
import { API_BASE_URL } from './api-base-url';

/** `GET /api/v1/auth/session`: who is signed in (or anonymous). */
export interface Session {
  isAuthenticated: boolean;
  email: string | null;
  roles: string[];
}

export const ANONYMOUS_SESSION: Session = { isAuthenticated: false, email: null, roles: [] };

/** How long the first render waits for the session before showing the shop signed out. */
export const SESSION_STARTUP_WAIT_MS = 3000;

@Service()
export class SessionApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);
  private readonly current = signal<Session>(ANONYMOUS_SESSION);

  /** The current session; anonymous until the API has answered. */
  readonly session = this.current.asReadonly();
  readonly isAuthenticated = computed(() => this.current().isAuthenticated);

  /**
   * Fetches the session. The response also sets the `XSRF-TOKEN` cookie that every
   * POST/PUT/PATCH/DELETE must echo, so this has to run before the first mutating call.
   */
  load(): Observable<Session> {
    return this.http
      .get<Session>(`${this.baseUrl}/auth/session`)
      .pipe(tap((session) => this.current.set(session)));
  }
}

/**
 * App initializer: loads the session (and with it the XSRF cookie) before the first render.
 * Never blocks start-up for long: if the API is down the shop starts signed out, and if it is
 * slow the request keeps running (it is not cancelled) and updates `session()` when it lands.
 */
export function loadSessionOnStartup(): Promise<void> {
  const sessionApi = inject(SessionApi);
  const loaded = firstValueFrom(sessionApi.load()).then(
    () => undefined,
    (error: unknown) => console.error('Could not load the session; starting signed out.', error),
  );
  let timer: ReturnType<typeof setTimeout> | undefined;
  const gaveUp = new Promise<void>((resolve) => {
    timer = setTimeout(() => {
      console.warn('The session is slow to load; starting signed out until it arrives.');
      resolve();
    }, SESSION_STARTUP_WAIT_MS);
  });
  return Promise.race([loaded, gaveUp]).finally(() => clearTimeout(timer));
}
