import { useCallback, useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { scheduledJobsApi } from '../../../services/scheduledJobs';
import type {
  ProjectScheduledJobRunDetailDto,
  ProjectScheduledJobRunSummaryDto,
  ProjectScheduledJobSummaryDto,
} from '../../../types/scheduledJob';

interface ScheduledJobRunHistoryDialogProps {
  projectId: string;
  job: ProjectScheduledJobSummaryDto;
  isOpen: boolean;
  onClose: () => void;
}

export function ScheduledJobRunHistoryDialog({
  projectId,
  job,
  isOpen,
  onClose,
}: ScheduledJobRunHistoryDialogProps) {
  const [runs, setRuns] = useState<ProjectScheduledJobRunSummaryDto[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [page, setPage] = useState(1);
  const [selectedRun, setSelectedRun] = useState<ProjectScheduledJobRunDetailDto | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const pageSize = 10;

  const loadRuns = useCallback(async () => {
    setIsLoading(true);
    setError(null);
    try {
      const result = await scheduledJobsApi.listRuns(projectId, job.id, page, pageSize);
      setRuns(result.items);
      setTotalCount(result.totalCount);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load run history');
    } finally {
      setIsLoading(false);
    }
  }, [projectId, job.id, page]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    loadRuns();
  }, [isOpen, loadRuns]);

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && isOpen) {
        onClose();
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isOpen, onClose]);

  const openRunDetail = async (runId: string) => {
    try {
      const detail = await scheduledJobsApi.getRun(projectId, job.id, runId);
      setSelectedRun(detail);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load run details');
    }
  };

  if (!isOpen) {
    return null;
  }

  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));

  return createPortal(
    <div
      className="fixed inset-0 bg-black bg-opacity-50 z-[9999] flex items-center justify-center p-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby="scheduled-job-history-title"
    >
      <div className="bg-white rounded-lg shadow-xl max-w-4xl w-full max-h-[90vh] overflow-hidden flex flex-col">
        <div className="px-6 py-4 border-b border-gray-200 flex items-center justify-between">
          <div>
            <h2 id="scheduled-job-history-title" className="text-lg font-semibold text-gray-900">
              Run history — {job.name}
            </h2>
            <p className="text-sm text-gray-500">{job.scheduleSummary}</p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="text-gray-500 hover:text-gray-700"
            aria-label="Close run history"
          >
            ✕
          </button>
        </div>

        {error && (
          <div className="mx-6 mt-4 text-sm text-red-700 bg-red-50 border border-red-200 rounded-md px-3 py-2" role="alert">
            {error}
          </div>
        )}

        <div className="flex-1 overflow-auto px-6 py-4">
          {isLoading ? (
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
                    <td className="py-2 pr-4">{new Date(run.startedUtc).toLocaleString()}</td>
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
                <h3 className="font-medium text-gray-900">Run details</h3>
                <button type="button" className="text-sm text-gray-500 hover:text-gray-700" onClick={() => setSelectedRun(null)}>
                  Close details
                </button>
              </div>
              {selectedRun.errorMessage && (
                <p className="text-sm text-red-700">{selectedRun.errorMessage}</p>
              )}
              {selectedRun.createdConversationId && (
                <p className="text-sm text-gray-700">
                  Conversation ID: <code>{selectedRun.createdConversationId}</code>
                </p>
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

        <div className="px-6 py-3 border-t border-gray-200 flex items-center justify-between">
          <span className="text-sm text-gray-500">
            Page {page} of {totalPages} ({totalCount} runs)
          </span>
          <div className="flex gap-2">
            <button
              type="button"
              disabled={page <= 1}
              onClick={() => setPage((p) => Math.max(1, p - 1))}
              className="px-3 py-1 text-sm border rounded disabled:opacity-50"
            >
              Previous
            </button>
            <button
              type="button"
              disabled={page >= totalPages}
              onClick={() => setPage((p) => p + 1)}
              className="px-3 py-1 text-sm border rounded disabled:opacity-50"
            >
              Next
            </button>
          </div>
        </div>
      </div>
    </div>,
    document.body,
  );
}
