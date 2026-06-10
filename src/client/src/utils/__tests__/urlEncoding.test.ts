import { describe, it, expect } from 'vitest';
import { safeDecodeURIComponent } from '../urlEncoding';

describe('urlEncoding', () => {
  describe('safeDecodeURIComponent', () => {
    it('decodes valid percent-encoded strings', () => {
      expect(safeDecodeURIComponent('hello%20world')).toBe('hello world');
      expect(safeDecodeURIComponent('path%2Fto%2Ffile')).toBe('path/to/file');
    });

    it('returns original value when decoding throws', () => {
      expect(safeDecodeURIComponent('%E0%A4%A')).toBe('%E0%A4%A');
      expect(safeDecodeURIComponent('%')).toBe('%');
    });

    it('returns plain strings unchanged', () => {
      expect(safeDecodeURIComponent('plain-text')).toBe('plain-text');
    });
  });
});
