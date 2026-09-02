import { describe, expect, it } from 'vitest';
import { isPlainTextFile } from '../textFiles';

describe('isPlainTextFile', () => {
  it('matches .txt by extension', () => {
    expect(isPlainTextFile('notes.txt')).toBe(true);
    expect(isPlainTextFile('NOTES.TXT')).toBe(true);
  });

  it('matches text/plain content type', () => {
    expect(isPlainTextFile('readme', 'text/plain')).toBe(true);
  });

  it('does not match other text types', () => {
    expect(isPlainTextFile('data.json', 'application/json')).toBe(false);
    expect(isPlainTextFile('script.ts', 'application/typescript')).toBe(false);
    expect(isPlainTextFile('page.html', 'text/html')).toBe(false);
  });
});
