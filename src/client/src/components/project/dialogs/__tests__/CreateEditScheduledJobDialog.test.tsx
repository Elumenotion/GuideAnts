import { describe, expect, it, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { ToastProvider } from '../../../../components/common/Toast';
import { CreateEditScheduledJobDialog } from '../CreateEditScheduledJobDialog';
import { scheduledJobsApi } from '../../../../services/scheduledJobs';
import { api } from '../../../../services/api';

vi.mock('../../../../services/scheduledJobs', () => ({
  scheduledJobsApi: {
    create: vi.fn(),
    update: vi.fn(),
  },
}));

vi.mock('../../../../services/api', () => ({
  api: {
    projects: {
      notebookTemplates: {
        getAssistants: vi.fn(),
      },
    },
  },
}));

vi.mock('../../notebook/conversations/assistant-selector/AssistantDropdown', () => ({
  default: ({
    assistants,
    selectedAssistant,
    onSelect,
  }: {
    assistants: Array<{ name: string }>;
    selectedAssistant: string;
    onSelect: (name: string) => void;
  }) => (
    <select
      aria-label="Assistant"
      value={selectedAssistant}
      onChange={(e) => onSelect(e.target.value)}
    >
      {assistants.map((assistant) => (
        <option key={assistant.name} value={assistant.name}>{assistant.name}</option>
      ))}
    </select>
  ),
}));

vi.mock('../scheduling/NotebookPythonFilePicker', () => ({
  NotebookPythonFilePicker: () => <div>python-file-picker</div>,
}));

const notebooks = [
  {
    id: 'notebook-1',
    title: 'Ops Notebook',
    guideId: 'guide-1',
    projectId: 'project-1',
    createdUtc: '2026-01-01T00:00:00Z',
    updatedUtc: '2026-01-01T00:00:00Z',
  },
];

describe('CreateEditScheduledJobDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.projects.notebookTemplates.getAssistants).mockResolvedValue([
      { name: 'assistant', model: 'gpt-test' },
    ]);
    vi.mocked(scheduledJobsApi.create).mockResolvedValue({ id: 'job-new' } as never);
  });

  it('creates a new conversation job when the form is valid', async () => {
    const user = userEvent.setup();
    const onSaved = vi.fn();
    const onClose = vi.fn();

    render(
      <ToastProvider>
        <CreateEditScheduledJobDialog
          projectId="project-1"
          isOpen
          onClose={onClose}
          onSaved={onSaved}
          notebooks={notebooks as never}
        />
      </ToastProvider>,
    );

    await user.type(screen.getByLabelText('Name'), 'Morning briefing');
    await user.type(screen.getByLabelText('Prompt'), 'Summarize overnight activity.');
    await user.click(screen.getByRole('button', { name: /Create job/i }));

    await waitFor(() => {
      expect(scheduledJobsApi.create).toHaveBeenCalledWith(
        'project-1',
        expect.objectContaining({
          name: 'Morning briefing',
          jobType: 'NewConversation',
          notebookId: 'notebook-1',
          prompt: 'Summarize overnight activity.',
        }),
      );
    });
    expect(onSaved).toHaveBeenCalledWith(expect.objectContaining({ id: 'job-new' }));
    expect(onClose).toHaveBeenCalled();
  });

  it('shows validation errors for missing required fields', async () => {
    const user = userEvent.setup();

    render(
      <ToastProvider>
        <CreateEditScheduledJobDialog
          projectId="project-1"
          isOpen
          onClose={vi.fn()}
          onSaved={vi.fn()}
          notebooks={notebooks as never}
        />
      </ToastProvider>,
    );

    fireEvent.submit(screen.getByRole('dialog').querySelector('form')!);
    expect(await screen.findByText('Name is required.')).toBeInTheDocument();
    expect(scheduledJobsApi.create).not.toHaveBeenCalled();
  });

  it('updates an existing job', async () => {
    const user = userEvent.setup();
    vi.mocked(scheduledJobsApi.update).mockResolvedValue({ id: 'job-1' } as never);

    render(
      <ToastProvider>
        <CreateEditScheduledJobDialog
          projectId="project-1"
          isOpen
          onClose={vi.fn()}
          onSaved={vi.fn()}
          notebooks={notebooks as never}
          job={{
          id: 'job-1',
          name: 'Existing',
          jobType: 'NewConversation',
          notebookId: 'notebook-1',
          notebookTitle: 'Ops Notebook',
          isEnabled: true,
          cronExpression: '0 9 * * *',
          timeZoneId: 'UTC',
          scheduleSummary: 'Daily',
          friendlySchedule: {
            frequency: 'Daily',
            timeOfDay: '09:00',
          },
          createdUtc: '2026-01-01T00:00:00Z',
          updatedUtc: '2026-01-01T00:00:00Z',
          exposeSandboxWireApi: false,
          wireCreateAttributionConversationPerRun: false,
          createdByUserId: 'user-1',
        } as never}
        />
      </ToastProvider>,
    );

    await waitFor(() => expect(api.projects.notebookTemplates.getAssistants).toHaveBeenCalled());
    await user.clear(screen.getByLabelText('Name'));
    await user.type(screen.getByLabelText('Name'), 'Renamed job');
    fireEvent.submit(screen.getByRole('dialog').querySelector('form')!);

    await waitFor(() => {
      expect(scheduledJobsApi.update).toHaveBeenCalledWith(
        'project-1',
        'job-1',
        expect.objectContaining({ name: 'Renamed job' }),
      );
    });
  });
});
