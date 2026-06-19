import { useCallback, useEffect, useState } from 'react';
import { hostFolderMountsApi } from '../services/hostFolderMounts';
import type { NotebookHostMountEntry } from '../types/hostFolderMount';
import { buildNotebookHostMountEntry } from '../utils/hostMountDisplayState';

interface UseNotebookHostMountsResult {
  mounts: NotebookHostMountEntry[];
  isLoading: boolean;
  error: string | null;
  refresh: () => Promise<void>;
}

export function useNotebookHostMounts(
  projectId: string | undefined,
  notebookId: string | undefined,
  isAdmin: boolean,
): UseNotebookHostMountsResult {
  const [mounts, setMounts] = useState<NotebookHostMountEntry[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    if (!projectId || !notebookId || !isAdmin) {
      setMounts([]);
      setError(null);
      return;
    }

    setIsLoading(true);
    setError(null);
    try {
      const summaries = await hostFolderMountsApi.list(projectId);
      const activeSummaries = summaries.filter((summary) => summary.status !== 'Removed');
      const details = await Promise.all(
        activeSummaries.map((summary) => hostFolderMountsApi.getDetail(projectId, summary.mountId)),
      );

      const entries = activeSummaries
        .map((summary, index) => buildNotebookHostMountEntry(summary, details[index], notebookId))
        .filter((entry): entry is NotebookHostMountEntry => entry != null);

      setMounts(entries);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to load host folder mounts';
      setError(message);
      setMounts([]);
    } finally {
      setIsLoading(false);
    }
  }, [projectId, notebookId, isAdmin]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  useEffect(() => {
    const handleRefresh = () => {
      void refresh();
    };
    window.addEventListener('refresh-notebook-files', handleRefresh);
    return () => window.removeEventListener('refresh-notebook-files', handleRefresh);
  }, [refresh]);

  return { mounts, isLoading, error, refresh };
}
