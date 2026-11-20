import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { API_BASE_URL } from './api-base-url';

export interface PartnerSyncError {
  occurredUtc: string;
  /** What Lumen was doing, e.g. "catalog", "stock". */
  operation: string;
  /** The partner's HTTP status, or `null` for timeouts and network failures. */
  statusCode: number | null;
  message: string;
}

/** Catalogue sync health for one partner shop. */
export interface PartnerStatus {
  partner: string;
  name: string;
  lastSuccessUtc: string | null;
  lastAttemptUtc: string | null;
  lastError: string | null;
  liveProductCount: number;
  /** Set when an admin asked for a refresh that has not run yet. */
  refreshRequestedUtc: string | null;
  recentErrors: PartnerSyncError[];
}

/** `GET /api/v1/admin/partners` and `POST /api/v1/admin/partners/{partner}/refresh` (admin only). */
@Service()
export class AdminPartnersApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);

  /** 401 when signed out, 403 when signed in without the admin role. */
  list(): Observable<PartnerStatus[]> {
    return this.http.get<PartnerStatus[]>(`${this.baseUrl}/admin/partners`);
  }

  /**
   * Asks the background sync to refresh one partner now. Answers 202 at once; the refresh runs
   * later, so callers poll `list()`. HttpClient adds X-XSRF-TOKEN.
   *
   * Emits when the server accepted the request, read from its `Date` header (epoch ms on the
   * server's clock, comparable with the status timestamps whatever the browser's clock says), or
   * `null` when the header is missing.
   */
  refresh(partner: string): Observable<number | null> {
    return this.http
      .post(`${this.baseUrl}/admin/partners/${encodeURIComponent(partner)}/refresh`, null, {
        observe: 'response',
      })
      .pipe(
        map((response) => {
          const accepted = Date.parse(response.headers.get('Date') ?? '');
          return Number.isNaN(accepted) ? null : accepted;
        }),
      );
  }
}

/** Epoch ms of a status timestamp; `NaN` (never greater or equal) for `null` or junk. */
export function instantOf(isoUtc: string | null): number {
  return isoUtc ? Date.parse(isoUtc) : Number.NaN;
}

/**
 * The refresh requested at `requestedAt` (epoch ms) has run: the request is no longer pending and
 * an attempt started at or after it. An earlier scheduled sync moving `lastAttemptUtc` does not
 * count.
 */
export function refreshHasRun(partner: PartnerStatus, requestedAt: number): boolean {
  return partner.refreshRequestedUtc === null && instantOf(partner.lastAttemptUtc) >= requestedAt;
}
