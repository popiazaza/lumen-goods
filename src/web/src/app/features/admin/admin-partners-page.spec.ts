import { LiveAnnouncer } from '@angular/cdk/a11y';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { PartnerStatus } from '../../core/api/admin-partners.api';
import { provideAppIcons } from '../../core/icons/app-icons';
import { aPartner } from '../../testing/fixtures';
import {
  AdminPartnersPage,
  REFRESH_POLL_INTERVAL_MS,
  REFRESH_POLL_TIMEOUT_MS,
} from './admin-partners-page';

const HARBOR = aPartner();
const KESTREL = aPartner({
  partner: 'kestrel',
  name: 'Kestrel Outfitters',
  lastSuccessUtc: '2026-11-20T05:45:00Z',
  lastAttemptUtc: '2026-11-20T06:00:00Z',
  lastError: 'Stock CSV timed out after 10 s',
  liveProductCount: 96,
  recentErrors: Array.from({ length: 7 }, (_, index) => ({
    occurredUtc: `2026-11-20T0${index}:00:00Z`,
    operation: 'stock',
    statusCode: index % 2 ? 503 : null,
    message: `Failure ${index + 1}`,
  })),
});

const text = (root: ParentNode, selector: string) =>
  root.querySelector(selector)?.textContent?.replaceAll(/\s+/gu, ' ').trim();

