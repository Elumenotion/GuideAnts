import { useCallback, useState } from 'react';

import LoadingSpinner from '../../LoadingSpinner';

import type { ProjectScheduledJobDetailDto } from '../../../types/scheduledJob';

import { formatInUserLocal, formatNextRun } from '../../../lib/scheduledJobDateTime';

import { ScheduledJobRunHistoryDialog } from '../dialogs/ScheduledJobRunHistoryDialog';



interface ScheduledJobContentProps {

  projectId: string;

  jobId: string;

  job: ProjectScheduledJobDetailDto | null;

  isLoading?: boolean;

  error?: string | null;

  canRun?: boolean;

  onRefresh?: () => void;

  onJobFieldsPatch?: (

    fields: Partial<Pick<ProjectScheduledJobDetailDto, 'lastRunUtc' | 'lastRunStatus' | 'nextRunUtc' | 'isEnabled'>>

  ) => void;

}



function jobTypeLabel(jobType: ProjectScheduledJobDetailDto['jobType']): string {

  return jobType === 'NewConversation' ? 'New conversation in notebook' : 'Run Python script in notebook';

}



export function ScheduledJobContent({

  projectId,

  jobId,

  job,

  isLoading = false,

  error = null,

  canRun = false,

  onRefresh,

  onJobFieldsPatch,

}: ScheduledJobContentProps) {

  const [runDialog, setRunDialog] = useState<{ runOnOpen: boolean } | null>(null);



  const handleTimingFieldsUpdate = useCallback((

    fields: Pick<ProjectScheduledJobDetailDto, 'lastRunUtc' | 'lastRunStatus' | 'nextRunUtc'>

  ) => {

    onJobFieldsPatch?.(fields);

  }, [onJobFieldsPatch]);



  const handleCloseRunDialog = useCallback(() => {

    setRunDialog(null);

    onRefresh?.();

  }, [onRefresh]);



  if (isLoading && !job) {

    return <LoadingSpinner message="Loading scheduled job…" />;

  }



  if (!job || job.id !== jobId) {

    return (

      <div className="p-6">

        <p className="text-sm text-red-700">{error ?? 'Scheduled job not found.'}</p>

      </div>

    );

  }



  const isRunActive = job.lastRunStatus === 'Running';



  return (

    <>

      <div className="p-6 max-w-4xl space-y-6">

        <div>

          <div className="flex items-start justify-between gap-4">

            <div>

              <h2 className="text-xl font-semibold text-gray-900">{job.name}</h2>

              <p className="mt-1 text-sm text-gray-500">{job.scheduleSummary}</p>

            </div>

            <div className="flex items-center gap-2 shrink-0">

              {canRun && job.isEnabled && (

                <button

                  type="button"

                  onClick={() => setRunDialog({ runOnOpen: true })}

                  className="px-3 py-1.5 text-sm text-white bg-blue-600 rounded-md hover:bg-blue-700"

                >

                  Run now

                </button>

              )}

              <button

                type="button"

                onClick={() => setRunDialog({ runOnOpen: false })}

                className="px-3 py-1.5 text-sm text-gray-700 border border-gray-300 rounded-md hover:bg-gray-50"

              >

                View history

              </button>

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



        {!job.isEnabled && (

          <div className="p-4 bg-amber-50 border border-amber-200 rounded-md" role="status">

            <p className="text-sm font-medium text-amber-900">This job is disabled</p>

            <p className="text-xs text-amber-800 mt-1">

              It will not run on schedule. Enable it from the sidebar context menu to resume automatic runs.

            </p>

          </div>

        )}



        {isRunActive && (

          <div className="p-4 bg-blue-50 border border-blue-200 rounded-md flex items-start gap-3" role="status">

            <svg className="w-5 h-5 text-blue-600 animate-spin mt-0.5 shrink-0" fill="none" viewBox="0 0 24 24" aria-hidden="true">

              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />

              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z" />

            </svg>

            <div>

              <p className="text-sm font-medium text-blue-900">Run in progress</p>

              <p className="text-xs text-blue-700 mt-1">

                Last run and next run update automatically while this job is running.

              </p>

            </div>

          </div>

        )}



        <div className="border border-gray-200 rounded-lg p-4 space-y-3">

          <h3 className="text-sm font-medium text-gray-900">Configuration</h3>

          <dl className="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-3 text-sm">

            <div>

              <dt className="text-gray-500">Status</dt>

              <dd className={job.isEnabled ? 'text-green-700 font-medium' : 'text-amber-700 font-medium'}>

                {job.isEnabled ? 'Enabled — runs on schedule' : 'Disabled — automatic runs paused'}

              </dd>

            </div>

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

            <div className="sm:col-span-2">

              <dt className="text-gray-500">Next run</dt>

              <dd className={job.isEnabled ? 'text-gray-900' : 'text-gray-500'}>

                {formatNextRun(job.nextRunUtc, job.timeZoneId, job.isEnabled)}

              </dd>

              {job.isEnabled && job.nextRunUtc && (

                <p className="text-xs text-gray-500 mt-0.5">

                  Scheduled in {job.timeZoneId}

                </p>

              )}

            </div>

            <div>

              <dt className="text-gray-500">Last run</dt>

              <dd className="text-gray-900">

                {formatInUserLocal(job.lastRunUtc)}

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

      </div>



      {runDialog && (

        <ScheduledJobRunHistoryDialog

          projectId={projectId}

          job={job}

          isOpen

          runOnOpen={runDialog.runOnOpen}

          canRun={canRun && runDialog.runOnOpen}

          onClose={handleCloseRunDialog}

          onTimingFieldsUpdate={handleTimingFieldsUpdate}

        />

      )}

    </>

  );

}
