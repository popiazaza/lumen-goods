const relative = new Intl.RelativeTimeFormat('en-US', { numeric: 'auto' });

const MINUTE = 60;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/**
 * "just now", "5 minutes ago", "3 hours ago", "yesterday". Always paired with the absolute time
 * in a `<time>` element, so this is a reading aid, not the record.
 */
export function relativeTime(isoUtc: string, nowMs: number): string {
  const thenMs = Date.parse(isoUtc);
  if (Number.isNaN(thenMs)) {
    return '';
  }
  const seconds = Math.round((thenMs - nowMs) / 1000);
  const abs = Math.abs(seconds);
  if (abs < 45) {
    return 'just now';
  }
  if (abs < 45 * MINUTE) {
    return relative.format(Math.round(seconds / MINUTE), 'minute');
  }
  if (abs < 22 * HOUR) {
    return relative.format(Math.round(seconds / HOUR), 'hour');
  }
  return relative.format(Math.round(seconds / DAY), 'day');
}
