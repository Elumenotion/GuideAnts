import { useCallback, useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { ScheduledJobRunHistory } from '../content/ScheduledJobRunHistory';
import type {
  ProjectScheduledJobDetailDto,
  ProjectScheduledJobSummaryDto,
} from '../../../types/scheduledJob';

interface ScheduledJobRunHistoryDialogProps {
  projectId: string;
  job: ProjectScheduledJobSummaryDto;
  isOpen: boolean;
  onClose: () => void;
  runOnOpen?: boolean;
  canRun?: boolean;
  onTimingFieldsUpdate?: (
    fields: Pick<ProjectScheduledJobDetailDto, 'lastRunUtc' | 'lastRunStatus' | 'nextRunUtc'>
  ) => void;
}

export function ScheduledJobRunHistoryDialog({
  projectId,
  job,
  isOpen,
  onClose,
  runOnOpen = false,
  canRun = false,
  onTimingFieldsUpdate,
}: ScheduledJobRunHistoryDialogProps) {
  const [error, setError] = useState<string | null>(null);
  const [isActive, setIsActive] = useState(false);

  useEffect(() => {
    if (!isOpen) {
      setError(null);
      setIsActive(false);
    }
  }, [isOpen]);

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && isOpen) {
        onClose();
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isOpen, onClose]);

  const handleActivityChange = useCallback((active: boolean) => {
    setIsActive(active);
  }, []);

  if (!isOpen) {
    return null;
  }

  const title = runOnOpen && isActive
    ? `Running — ${job.name}`
    : `Run history — ${job.name}`;

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
              {title}
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

        <div className="flex-1 overflow-auto">
          <ScheduledJobRunHistory
            projectId={projectId}
            jobId={job.id}
            notebookId={job.notebookId}
            jobType={job.jobType}
            runOnOpen={runOnOpen}
            canRun={canRun}
            embedded
            onTimingFieldsUpdate={onTimingFieldsUpdate}
            onError={setError}
            onActivityChange={handleActivityChange}
          />
        </div>
      </div>
    </div>,
    document.body,
  );
}
