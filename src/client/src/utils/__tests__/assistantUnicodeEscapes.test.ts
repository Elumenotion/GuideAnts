import { describe, it, expect } from 'vitest';
import { decodeAssistantUnicodeEscapes } from '../assistantUnicodeEscapes';

describe('decodeAssistantUnicodeEscapes', () => {
  it('returns empty and plain strings unchanged', () => {
    expect(decodeAssistantUnicodeEscapes('')).toBe('');
    expect(decodeAssistantUnicodeEscapes('hello world')).toBe('hello world');
  });

  it('does not decode non-emoji surrogate escapes', () => {
    expect(decodeAssistantUnicodeEscapes('\\u0041')).toBe('\\u0041');
    expect(decodeAssistantUnicodeEscapes('Use \\u0026 for ampersand')).toBe(
      'Use \\u0026 for ampersand',
    );
  });

  it('decodes emoji surrogate pairs', () => {
    expect(decodeAssistantUnicodeEscapes('\\uD83C\\uDDFA\\uD83C\\uDDF8')).toBe('🇺🇸');
    expect(decodeAssistantUnicodeEscapes('Flag \\uD83C\\uDDFA\\uD83C\\uDDF8 here')).toBe('Flag 🇺🇸 here');
  });

  it('decodes all valid escapes once emoji surrogates are detected', () => {
    expect(decodeAssistantUnicodeEscapes('\\uD83D\\uDE00 and \\u0041')).toBe('😀 and A');
  });

  it('ignores invalid escape sequences', () => {
    expect(decodeAssistantUnicodeEscapes('\\uZZZZ')).toBe('\\uZZZZ');
    expect(decodeAssistantUnicodeEscapes('\\uD83C')).toBe('\\uD83C');
  });

  it('does not decode escaped backslashes before unicode sequences', () => {
    expect(decodeAssistantUnicodeEscapes('\\\\uD83C\\\\uDDFA')).toBe('\\\\uD83C\\\\uDDFA');
  });
});
