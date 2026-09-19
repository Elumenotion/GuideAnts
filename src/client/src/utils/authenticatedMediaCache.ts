// Shared session cache for authenticated notebook-media blob URLs.
//
// WHY THIS EXISTS (regression fix): chat cells render notebook files (images/audio/video)
// through `api.utils.getAuthenticatedUrl`, which returns `URL.createObjectURL(blob)`.
// Four components each kept a private, URL-keyed module-level cache. When the LLM re-emits
// a file (same path, new bytes), the URL does not change, so every cell -- including the
// previous one on re-render -- kept resolving to the FIRST blob fetched this session.
// The only way to see the new content was a hard refresh.
//
// FIX: a single shared cache keyed by URL + per-notebook REVISION. When a turn reports
// file changes (SSE `complete` payload, or the server-persisted turnFilesCreated/Modified
// props), the notebook's revision is bumped:
//   - cells that already rendered keep pointing at their existing blob URL, which is
//     NEVER revoked, so the previous version keeps displaying until a hard refresh;
//   - cells that (re)fetch after the bump use a new cache key, so they fetch fresh bytes.
// No hard refresh is ever required to see updated media.
//
// Old entries are left in the map on purpose: revoking their blob URLs would break the
// previous cells that are still displaying them. The blobs are garbage-collected when the
// page unloads.

const NOTEBOOK_KEY = /\/projects\/([^/]+)\/notebooks\/([^/]+)\/files\/content/;

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const blobCache = new Map<string, string>(); // cacheKey -> objectUrl
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const revisions = new Map<string, number>(); // `${projectId}:${notebookId}` -> revision

export function getMediaCacheKey(url: string): string {
    const match = NOTEBOOK_KEY.exec(url);
    if (!match) {
        return url;
    }
    const scope = `${match[1]}:${match[2]}`;
    const rev = revisions.get(scope) ?? 0;
    return rev === 0 ? url : `${url}&_rev=${rev}`;
}

export function getCachedMediaUrl(url: string): string | null {
    return blobCache.get(getMediaCacheKey(url)) ?? null;
}

/** Raw lookup by cache key (bypasses revisioning). Exposed for tests. */
export function peekCachedMediaUrl(rawKey: string): string | null {
    return blobCache.get(rawKey) ?? null;
}

export function setCachedMediaUrl(url: string, objectUrl: string): void {
    blobCache.set(getMediaCacheKey(url), objectUrl);
}

/**
 * Bump the media revision for a notebook: every subsequent fetch of a
 * `/projects/{projectId}/notebooks/{notebookId}/files/content` URL uses a fresh cache
 * key (fresh network fetch), while existing <img>/<audio>/<video> elements keep their
 * unrevoked blob URLs and continue displaying the version they already loaded.
 *
 * Accepts any mix of path formats (CWD-relative, notebook-relative, absolute) -- the
 * paths are only used to decide whether anything changed, never to match cache keys.
 */
export function invalidateNotebookMediaCache(
    projectId: string | undefined,
    notebookId: string | undefined,
    changedPaths: readonly string[] | null | undefined
): void {
    if (!projectId || !notebookId) return;
    if (!changedPaths || changedPaths.length === 0) return;
    const scope = `${projectId}:${notebookId}`;
    revisions.set(scope, (revisions.get(scope) ?? 0) + 1);
}

/** Reset all state (tests). */
export function clearAuthenticatedMediaCache(): void {
    blobCache.clear();
    revisions.clear();
}
