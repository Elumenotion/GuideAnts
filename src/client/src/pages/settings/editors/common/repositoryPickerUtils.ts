/**
 * Format a byte count into a human-readable string (e.g. "1.2 GB").
 * Returns "unknown size" when the input is null/undefined/non-positive —
 * the shared picker uses this for each file row so operators can see
 * download sizes at a glance.
 */
export function formatBytes(size: number | null | undefined): string {
  if (size === null || size === undefined || !Number.isFinite(size) || size <= 0) {
    return 'unknown size';
  }
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = size;
  let unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex += 1;
  }
  return `${value.toFixed(value >= 100 || unitIndex === 0 ? 0 : value >= 10 ? 1 : 2)} ${units[unitIndex]}`;
}

/**
 * Normalize a user-entered Hugging Face repository reference. Accepts
 * either the canonical <c>owner/repo</c> form or a full Hugging Face model
 * URL (<c>https://huggingface.co/owner/repo</c> with optional trailing
 * path segments like <c>/tree/main</c>). Returns the canonical
 * <c>owner/repo</c> form on success and <c>null</c> when the input cannot
 * be parsed — the picker surfaces a client-side <c>REPO_INVALID</c> in
 * that case instead of round-tripping to the server.
 */
export function normalizeHuggingFaceRepository(input: string): string | null {
  const raw = (input ?? '').trim();
  if (!raw) {
    return null;
  }
  let slug = raw;
  const urlMatch = raw.match(/^https?:\/\/(?:www\.)?huggingface\.co\/(.+)$/i);
  if (urlMatch) {
    slug = urlMatch[1];
  }
  slug = slug.replace(/^\/+|\/+$/g, '');
  const parts = slug.split('/').filter((part) => part.length > 0);
  if (parts.length < 2) {
    return null;
  }
  const owner = parts[0];
  const repo = parts[1];
  if (!owner || !repo) {
    return null;
  }
  return `${owner}/${repo}`;
}
