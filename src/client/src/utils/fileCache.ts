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

// Some environments (Vitest/JSDOM) don't expose indexedDB.  Fail gracefully.
let hasIndexedDB = typeof indexedDB !== 'undefined';

let dbPromise: Promise<IDBPDatabase | null> | null = null;

function getDb() {
  if (!hasIndexedDB) {
    // eslint-disable-next-line @typescript-eslint/ban-ts-comment
    // @ts-ignore – return dummy promise for non-browser envs
    return Promise.resolve(null);
  }

  if (!dbPromise) {
    dbPromise = openDB(DB_NAME, DB_VERSION, {
      upgrade(db: IDBPDatabase) {
        if (!db.objectStoreNames.contains(STORE_NAME)) {
          db.createObjectStore(STORE_NAME);
        }
      }
    })
    // New: gracefully handle environments where IndexedDB cannot be opened
    .catch(err => {
      console.warn('IndexedDB unavailable – disabling file cache', err);
      hasIndexedDB = false;
      return null; // callers will fall back to network
    });
  }
  return dbPromise;
}

function buildKey(projectId: string, fileId: string) {
  return `${projectId}:${fileId}`;
}

export async function cacheFile(
  projectId: string,
  fileId: string,
  data: Omit<CachedFile, 'updatedAt'>
) {
  if (!hasIndexedDB) return;
  const db = await getDb();
  if (!db) return;
  const record: CachedFile = { ...data, updatedAt: Date.now() };
  await db.put(STORE_NAME, record, buildKey(projectId, fileId));
}

export async function getCachedFile(
  projectId: string,
  fileId: string
): Promise<CachedFile | null> {
  if (!hasIndexedDB) return null;
  const db = await getDb();
  if (!db) return null;
  return (await db.get(STORE_NAME, buildKey(projectId, fileId))) as CachedFile | undefined || null;
}

export async function deleteCachedFile(projectId: string, fileId: string) {
  if (!hasIndexedDB) return;
  const db = await getDb();
  if (!db) return;
  await db.delete(STORE_NAME, buildKey(projectId, fileId));
} 