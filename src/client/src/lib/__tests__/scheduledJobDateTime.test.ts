import { describe, expect, it } from 'vitest';
import {
  formatInTimeZone,
  formatInUserLocal,
  formatNextRun,
  parseUtcInstant,
} from '../scheduledJobDateTime';

describe('scheduledJobDateTime', () => {
  it('parses UTC instants without a Z suffix as UTC', () => {
    const parsed = parseUtcInstant('2026-07-15T14:30:00');
    expect(parsed.toISOString()).toBe('2026-07-15T14:30:00.000Z');
  });

  it('formats next run in the job timezone', () => {
    const formatted = formatInTimeZone('2026-07-15T14:30:00Z', 'America/New_York', 'en-US');
    expect(formatted).toMatch(/15/);
    expect(formatted).toMatch(/10:30/);
    expect(formatted).toMatch(/EDT|EST/);
  });

  it('formats disabled next run copy', () => {
    expect(formatNextRun('2026-07-15T14:30:00Z', 'UTC', false)).toBe('Not scheduled while disabled');
  });

  it('formats user-local instants', () => {
    const formatted = formatInUserLocal('2026-07-15T14:30:00Z', 'en-US');
    expect(formatted).toMatch(/2026|15/);
  });
});
