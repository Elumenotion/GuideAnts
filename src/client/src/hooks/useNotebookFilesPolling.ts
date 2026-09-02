import { useEffect, useRef, useCallback, useSyncExternalStore } from 'react';
import { api } from '../services/api';
import { NotebookFolderTreeDto } from '../types/notebook';

interface UseNotebookFilesPollingProps {
  projectId: string;
  notebookId: string;
  enabled?: boolean;
  pollInterval?: number; // milliseconds, default 10s
}

interface NotebookFilesPollingSnapshot {
  folderTree: NotebookFolderTreeDto | null;
  isLoading: boolean;
  error: string | null;
  lastUpdated: Date | null;
  version: number;
}

interface SharedNotebookFilesPollingState {
  snapshot: NotebookFilesPollingSnapshot;
  subscribers: Set<() => void>;
  refCount: number;
  pollInterval: number;
  enabled: boolean;
  intervalId: ReturnType<typeof setInterval> | null;
  abortController: AbortController | null;
  inFlight: boolean;
}

const EMPTY_SNAPSHOT: NotebookFilesPollingSnapshot = {
  folderTree: null,
  isLoading: false,
  error: null,
  lastUpdated: null,
  version: 0,
};

const sharedPollers = new Map<string, SharedNotebookFilesPollingState>();

function buildPollerKey(projectId: string, notebookId: string): string {
  return `${projectId}:${notebookId}`;
}

function publishSnapshot(
  state: SharedNotebookFilesPollingState,
  patch: Partial<Omit<NotebookFilesPollingSnapshot, 'version'>>
): void {
  state.snapshot = {
    ...state.snapshot,
    ...patch,
    version: state.snapshot.version + 1,
  };

  for (const subscriber of state.subscribers) {
    subscriber();
  }
}

/**
 * Deep comparison of two folder trees to determine if they're structurally identical.
 * Returns true if the trees are the same (no update needed).
 */
function areTreesEqual(a: NotebookFolderTreeDto | null, b: NotebookFolderTreeDto | null): boolean {
  if (a === b) return true;
  if (!a || !b) return false;

  if (a.name !== b.name || a.relativePath !== b.relativePath) return false;

  if (a.files.length !== b.files.length) return false;
  for (let i = 0; i < a.files.length; i++) {
    const fileA = a.files[i];
    const fileB = b.files[i];
    if (
      fileA.id !== fileB.id ||
      fileA.fileName !== fileB.fileName ||
      fileA.relativePath !== fileB.relativePath ||
      fileA.fileSize !== fileB.fileSize ||
      fileA.fileHash !== fileB.fileHash
    ) {
      return false;
    }
  }

  if (a.subFolders.length !== b.subFolders.length) return false;
  for (let i = 0; i < a.subFolders.length; i++) {
    if (!areTreesEqual(a.subFolders[i], b.subFolders[i])) {
      return false;
    }
  }

  return true;
}

function getOrCreateSharedState(
  key: string,
  pollInterval: number,
  enabled: boolean
): SharedNotebookFilesPollingState {
  let state = sharedPollers.get(key);
  if (!state) {
    state = {
      snapshot: { ...EMPTY_SNAPSHOT },
      subscribers: new Set(),
      refCount: 0,
      pollInterval,
      enabled,
      intervalId: null,
      abortController: null,
      inFlight: false,
    };
    sharedPollers.set(key, state);
  }

  state.pollInterval = pollInterval;
  state.enabled = enabled;
  return state;
}

