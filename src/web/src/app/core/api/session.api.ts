import { HttpClient } from '@angular/common/http';
import { Service, computed, inject, signal } from '@angular/core';
import { Observable, firstValueFrom, tap } from 'rxjs';
import { API_BASE_URL } from './api-base-url';

/**
`GET /api/v1/auth/session`: who is signed in (or anonymous).
*/
export interface Session {
  isAuthenticated: boolean;
  email: string | null;
  roles: string[];
}

/**
Body of `POST /auth/sign-in` and `POST /auth/register`.
*/
export interface Credentials {
  email: string;
  password: string;
}

export const ANONYMOUS_SESSION: Session = { isAuthenticated: false, email: null, roles: [] };

/**
The role the API gives administrators (`Lumen.Api.Identity.Roles.Admin`).
*/
export const ADMIN_ROLE = 'admin';

/**
The role the API gives shoppers (`Lumen.Api.Identity.Roles.Customer`). Only customers have a wallet (BR-PAY-06).
*/
export const CUSTOMER_ROLE = 'customer';

/**
How long the first render waits for the session before showing the shop signed out.
*/
export const SESSION_STARTUP_WAIT_MS = 3000;

@Service()
export class SessionApi {
  private readonly http = inject(HttpClient);

  private readonly baseUrl = inject(API_BASE_URL);

  private readonly current = signal<Session>(ANONYMOUS_SESSION);

  private known: Promise<Session> | undefined;

  /**
  The current session; anonymous until the API has answered.
  */
  readonly session = this.current.asReadonly();

  readonly isAuthenticated = computed(() => this.current().isAuthenticated);

  readonly isAdmin = computed(() => this.current().roles.includes(ADMIN_ROLE));

  readonly isCustomer = computed(() => this.current().roles.includes(CUSTOMER_ROLE));

  private remember(session: Session): void {
    this.current.set(session);
    this.known = Promise.resolve(session);
  }

  private async loadOrSignedOut(): Promise<Session> {
    try {
      return await firstValueFrom(this.load());
    } catch (error) {
      console.error('Could not load the session; continuing signed out.', error);
      return ANONYMOUS_SESSION;
    }
  }

  /**
   * Fetches the session. The response also sets the `XSRF-TOKEN` cookie that every
   * POST/PUT/PATCH/DELETE must echo, so this has to run before the first mutating call.
   */
  load(): Observable<Session> {
    return this.http
      .get<Session>(`${this.baseUrl}/auth/session`)
      .pipe(tap((session) => this.current.set(session)));
  }

  /**
   * The session once the API has answered at least once: the start-up load if it is still
   * running, else the last known answer. Starts a load if none ran yet. Never rejects (a failed
   * load counts as signed out). Guards use it so a slow start-up never reads as "signed out".
   */
  ready(): Promise<Session> {
    this.known ??= this.loadOrSignedOut();
    return this.known;
  }

  /**
   * 200 with the session (the API also merges the guest cart, BR-CART-01, and issues a new XSRF
   * token); 401 for a wrong email or password; 429 when the account is locked out.
   */
  signIn(credentials: Credentials): Observable<Session> {
    return this.http
      .post<Session>(`${this.baseUrl}/auth/sign-in`, credentials)
      .pipe(tap((session) => this.remember(session)));
  }

  /**
  200 with the new customer's session; 400 validation problem keyed `Email` or `Password`.
  */
  register(credentials: Credentials): Observable<Session> {
    return this.http
      .post<Session>(`${this.baseUrl}/auth/register`, credentials)
      .pipe(tap((session) => this.remember(session)));
  }

  /**
   * The API refused a call with 401 although this tab was signed in (`signedOutInterceptor`): shows
   * the shop signed out at once, then reads the session again, which also renews the XSRF cookie.
   */
  sessionEnded(): void {
    this.current.set(ANONYMOUS_SESSION);
    this.known = this.loadOrSignedOut();
  }

  signOut(): Observable<Session> {
    return this.http
      .post<Session>(`${this.baseUrl}/auth/sign-out`, null)
      .pipe(tap((session) => this.remember(session)));
  }
}

/**
 * App initializer: loads the session (and with it the XSRF cookie) before the first render.
 * Never blocks start-up for long: if the API is down the shop starts signed out, and if it is
 * slow the request keeps running (it is not cancelled) and updates `session()` when it lands.
 */
export const loadSessionOnStartup = async (): Promise<void> => {
  const loaded = inject(SessionApi).ready();
  let timer: ReturnType<typeof setTimeout> | undefined;
  // eslint-disable-next-line promise/avoid-new -- wraps setTimeout; Promise.withResolvers (ES2024) is outside Angular 22's browser baseline
  const gaveUp = new Promise<void>((resolve) => {
    timer = setTimeout(() => {
      console.warn('The session is slow to load; starting signed out until it arrives.');
      resolve();
    }, SESSION_STARTUP_WAIT_MS);
  });
  try {
    await Promise.race([loaded, gaveUp]);
  } finally {
    clearTimeout(timer);
  }
};
