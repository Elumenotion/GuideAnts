type BomEncoding = 'utf-8' | 'utf-16le' | 'utf-16be' | 'utf-32le' | 'utf-32be';

interface BomInfo {
  encoding: BomEncoding;
  offset: number;
}

const DEFAULT_SAMPLE_SIZE = 16 * 1024;
const MAX_DISALLOWED_CONTROL_RATIO = 0.01;

function detectBom(bytes: Uint8Array): BomInfo | null {
  if (bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
    return { encoding: 'utf-8', offset: 3 };
  }

  if (bytes.length >= 4 && bytes[0] === 0xff && bytes[1] === 0xfe && bytes[2] === 0x00 && bytes[3] === 0x00) {
    return { encoding: 'utf-32le', offset: 4 };
  }

  if (bytes.length >= 4 && bytes[0] === 0x00 && bytes[1] === 0x00 && bytes[2] === 0xfe && bytes[3] === 0xff) {
    return { encoding: 'utf-32be', offset: 4 };
  }

  if (bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe) {
    return { encoding: 'utf-16le', offset: 2 };
  }

  if (bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff) {
    return { encoding: 'utf-16be', offset: 2 };
  }

  return null;
}

function hasNullByte(bytes: Uint8Array): boolean {
  for (let i = 0; i < bytes.length; i++) {
    if (bytes[i] === 0x00) {
      return true;
    }
  }

  return false;
}

function decodeUtf32(bytes: Uint8Array, littleEndian: boolean): string | null {
  if (bytes.length % 4 !== 0) {
    return null;
  }

  let text = '';
  for (let i = 0; i < bytes.length; i += 4) {
    const codePoint = littleEndian
      ? (bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16) | (bytes[i + 3] << 24)) >>> 0
      : (bytes[i + 3] | (bytes[i + 2] << 8) | (bytes[i + 1] << 16) | (bytes[i] << 24)) >>> 0;

    if (codePoint > 0x10ffff) {
      return null;
    }

    if (codePoint >= 0xd800 && codePoint <= 0xdfff) {
      return null;
    }

    try {
      text += String.fromCodePoint(codePoint);
    } catch {
      return null;
    }
  }

  return text;
}

function decodeBytes(bytes: Uint8Array, encoding: BomEncoding | 'utf-8-strict'): string | null {
  try {
    if (encoding === 'utf-32le') {
      return decodeUtf32(bytes, true);
    }

    if (encoding === 'utf-32be') {
      return decodeUtf32(bytes, false);
    }

    if (encoding === 'utf-8-strict') {
      return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    }

    return new TextDecoder(encoding, { fatal: true }).decode(bytes);
  } catch {
    return null;
  }
}

function isMostlyText(text: string): boolean {
  if (text.length === 0) {
    return false;
  }

  let disallowedControlCount = 0;
  for (const ch of text) {
    if (ch === '\uFFFD') {
      return false;
    }

    if (ch === '\u0000') {
      return false;
    }

    if (/\p{Cc}/u.test(ch) && ch !== '\t' && ch !== '\n' && ch !== '\r') {
      disallowedControlCount++;
    }
  }

  return disallowedControlCount / text.length <= MAX_DISALLOWED_CONTROL_RATIO;
}

function decodeWithBom(bytes: Uint8Array, bom: BomInfo): string | null {
  const sliced = bytes.subarray(bom.offset);
  return decodeBytes(sliced, bom.encoding);
}

async function readBlobAsBytes(blob: Blob): Promise<Uint8Array> {
  const blobWithArrayBuffer = blob as Blob & { arrayBuffer?: () => Promise<ArrayBuffer> };
  if (typeof blobWithArrayBuffer.arrayBuffer === 'function') {
    return new Uint8Array(await blobWithArrayBuffer.arrayBuffer());
  }

  if (typeof FileReader !== 'undefined') {
    return await new Promise<Uint8Array>((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => {
        if (reader.result instanceof ArrayBuffer) {
          resolve(new Uint8Array(reader.result));
          return;
        }

        reject(new Error('Failed to read blob bytes.'));
      };
      reader.onerror = () => reject(reader.error ?? new Error('Failed to read blob bytes.'));
      reader.readAsArrayBuffer(blob);
    });
  }

  if (typeof Response !== 'undefined') {
    const response = new Response(blob);
    return new Uint8Array(await response.arrayBuffer());
  }

  const text = await blob.text();
  return new TextEncoder().encode(text);
}

/**
 * Returns decoded text if blob looks like text; otherwise null.
 * Designed for "unknown/unsupported preview handler" fallback.
 */
export async function sniffBlobAsText(blob: Blob, sampleSize: number = DEFAULT_SAMPLE_SIZE): Promise<string | null> {
  if (!blob || blob.size <= 0) {
    return null;
  }

  const probeSize = Math.min(sampleSize, blob.size);
  const sampleBytes = await readBlobAsBytes(blob.slice(0, probeSize));
  const bom = detectBom(sampleBytes);

  if (!bom && hasNullByte(sampleBytes)) {
    return null;
  }

  const sampleText = bom
    ? decodeWithBom(sampleBytes, bom)
    : decodeBytes(sampleBytes, 'utf-8-strict');

  if (sampleText === null || !isMostlyText(sampleText)) {
    return null;
  }

  const fullBytes = await readBlobAsBytes(blob);
  const fullText = bom
    ? decodeWithBom(fullBytes, bom)
    : decodeBytes(fullBytes, 'utf-8-strict');

  if (fullText === null || !isMostlyText(fullText)) {
    return null;
  }

  return fullText;
}
