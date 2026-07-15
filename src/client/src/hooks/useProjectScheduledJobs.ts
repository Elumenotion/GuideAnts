import { useState, useEffect, useRef, useCallback } from 'react';
import { scheduledJobsApi } from '../services/scheduledJobs';
import type {
  ProjectScheduledJobDetailDto,
  ProjectScheduledJobSummaryDto,
} from '../types/scheduledJob';

interface UseProjectScheduledJobsProps {
  projectId: string;
  selectedJobId?: string | null;
  enabled?: boolean;
  listPollInterval?: number;
  detailPollInterval?: number;
  detailActivePollInterval?: number;
}

type JobSummaryPatch = Partial<
  Pick<
    ProjectScheduledJobSummaryDto,
    'name' | 'isEnabled' | 'nextRunUtc' | 'lastRunUtc' | 'lastRunStatus' | 'scheduleSummary' | 'updatedUtc'
  >
>;

function summaryFieldsFromDetail(
  detail: ProjectScheduledJobDetailDto
): JobSummaryPatch {
  return {
    name: detail.name,
    isEnabled: detail.isEnabled,
    nextRunUtc: detail.nextRunUtc,
    lastRunUtc: detail.lastRunUtc,
    lastRunStatus: detail.lastRunStatus,
    scheduleSummary: detail.scheduleSummary,
    updatedUtc: detail.updatedUtc,
  };
}

function mergeDetailIntoJobs(
  jobs: ProjectScheduledJobSummaryDto[],
  detail: ProjectScheduledJobDetailDto
): ProjectScheduledJobSummaryDto[] {
  return jobs.map((job) =>
    job.id === detail.id ? { ...job, ...summaryFieldsFromDetail(detail) } : job
  );
}