async function fetchSharedNotebookFiles(
  key: string,
  projectId: string,
  notebookId: string,
  isInitialLoad: boolean
): Promise<void> {
  const state = sharedPollers.get(key);
  if (!state || !state.enabled || !projectId || !notebookId) {
    return;
  }

  if (state.inFlight) {
    return;
  }

  state.inFlight = true;
  if (state.abortController) {
    state.abortController.abort();
  }

  const controller = new AbortController();
  state.abortController = controller;

  if (isInitialLoad) {
    publishSnapshot(state, { isLoading: true, error: null });
  } else {
    publishSnapshot(state, { error: null });
  }

  try {
    if (controller.signal.aborted) {
      return;
    }

    const tree = await api.projects.notebooks.getNotebookFolderTree(projectId, notebookId);

    if (!controller.signal.aborted) {
      const nextTree = areTreesEqual(state.snapshot.folderTree, tree)
        ? state.snapshot.folderTree
        : tree;
      publishSnapshot(state, {
        folderTree: nextTree,
        lastUpdated: new Date(),
        isLoading: false,
        error: null,
      });
    }
  } catch (err) {
    if (!controller.signal.aborted) {
      const errorMessage = err instanceof Error ? err.message : 'Failed to fetch notebook files';
      publishSnapshot(state, {
        error: errorMessage,
        isLoading: false,
      });
    }
  } finally {
    if (state.abortController === controller) {
      state.abortController = null;
    }

    state.inFlight = false;
  }
}

function startSharedPolling(key: string, projectId: string, notebookId: string): void {
  const state = sharedPollers.get(key);
  if (!state || !state.enabled || !projectId || !notebookId) {
    return;
  }

  if (state.intervalId) {
    return;
  }

  void fetchSharedNotebookFiles(key, projectId, notebookId, true);

  state.intervalId = setInterval(() => {
    void fetchSharedNotebookFiles(key, projectId, notebookId, false);
  }, state.pollInterval);
}

function stopSharedPolling(key: string): void {
  const state = sharedPollers.get(key);
  if (!state) {
    return;
  }

  if (state.intervalId) {
    clearInterval(state.intervalId);
    state.intervalId = null;
  }

  if (state.abortController) {
    state.abortController.abort();
    state.abortController = null;
  }

  state.inFlight = false;
}

function acquireSharedPoller(
  key: string,
  projectId: string,
  notebookId: string,
  pollInterval: number,
  enabled: boolean
): SharedNotebookFilesPollingState {
  const state = getOrCreateSharedState(key, pollInterval, enabled);
  state.refCount += 1;

  if (enabled && projectId && notebookId) {
    startSharedPolling(key, projectId, notebookId);
  } else {
    stopSharedPolling(key);
  }

  return state;
}

function releaseSharedPoller(key: string): void {
  const state = sharedPollers.get(key);
  if (!state) {
    return;
  }

  state.refCount = Math.max(0, state.refCount - 1);
  if (state.refCount === 0) {
    stopSharedPolling(key);
    sharedPollers.delete(key);
  }
}

function refreshSharedPoller(key: string, projectId: string, notebookId: string): void {
  void fetchSharedNotebookFiles(key, projectId, notebookId, true);
}

export function useNotebookFilesPolling({
  projectId,
  notebookId,
  enabled = true,
  pollInterval = 10000,
}: UseNotebookFilesPollingProps) {
  const key = buildPollerKey(projectId, notebookId);
  const keyRef = useRef(key);
  keyRef.current = key;

  const subscribe = useCallback(
    (onStoreChange: () => void) => {
      const state = acquireSharedPoller(keyRef.current, projectId, notebookId, pollInterval, enabled);
      state.subscribers.add(onStoreChange);
      return () => {
        state.subscribers.delete(onStoreChange);
        releaseSharedPoller(keyRef.current);
      };
    },
    [projectId, notebookId, pollInterval, enabled]
  );

  const getSnapshot = useCallback(
    () => sharedPollers.get(keyRef.current)?.snapshot ?? EMPTY_SNAPSHOT,
    []
  );

  const snapshot = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);

  useEffect(() => {
    const state = sharedPollers.get(key);
    if (!state) {
      return;
    }

    state.pollInterval = pollInterval;
    state.enabled = enabled;

    if (enabled && projectId && notebookId) {
      if (!state.intervalId) {
        startSharedPolling(key, projectId, notebookId);
      }
    } else {
      stopSharedPolling(key);
    }
  }, [key, projectId, notebookId, pollInterval, enabled]);

  const refresh = useCallback(() => {
    refreshSharedPoller(keyRef.current, projectId, notebookId);
  }, [projectId, notebookId]);

  return {
    folderTree: snapshot.folderTree,
    isLoading: snapshot.isLoading,
    error: snapshot.error,
    lastUpdated: snapshot.lastUpdated,
    refresh,
  };
}
