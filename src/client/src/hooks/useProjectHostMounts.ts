import { useCallback, useEffect, useState } from 'react';
import { hostFolderMountsApi } from '../services/hostFolderMounts';
import type { ProjectHostMountEntry } from '../types/hostFolderMount';
import { buildProjectHostMountEntry } from '../utils/hostMountDisplayState';

interface UseProjectHostMountsResult {
  mounts: ProjectHostMountEntry[];
  isLoading: boolean;
  error: string | null;
  refresh: () => Promise<void>;
}

export function useProjectHostMounts(
  projectId: string | undefined,
  isAdmin: boolean,
): UseProjectHostMountsResult {
  const [mounts, setMounts] = useState<ProjectHostMountEntry[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    if (!projectId || !isAdmin) {
      setMounts([]);
      setError(null);
      return;
    }

    setIsLoading(true);
    setError(null);
    try {
      const summaries = await hostFolderMountsApi.list(projectId);
      const projectSummaries = summaries.filter(
        (summary) => summary.scope === 'Project' && summary.status !== 'Removed',
      );
      const details = await Promise.all(
        projectSummaries.map((summary) => hostFolderMountsApi.getDetail(projectId, summary.mountId)),
      );

      const entries = projectSummaries
        .map((summary, index) => buildProjectHostMountEntry(summary, details[index]))
        .filter((entry): entry is ProjectHostMountEntry => entry != null);

      setMounts(entries);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Failed to load project host folder mounts';
      setError(message);
      setMounts([]);
    } finally {
      setIsLoading(false);
    }
  }, [projectId, isAdmin]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  useEffect(() => {
    const handleRefresh = () => {
      void refresh();
    };
    window.addEventListener('refresh-project-mounts', handleRefresh);
    return () => window.removeEventListener('refresh-project-mounts', handleRefresh);
  }, [refresh]);

  return { mounts, isLoading, error, refresh };
}
