const HEX_CODE_UNIT_LENGTH = 4;

function isHexCodeUnit(value: string): boolean {
  return /^[0-9a-fA-F]{4}$/.test(value);
}

function countPrecedingBackslashes(text: string, index: number): number {
  let count = 0;
  let cursor = index - 1;
  while (cursor >= 0 && text[cursor] === '\\') {
    count++;
    cursor--;
  }
  return count;
}

function readEscapedCodeUnit(text: string, index: number): number | null {
  if (index + 6 > text.length) return null;
  if (text[index] !== '\\' || text[index + 1] !== 'u') return null;
  if (countPrecedingBackslashes(text, index) % 2 !== 0) return null;

  const hex = text.slice(index + 2, index + 2 + HEX_CODE_UNIT_LENGTH);
  if (!isHexCodeUnit(hex)) return null;
  return parseInt(hex, 16);
}

function hasEmojiStyleEscapes(text: string): boolean {
  for (let i = 0; i < text.length; i++) {
    const high = readEscapedCodeUnit(text, i);
    if (high == null) continue;

    if (high >= 0xd800 && high <= 0xdbff) {
      const low = readEscapedCodeUnit(text, i + 6);
      if (low != null && low >= 0xdc00 && low <= 0xdfff) {
        return true;
      }
    }
  }
  return false;
}

/**
 * Decodes JSON-style unicode escapes in assistant text when it appears that
 * emoji/surrogate escapes leaked through as literals (e.g. "\uD83C\uDDFA").
 *
 * We intentionally gate this to strings containing surrogate-pair escapes to
 * reduce accidental rewrites of ordinary technical content that may
 * intentionally include sequences like "\u0041".
 *
 * Tradeoff:
 * - Once we detect emoji-style surrogate escapes in a message, we decode
 *   all valid \uXXXX escapes in that message (not only the emoji pair).
 * - That means some characters that were previously shown literally as escaped
 *   text may now render as real characters.
 * - This is a deliberate display-time fix for assistant text quality, not a
 *   lossless round-trip representation of the original raw payload.
 */
export function decodeAssistantUnicodeEscapes(text: string): string {
  if (!text || !text.includes('\\u')) {
    return text;
  }

  if (!hasEmojiStyleEscapes(text)) {
    return text;
  }

  let output = '';
  let i = 0;
  let changed = false;

  while (i < text.length) {
    const high = readEscapedCodeUnit(text, i);
    if (high == null) {
      output += text[i];
      i++;
      continue;
    }

    if (high >= 0xd800 && high <= 0xdbff) {
      const low = readEscapedCodeUnit(text, i + 6);
      if (low != null && low >= 0xdc00 && low <= 0xdfff) {
        const codePoint = 0x10000 + ((high - 0xd800) << 10) + (low - 0xdc00);
        output += String.fromCodePoint(codePoint);
        i += 12;
        changed = true;
        continue;
      }
    }

    output += String.fromCharCode(high);
    i += 6;
    changed = true;
  }

  return changed ? output : text;
}
