import { LiveAnnouncer } from '@angular/cdk/a11y';
import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatCard } from '@angular/material/card';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { concatMap, first, interval, map, switchMap, take, tap, timer } from 'rxjs';
import {
  AdminPartnersApi,
  PartnerStatus,
  instantOf,
  refreshHasRun,
} from '../../core/api/admin-partners.api';
import { httpStatusOf, isAccessDenied, problemMessage } from '../../core/api/problem-details';
import { relativeTime } from './relative-time';

/** How often the page re-reads the status after "Refresh now", and for how long at most. */
export const REFRESH_POLL_INTERVAL_MS = 3000;
export const REFRESH_POLL_TIMEOUT_MS = 60_000;

/** Recent errors shown before "Show all". */
export const RECENT_ERRORS_SHOWN = 5;

type RefreshState =
  { kind: 'running' } | { kind: 'succeeded' | 'failed' | 'timed-out'; message: string };

interface Health {
  tone: 'ok' | 'failed' | 'idle';
  icon: string;
  text: string;
}

/** Admin: catalogue sync health per partner, with "Refresh now" (`/admin/partners`). */
@Component({
  selector: 'app-admin-partners-page',
  imports: [DatePipe, DecimalPipe, RouterLink, MatButton, MatCard, MatIcon, MatProgressBar],
  templateUrl: './admin-partners-page.html',
  styleUrl: './admin-partners-page.scss',
})
export class AdminPartnersPage {
  private readonly api = inject(AdminPartnersApi);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly partners = rxResource({ stream: () => this.api.list() });
  protected readonly list = computed(() =>
    this.partners.hasValue() ? this.partners.value() : undefined,
  );

  /** A refresh call can also come back 401/403 (session expired, role removed). */
  private readonly deniedStatus = signal<number | undefined>(undefined);
  protected readonly accessStatus = computed(() => {
    const error = this.partners.error();
    return isAccessDenied(error) ? httpStatusOf(error) : this.deniedStatus();
  });

  protected readonly now = signal(Date.now());
  protected readonly refreshes = signal<Readonly<Record<string, RefreshState>>>({});
  private readonly expanded = signal<ReadonlySet<string>>(new Set());

  protected readonly pollSeconds = REFRESH_POLL_INTERVAL_MS / 1000;
  protected readonly recentErrorsShown = RECENT_ERRORS_SHOWN;
  protected readonly relativeTime = relativeTime;

  constructor() {
    // Keeps "5 minutes ago" honest while the page stays open.
    interval(30_000)
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.now.set(Date.now()));
  }

  protected health(partner: PartnerStatus): Health {
    if (partner.lastError) {
      return { tone: 'failed', icon: 'error', text: 'Last refresh failed' };
    }
    if (!partner.lastSuccessUtc) {
      return { tone: 'idle', icon: 'warning', text: 'Not refreshed yet' };
    }
    return { tone: 'ok', icon: 'check_circle', text: 'Last refresh succeeded' };
  }

  /** An admin asked for a refresh that has not started yet. */
  protected isQueued(partner: PartnerStatus): boolean {
    const requested = partner.refreshRequestedUtc;
    return (
      !!requested &&
      (!partner.lastAttemptUtc || instantOf(requested) > instantOf(partner.lastAttemptUtc))
    );
  }

  protected isRefreshing(partner: PartnerStatus): boolean {
    return this.refreshes()[partner.partner]?.kind === 'running';
  }

  protected isExpanded(partner: PartnerStatus): boolean {
    return this.expanded().has(partner.partner);
  }

  protected shownErrors(partner: PartnerStatus): PartnerStatus['recentErrors'] {
    return this.isExpanded(partner)
      ? partner.recentErrors
      : partner.recentErrors.slice(0, RECENT_ERRORS_SHOWN);
  }

  protected toggleErrors(partner: PartnerStatus): void {
    this.expanded.update((expanded) => {
      const next = new Set(expanded);
      if (!next.delete(partner.partner)) {
        next.add(partner.partner);
      }
      return next;
    });
  }

  /**
   * Asks for a refresh (202), then re-reads the status every few seconds until the refresh it asked
   * for has run (`refreshHasRun`), or gives up after a minute. The baseline is when the request was
   * accepted, not the status loaded with the page, so an unrelated scheduled sync in between does
   * not end the wait early.
   */
  protected refresh(partner: PartnerStatus): void {
    if (this.isRefreshing(partner)) {
      return;
    }
    this.setRefresh(partner, { kind: 'running' });
    void this.announcer.announce(`Refreshing ${partner.name}`, 'polite');
    // Fallback baseline if the server sends no Date header; taken before the POST, so a run the
    // POST triggers is never older than it.
    const sentAt = Date.now();

    this.api
      .refresh(partner.partner)
      .pipe(
        switchMap((acceptedAt) => {
          const requestedAt = acceptedAt ?? sentAt;
          return timer(REFRESH_POLL_INTERVAL_MS, REFRESH_POLL_INTERVAL_MS).pipe(
            take(Math.floor(REFRESH_POLL_TIMEOUT_MS / REFRESH_POLL_INTERVAL_MS)),
            concatMap(() => this.api.list()),
            tap((list) => {
              this.partners.set(list);
              this.now.set(Date.now());
            }),
            map((list) => list.find((p) => p.partner === partner.partner)),
            first((p) => !!p && refreshHasRun(p, requestedAt), undefined),
            map((after) => ({ after, requestedAt })),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: ({ after, requestedAt }) => this.finish(partner, after, requestedAt),
        error: (error: unknown) => this.fail(partner, error),
      });
  }

  private finish(
    before: PartnerStatus,
    after: PartnerStatus | undefined,
    requestedAt: number,
  ): void {
    let state: Exclude<RefreshState, { kind: 'running' }>;
    if (!after) {
      state = {
        kind: 'timed-out',
        message: `${before.name} has not finished refreshing after ${REFRESH_POLL_TIMEOUT_MS / 1000} seconds. It may still be running; reload this page later to check.`,
      };
    } else if (instantOf(after.lastSuccessUtc) >= requestedAt) {
      state = {
        kind: 'succeeded',
        message: `${after.name} refreshed: ${after.liveProductCount.toLocaleString('en-US')} live products.`,
      };
    } else {
      state = {
        kind: 'failed',
        message: `${after.name} refresh failed: ${after.lastError ?? 'see the recent errors.'}`,
      };
    }
    this.setRefresh(before, state);
    void this.announcer.announce(state.message, 'polite');
  }

  private fail(partner: PartnerStatus, error: unknown): void {
    if (isAccessDenied(error)) {
      this.deniedStatus.set(httpStatusOf(error));
    }
    const message = `Couldn’t refresh ${partner.name}: ${problemMessage(error, 'the request failed. Try again.')}`;
    this.setRefresh(partner, { kind: 'failed', message });
    void this.announcer.announce(message, 'assertive');
  }

  private setRefresh(partner: PartnerStatus, state: RefreshState): void {
    this.refreshes.update((all) => ({ ...all, [partner.partner]: state }));
  }
}
