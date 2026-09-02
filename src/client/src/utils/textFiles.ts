/**
 * Plain .txt files — not generic text/* (json, code, etc.).
 */
export function isPlainTextFile(fileName: string, contentType?: string | null): boolean {
  const ct = (contentType ?? '').toLowerCase();
  if (ct === 'text/plain') {
    return true;
  }
  const ext = fileName.split('.').pop()?.toLowerCase();
  return ext === 'txt';
}
