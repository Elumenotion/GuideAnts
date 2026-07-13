import { describe, expect, it } from 'vitest';
import { parseOptionalPositiveInt } from '../toolLimitForm';

describe('parseOptionalPositiveInt', () => {
  it('returns undefined for blank input', () => {
    expect(parseOptionalPositiveInt('')).toEqual({ ok: true, value: undefined });
    expect(parseOptionalPositiveInt('   ')).toEqual({ ok: true, value: undefined });
  });

  it('parses positive integers', () => {
    expect(parseOptionalPositiveInt('12')).toEqual({ ok: true, value: 12 });
  });

  it('rejects negative values', () => {
    const result = parseOptionalPositiveInt('-1');
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.error).toContain('at least 1');
    }
  });

  it('rejects zero', () => {
    const result = parseOptionalPositiveInt('0');
    expect(result.ok).toBe(false);
  });
});