export function useProjectScheduledJobs({
  projectId,
  selectedJobId = null,
  enabled = true,
  listPollInterval = 15000,
  detailPollInterval = 15000,
  detailActivePollInterval = 3000,
}: UseProjectScheduledJobsProps) {
  const [jobs, setJobs] = useState<ProjectScheduledJobSummaryDto[]>([]);
  const [selectedJobDetail, setSelectedJobDetail] = useState<ProjectScheduledJobDetailDto | null>(null);
  const [isLoadingJobs, setIsLoadingJobs] = useState(false);
  const [isLoadingDetail, setIsLoadingDetail] = useState(false);
  const [jobsError, setJobsError] = useState<string | null>(null);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
  const [detailRunActiveUntil, setDetailRunActiveUntil] = useState<number | null>(null);

  const jobsAbortRef = useRef<AbortController | null>(null);
  const selectedJobIdRef = useRef<string | null>(selectedJobId ?? null);

  selectedJobIdRef.current = selectedJobId ?? null;

  const fetchJobs = useCallback(
    async (options?: { signal?: AbortSignal; silent?: boolean }) => {
      if (!projectId) {
        return;
      }

      const silent = options?.silent ?? false;
      const signal = options?.signal;

      try {
        if (!silent) {
          setIsLoadingJobs(true);
        }
        setJobsError(null);

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
          setJobsError(err instanceof Error ? err.message : 'Failed to fetch scheduled jobs');
        }
      } finally {
        if (!silent && !signal?.aborted) {
          setIsLoadingJobs(false);
        }
      }
    },
    [projectId]
  );

  const fetchDetail = useCallback(
    async (jobId: string, options?: { initial?: boolean; silent?: boolean }) => {
      if (!projectId || !jobId) {
        return;
      }

      const isInitial = options?.initial ?? false;
      const silent = options?.silent ?? false;

      try {
        if (isInitial) {
          setIsLoadingDetail(true);
          setDetailError(null);
        }

        const detail = await scheduledJobsApi.get(projectId, jobId);

        if (selectedJobIdRef.current !== jobId) {
          return;
        }

        setSelectedJobDetail(detail);
        setJobs((current) => mergeDetailIntoJobs(current, detail));

        if (detail.lastRunStatus === 'Running') {
          setDetailRunActiveUntil(Date.now() + 120_000);
        }
      } catch (err) {
        if (selectedJobIdRef.current === jobId) {
          if (!silent) {
            setDetailError(err instanceof Error ? err.message : 'Failed to load scheduled job');
          }
          if (isInitial) {
            setSelectedJobDetail(null);
          }
        }
      } finally {
        if (isInitial && selectedJobIdRef.current === jobId) {
          setIsLoadingDetail(false);
        }
      }
    },
    [projectId]
  );

  const applyJobDetail = useCallback((detail: ProjectScheduledJobDetailDto) => {
    setJobs((current) => mergeDetailIntoJobs(current, detail));
    setSelectedJobDetail((current) => (current?.id === detail.id ? detail : current));
    if (detail.lastRunStatus === 'Running') {
      setDetailRunActiveUntil(Date.now() + 120_000);
    }
  }, []);

  const patchJob = useCallback((jobId: string, fields: JobSummaryPatch) => {
    setJobs((current) =>
      current.map((job) => (job.id === jobId ? { ...job, ...fields } : job))
    );
    setSelectedJobDetail((current) =>
      current?.id === jobId ? { ...current, ...fields } : current
    );

    if (fields.lastRunStatus === 'Running') {
      setDetailRunActiveUntil(Date.now() + 120_000);
    }
  }, []);

  const refreshJobs = useCallback(() => {
    if (jobsAbortRef.current) {
      jobsAbortRef.current.abort();
    }
    const controller = new AbortController();
    jobsAbortRef.current = controller;
    void fetchJobs({ signal: controller.signal, silent: true });
  }, [fetchJobs]);

  const refreshDetail = useCallback(() => {
    const jobId = selectedJobIdRef.current;
    if (!jobId) {
      return;
    }
    void fetchDetail(jobId, { silent: true });
  }, [fetchDetail]);

  const refreshAll = useCallback(() => {
    refreshJobs();
    refreshDetail();
  }, [refreshJobs, refreshDetail]);

  const patchSelectedJobFields = useCallback(
    (fields: JobSummaryPatch) => {
      const jobId = selectedJobIdRef.current;
      if (!jobId) {
        return;
      }
      patchJob(jobId, fields);
    },
    [patchJob]
  );

  const effectiveListPollInterval = selectedJobId ? Math.max(listPollInterval, 60_000) : listPollInterval;

  useEffect(() => {
    if (!enabled || !projectId) {
      return;
    }

    const controller = new AbortController();
    jobsAbortRef.current = controller;
    void fetchJobs({ signal: controller.signal, silent: false });

    const intervalId = window.setInterval(() => {
      void fetchJobs({ silent: true });
    }, effectiveListPollInterval);

    return () => {
      window.clearInterval(intervalId);
      controller.abort();
      jobsAbortRef.current = null;
    };
  }, [enabled, projectId, effectiveListPollInterval, fetchJobs]);

  useEffect(() => {
    if (!selectedJobId) {
      setSelectedJobDetail(null);
      setDetailError(null);
      setIsLoadingDetail(false);
      setDetailRunActiveUntil(null);
      return;
    }

    void fetchDetail(selectedJobId, { initial: true });
  }, [selectedJobId, fetchDetail]);

  const selectedSummary =
    jobs.find((job) => job.id === selectedJobId) ?? null;

  const isDetailRunActive =
    selectedJobDetail?.lastRunStatus === 'Running' ||
    selectedSummary?.lastRunStatus === 'Running' ||
    (detailRunActiveUntil != null && Date.now() < detailRunActiveUntil);

  const activeDetailPollInterval = isDetailRunActive ? detailActivePollInterval : detailPollInterval;

  useEffect(() => {
    if (!enabled || !projectId || !selectedJobId) {
      return;
    }

    const intervalId = window.setInterval(() => {
      void fetchDetail(selectedJobId, { silent: true });
      if (
        detailRunActiveUntil != null &&
        Date.now() >= detailRunActiveUntil &&
        selectedJobDetail?.lastRunStatus !== 'Running' &&
        selectedSummary?.lastRunStatus !== 'Running'
      ) {
        setDetailRunActiveUntil(null);
      }
    }, activeDetailPollInterval);

    return () => {
      window.clearInterval(intervalId);
    };
  }, [
    enabled,
    projectId,
    selectedJobId,
    activeDetailPollInterval,
    detailRunActiveUntil,
    fetchDetail,
    selectedJobDetail?.lastRunStatus,
    selectedSummary?.lastRunStatus,
  ]);

  useEffect(() => {
    const handleRunTriggered = (event: Event) => {
      const detail = (event as CustomEvent<{ jobId: string }>).detail;
      if (!detail?.jobId) {
        return;
      }
      setDetailRunActiveUntil(Date.now() + 120_000);
      patchJob(detail.jobId, { lastRunStatus: 'Running' });
      if (detail.jobId === selectedJobIdRef.current) {
        void fetchDetail(detail.jobId, { silent: true });
      } else {
        void fetchJobs({ silent: true });
      }
    };

    window.addEventListener('scheduled-job-run-triggered', handleRunTriggered);
    return () => window.removeEventListener('scheduled-job-run-triggered', handleRunTriggered);
  }, [fetchDetail, fetchJobs, patchJob]);

  return {
    jobs,
    selectedJobDetail,
    isLoadingJobs,
    isLoadingDetail,
    jobsError,
    detailError,
    lastUpdated,
    refreshJobs,
    refreshDetail,
    refreshAll,
    applyJobDetail,
    patchJob,
    patchSelectedJobFields,
  };
}
