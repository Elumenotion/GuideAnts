import { useState, useEffect, useRef, useCallback } from 'react';
import { api } from '../services/api';
import { NotebookFolderTreeDto } from '../types/notebook';

interface UseNotebookFilesPollingProps {
  projectId: string;
  notebookId: string;
  enabled?: boolean;
  pollInterval?: number; // milliseconds, default 10s
}

/**
 * Deep comparison of two folder trees to determine if they're structurally identical.
 * Returns true if the trees are the same (no update needed).
 */
function areTreesEqual(a: NotebookFolderTreeDto | null, b: NotebookFolderTreeDto | null): boolean {
  if (a === b) return true;
  if (!a || !b) return false;
  
  // Compare basic properties
  if (a.name !== b.name || a.relativePath !== b.relativePath) return false;
  
  // Compare files
  if (a.files.length !== b.files.length) return false;
  for (let i = 0; i < a.files.length; i++) {
    const fileA = a.files[i];
    const fileB = b.files[i];
    if (fileA.id !== fileB.id || 
        fileA.fileName !== fileB.fileName || 
        fileA.relativePath !== fileB.relativePath ||
        fileA.fileSize !== fileB.fileSize ||
        fileA.fileHash !== fileB.fileHash) {
      return false;
    }
  }
  
  // Compare subfolders recursively
  if (a.subFolders.length !== b.subFolders.length) return false;
  for (let i = 0; i < a.subFolders.length; i++) {
    if (!areTreesEqual(a.subFolders[i], b.subFolders[i])) {
      return false;
    }
  }
  
  return true;
}

export function useNotebookFilesPolling({
  projectId,
  notebookId,
  enabled = true,
  pollInterval = 10000,
}: UseNotebookFilesPollingProps) {
  const [folderTree, setFolderTree] = useState<NotebookFolderTreeDto | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);

  const intervalRef = useRef<NodeJS.Timeout | null>(null);
  const abortControllerRef = useRef<AbortController | null>(null);

  const fetchNotebookFiles = useCallback(
    async (signal?: AbortSignal, isInitialLoad = false) => {
      try {
        // Only show loading state for initial load, not polling updates
        // This prevents UI disruption during background refreshes
        if (isInitialLoad) {
          setIsLoading(true);
        }
        setError(null);
        
        if (signal?.aborted) {
          return;
        }

        const tree = await api.projects.notebooks.getNotebookFolderTree(projectId, notebookId);

        if (!signal?.aborted) {
          // Only update state if the tree has actually changed
          // This prevents unnecessary re-renders that interrupt user interactions
          setFolderTree(prev => {
            if (areTreesEqual(prev, tree)) {
              // No change - keep the same reference to prevent re-render
              return prev;
            }
            // Tree changed - update with new data
            return tree;
          });
          setLastUpdated(new Date());
        }
      } catch (err) {
        if (!signal?.aborted) {
          const errorMessage = err instanceof Error ? err.message : 'Failed to fetch notebook files';
          setError(errorMessage);
        }
      } finally {
        if (!signal?.aborted && isInitialLoad) {
          setIsLoading(false);
        }
      }
    },
    [projectId, notebookId]
  );

  useEffect(() => {
    if (!enabled || !projectId || !notebookId) {
      //console.log('📄 Notebook files polling disabled or missing IDs');
      return;
    }

    //console.log(`📄 Starting notebook files polling for notebook ${notebookId}`);

    const controller = new AbortController();
    abortControllerRef.current = controller;
    fetchNotebookFiles(controller.signal, true); // Initial load

    intervalRef.current = setInterval(() => {
      const newController = new AbortController();
      abortControllerRef.current = newController;
      fetchNotebookFiles(newController.signal, false); // Polling update
    }, pollInterval);

    return () => {
      if (intervalRef.current) {
        clearInterval(intervalRef.current);
        intervalRef.current = null;
      }
      if (abortControllerRef.current) {
        abortControllerRef.current.abort();
        abortControllerRef.current = null;
      }
    };
  }, [enabled, projectId, notebookId, pollInterval, fetchNotebookFiles]);

  const refresh = useCallback(() => {
    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
    }
    const controller = new AbortController();
    abortControllerRef.current = controller;
    fetchNotebookFiles(controller.signal, true); // Manual refresh shows loading
  }, [fetchNotebookFiles]);

  return {
    folderTree,
    isLoading,
    error,
    lastUpdated,
    refresh,
  };
}

