/**
 * Canonical date/time presentation helpers.
 *
 * The API stores UTC instants; SQL/JSON may omit the Z suffix, so every parse
 * goes through parseUtcInstant, which treats zone-less values as UTC.
 *
 * Presentation rule: render in the BROWSER's own region (timezone + locale).
 * There is no user-configurable preference; Intl resolves it at call time.
 */

/** API stores UTC instants; SQL/JSON may omit the Z suffix. */
export function parseUtcInstant(value: string): Date {
  const trimmed = value.trim();
  if (!trimmed) {
    return new Date(Number.NaN);
  }

  if (trimmed.endsWith('Z') || /[\u002B-]\d{2}:\d{2}$/.test(trimmed)) {
    return new Date(trimmed);
  }

  return new Date(`${trimmed}Z`);
}

/** Parses a stored UTC instant into a Date, or null when empty/unparseable. */
export function parseUtc(value: string | null | undefined): Date | null {
  if (!value) {
    return null;
  }
  const date = parseUtcInstant(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

export function browserTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

export function browserLocale(): string {
  return Intl.DateTimeFormat().resolvedOptions().locale;
}

/**
 * Formats a stored UTC instant in the browser's timezone and locale.
 * Returns an em-dash for empty values and the raw value when unparseable.
 */
export function formatDateTime(
  value: string | null | undefined,
  options: { timeZone?: string; locale?: string | string[]; timeZoneName?: 'short' | 'long' | 'shortGeneric' | 'longGeneric' } = {}
): string {
  if (!value) {
    return '\u2014';
  }

  const date = parseUtcInstant(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  const timeZone = options.timeZone ?? browserTimeZone();
  const locale = options.locale ?? browserLocale();
  try {
    return new Intl.DateTimeFormat(locale, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
      hour: 'numeric',
      minute: '2-digit',
      timeZone,
      ...(options.timeZoneName ? { timeZoneName: options.timeZoneName } : {}),
    }).format(date);
  } catch {
    return date.toLocaleString(locale, {
      timeZone,
      ...(options.timeZoneName ? { timeZoneName: options.timeZoneName } : {}),
    });
  }
}

/** Time-of-day (no date) in the browser's timezone. */
export function formatTime(value: string | null | undefined): string {
  if (!value) {
    return '\u2014';
  }

  const date = parseUtcInstant(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  const locale = browserLocale();
  try {
    return new Intl.DateTimeFormat(locale, {
      hour: 'numeric',
      minute: '2-digit',
      second: '2-digit',
      timeZone: browserTimeZone(),
    }).format(date);
  } catch {
    return date.toLocaleTimeString(locale);
  }
}

/**
 * Compact relative time ("just now", "5m ago", "3h ago", "2d ago"),
 * falling back to a short date for anything older than a week.
 * Mirrors the grouping the conversation lists already display.
 */
export function formatRelative(value: string | null | undefined): string {
  if (!value) {
    return '\u2014';
  }

  const date = parseUtcInstant(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  const diffMs = Date.now() - date.getTime();
  const minutes = Math.round(diffMs / 60000);
  if (minutes < 1) {
    return 'just now';
  }
  if (minutes < 60) {
    return `${minutes}m ago`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours}h ago`;
  }
  const days = Math.floor(hours / 24);
  if (days < 7) {
    return `${days}d ago`;
  }

  try {
    return new Intl.DateTimeFormat(browserLocale(), {
      month: 'short',
      day: 'numeric',
    }).format(date);
  } catch {
    return value;
  }
}

/** Formats an instant in an explicit IANA timezone (e.g. a job's TimeZoneId). */
export function formatInTimeZone(
  value: string | null | undefined,
  timeZoneId: string,
  locale?: string | string[]
): string {
  return formatDateTime(value, { timeZone: timeZoneId, locale, timeZoneName: 'short' });
}

/** Historical instants (last run, run history) in the viewer's local timezone. */
export function formatInUserLocal(
  value: string | null | undefined,
  locale?: string | string[]
): string {
  return formatDateTime(value, { locale });
}

export function formatNextRun(
  value: string | null | undefined,
  timeZoneId: string,
  isEnabled: boolean,
  locale?: string | string[]
): string {
  if (!isEnabled) {
    return 'Not scheduled while disabled';
  }

  if (!value) {
    return '\u2014';
  }

  return formatInTimeZone(value, timeZoneId, locale);
}
