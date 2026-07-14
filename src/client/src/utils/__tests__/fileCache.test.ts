import { describe, it, expect, vi, afterEach } from 'vitest';

// Helper to dynamically import fresh module after env changes
const importFresh = async () => {
  vi.resetModules();
  return await import('../fileCache');
};

describe('fileCache utility', () => {
  afterEach(() => {
    // restore global indexedDB
    // @ts-ignore
    delete global.indexedDB;
  });

  it('gracefully no-ops when indexedDB is unavailable', async () => {
    // Ensure indexedDB undefined
    // @ts-ignore
    global.indexedDB = undefined;
    const { cacheFile, getCachedFile, deleteCachedFile } = await importFresh();
    await expect(getCachedFile('p', 'f')).resolves.toBeNull();
    await expect(cacheFile('p', 'f', { blob: new Blob(), contentType: 'text/plain', fileName: 'a' })).resolves.toBeUndefined();
    await expect(deleteCachedFile('p', 'f')).resolves.toBeUndefined();
  });

  it('stores, retrieves, and deletes when indexedDB is present', async () => {
    const put = vi.fn();
    const get = vi.fn().mockResolvedValue(undefined);
    const del = vi.fn();
    const createObjectStore = vi.fn();

    const dbMock = {
      put,
      get,
      delete: del,
      objectStoreNames: { contains: () => false },
      createObjectStore,
    } as any;

    // Mock idb.openDB at runtime (non-hoisted) before importing module under test
    const openDBMock = vi.fn().mockImplementation((_name, _version, options) => {
      options?.upgrade?.(dbMock);
      return Promise.resolve(dbMock);
    });

    vi.doMock('idb', () => ({ openDB: openDBMock }));

    // Provide dummy indexedDB to let fileCache detect availability
    // @ts-ignore
    global.indexedDB = {};

    const { cacheFile, getCachedFile, deleteCachedFile } = await importFresh();

    await cacheFile('p', 'f', { blob: new Blob(), contentType: 'text/plain', fileName: 'x' });
    expect(openDBMock).toHaveBeenCalled();
    expect(put).toHaveBeenCalled();

    await getCachedFile('p', 'f');
    expect(get).toHaveBeenCalled();

    await deleteCachedFile('p', 'f');
    expect(del).toHaveBeenCalled();
  });

  it('disables cache when indexedDB open fails', async () => {
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const openDBMock = vi.fn().mockRejectedValue(new Error('idb blocked'));

    vi.doMock('idb', () => ({ openDB: openDBMock }));

    // @ts-ignore
    global.indexedDB = {};

    const { cacheFile, getCachedFile } = await importFresh();

    await cacheFile('p', 'f', { blob: new Blob(), contentType: 'text/plain', fileName: 'x' });
    await expect(getCachedFile('p', 'f')).resolves.toBeNull();
    expect(warnSpy).toHaveBeenCalled();
    warnSpy.mockRestore();
  });

  it('disables cache when indexedDB open hangs', async () => {
    vi.useFakeTimers();
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const openDBMock = vi.fn().mockImplementation(() => new Promise(() => {}));

    vi.doMock('idb', () => ({ openDB: openDBMock }));

    // @ts-ignore
    global.indexedDB = {};

    const { getCachedFile } = await importFresh();
    const pending = getCachedFile('p', 'f');
    await vi.advanceTimersByTimeAsync(5000);
    await expect(pending).resolves.toBeNull();
    expect(warnSpy).toHaveBeenCalled();
    warnSpy.mockRestore();
    vi.useRealTimers();
  });
}); 