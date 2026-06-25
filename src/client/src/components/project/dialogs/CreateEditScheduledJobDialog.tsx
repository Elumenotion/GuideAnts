import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { resolveAgainstApiBase } from '../../../config/apiConfig';
import { ScheduleBuilder } from '../scheduling/ScheduleBuilder';
import { NotebookPythonFilePicker } from '../scheduling/NotebookPythonFilePicker';
import { scheduledJobsApi } from '../../../services/scheduledJobs';
import { api } from '../../../services/api';
import type { ProjectNotebook } from '../../../types/project';
import type {
  CreateProjectScheduledJobRequest,
  FriendlyScheduleDto,
  ProjectScheduledJobDetailDto,
  ScheduledJobType,
} from '../../../types/scheduledJob';
import { buildScheduleSummary, defaultFriendlySchedule } from '../../../types/scheduledJob';
import type { AssistantOption } from '../../notebook/conversations/AssistantSelector';
import AssistantDropdown from '../../notebook/conversations/assistant-selector/AssistantDropdown';

interface CreateEditScheduledJobDialogProps {
  projectId: string;
  isOpen: boolean;
  onClose: () => void;
  onSaved: () => void;
  notebooks: ProjectNotebook[];
  job?: ProjectScheduledJobDetailDto | null;
  disabled?: boolean;
}