describe('AdminPartnersPage', () => {
  let fixture: ComponentFixture<AdminPartnersPage>;
  let http: HttpTestingController;
  let announcer: LiveAnnouncer;

  const host = () => fixture.nativeElement as HTMLElement;
  const card = (name: string) =>
    [...host().querySelectorAll<HTMLElement>('mat-card')].find((element) =>
      element.querySelector('h2')?.textContent?.includes(name)
    ) as HTMLElement;
  const refreshButton = (name: string) =>
    card(name).querySelector<HTMLButtonElement>(':scope .actions button') as HTMLButtonElement;

  const load = async (partners: PartnerStatus[] = [HARBOR, KESTREL]): Promise<void> => {
    fixture = TestBed.createComponent(AdminPartnersPage);
    fixture.detectChanges();
    http.expectOne('/api/v1/admin/partners').flush(partners);
    await fixture.whenStable();
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAppIcons(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    announcer = TestBed.inject(LiveAnnouncer);
    vi.spyOn(announcer, 'announce').mockResolvedValue(undefined);
  });

  afterEach(() => {
    http.verify();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('shows one card per partner with refresh times, live count and errors', async () => {
    vi.useFakeTimers({ now: Date.parse('2026-11-20T06:12:00Z'), toFake: ['Date'] });
    await load();

    const harbor = card('Harbor Supply Co.');

    expect({
      health: text(harbor, '.health'),
      lastRefresh: text(harbor, '.facts div:first-child dd'),
      lastRefreshAt: harbor.querySelector('time')?.getAttribute('datetime'),
      liveProducts: text(harbor, '.number'),
      errors: text(harbor, '.muted'),
    }).toStrictEqual({
      health: 'Last refresh succeeded',
      lastRefresh: expect.stringMatching(/^12 minutes ago \(Nov 20, 2026, .+\)$/u),
      lastRefreshAt: '2026-11-20T06:00:00Z',
      liveProducts: '214',
      errors: 'No recent errors.',
    });

    const kestrel = card('Kestrel Outfitters');

    expect({
      health: text(kestrel, '.health'),
      lastErrorLabel: text(kestrel, '.last-error strong'),
      lastError: text(kestrel, '.last-error p'),
      errorsShown: kestrel.querySelectorAll(':scope .errors li').length,
      firstError: text(kestrel, '.errors li'),
    }).toStrictEqual({
      health: 'Last refresh failed',
      lastErrorLabel: 'Last error',
      lastError: 'Stock CSV timed out after 10 s',
      errorsShown: 5,
      firstError: expect.stringContaining('stock'),
    });

    const showAll = [...kestrel.querySelectorAll('button')].find((b) =>
      b.textContent?.includes('Show all')
    );
    showAll?.click();
    await fixture.whenStable();

    expect({
      errorsShown: kestrel.querySelectorAll(':scope .errors li').length,
      expanded: showAll?.getAttribute('aria-expanded'),
    }).toStrictEqual({ errorsShown: 7, expanded: 'true' });
  });

  it('says how many products the mass-removal guard is holding back', async () => {
    await load([HARBOR, { ...KESTREL, heldRemovals: 12 }]);

    expect(text(card('Kestrel Outfitters'), '.facts')).toContain(
      'Held back from removal 12 products missing from the last catalogue, still listed until confirmed'
    );
    expect(text(card('Harbor Supply Co.'), '.facts')).not.toContain('Held back');
  });

  it('confirms held removals only after a second click, then refreshes with confirmRemovals', async () => {
    await load([HARBOR, { ...KESTREL, heldRemovals: 12 }]);
    const kestrel = card('Kestrel Outfitters');
    const confirmButton = () => kestrel.querySelector<HTMLButtonElement>('.confirm-removals');
    const checkButtons = () => [
      ...kestrel.querySelectorAll<HTMLButtonElement>(':scope .removal-check button'),
    ];

    expect(card('Harbor Supply Co.').querySelector('.confirm-removals')).toBeNull();

    confirmButton()?.click();
    fixture.detectChanges();

    expect(text(kestrel, '.removal-check p')).toBe(
      'Remove 12 products from the shop? Check first that they have really left the Kestrel Outfitters catalogue.'
    );

    http.expectNone((request) => request.url.endsWith('/refresh'));

    checkButtons().at(1)?.click();
    fixture.detectChanges();

    expect(kestrel.querySelector('.removal-check')).toBeNull();

    http.expectNone((request) => request.url.endsWith('/refresh'));

    confirmButton()?.click();
    fixture.detectChanges();
    checkButtons().at(0)?.click();
    fixture.detectChanges();
    const post = http.expectOne('/api/v1/admin/partners/kestrel/refresh?confirmRemovals=true');

    expect(post.request.method).toBe('POST');
    expect(announcer.announce).toHaveBeenCalledWith(
      'Refreshing Kestrel Outfitters and removing the held products',
      'polite'
    );

    post.flush(null, ACCEPTED);
    // Leaving the page stops the status polling the refresh started.
    fixture.destroy();
  });

  /**
  The 202 carries the server's clock: the refresh was accepted at 06:10:00Z.
  */
  const ACCEPTED = {
    status: 202,
    statusText: 'Accepted',
    headers: { Date: 'Fri, 20 Nov 2026 06:10:00 GMT' },
  };
  const QUEUED = { refreshRequestedUtc: '2026-11-20T06:10:00Z' };
  const isBusy = (name: string) => refreshButton(name).getAttribute('aria-disabled') === 'true';

  const startRefresh = async (name: string, partner: string): Promise<void> => {
    await load();
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'setTimeout', 'clearTimeout'] });
    refreshButton(name).click();
    const post = http.expectOne(`/api/v1/admin/partners/${partner}/refresh`);

    expect(post.request.method).toBe('POST');

    post.flush(null, ACCEPTED);
    fixture.detectChanges();
  };

  const poll = async (partners: PartnerStatus[]): Promise<void> => {
    await vi.advanceTimersByTimeAsync(REFRESH_POLL_INTERVAL_MS);
    http.expectOne('/api/v1/admin/partners').flush(partners);
    fixture.detectChanges();
  };

  /**
  Answers `count` polls in a row with the same list; each poll waits for the one before it.
  */
  const pollRepeatedly = async (count: number, partners: PartnerStatus[]): Promise<void> => {
    if (count === 0) {
      return;
    }
    await poll(partners);
    await pollRepeatedly(count - 1, partners);
  };

  it('refreshes: POST, then polls every 3 s until the requested refresh has run', async () => {
    await startRefresh('Harbor Supply Co.', 'harbor');

    // Stays focusable while busy (disabledInteractive), and a second click does nothing.
    refreshButton('Harbor Supply Co.').click();
    http.expectNone('/api/v1/admin/partners/harbor/refresh');

    expect({
      busy: isBusy('Harbor Supply Co.'),
      disabled: refreshButton('Harbor Supply Co.').disabled,
      label: refreshButton('Harbor Supply Co.').textContent,
      progressBar: card('Harbor Supply Co.').querySelector('mat-progress-bar'),
    }).toStrictEqual({
      busy: true,
      disabled: false,
      label: expect.stringContaining('Refreshing…'),
      progressBar: expect.any(Element),
    });

    // Not run yet: the request is still pending.
    http.expectNone('/api/v1/admin/partners');
    await poll([{ ...HARBOR, ...QUEUED }, KESTREL]);

    expect(isBusy('Harbor Supply Co.')).toBeTruthy();

    // A scheduled sync that started before the request moved lastAttemptUtc: not ours, keep going.
    await poll([
      {
        ...HARBOR,
        ...QUEUED,
        lastAttemptUtc: '2026-11-20T06:08:00Z',
        lastSuccessUtc: '2026-11-20T06:08:00Z',
      },
      KESTREL,
    ]);

    expect(isBusy('Harbor Supply Co.')).toBeTruthy();

    // Ours has run.
    await poll([
      {
        ...HARBOR,
        refreshRequestedUtc: null,
        lastAttemptUtc: '2026-11-20T06:10:02Z',
        lastSuccessUtc: '2026-11-20T06:10:05.250Z',
        liveProductCount: 220,
      },
      KESTREL,
    ]);

    // Polling has stopped.
    await vi.advanceTimersByTimeAsync(REFRESH_POLL_INTERVAL_MS * 3);
    http.expectNone('/api/v1/admin/partners');

    expect({
      busy: isBusy('Harbor Supply Co.'),
      liveProducts: text(card('Harbor Supply Co.'), '.number'),
      outcome: text(card('Harbor Supply Co.'), '.outcome'),
    }).toStrictEqual({
      busy: false,
      liveProducts: '220',
      outcome: 'Harbor Supply Co. refreshed: 220 live products.',
    });
    expect(announcer.announce).toHaveBeenLastCalledWith(
      'Harbor Supply Co. refreshed: 220 live products.',
      'polite'
    );
  });

  it('reports a refresh that ran but failed, with its error', async () => {
    await startRefresh('Kestrel Outfitters', 'kestrel');

    await poll([
      HARBOR,
      { ...KESTREL, lastAttemptUtc: '2026-11-20T06:10:03Z', lastError: 'HTTP 503 from stock CSV' },
    ]);

    expect(isBusy('Kestrel Outfitters')).toBeFalsy();
    expect(text(card('Kestrel Outfitters'), '.outcome')).toBe(
      'Kestrel Outfitters refresh failed: HTTP 503 from stock CSV'
    );
  });

  it('compares instants, not strings, and uses the browser clock without a Date header', async () => {
    await load();
    vi.useFakeTimers({
      now: Date.parse('2026-11-20T06:10:00Z'),
      toFake: ['Date', 'setInterval', 'clearInterval', 'setTimeout', 'clearTimeout'],
    });
    refreshButton('Harbor Supply Co.').click();
    http
      .expectOne('/api/v1/admin/partners/harbor/refresh')
      .flush(null, { status: 202, statusText: 'Accepted' });

    // "06:10:00.5Z" sorts before "06:10:00Z" as text but is later as a time.
    await poll([
      {
        ...HARBOR,
        refreshRequestedUtc: '2026-11-20T06:10:00.5Z',
        lastAttemptUtc: '2026-11-20T06:10:00Z',
      },
      KESTREL,
    ]);

    expect(text(card('Harbor Supply Co.'), '.facts')).toContain('Refresh requested');
    expect(isBusy('Harbor Supply Co.')).toBeTruthy();

    await poll([
      {
        ...HARBOR,
        refreshRequestedUtc: null,
        lastAttemptUtc: '2026-11-20T06:10:04+00:00',
        lastSuccessUtc: '2026-11-20T06:10:04+00:00',
      },
      KESTREL,
    ]);

    expect(text(card('Harbor Supply Co.'), '.outcome')).toBe(
      'Harbor Supply Co. refreshed: 214 live products.'
    );
  });

  it('stops polling on a 500, shows the error and enables the button again', async () => {
    await startRefresh('Harbor Supply Co.', 'harbor');

    await vi.advanceTimersByTimeAsync(REFRESH_POLL_INTERVAL_MS);
    http
      .expectOne('/api/v1/admin/partners')
      .flush(
        { title: 'Internal Server Error', detail: 'The partner status store is unavailable.' },
        { status: 500, statusText: 'Internal Server Error' }
      );
    fixture.detectChanges();

    await vi.advanceTimersByTimeAsync(REFRESH_POLL_INTERVAL_MS * 3);
    http.expectNone('/api/v1/admin/partners');

    expect(isBusy('Harbor Supply Co.')).toBeFalsy();
    expect(text(card('Harbor Supply Co.'), '.outcome')).toBe(
      'Couldn’t refresh Harbor Supply Co.: The partner status store is unavailable.'
    );
    expect(announcer.announce).toHaveBeenLastCalledWith(
      'Couldn’t refresh Harbor Supply Co.: The partner status store is unavailable.',
      'assertive'
    );
  });

  it('stops polling when the page is destroyed mid-refresh', async () => {
    await startRefresh('Harbor Supply Co.', 'harbor');
    await poll([{ ...HARBOR, ...QUEUED }, KESTREL]);

    fixture.destroy();

    await vi.advanceTimersByTimeAsync(REFRESH_POLL_TIMEOUT_MS);
    http.expectNone('/api/v1/admin/partners');
  });

  it('gives up polling after a minute and says the refresh may still be running', async () => {
    await startRefresh('Harbor Supply Co.', 'harbor');

    await pollRepeatedly(REFRESH_POLL_TIMEOUT_MS / REFRESH_POLL_INTERVAL_MS, [
      { ...HARBOR, ...QUEUED },
      KESTREL,
    ]);
    await vi.advanceTimersByTimeAsync(REFRESH_POLL_INTERVAL_MS);
    http.expectNone('/api/v1/admin/partners');
    fixture.detectChanges();

    expect(isBusy('Harbor Supply Co.')).toBeFalsy();
    expect(text(card('Harbor Supply Co.'), '.outcome')).toContain(
      'has not finished refreshing after 60 seconds'
    );
  });

  it.each([401, 403])('asks the user to sign in as an admin on %d', async (status) => {
    fixture = TestBed.createComponent(AdminPartnersPage);
    fixture.detectChanges();
    http
      .expectOne('/api/v1/admin/partners')
      .flush({ title: 'Forbidden', status }, { status, statusText: 'Denied' });
    await fixture.whenStable();

    expect(text(host(), '.state strong')).toBe('Sign in as an admin to see this page.');
    expect(host().querySelector(':scope .state a')?.getAttribute('href')).toBe('/sign-in');
    expect(host().querySelector('mat-card')).toBeNull();
    expect(host().textContent?.includes('does not have the admin role')).toBe(status === 403);
  });

  it('shows the sign-in message when the refresh call itself is refused', async () => {
    await load();

    refreshButton('Harbor Supply Co.').click();
    http
      .expectOne('/api/v1/admin/partners/harbor/refresh')
      .flush({ title: 'Forbidden', status: 403 }, { status: 403, statusText: 'Forbidden' });
    await fixture.whenStable();

    expect(text(host(), '.state strong')).toBe('Sign in as an admin to see this page.');
  });

  it('offers Retry when the status fails to load', async () => {
    fixture = TestBed.createComponent(AdminPartnersPage);
    fixture.detectChanges();
    http.expectOne('/api/v1/admin/partners').flush('down', { status: 500, statusText: 'Error' });
    await fixture.whenStable();

    (host().querySelector(':scope [role="alert"] button') as HTMLButtonElement).click();
    await vi.waitFor(() => http.expectOne('/api/v1/admin/partners').flush([HARBOR]));
    await fixture.whenStable();

    expect(host().querySelectorAll('mat-card')).toHaveLength(1);
  });
});
