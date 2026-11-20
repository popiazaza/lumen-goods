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

describe('AdminPartnersPage', () => {
  let fixture: ComponentFixture<AdminPartnersPage>;
  let http: HttpTestingController;
  let announcer: LiveAnnouncer;

  const host = () => fixture.nativeElement as HTMLElement;
  const text = (root: ParentNode, selector: string) =>
    root.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();
  const card = (name: string) =>
    [...host().querySelectorAll<HTMLElement>('mat-card')].find((element) =>
      element.querySelector('h2')?.textContent?.includes(name),
    ) as HTMLElement;
  const refreshButton = (name: string) =>
    card(name).querySelector<HTMLButtonElement>('.actions button') as HTMLButtonElement;

  async function load(partners: PartnerStatus[] = [HARBOR, KESTREL]): Promise<void> {
    fixture = TestBed.createComponent(AdminPartnersPage);
    fixture.detectChanges();
    http.expectOne('/api/v1/admin/partners').flush(partners);
    await fixture.whenStable();
  }

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
    expect(text(harbor, '.health')).toBe('Last refresh succeeded');
    expect(text(harbor, '.facts div:first-child dd')).toMatch(
      /^12 minutes ago \(Nov 20, 2026, .+\)$/,
    );
    expect(harbor.querySelector('time')?.getAttribute('datetime')).toBe('2026-11-20T06:00:00Z');
    expect(text(harbor, '.number')).toBe('214');
    expect(text(harbor, '.muted')).toBe('No recent errors.');

    const kestrel = card('Kestrel Outfitters');
    expect(text(kestrel, '.health')).toBe('Last refresh failed');
    expect(text(kestrel, '.last-error strong')).toBe('Last error');
    expect(text(kestrel, '.last-error p')).toBe('Stock CSV timed out after 10 s');
    expect(kestrel.querySelectorAll('.errors li')).toHaveLength(5);
    expect(text(kestrel, '.errors li')).toContain('stock');

    const showAll = [...kestrel.querySelectorAll('button')].find((b) =>
      b.textContent?.includes('Show all'),
    );
    showAll?.click();
    await fixture.whenStable();
    expect(kestrel.querySelectorAll('.errors li')).toHaveLength(7);
    expect(showAll?.getAttribute('aria-expanded')).toBe('true');
  });

  it('refreshes: POST, then polls every 3 s until lastAttemptUtc changes', async () => {
    await load();
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'setTimeout', 'clearTimeout'] });

    refreshButton('Harbor Supply Co.').click();
    const post = http.expectOne('/api/v1/admin/partners/harbor/refresh');
    expect(post.request.method).toBe('POST');
    post.flush(null, { status: 202, statusText: 'Accepted' });
    fixture.detectChanges();
    expect(refreshButton('Harbor Supply Co.').disabled).toBe(true);
    expect(refreshButton('Harbor Supply Co.').textContent).toContain('Refreshing…');
    expect(card('Harbor Supply Co.').querySelector('mat-progress-bar')).not.toBeNull();

    // First poll: the job has not run yet.
    http.expectNone('/api/v1/admin/partners');
    await vi.advanceTimersByTimeAsync(REFRESH_POLL_INTERVAL_MS);
    http.expectOne('/api/v1/admin/partners').flush([HARBOR, KESTREL]);

    // Second poll: it has.
    await vi.advanceTimersByTimeAsync(REFRESH_POLL_INTERVAL_MS);
    const refreshed = {
      ...HARBOR,
      lastAttemptUtc: '2026-11-20T06:20:00Z',
      lastSuccessUtc: '2026-11-20T06:20:00Z',
      liveProductCount: 220,
    };
    http.expectOne('/api/v1/admin/partners').flush([refreshed, KESTREL]);
    fixture.detectChanges();

    // Polling has stopped.
    await vi.advanceTimersByTimeAsync(REFRESH_POLL_INTERVAL_MS * 3);
    http.expectNone('/api/v1/admin/partners');

    expect(refreshButton('Harbor Supply Co.').disabled).toBe(false);
    expect(text(card('Harbor Supply Co.'), '.number')).toBe('220');
    expect(text(card('Harbor Supply Co.'), '.outcome')).toBe(
      'Harbor Supply Co. refreshed: 220 live products.',
    );
    expect(announcer.announce).toHaveBeenLastCalledWith(
      'Harbor Supply Co. refreshed: 220 live products.',
      'polite',
    );
  });

  it('reports a refresh that ran but failed', async () => {
    await load();
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'setTimeout', 'clearTimeout'] });

    refreshButton('Kestrel Outfitters').click();
    http
      .expectOne('/api/v1/admin/partners/kestrel/refresh')
      .flush(null, { status: 202, statusText: 'Accepted' });
    await vi.advanceTimersByTimeAsync(REFRESH_POLL_INTERVAL_MS);
    http
      .expectOne('/api/v1/admin/partners')
      .flush([
        HARBOR,
        { ...KESTREL, lastAttemptUtc: '2026-11-20T06:30:00Z', lastError: 'HTTP 503' },
      ]);
    fixture.detectChanges();

    expect(text(card('Kestrel Outfitters'), '.outcome')).toBe(
      'Kestrel Outfitters refresh failed: HTTP 503',
    );
  });

  it('gives up polling after a minute and says the refresh may still be running', async () => {
    await load();
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'setTimeout', 'clearTimeout'] });

    refreshButton('Harbor Supply Co.').click();
    http
      .expectOne('/api/v1/admin/partners/harbor/refresh')
      .flush(null, { status: 202, statusText: 'Accepted' });
    const polls = REFRESH_POLL_TIMEOUT_MS / REFRESH_POLL_INTERVAL_MS;
    for (let poll = 0; poll < polls; poll++) {
      await vi.advanceTimersByTimeAsync(REFRESH_POLL_INTERVAL_MS);
      http.expectOne('/api/v1/admin/partners').flush([HARBOR, KESTREL]);
    }
    await vi.advanceTimersByTimeAsync(REFRESH_POLL_INTERVAL_MS);
    http.expectNone('/api/v1/admin/partners');
    fixture.detectChanges();

    expect(refreshButton('Harbor Supply Co.').disabled).toBe(false);
    expect(text(card('Harbor Supply Co.'), '.outcome')).toContain(
      'has not finished refreshing after 60 seconds',
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
    expect(host().querySelector('.state a')?.getAttribute('href')).toBe('/sign-in');
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

    (host().querySelector('[role="alert"] button') as HTMLButtonElement).click();
    await vi.waitFor(() => http.expectOne('/api/v1/admin/partners').flush([HARBOR]));
    await fixture.whenStable();

    expect(host().querySelectorAll('mat-card')).toHaveLength(1);
  });
});
