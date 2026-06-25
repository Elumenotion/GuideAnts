import { useCallback, useEffect, useState } from 'react';
import LoadingSpinner from '../../LoadingSpinner';
import { scheduledJobsApi } from '../../../services/scheduledJobs';
import type { ProjectScheduledJobDetailDto } from '../../../types/scheduledJob';
import { ScheduledJobRunHistory } from './ScheduledJobRunHistory';

interface ScheduledJobContentProps {
  projectId: string;
  jobId: string;
  canRun?: boolean;
}

function formatUtc(value?: string | null): string {
  if (!value) {
    return '—';
  }
  return new Date(value).toLocaleString();
}

function jobTypeLabel(jobType: ProjectScheduledJobDetailDto['jobType']): string {
  return jobType === 'NewConversation' ? 'New conversation in notebook' : 'Run Python script in notebook';
}

export function ScheduledJobContent({ projectId, jobId, canRun = false }: ScheduledJobContentProps) {
  const [job, setJob] = useState<ProjectScheduledJobDetailDto | null>(null);
  const [isLoadingJob, setIsLoadingJob] = useState(true);
  const [isRunningNow, setIsRunningNow] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setIsLoadingJob(true);
    setError(null);

    scheduledJobsApi.get(projectId, jobId)
      .then((detail) => {
        if (!cancelled) {
          setJob(detail);
        }
      })
      .catch((err) => {
        if (!cancelled) {
          setJob(null);
          setError(err instanceof Error ? err.message : 'Failed to load scheduled job');
        }
      })
      .finally(() => {
        if (!cancelled) {
          setIsLoadingJob(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [projectId, jobId]);

  const handleTimingFieldsUpdate = useCallback((
    fields: Pick<ProjectScheduledJobDetailDto, 'lastRunUtc' | 'lastRunStatus' | 'nextRunUtc'>
  ) => {
    setJob((current) => (current ? { ...current, ...fields } : current));
  }, []);

  const handleRunHistoryError = useCallback((message: string) => {
    setError(message);
  }, []);

  const handleRunNow = async () => {
    if (!canRun || isRunningNow) {
      return;
    }

    setIsRunningNow(true);
    setError(null);
    try {
      await scheduledJobsApi.runNow(projectId, jobId);
      window.dispatchEvent(new CustomEvent('scheduled-job-run-triggered', { detail: { jobId } }));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to start job run');
    } finally {
      setIsRunningNow(false);
    }
  };

  if (isLoadingJob) {
    return <LoadingSpinner message="Loading scheduled job…" />;
  }

  if (!job) {
    return (
      <div className="p-6">
        <p className="text-sm text-red-700">{error ?? 'Scheduled job not found.'}</p>
      </div>
    );
  }

  return (
    <div className="p-6 max-w-4xl space-y-6">
      <div>
        <div className="flex items-start justify-between gap-4">
          <div>
            <h2 className="text-xl font-semibold text-gray-900">{job.name}</h2>
            <p className="mt-1 text-sm text-gray-500">{job.scheduleSummary}</p>
          </div>
          <div className="flex items-center gap-2 shrink-0">
            {canRun && (
              <button
                type="button"
                onClick={handleRunNow}
                disabled={isRunningNow}
                className="px-3 py-1.5 text-sm text-white bg-blue-600 rounded-md hover:bg-blue-700 disabled:opacity-50"
              >
                {isRunningNow ? 'Starting…' : 'Run now'}
              </button>
            )}
            <span
              className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${
                job.isEnabled ? 'bg-green-100 text-green-800' : 'bg-amber-100 text-amber-800'
              }`}
            >
              {job.isEnabled ? 'Enabled' : 'Disabled'}
            </span>
          </div>
        </div>
      </div>

      {error && (
        <div className="text-sm text-red-700 bg-red-50 border border-red-200 rounded-md px-3 py-2" role="alert">
          {error}
        </div>
      )}

      <div className="border border-gray-200 rounded-lg p-4 space-y-3">
        <h3 className="text-sm font-medium text-gray-900">Configuration</h3>
        <dl className="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-3 text-sm">
          <div>
            <dt className="text-gray-500">Job type</dt>
            <dd className="text-gray-900">{jobTypeLabel(job.jobType)}</dd>
          </div>
          <div>
            <dt className="text-gray-500">Notebook</dt>
            <dd className="text-gray-900">{job.notebookTitle}</dd>
          </div>
          <div>
            <dt className="text-gray-500">Time zone</dt>
            <dd className="text-gray-900">{job.timeZoneId}</dd>
          </div>
          <div>
            <dt className="text-gray-500">Next run</dt>
            <dd className="text-gray-900">{formatUtc(job.nextRunUtc)}</dd>
          </div>
          <div>
            <dt className="text-gray-500">Last run</dt>
            <dd className="text-gray-900">
              {formatUtc(job.lastRunUtc)}
              {job.lastRunStatus ? ` (${job.lastRunStatus})` : ''}
            </dd>
          </div>
          {job.jobType === 'NewConversation' && (
            <>
              <div className="sm:col-span-2">
                <dt className="text-gray-500">Conversation title</dt>
                <dd className="text-gray-900">{job.conversationTitle ?? '—'}</dd>
              </div>
              <div>
                <dt className="text-gray-500">Assistant</dt>
                <dd className="text-gray-900">{job.assistantName ?? '—'}</dd>
              </div>
              <div className="sm:col-span-2">
                <dt className="text-gray-500">Prompt</dt>
                <dd className="text-gray-900 whitespace-pre-wrap">{job.prompt ?? '—'}</dd>
              </div>
            </>
          )}
          {job.jobType === 'RunPythonScript' && (
            <div className="sm:col-span-2">
              <dt className="text-gray-500">Script</dt>
              <dd className="text-gray-900 font-mono text-xs">{job.scriptRelativePath ?? '—'}</dd>
            </div>
          )}
        </dl>
      </div>

      <ScheduledJobRunHistory
        projectId={projectId}
        jobId={jobId}
        notebookId={job.notebookId}
        jobType={job.jobType}
        onTimingFieldsUpdate={handleTimingFieldsUpdate}
        onError={handleRunHistoryError}
      />
    </div>
  );
}
