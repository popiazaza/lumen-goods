import { relativeTime } from './relative-time';

describe('relativeTime', () => {
  const now = Date.parse('2026-11-20T12:00:00Z');

  it.each([
    ['2026-11-20T11:59:30Z', 'just now'],
    ['2026-11-20T11:55:00Z', '5 minutes ago'],
    ['2026-11-20T09:00:00Z', '3 hours ago'],
    ['2026-11-19T10:00:00Z', 'yesterday'],
    ['2026-11-17T12:00:00Z', '3 days ago'],
    ['2026-11-20T12:10:00Z', 'in 10 minutes'],
  ])('%s -> %s', (iso, text) => {
    expect(relativeTime(iso, now)).toBe(text);
  });

  it('returns an empty string for an unparseable timestamp', () => {
    expect(relativeTime('not a date', now)).toBe('');
  });
});
