import { memo, useCallback, useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { scheduledJobsApi } from '../../../services/scheduledJobs';
import type {
  ProjectScheduledJobDetailDto,
  ProjectScheduledJobRunDetailDto,
  ProjectScheduledJobRunSummaryDto,
  ScheduledJobType,
} from '../../../types/scheduledJob';
import { formatInUserLocal } from '../../../lib/scheduledJobDateTime';

interface ScheduledJobRunHistoryProps {
  projectId: string;
  jobId: string;
  notebookId: string;
  jobType: ScheduledJobType;
  runOnOpen?: boolean;
  canRun?: boolean;
  embedded?: boolean;
  onTimingFieldsUpdate?: (
    fields: Pick<ProjectScheduledJobDetailDto, 'lastRunUtc' | 'lastRunStatus' | 'nextRunUtc'>
  ) => void;
  onError?: (message: string) => void;
  onActivityChange?: (active: boolean) => void;
}

function statusClassName(status: ProjectScheduledJobRunSummaryDto['status']): string {
  switch (status) {
    case 'Running':
      return 'text-blue-700 font-medium';
    case 'Succeeded':
      return 'text-green-700';
    case 'Failed':
      return 'text-red-700';
    case 'Cancelled':
      return 'text-amber-700';
    default:
      return 'text-gray-900';
  }
}

export const ScheduledJobRunHistory = memo(function ScheduledJobRunHistory({
  projectId,
  jobId,
  notebookId,
  jobType,
  runOnOpen = false,
  canRun = false,
  embedded = false,
  onTimingFieldsUpdate,
  onError,
  onActivityChange,
}: ScheduledJobRunHistoryProps) {
  const navigate = useNavigate();
  const [runs, setRuns] = useState<ProjectScheduledJobRunSummaryDto[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [page, setPage] = useState(1);
  const [selectedRun, setSelectedRun] = useState<ProjectScheduledJobRunDetailDto | null>(null);
  const [isLoadingRuns, setIsLoadingRuns] = useState(true);
  const [isStartingRun, setIsStartingRun] = useState(false);
  const [pollRunsUntil, setPollRunsUntil] = useState<number | null>(null);
  const runOnOpenSessionRef = useRef<{ jobId: string | null; triggered: boolean }>({
    jobId: null,
    triggered: false,
  });
  const pageSize = 10;

  const refreshTimingFields = useCallback(async () => {
    if (!onTimingFieldsUpdate) {
      return;
    }

    try {
      const detail = await scheduledJobsApi.get(projectId, jobId);
      onTimingFieldsUpdate({
        lastRunUtc: detail.lastRunUtc,
        lastRunStatus: detail.lastRunStatus,
        nextRunUtc: detail.nextRunUtc,
      });
    } catch {
      // keep existing timing fields visible
    }
  }, [projectId, jobId, onTimingFieldsUpdate]);

  const loadRuns = useCallback(async (options?: { silent?: boolean }) => {
    if (!options?.silent) {
      setIsLoadingRuns(true);
    }

    try {
      const result = await scheduledJobsApi.listRuns(projectId, jobId, page, pageSize);
      setRuns(result.items);
      setTotalCount(result.totalCount);
      return result.items;
    } catch (err) {
      onError?.(err instanceof Error ? err.message : 'Failed to load run history');
      return null;
    } finally {
      if (!options?.silent) {
        setIsLoadingRuns(false);
      }
    }
  }, [projectId, jobId, page, onError]);

  const openRunDetail = useCallback(async (runId: string, options?: { silent?: boolean }) => {
    try {
      const detail = await scheduledJobsApi.getRun(projectId, jobId, runId);
      setSelectedRun(detail);
      return detail;
    } catch (err) {
      if (!options?.silent) {
        onError?.(err instanceof Error ? err.message : 'Failed to load run details');
      }
      return null;
    }
  }, [projectId, jobId, onError]);

  const beginPolling = useCallback(() => {
    setPollRunsUntil(Date.now() + 120_000);
    setPage(1);
  }, []);

  const triggerRunNow = useCallback(async () => {
    if (!canRun || isStartingRun) {
      return;
    }

    setIsStartingRun(true);
    try {
      await scheduledJobsApi.runNow(projectId, jobId);
      beginPolling();
      window.dispatchEvent(new CustomEvent('scheduled-job-run-triggered', { detail: { jobId } }));
      void loadRuns({ silent: true });
      void refreshTimingFields();
    } catch (err) {
      onError?.(err instanceof Error ? err.message : 'Failed to start job run');
    } finally {
      setIsStartingRun(false);
    }
  }, [beginPolling, canRun, isStartingRun, jobId, loadRuns, onError, projectId, refreshTimingFields]);

  useEffect(() => {
    setPage(1);
    setSelectedRun(null);
    setPollRunsUntil(null);
    runOnOpenSessionRef.current = { jobId: null, triggered: false };
  }, [projectId, jobId]);

  useEffect(() => {
    void loadRuns();
  }, [loadRuns]);

  useEffect(() => {
    if (!runOnOpen || !canRun) {
      return;
    }

    const session = runOnOpenSessionRef.current;
    if (session.jobId === jobId && session.triggered) {
      return;
    }

    runOnOpenSessionRef.current = { jobId, triggered: true };
    void triggerRunNow();
  }, [runOnOpen, canRun, jobId, triggerRunNow]);

  useEffect(() => {
    const handleRunTriggered = (event: Event) => {
      const detail = (event as CustomEvent<{ jobId: string }>).detail;
      if (detail?.jobId !== jobId) {
        return;
      }
      beginPolling();
      void loadRuns({ silent: true });
      void refreshTimingFields();
    };

    window.addEventListener('scheduled-job-run-triggered', handleRunTriggered);
    return () => window.removeEventListener('scheduled-job-run-triggered', handleRunTriggered);
  }, [beginPolling, jobId, loadRuns, refreshTimingFields]);

  const hasRunningRun = runs.some((run) => run.status === 'Running');
  const latestRun = runs[0] ?? null;
  const isActive = isStartingRun || hasRunningRun || (pollRunsUntil != null && Date.now() < pollRunsUntil);

  useEffect(() => {
    onActivityChange?.(isActive);
  }, [isActive, onActivityChange]);

  useEffect(() => {
    const shouldPoll = hasRunningRun || isStartingRun || (pollRunsUntil != null && Date.now() < pollRunsUntil);
    if (!shouldPoll) {
      return;
    }

    const intervalId = window.setInterval(() => {
      void loadRuns({ silent: true });
      void refreshTimingFields();
      if (selectedRun?.status === 'Running') {
        void openRunDetail(selectedRun.id, { silent: true });
      }
      if (pollRunsUntil != null && Date.now() >= pollRunsUntil && !hasRunningRun && !isStartingRun) {
        setPollRunsUntil(null);
      }
    }, 3000);

    return () => window.clearInterval(intervalId);
  }, [
    hasRunningRun,
    isStartingRun,
    pollRunsUntil,
    loadRuns,
    openRunDetail,
    refreshTimingFields,
    selectedRun,
  ]);

  useEffect(() => {
    const runningRun = runs.find((run) => run.status === 'Running');
    if (!runningRun) {
      return;
    }

    if (selectedRun?.id === runningRun.id && selectedRun.status === 'Running') {
      return;
    }

    void openRunDetail(runningRun.id, { silent: true });
  }, [openRunDetail, runs, selectedRun]);

  const openConversation = (conversationId: string) => {
    navigate(`/projects/${projectId}/notebooks/${notebookId}`, {
      state: { conversationId },
    });
  };

  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));
  const activeConversationId = selectedRun?.createdConversationId
    ?? latestRun?.createdConversationId
    ?? null;

  const content = (
    <>
      {(isStartingRun || hasRunningRun) && (
        <div className={`${embedded ? 'px-6' : 'px-4'} pt-4`}>
          <div className="p-4 bg-blue-50 border border-blue-200 rounded-md flex items-start gap-3">
            <svg className="w-5 h-5 text-blue-600 animate-spin mt-0.5 shrink-0" fill="none" viewBox="0 0 24 24" aria-hidden="true">
              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z" />
            </svg>
            <div className="min-w-0 flex-1 space-y-2">
              <div>
                <p className="text-sm font-medium text-blue-900">
                  {isStartingRun ? 'Starting run…' : 'Run in progress'}
                </p>
                <p className="text-xs text-blue-700">
                  Status updates automatically. You can close this dialog and the job will keep running.
                </p>
              </div>
              {jobType === 'NewConversation' && activeConversationId && (
                <button
                  type="button"
                  className="text-sm font-medium text-blue-700 hover:underline"
                  onClick={() => openConversation(activeConversationId)}
                >
                  Open conversation
                </button>
              )}
            </div>
          </div>
        </div>
      )}

      <div className={`${embedded ? 'px-6 py-4' : 'px-4 py-4'}`}>
        {isLoadingRuns ? (
          <p className="text-sm text-gray-500">Loading runs…</p>
        ) : runs.length === 0 ? (
          <p className="text-sm text-gray-500">
            {isStartingRun ? 'Waiting for the run to appear…' : 'No runs recorded yet.'}
          </p>
        ) : (
          <table className="min-w-full text-sm">
            <thead>
              <tr className="text-left text-gray-500 border-b">
                <th className="py-2 pr-4">Started</th>
                <th className="py-2 pr-4">Status</th>
                <th className="py-2 pr-4">Trigger</th>
                <th className="py-2 pr-4">Details</th>
              </tr>
            </thead>
            <tbody>
              {runs.map((run) => (
                <tr
                  key={run.id}
                  className={`border-b border-gray-100 ${run.status === 'Running' ? 'bg-blue-50/60' : ''}`}
                >
                  <td className="py-2 pr-4">{formatInUserLocal(run.startedUtc)}</td>
                  <td className={`py-2 pr-4 ${statusClassName(run.status)}`}>{run.status}</td>
                  <td className="py-2 pr-4">{run.triggeredBy}</td>
                  <td className="py-2 pr-4">
                    <button
                      type="button"
                      className="text-blue-600 hover:underline"
                      onClick={() => openRunDetail(run.id)}
                    >
                      View output
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {selectedRun && (
          <div className="mt-6 border border-gray-200 rounded-md p-4 space-y-3">
            <div className="flex items-center justify-between">
              <h4 className="font-medium text-gray-900">Run details</h4>
              <button
                type="button"
                className="text-sm text-gray-500 hover:text-gray-700"
                onClick={() => setSelectedRun(null)}
              >
                Close details
              </button>
            </div>
            {selectedRun.errorMessage && (
              <p className="text-sm text-red-700">{selectedRun.errorMessage}</p>
            )}
            {jobType === 'NewConversation' && selectedRun.createdConversationId && (
              <button
                type="button"
                className="text-sm text-blue-600 hover:underline"
                onClick={() => openConversation(selectedRun.createdConversationId!)}
              >
                Open conversation
              </button>
            )}
            <div>
              <label className="block text-xs font-medium text-gray-500 mb-1">stdout</label>
              <pre className="text-xs bg-gray-50 border rounded p-3 overflow-auto max-h-48 whitespace-pre-wrap">
                {selectedRun.standardOutput || '(empty)'}
              </pre>
            </div>
            <div>
              <label className="block text-xs font-medium text-gray-500 mb-1">stderr</label>
              <pre className="text-xs bg-gray-50 border rounded p-3 overflow-auto max-h-48 whitespace-pre-wrap">
                {selectedRun.standardError || '(empty)'}
              </pre>
            </div>
          </div>
        )}
      </div>

      <div className={`${embedded ? 'px-6 py-3' : 'px-4 py-3'} border-t border-gray-200 flex items-center justify-between bg-gray-50`}>
        <span className="text-sm text-gray-500">
          Page {page} of {totalPages} ({totalCount} runs)
        </span>
        <div className="flex gap-2">
          <button
            type="button"
            disabled={page <= 1}
            onClick={() => setPage((current) => Math.max(1, current - 1))}
            className="px-3 py-1 text-sm border rounded disabled:opacity-50"
          >
            Previous
          </button>
          <button
            type="button"
            disabled={page >= totalPages}
            onClick={() => setPage((current) => current + 1)}
            className="px-3 py-1 text-sm border rounded disabled:opacity-50"
          >
            Next
          </button>
        </div>
      </div>
    </>
  );

  if (embedded) {
    return content;
  }

  return (
    <div className="border border-gray-200 rounded-lg overflow-hidden">
      <div className="px-4 py-3 border-b border-gray-200 bg-gray-50 flex items-center justify-between">
        <h3 className="text-sm font-medium text-gray-900">Run history</h3>
        {isActive && (
          <span className="text-xs text-blue-600">Refreshing…</span>
        )}
      </div>
      {content}
    </div>
  );
});
