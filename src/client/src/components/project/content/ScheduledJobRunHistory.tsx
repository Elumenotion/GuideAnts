import { memo, useCallback, useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { scheduledJobsApi } from '../../../services/scheduledJobs';
import type {
  ProjectScheduledJobDetailDto,
  ProjectScheduledJobRunDetailDto,
  ProjectScheduledJobRunSummaryDto,
  ScheduledJobType,
} from '../../../types/scheduledJob';

interface ScheduledJobRunHistoryProps {
  projectId: string;
  jobId: string;
  notebookId: string;
  jobType: ScheduledJobType;
  onTimingFieldsUpdate?: (
    fields: Pick<ProjectScheduledJobDetailDto, 'lastRunUtc' | 'lastRunStatus' | 'nextRunUtc'>
  ) => void;
  onError?: (message: string) => void;
}

function formatUtc(value?: string | null): string {
  if (!value) {
    return '—';
  }
  return new Date(value).toLocaleString();
}

export const ScheduledJobRunHistory = memo(function ScheduledJobRunHistory({
  projectId,
  jobId,
  notebookId,
  jobType,
  onTimingFieldsUpdate,
  onError,
}: ScheduledJobRunHistoryProps) {
  const navigate = useNavigate();
  const [runs, setRuns] = useState<ProjectScheduledJobRunSummaryDto[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [page, setPage] = useState(1);
  const [selectedRun, setSelectedRun] = useState<ProjectScheduledJobRunDetailDto | null>(null);
  const [isLoadingRuns, setIsLoadingRuns] = useState(true);
  const [pollRunsUntil, setPollRunsUntil] = useState<number | null>(null);
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

  useEffect(() => {
    setPage(1);
    setSelectedRun(null);
    setPollRunsUntil(null);
  }, [projectId, jobId]);

  useEffect(() => {
    void loadRuns();
  }, [loadRuns]);

  useEffect(() => {
    const handleRunTriggered = (event: Event) => {
      const detail = (event as CustomEvent<{ jobId: string }>).detail;
      if (detail?.jobId !== jobId) {
        return;
      }
      setPollRunsUntil(Date.now() + 120_000);
      setPage(1);
      void loadRuns({ silent: true });
      void refreshTimingFields();
    };

    window.addEventListener('scheduled-job-run-triggered', handleRunTriggered);
    return () => window.removeEventListener('scheduled-job-run-triggered', handleRunTriggered);
  }, [jobId, loadRuns, refreshTimingFields]);

  const hasRunningRun = runs.some((run) => run.status === 'Running');

  useEffect(() => {
    const shouldPoll = hasRunningRun || (pollRunsUntil != null && Date.now() < pollRunsUntil);
    if (!shouldPoll) {
      return;
    }

    const intervalId = window.setInterval(() => {
      void loadRuns({ silent: true });
      void refreshTimingFields();
      if (pollRunsUntil != null && Date.now() >= pollRunsUntil && !hasRunningRun) {
        setPollRunsUntil(null);
      }
    }, 3000);

    return () => window.clearInterval(intervalId);
  }, [hasRunningRun, pollRunsUntil, loadRuns, refreshTimingFields]);

  const openRunDetail = async (runId: string) => {
    try {
      const detail = await scheduledJobsApi.getRun(projectId, jobId, runId);
      setSelectedRun(detail);
    } catch (err) {
      onError?.(err instanceof Error ? err.message : 'Failed to load run details');
    }
  };

  const openConversation = () => {
    if (!selectedRun?.createdConversationId) {
      return;
    }

    navigate(`/projects/${projectId}/notebooks/${notebookId}`, {
      state: { conversationId: selectedRun.createdConversationId },
    });
  };

  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));

  return (
    <div className="border border-gray-200 rounded-lg overflow-hidden">
      <div className="px-4 py-3 border-b border-gray-200 bg-gray-50 flex items-center justify-between">
        <h3 className="text-sm font-medium text-gray-900">Run history</h3>
        {(hasRunningRun || pollRunsUntil != null) && (
          <span className="text-xs text-blue-600">Refreshing…</span>
        )}
      </div>

      <div className="px-4 py-4">
        {isLoadingRuns ? (
          <p className="text-sm text-gray-500">Loading runs…</p>
        ) : runs.length === 0 ? (
          <p className="text-sm text-gray-500">No runs recorded yet.</p>
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
                <tr key={run.id} className="border-b border-gray-100">
                  <td className="py-2 pr-4">{formatUtc(run.startedUtc)}</td>
                  <td className="py-2 pr-4">{run.status}</td>
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
                onClick={openConversation}
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

      <div className="px-4 py-3 border-t border-gray-200 flex items-center justify-between bg-gray-50">
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
    </div>
  );
});