export function CreateEditScheduledJobDialog({
  projectId,
  isOpen,
  onClose,
  onSaved,
  notebooks,
  job = null,
  disabled = false,
}: CreateEditScheduledJobDialogProps) {
  const [name, setName] = useState('');
  const [jobType, setJobType] = useState<ScheduledJobType>('NewConversation');
  const [notebookId, setNotebookId] = useState('');
  const [isEnabled, setIsEnabled] = useState(true);
  const [timeZoneId, setTimeZoneId] = useState(
    Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC',
  );
  const [schedule, setSchedule] = useState<FriendlyScheduleDto>(defaultFriendlySchedule());
  const [conversationTitle, setConversationTitle] = useState('Scheduled {timestamp}');
  const [prompt, setPrompt] = useState('');
  const [assistantName, setAssistantName] = useState('assistant');
  const [scriptNotebookFileId, setScriptNotebookFileId] = useState<string | null>(null);
  const [assistants, setAssistants] = useState<AssistantOption[]>([]);
  const [isLoadingAssistants, setIsLoadingAssistants] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const notebooksRef = useRef(notebooks);
  notebooksRef.current = notebooks;

  const initSessionRef = useRef<{ isOpen: boolean; jobId: string | null }>({
    isOpen: false,
    jobId: null,
  });

  useEffect(() => {
    if (!isOpen) {
      initSessionRef.current = { isOpen: false, jobId: null };
      return;
    }

    const jobId = job?.id ?? null;
    const { isOpen: wasOpen, jobId: previousJobId } = initSessionRef.current;
    if (wasOpen && previousJobId === jobId) {
      return;
    }

    initSessionRef.current = { isOpen: true, jobId };

    setError(null);
    if (job) {
      setName(job.name);
      setJobType(job.jobType);
      setNotebookId(job.notebookId);
      setIsEnabled(job.isEnabled);
      setTimeZoneId(job.timeZoneId);
      setSchedule(job.friendlySchedule ?? defaultFriendlySchedule());
      setConversationTitle(job.conversationTitle ?? 'Scheduled {timestamp}');
      setPrompt(job.prompt ?? '');
      setAssistantName(job.assistantName ?? 'assistant');
      setScriptNotebookFileId(job.scriptNotebookFileId ?? null);
    } else {
      setName('');
      setJobType('NewConversation');
      setNotebookId(notebooksRef.current[0]?.id ?? '');
      setIsEnabled(true);
      setTimeZoneId(Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC');
      setSchedule(defaultFriendlySchedule());
      setConversationTitle('Scheduled {timestamp}');
      setPrompt('');
      setAssistantName('assistant');
      setScriptNotebookFileId(null);
    }
  }, [isOpen, job]);

  const selectedNotebookGuideId = useMemo(
    () => notebooks.find((item) => item.id === notebookId)?.guideId,
    [notebooks, notebookId],
  );

  useEffect(() => {
    if (!isOpen || jobType !== 'NewConversation' || !notebookId || !selectedNotebookGuideId) {
      setAssistants([]);
      return;
    }

    let cancelled = false;
    setIsLoadingAssistants(true);

    api.projects.notebookTemplates.getAssistants(selectedNotebookGuideId, projectId)
      .then((assistantList) => {
        if (cancelled) {
          return;
        }

        const normalized: AssistantOption[] = assistantList.map((assistant: {
          name: string;
          model?: string;
          modelDeploymentId?: string;
          avatarUrl?: string;
        }) => {
          let avatarUrl = assistant.avatarUrl ?? '';
          if (avatarUrl && !avatarUrl.startsWith('http')) {
            const urlWithProject = avatarUrl.includes('?')
              ? `${avatarUrl}&projectId=${projectId}`
              : `${avatarUrl}?projectId=${projectId}`;
            avatarUrl = resolveAgainstApiBase(urlWithProject).toString();
          }

          return {
            name: assistant.name,
            model: assistant.modelDeploymentId ?? assistant.model ?? '',
            avatarUrl,
          };
        });

        setAssistants(normalized);
        setAssistantName((current) => {
          if (normalized.length === 0) {
            return current;
          }
          if (normalized.some((assistant) => assistant.name === current)) {
            return current;
          }
          return normalized[0].name;
        });
      })
      .catch(() => {
        if (!cancelled) {
          setAssistants([]);
        }
      })
      .finally(() => {
        if (!cancelled) {
          setIsLoadingAssistants(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [isOpen, jobType, notebookId, projectId, selectedNotebookGuideId]);

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && isOpen && !isSubmitting) {
        onClose();
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isOpen, isSubmitting, onClose]);

  const schedulePreview = useMemo(
    () => buildScheduleSummary(schedule, timeZoneId),
    [schedule, timeZoneId],
  );

  const handleSubmit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (disabled || isSubmitting) {
      return;
    }

    if (!name.trim()) {
      setError('Name is required.');
      return;
    }
    if (!notebookId) {
      setError('Notebook is required.');
      return;
    }

    if (jobType === 'NewConversation' && assistants.length === 0) {
      setError('The selected notebook has no assistants available.');
      return;
    }

    const payload: CreateProjectScheduledJobRequest = {
      name: name.trim(),
      jobType,
      notebookId,
      isEnabled,
      timeZoneId,
      schedule,
      conversationTitle: jobType === 'NewConversation' ? conversationTitle.trim() : null,
      prompt: jobType === 'NewConversation' ? prompt.trim() : null,
      assistantName: jobType === 'NewConversation' ? assistantName : null,
      scriptNotebookFileId: jobType === 'RunPythonScript' ? scriptNotebookFileId : null,
    };

    setIsSubmitting(true);
    setError(null);
    try {
      if (job) {
        await scheduledJobsApi.update(projectId, job.id, payload);
      } else {
        await scheduledJobsApi.create(projectId, payload);
      }
      onSaved();
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save scheduled job');
    } finally {
      setIsSubmitting(false);
    }
  };

  if (!isOpen) {
    return null;
  }

  return createPortal(
    <div
      className="fixed inset-0 bg-black bg-opacity-50 z-[9999] flex items-center justify-center p-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby="scheduled-job-dialog-title"
    >
      <div className="bg-white rounded-lg shadow-xl max-w-2xl w-full max-h-[90vh] overflow-y-auto">
        <form onSubmit={handleSubmit} className="p-6 space-y-5">
          <h2 id="scheduled-job-dialog-title" className="text-lg font-semibold text-gray-900">
            {job ? 'Edit scheduled job' : 'New scheduled job'}
          </h2>

          {error && (
            <div className="text-sm text-red-700 bg-red-50 border border-red-200 rounded-md px-3 py-2" role="alert">
              {error}
            </div>
          )}

          <div>
            <label htmlFor="job-name" className="block text-sm font-medium text-gray-700 mb-1">Name</label>
            <input
              id="job-name"
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              disabled={disabled || isSubmitting}
              className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-blue-500 focus:border-blue-500 disabled:opacity-50"
              required
            />
          </div>

          <fieldset>
            <legend className="block text-sm font-medium text-gray-700 mb-2">Job type</legend>
            <div className="space-y-2">
              <label className="flex items-center gap-2 text-sm">
                <input
                  type="radio"
                  name="job-type"
                  checked={jobType === 'NewConversation'}
                  onChange={() => setJobType('NewConversation')}
                  disabled={disabled || isSubmitting}
                />
                New conversation in notebook
              </label>
              <label className="flex items-center gap-2 text-sm">
                <input
                  type="radio"
                  name="job-type"
                  checked={jobType === 'RunPythonScript'}
                  onChange={() => setJobType('RunPythonScript')}
                  disabled={disabled || isSubmitting}
                />
                Run Python script in notebook
              </label>
            </div>
          </fieldset>

          <div>
            <label htmlFor="job-notebook" className="block text-sm font-medium text-gray-700 mb-1">Notebook</label>
            <select
              id="job-notebook"
              value={notebookId}
              onChange={(e) => {
                setNotebookId(e.target.value);
                setScriptNotebookFileId(null);
              }}
              disabled={disabled || isSubmitting}
              className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-blue-500 focus:border-blue-500 disabled:opacity-50"
              required
            >
              <option value="">Select notebook</option>
              {notebooks.map((notebook) => (
                <option key={notebook.id} value={notebook.id}>{notebook.title}</option>
              ))}
            </select>
          </div>

          {jobType === 'NewConversation' && (
            <div className="space-y-4 border-t border-gray-100 pt-4">
              <div>
                <label htmlFor="conversation-title" className="block text-sm font-medium text-gray-700 mb-1">
                  Conversation title
                </label>
                <input
                  id="conversation-title"
                  type="text"
                  value={conversationTitle}
                  onChange={(e) => setConversationTitle(e.target.value)}
                  disabled={disabled || isSubmitting}
                  className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-blue-500 focus:border-blue-500 disabled:opacity-50"
                />
                <p className="mt-1 text-xs text-gray-500">Use {'{timestamp}'} for a unique UTC timestamp.</p>
              </div>

              <div>
                <label htmlFor="job-prompt" className="block text-sm font-medium text-gray-700 mb-1">Prompt</label>
                <textarea
                  id="job-prompt"
                  value={prompt}
                  onChange={(e) => setPrompt(e.target.value)}
                  disabled={disabled || isSubmitting}
                  rows={4}
                  className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-blue-500 focus:border-blue-500 disabled:opacity-50"
                  required={jobType === 'NewConversation'}
                />
              </div>

              <div>
                <span className="block text-sm font-medium text-gray-700 mb-1">Assistant</span>
                {isLoadingAssistants ? (
                  <p className="text-sm text-gray-500">Loading assistants…</p>
                ) : assistants.length === 0 ? (
                  <p className="text-sm text-gray-500">
                    No assistants are configured for this notebook&apos;s guide.
                  </p>
                ) : (
                  <AssistantDropdown
                    assistants={assistants}
                    selectedName={assistantName}
                    onSelect={setAssistantName}
                    disabled={disabled || isSubmitting}
                    fullWidth
                    searchPlaceholder="Search assistants by name"
                  />
                )}
              </div>
            </div>
          )}

          {jobType === 'RunPythonScript' && notebookId && (
            <div className="border-t border-gray-100 pt-4">
              <NotebookPythonFilePicker
                projectId={projectId}
                notebookId={notebookId}
                selectedFileId={scriptNotebookFileId}
                onSelect={(fileId) => setScriptNotebookFileId(fileId)}
                disabled={disabled || isSubmitting}
              />
            </div>
          )}

          <div className="border-t border-gray-100 pt-4">
            <h3 className="text-sm font-medium text-gray-900 mb-3">Schedule</h3>
            <ScheduleBuilder
              schedule={schedule}
              timeZoneId={timeZoneId}
              onScheduleChange={setSchedule}
              onTimeZoneChange={setTimeZoneId}
              disabled={disabled || isSubmitting}
              previewText={schedulePreview}
            />
          </div>

          <label className="inline-flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={isEnabled}
              onChange={(e) => setIsEnabled(e.target.checked)}
              disabled={disabled || isSubmitting}
              className="rounded border-gray-300 text-blue-600 focus:ring-blue-500"
            />
            Enabled
          </label>

          <div className="flex justify-end gap-3 pt-2">
            <button
              type="button"
              onClick={onClose}
              disabled={isSubmitting}
              className="px-4 py-2 text-sm text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50 disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={disabled || isSubmitting}
              className="px-4 py-2 text-sm text-white bg-blue-600 rounded-md hover:bg-blue-700 disabled:opacity-50"
            >
              {isSubmitting ? 'Saving…' : job ? 'Save changes' : 'Create job'}
            </button>
          </div>
        </form>
      </div>
    </div>,
    document.body,
  );
}
