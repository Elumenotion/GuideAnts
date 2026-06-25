import { useState, useEffect, useRef, useCallback } from 'react';
import { scheduledJobsApi } from '../services/scheduledJobs';
import type { ProjectScheduledJobSummaryDto } from '../types/scheduledJob';

interface UseProjectScheduledJobsPollingProps {
  projectId: string;
  enabled?: boolean;
  pollInterval?: number;
}

export function useProjectScheduledJobsPolling({
  projectId,
  enabled = true,
  pollInterval = 15000,
}: UseProjectScheduledJobsPollingProps) {
  const [jobs, setJobs] = useState<ProjectScheduledJobSummaryDto[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);

  const intervalRef = useRef<NodeJS.Timeout | null>(null);
  const abortControllerRef = useRef<AbortController | null>(null);

  const fetchJobs = useCallback(async (signal?: AbortSignal) => {
    try {
      setIsLoading(true);
      setError(null);

      if (signal?.aborted) {
        return;
      }

      const result = await scheduledJobsApi.list(projectId);

      if (!signal?.aborted) {
        setJobs(result);
        setLastUpdated(new Date());
      }
    } catch (err) {
      if (!signal?.aborted) {
        const errorMessage = err instanceof Error ? err.message : 'Failed to fetch scheduled jobs';
        setError(errorMessage);
      }
    } finally {
      if (!signal?.aborted) {
        setIsLoading(false);
      }
    }
  }, [projectId]);

  useEffect(() => {
    if (!enabled || !projectId) {
      return;
    }

    const controller = new AbortController();
    abortControllerRef.current = controller;
    fetchJobs(controller.signal);

    intervalRef.current = setInterval(() => {
      const newController = new AbortController();
      abortControllerRef.current = newController;
      fetchJobs(newController.signal);
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
  }, [enabled, projectId, pollInterval, fetchJobs]);

  const refresh = useCallback(() => {
    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
    }
    const controller = new AbortController();
    abortControllerRef.current = controller;
    fetchJobs(controller.signal);
  }, [fetchJobs]);

  return {
    jobs,
    isLoading,
    error,
    lastUpdated,
    refresh,
  };
}
