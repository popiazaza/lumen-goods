import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
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
   * later, so callers poll `list()` until `lastAttemptUtc` changes. HttpClient adds X-XSRF-TOKEN.
   */
  refresh(partner: string): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/admin/partners/${encodeURIComponent(partner)}/refresh`,
      null,
    );
  }
}
