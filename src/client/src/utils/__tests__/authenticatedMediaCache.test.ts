import { describe, it, expect, beforeEach } from 'vitest';
import {
  getMediaCacheKey,
  getCachedMediaUrl,
  peekCachedMediaUrl,
  setCachedMediaUrl,
  invalidateNotebookMediaCache,
  clearAuthenticatedMediaCache,
} from '../authenticatedMediaCache';

const URL_NB1 = '/api/projects/proj-1/notebooks/nb-1/files/content?path=sine-wave.png';
const URL_NB2 = '/api/projects/proj-1/notebooks/nb-2/files/content?path=sine-wave.png';
const URL_OTHER_PROJECT = '/api/projects/proj-9/notebooks/nb-1/files/content?path=sine-wave.png';
const URL_EXTERNAL = 'https://example.com/media.png';

describe('authenticatedMediaCache', () => {
  beforeEach(() => {
    clearAuthenticatedMediaCache();
  });

  it('round-trips blob URLs keyed by URL', () => {
    expect(getCachedMediaUrl(URL_NB1)).toBeNull();
    setCachedMediaUrl(URL_NB1, 'blob:old');
    expect(getCachedMediaUrl(URL_NB1)).toBe('blob:old');
  });

  it('keeps the old blob retrievable after a revision bump (old cell keeps its version)', () => {
    setCachedMediaUrl(URL_NB1, 'blob:old');
    const oldKey = getMediaCacheKey(URL_NB1); // revision 0: bare URL

    invalidateNotebookMediaCache('proj-1', 'nb-1', ['sine-wave.png']);

    // Fresh fetches use a new cache key and miss (they hit the network for new bytes)...
    expect(getMediaCacheKey(URL_NB1)).not.toBe(oldKey);
    expect(getMediaCacheKey(URL_NB1)).toBe(`${URL_NB1}&_rev=1`);
    expect(getCachedMediaUrl(URL_NB1)).toBeNull();
    // ...while the old blob URL remains retrievable under its old key, so a previous
    // cell that already rendered keeps displaying the version it loaded.
    expect(peekCachedMediaUrl(oldKey)).toBe('blob:old');

    // And the new fetch stores under the new key.
    setCachedMediaUrl(URL_NB1, 'blob:new');
    expect(getCachedMediaUrl(URL_NB1)).toBe('blob:new');
  });

  it('isolates revisions per project:notebook scope', () => {
    setCachedMediaUrl(URL_NB1, 'blob:old');
    invalidateNotebookMediaCache('proj-1', 'nb-1', ['x.png']);
    // Same file in a different notebook is unaffected (still rev 0).
    expect(getMediaCacheKey(URL_NB2)).toBe(URL_NB2);
    setCachedMediaUrl(URL_NB2, 'blob:nb2');
    expect(getCachedMediaUrl(URL_NB2)).toBe('blob:nb2');
    // Same notebook in a different project is unaffected.
    expect(getMediaCacheKey(URL_OTHER_PROJECT)).toBe(URL_OTHER_PROJECT);
  });

  it('bumps again on a second change in the same session', () => {
    invalidateNotebookMediaCache('proj-1', 'nb-1', ['a.png']);
    expect(getMediaCacheKey(URL_NB1)).toBe(`${URL_NB1}&_rev=1`);
    invalidateNotebookMediaCache('proj-1', 'nb-1', ['b.png']);
    expect(getMediaCacheKey(URL_NB1)).toBe(`${URL_NB1}&_rev=2`);
  });

  it('does not bump for empty/missing paths or missing ids', () => {
    invalidateNotebookMediaCache('proj-1', 'nb-1', []);
    invalidateNotebookMediaCache('proj-1', 'nb-1', undefined);
    invalidateNotebookMediaCache(undefined, 'nb-1', ['a.png']);
    invalidateNotebookMediaCache('proj-1', undefined, ['a.png']);
    expect(getMediaCacheKey(URL_NB1)).toBe(URL_NB1);
  });

  it('accepts CWD-relative paths (the format the server stores) for the bump decision', () => {
    // The server stores CWD-relative paths ("sine-wave.png", "Output/foo.wav");
    // they are never matched against URL keys -- any non-empty list bumps the scope.
    invalidateNotebookMediaCache('proj-1', 'nb-1', ['sine-wave.png']);
    expect(getMediaCacheKey(URL_NB1)).toBe(`${URL_NB1}&_rev=1`);
  });

  it('leaves non-notebook URLs unversioned', () => {
    setCachedMediaUrl(URL_EXTERNAL, 'blob:ext');
    invalidateNotebookMediaCache('proj-1', 'nb-1', ['a.png']);
    expect(getCachedMediaUrl(URL_EXTERNAL)).toBe('blob:ext');
    expect(getMediaCacheKey(URL_EXTERNAL)).toBe(URL_EXTERNAL);
  });
});
