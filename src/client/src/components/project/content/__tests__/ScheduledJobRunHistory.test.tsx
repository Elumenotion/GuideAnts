import { describe, expect, it, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { ScheduledJobRunHistory } from '../ScheduledJobRunHistory';
import { scheduledJobsApi } from '../../../../services/scheduledJobs';

vi.mock('../../../../services/scheduledJobs', () => ({
  scheduledJobsApi: {
    listRuns: vi.fn(),
    getRun: vi.fn(),
    get: vi.fn(),
    runNow: vi.fn(),
  },
}));

describe('ScheduledJobRunHistory', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(scheduledJobsApi.listRuns).mockResolvedValue({
      items: [
        {
          id: 'run-running',
          triggeredBy: 'Schedule',
          startedUtc: '2026-07-15T17:00:00Z',
          status: 'Running',
        },
        {
          id: 'run-failed',
          triggeredBy: 'Schedule',
          startedUtc: '2026-07-15T11:30:00Z',
          status: 'Failed',
          errorMessage: 'Run interrupted by process restart.',
        },
      ],
      totalCount: 2,
      page: 1,
      pageSize: 10,
    });
    vi.mocked(scheduledJobsApi.getRun).mockImplementation(async (_projectId, _jobId, runId) => {
      if (runId === 'run-running') {
        return {
          id: 'run-running',
          triggeredBy: 'Schedule',
          startedUtc: '2026-07-15T17:00:00Z',
          status: 'Running',
          standardOutput: null,
          standardError: null,
        };
      }

      return {
        id: 'run-failed',
        triggeredBy: 'Schedule',
        startedUtc: '2026-07-15T11:30:00Z',
        status: 'Failed',
        errorMessage: 'Run interrupted by process restart.',
        standardOutput: null,
        standardError: null,
      };
    });
  });

  it('does not auto-open empty details when viewing history during a scheduled run', async () => {
    render(
      <MemoryRouter>
        <ScheduledJobRunHistory
          projectId="project-1"
          jobId="job-1"
          notebookId="notebook-1"
          jobType="NewConversation"
          embedded
        />
      </MemoryRouter>,
    );

    await screen.findByText('Running');
    expect(screen.queryByText('Run details —')).not.toBeInTheDocument();
    expect(screen.queryByText('(empty)')).not.toBeInTheDocument();
  });

  it('keeps the user-selected run visible while another run is in progress', async () => {
    const user = userEvent.setup();

    render(
      <MemoryRouter>
        <ScheduledJobRunHistory
          projectId="project-1"
          jobId="job-1"
          notebookId="notebook-1"
          jobType="NewConversation"
          embedded
        />
      </MemoryRouter>,
    );

    await screen.findByText('Failed');
    await user.click(screen.getByRole('button', { name: 'View output' }));

    expect(await screen.findByText('Run interrupted by process restart.')).toBeInTheDocument();

    await waitFor(() => {
      expect(scheduledJobsApi.getRun).toHaveBeenCalledWith('project-1', 'job-1', 'run-failed');
    });

    await waitFor(() => {
      expect(scheduledJobsApi.getRun).not.toHaveBeenCalledWith('project-1', 'job-1', 'run-running');
    }, { timeout: 2000 });
  });

  it('shows failed run errors instead of empty stdout panels for conversation jobs', async () => {
    const user = userEvent.setup();

    render(
      <MemoryRouter>
        <ScheduledJobRunHistory
          projectId="project-1"
          jobId="job-1"
          notebookId="notebook-1"
          jobType="NewConversation"
          embedded
        />
      </MemoryRouter>,
    );

    await user.click(await screen.findByRole('button', { name: 'View output' }));

    expect(await screen.findByText('Run interrupted by process restart.')).toBeInTheDocument();
    expect(screen.queryByText('stdout')).not.toBeInTheDocument();
    expect(screen.queryByText('(empty)')).not.toBeInTheDocument();
  });
});
