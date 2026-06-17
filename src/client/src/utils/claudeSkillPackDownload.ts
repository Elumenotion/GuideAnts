import JSZip from 'jszip';

const PLACEHOLDER_KEY = 'gak_REPLACE_ME';

function toZipBlob(data: ArrayBuffer | Uint8Array): Blob {
  const bytes = data instanceof ArrayBuffer ? new Uint8Array(data) : data;
  return new Blob([bytes], { type: 'application/zip' });
}

/**
 * Patches `.env` inside a zip buffer. Exported for unit tests (avoids Blob round-trip in jsdom).
 */
export async function patchEnvInZipBuffer(
  zipBuffer: ArrayBuffer,
  apiKey: string
): Promise<ArrayBuffer> {
  const zip = await JSZip.loadAsync(zipBuffer);
  const envPath = Object.keys(zip.files).find(
    (path) => path.endsWith('/.env') && !path.endsWith('.env.example') && !zip.files[path].dir
  );

  if (!envPath) {
    return zipBuffer;
  }

  const entry = zip.file(envPath);
  if (!entry) {
    return zipBuffer;
  }

  const content = await entry.async('string');
  const patched = content.replace(
    /^GUIDEANTS_API_KEY=.*$/m,
    `GUIDEANTS_API_KEY=${apiKey}`
  );
  zip.file(envPath, patched);

  return zip.generateAsync({ type: 'arraybuffer' });
}

/**
 * Patches the skill pack `.env` inside a downloaded zip when the plaintext API key
 * is still available in the publish UI session.
 */
export async function patchClaudeSkillPackEnv(
  zipInput: Blob | ArrayBuffer,
  apiKey: string | null | undefined
): Promise<Blob> {
  if (!apiKey || apiKey === PLACEHOLDER_KEY) {
    return zipInput instanceof Blob
      ? zipInput
      : toZipBlob(zipInput);
  }

  const buffer =
    zipInput instanceof ArrayBuffer ? zipInput : await new Response(zipInput).arrayBuffer();
  const patched = await patchEnvInZipBuffer(buffer, apiKey);
  return toZipBlob(patched);
}

export function sanitizeClaudeSkillDownloadFileName(name: string): string {
  const sanitized = name.replace(/[^a-zA-Z0-9._-]+/g, '-').replace(/^-+|-+$/g, '');
  return sanitized || 'guide';
}

export function triggerBlobDownload(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
  URL.revokeObjectURL(url);
}
