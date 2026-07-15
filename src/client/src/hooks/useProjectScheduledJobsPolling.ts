import { useProjectScheduledJobs } from './useProjectScheduledJobs';

interface UseProjectScheduledJobsPollingProps {
  projectId: string;
  enabled?: boolean;
  pollInterval?: number;
}

/** List-only polling for surfaces that do not own scheduled-job detail state. */
export function useProjectScheduledJobsPolling({
  projectId,
  enabled = true,
  pollInterval = 15000,
}: UseProjectScheduledJobsPollingProps) {
  const { jobs, isLoadingJobs, jobsError, lastUpdated, refreshJobs } = useProjectScheduledJobs({
    projectId,
    enabled,
    listPollInterval: pollInterval,
  });

  return {
    jobs,
    isLoading: isLoadingJobs,
    error: jobsError,
    lastUpdated,
    refresh: refreshJobs,
  };
}
