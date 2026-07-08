import { describe, expect, it, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '@testing-library/react';
import { ScheduledJobRunHistoryDialog } from '../ScheduledJobRunHistoryDialog';
import { scheduledJobsApi } from '../../../../services/scheduledJobs';
import type { ProjectScheduledJobSummaryDto } from '../../../../types/scheduledJob';

vi.mock('../../../../services/scheduledJobs', () => ({
  scheduledJobsApi: {
    listRuns: vi.fn(),
    getRun: vi.fn(),
  },
}));

const job: ProjectScheduledJobSummaryDto = {
  id: 'job-1',
  name: 'Morning briefing',
  jobType: 'NewConversation',
  notebookId: 'notebook-1',
  notebookTitle: 'Ops Notebook',
  isEnabled: true,
  cronExpression: '0 9 * * *',
  timeZoneId: 'UTC',
  scheduleSummary: 'Daily at 09:00 (UTC)',
  friendlySchedule: {
    frequency: 'Daily',
    timeOfDay: '09:00',
  },
  createdUtc: '2026-01-01T00:00:00Z',
  updatedUtc: '2026-01-01T00:00:00Z',
};

describe('ScheduledJobRunHistoryDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders nothing when closed', () => {
    const { container } = render(
      <ScheduledJobRunHistoryDialog
        projectId="project-1"
        job={job}
        isOpen={false}
        onClose={vi.fn()}
      />,
    );

    expect(container).toBeEmptyDOMElement();
    expect(scheduledJobsApi.listRuns).not.toHaveBeenCalled();
  });

  it('shows empty state when no runs exist', async () => {
    vi.mocked(scheduledJobsApi.listRuns).mockResolvedValue({
      items: [],
      totalCount: 0,
      page: 1,
      pageSize: 10,
    });

    render(
      <ScheduledJobRunHistoryDialog
        projectId="project-1"
        job={job}
        isOpen
        onClose={vi.fn()}
      />,
    );

    expect(await screen.findByRole('dialog')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: /Run history — Morning briefing/i })).toBeInTheDocument();
    expect(screen.getByText('Daily at 09:00 (UTC)')).toBeInTheDocument();
    expect(await screen.findByText('No runs recorded yet.')).toBeInTheDocument();
    expect(scheduledJobsApi.listRuns).toHaveBeenCalledWith('project-1', 'job-1', 1, 10);
  });

  it('lists runs and opens run details', async () => {
    const user = userEvent.setup();
    vi.mocked(scheduledJobsApi.listRuns).mockResolvedValue({
      items: [
        {
          id: 'run-1',
          triggeredBy: 'Schedule',
          startedUtc: '2026-01-02T09:00:00Z',
          status: 'Succeeded',
        },
      ],
      totalCount: 1,
      page: 1,
      pageSize: 10,
    });
    vi.mocked(scheduledJobsApi.getRun).mockResolvedValue({
      id: 'run-1',
      triggeredBy: 'Schedule',
      startedUtc: '2026-01-02T09:00:00Z',
      status: 'Succeeded',
      standardOutput: 'hello stdout',
      standardError: '',
      createdConversationId: 'conv-1',
    });

    const onClose = vi.fn();

    render(
      <ScheduledJobRunHistoryDialog
        projectId="project-1"
        job={job}
        isOpen
        onClose={onClose}
      />,
    );

    expect(await screen.findByText('Succeeded')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'View output' }));

    await waitFor(() => {
      expect(scheduledJobsApi.getRun).toHaveBeenCalledWith('project-1', 'job-1', 'run-1');
    });
    expect(await screen.findByText('hello stdout')).toBeInTheDocument();
    expect(screen.getByText('conv-1')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Close run history' }));
    expect(onClose).toHaveBeenCalled();
  });
});
