import { describe, expect, it } from 'vitest';
import { formatLimitDisplay } from '../toolLimitDisplay';

describe('formatLimitDisplay', () => {
  it('returns Unlimited for nullish values', () => {
    expect(formatLimitDisplay(undefined)).toBe('Unlimited');
    expect(formatLimitDisplay(null)).toBe('Unlimited');
  });

  it('returns the numeric value as a string', () => {
    expect(formatLimitDisplay(12)).toBe('12');
  });
});
