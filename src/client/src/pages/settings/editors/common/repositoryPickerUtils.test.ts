import { describe, expect, it } from 'vitest';
import { formatBytes, normalizeHuggingFaceRepository } from './repositoryPickerUtils';

describe('formatBytes', () => {
  it('returns unknown size for invalid inputs', () => {
    expect(formatBytes(null)).toBe('unknown size');
    expect(formatBytes(undefined)).toBe('unknown size');
    expect(formatBytes(Number.NaN)).toBe('unknown size');
    expect(formatBytes(0)).toBe('unknown size');
    expect(formatBytes(-1)).toBe('unknown size');
  });

  it('formats byte counts across unit boundaries', () => {
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(1536)).toBe('1.50 KB');
    expect(formatBytes(10_485_760)).toBe('10.0 MB');
    expect(formatBytes(1_073_741_824)).toBe('1.00 GB');
    expect(formatBytes(1_099_511_627_776)).toBe('1.00 TB');
    expect(formatBytes(150_000)).toBe('146 KB');
  });
});

describe('normalizeHuggingFaceRepository', () => {
  it('returns null for empty or invalid slugs', () => {
    expect(normalizeHuggingFaceRepository('')).toBeNull();
    expect(normalizeHuggingFaceRepository('   ')).toBeNull();
    expect(normalizeHuggingFaceRepository('owner-only')).toBeNull();
    expect(normalizeHuggingFaceRepository('/repo-only')).toBeNull();
  });

  it('normalizes canonical and URL forms', () => {
    expect(normalizeHuggingFaceRepository('Qwen/Qwen3-9B')).toBe('Qwen/Qwen3-9B');
    expect(
      normalizeHuggingFaceRepository('https://huggingface.co/Qwen/Qwen3-9B/tree/main'),
    ).toBe('Qwen/Qwen3-9B');
    expect(
      normalizeHuggingFaceRepository('http://www.huggingface.co/Qwen/Qwen3-9B/'),
    ).toBe('Qwen/Qwen3-9B');
  });
});
