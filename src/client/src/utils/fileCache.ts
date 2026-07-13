import { openDB, IDBPDatabase } from 'idb';

interface CachedFile {
  blob: Blob;
  contentType: string;
  fileName: string;
  updatedAt: number;
  /** SHA-256 file hash so we can verify freshness */
  fileHash?: string; // optional for backward-compat
}

const DB_NAME = 'guideants-file-cache';
const STORE_NAME = 'files';
const DB_VERSION = 1;
const DB_OPEN_TIMEOUT_MS = 5000;
const DB_OP_TIMEOUT_MS = 3000;

// Some environments (Vitest/JSDOM) don't expose indexedDB.  Fail gracefully.
let hasIndexedDB = typeof indexedDB !== 'undefined';

let dbPromise: Promise<IDBPDatabase | null> | null = null;

function disableFileCache(reason: string, err?: unknown) {
  console.warn(reason, err);
  hasIndexedDB = false;
  dbPromise = null;
}

async function withTimeout<T>(
  promise: Promise<T>,
  timeoutMs: number,
  timeoutMessage: string
): Promise<T> {
  let timeoutId: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      promise,
      new Promise<T>((_, reject) => {
        timeoutId = setTimeout(() => reject(new Error(timeoutMessage)), timeoutMs);
      }),
    ]);
  } finally {
    if (timeoutId !== undefined) {
      clearTimeout(timeoutId);
    }
  }
}

function getDb() {
  if (!hasIndexedDB) {
    // eslint-disable-next-line @typescript-eslint/ban-ts-comment
    // @ts-ignore – return dummy promise for non-browser envs
    return Promise.resolve(null);
  }

  if (!dbPromise) {
    dbPromise = withTimeout(
      openDB(DB_NAME, DB_VERSION, {
        upgrade(db: IDBPDatabase) {
          if (!db.objectStoreNames.contains(STORE_NAME)) {
            db.createObjectStore(STORE_NAME);
          }
        }
      }),
      DB_OPEN_TIMEOUT_MS,
      `IndexedDB open timed out after ${DB_OPEN_TIMEOUT_MS}ms`
    )
      .catch((err) => {
        disableFileCache('IndexedDB unavailable – disabling file cache', err);
        return null;
      });
  }
  return dbPromise;
}

async function runDbOperation<T>(operation: (db: IDBPDatabase) => Promise<T>): Promise<T | null> {
  if (!hasIndexedDB) return null;
  const db = await getDb();
  if (!db) return null;

  try {
    return await withTimeout(
      operation(db),
      DB_OP_TIMEOUT_MS,
      `IndexedDB operation timed out after ${DB_OP_TIMEOUT_MS}ms`
    );
  } catch (err) {
    disableFileCache('IndexedDB operation failed – disabling file cache', err);
    return null;
  }
}

function buildKey(projectId: string, fileId: string) {
  return `${projectId}:${fileId}`;
}

export async function cacheFile(
  projectId: string,
  fileId: string,
  data: Omit<CachedFile, 'updatedAt'>
) {
  const record: CachedFile = { ...data, updatedAt: Date.now() };
  await runDbOperation((db) => db.put(STORE_NAME, record, buildKey(projectId, fileId)));
}

export async function getCachedFile(
  projectId: string,
  fileId: string
): Promise<CachedFile | null> {
  const cached = await runDbOperation((db) =>
    db.get(STORE_NAME, buildKey(projectId, fileId))
  );
  return (cached as CachedFile | undefined) ?? null;
}

export async function deleteCachedFile(projectId: string, fileId: string) {
  await runDbOperation((db) => db.delete(STORE_NAME, buildKey(projectId, fileId)));
} 