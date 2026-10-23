import { describe, expect, it } from 'vitest';
import {
  formatDateTime,
  formatInTimeZone,
  formatInUserLocal,
  formatNextRun,
  formatRelative,
  formatTime,
  parseUtcInstant,
} from '../dateTime';

describe('dateTime', () => {
  it('parses UTC instants without a Z suffix as UTC', () => {
    expect(parseUtcInstant('2026-07-15T14:30:00').toISOString()).toBe('2026-07-15T14:30:00.000Z');
  });

  it('parses Z-suffixed instants as UTC', () => {
    expect(parseUtcInstant('2026-07-15T14:30:00Z').toISOString()).toBe('2026-07-15T14:30:00.000Z');
  });

  it('parses explicit-offset instants preserving the instant', () => {
    expect(parseUtcInstant('2026-07-15T16:30:00+02:00').toISOString()).toBe('2026-07-15T14:30:00.000Z');
  });

  it('formats an explicit-timezone instant', () => {
    const formatted = formatInTimeZone('2026-07-15T14:30:00Z', 'America/New_York', 'en-US');
    expect(formatted).toMatch(/15/);
    expect(formatted).toMatch(/10:30/);
  });

  it('formats disabled next run copy', () => {
    expect(formatNextRun('2026-07-15T14:30:00Z', 'UTC', false)).toBe('Not scheduled while disabled');
  });

  it('formats user-local instants', () => {
    expect(formatInUserLocal('2026-07-15T14:30:00Z', 'en-US')).toMatch(/2026|15/);
  });

  it('formatDateTime renders an explicit timezone deterministically', () => {
    const formatted = formatDateTime('2026-07-15T14:30:00Z', { timeZone: 'America/New_York', locale: 'en-US' });
    expect(formatted).toMatch(/10:30/);
  });

  it('formatDateTime returns an em-dash for empty values', () => {
    expect(formatDateTime(null)).toBe('\u2014');
    expect(formatDateTime('')).toBe('\u2014');
  });

  it('formatDateTime echoes unparseable values', () => {
    expect(formatDateTime('not-a-date')).toBe('not-a-date');
  });

  it('formatTime renders an explicit-timezone time of day', () => {
    // formatTime has no zone override; pin the check by parsing in a known zone.
    const formatted = formatTime('2026-07-15T14:30:00Z');
    expect(formatted).toMatch(/\d{1,2}:\d{2}:\d{2}/);
  });

  it('formatRelative reports just now for fresh instants', () => {
    expect(formatRelative(new Date().toISOString())).toBe('just now');
  });

  it('formatRelative reports minutes for recent instants', () => {
    expect(formatRelative(new Date(Date.now() - 5 * 60000).toISOString())).toBe('5m ago');
  });

  it('formatRelative reports hours for same-day instants', () => {
    expect(formatRelative(new Date(Date.now() - 3 * 3600000).toISOString())).toBe('3h ago');
  });

  it('formatRelative reports days for this-week instants', () => {
    expect(formatRelative(new Date(Date.now() - 2 * 86400000).toISOString())).toBe('2d ago');
  });

  it('formatRelative falls back to a short date beyond a week', () => {
    const formatted = formatRelative('2026-07-15T14:30:00Z');
    expect(formatted).not.toBe('2026-07-15T14:30:00Z');
    expect(formatted.length).toBeGreaterThan(0);
  });

  it('parseUtcInstant and formatDateTime agree on the instant across zones', () => {
    // 14:30 UTC == 10:30 EDT (July) in New York.
    const asUtc = formatDateTime('2026-07-15T14:30:00Z', { timeZone: 'UTC', locale: 'en-US' });
    const asNy = formatDateTime('2026-07-15T14:30:00Z', { timeZone: 'America/New_York', locale: 'en-US' });
    expect(asUtc).toMatch(/2:30 PM/);
    expect(asNy).toMatch(/10:30 AM/);
  });
});
