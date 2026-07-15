/** API stores UTC instants; SQL/JSON may omit the Z suffix. */
export function parseUtcInstant(value: string): Date {
  const trimmed = value.trim();
  if (!trimmed) {
    return new Date(Number.NaN);
  }

  if (trimmed.endsWith('Z') || /[+-]\d{2}:\d{2}$/.test(trimmed)) {
    return new Date(trimmed);
  }

  return new Date(`${trimmed}Z`);
}

export function formatInTimeZone(
  value: string | null | undefined,
  timeZoneId: string,
  locale?: string | string[]
): string {
  if (!value) {
    return '—';
  }

  const date = parseUtcInstant(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  try {
    return new Intl.DateTimeFormat(locale, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
      hour: 'numeric',
      minute: '2-digit',
      timeZone: timeZoneId,
      timeZoneName: 'short',
    }).format(date);
  } catch {
    return date.toLocaleString(locale, { timeZone: timeZoneId, timeZoneName: 'short' });
  }
}

/** Historical instants (last run, run history) in the viewer's local timezone. */
export function formatInUserLocal(
  value: string | null | undefined,
  locale?: string | string[]
): string {
  if (!value) {
    return '—';
  }

  const date = parseUtcInstant(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return new Intl.DateTimeFormat(locale, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
    timeZoneName: 'short',
  }).format(date);
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
    return '—';
  }

  return formatInTimeZone(value, timeZoneId, locale);
}
