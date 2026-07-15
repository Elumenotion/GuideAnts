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

  it('reloads page 1 when a run is triggered from elsewhere', async () => {
    let listCalls = 0;
    vi.mocked(scheduledJobsApi.listRuns).mockImplementation(async (_projectId, _jobId, page) => {
      listCalls += 1;
      if (listCalls === 1) {
        return {
          items: [
            {
              id: 'run-old',
              triggeredBy: 'Schedule',
              startedUtc: '2026-07-15T17:30:00Z',
              status: 'Failed',
            },
          ],
          totalCount: 1,
          page,
          pageSize: 10,
        };
      }

      expect(page).toBe(1);
      return {
        items: [
          {
            id: 'run-new',
            triggeredBy: 'Manual',
            startedUtc: '2026-07-15T18:00:00Z',
            status: 'Running',
          },
        ],
        totalCount: 2,
        page: 1,
        pageSize: 10,
      };
    });
    vi.mocked(scheduledJobsApi.getRun).mockResolvedValue({
      id: 'run-new',
      triggeredBy: 'Manual',
      startedUtc: '2026-07-15T18:00:00Z',
      status: 'Running',
      standardOutput: null,
      standardError: null,
    });

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

    window.dispatchEvent(new CustomEvent('scheduled-job-run-triggered', { detail: { jobId: 'job-1' } }));

    await waitFor(() => {
      expect(scheduledJobsApi.listRuns.mock.calls.length).toBeGreaterThanOrEqual(2);
    });
    expect(await screen.findByText('Manual')).toBeInTheDocument();
  });

  it('loads runs for the selected page', async () => {
    const user = userEvent.setup();
    vi.mocked(scheduledJobsApi.listRuns).mockImplementation(async (_projectId, _jobId, page) => {
      if (page === 1) {
        return {
          items: [
            {
              id: 'run-page-1',
              triggeredBy: 'PageOne',
              startedUtc: '2026-07-15T17:30:00Z',
              status: 'Failed',
            },
          ],
          totalCount: 20,
          page: 1,
          pageSize: 10,
        };
      }

      return {
        items: [
          {
            id: 'run-page-2',
            triggeredBy: 'PageTwo',
            startedUtc: '2026-07-15T18:00:00Z',
            status: 'Succeeded',
          },
        ],
        totalCount: 20,
        page: 2,
        pageSize: 10,
      };
    });

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

    expect(await screen.findByText('PageOne')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => {
      expect(scheduledJobsApi.listRuns).toHaveBeenCalledWith('project-1', 'job-1', 2, 10);
    });
    expect(await screen.findByText('PageTwo')).toBeInTheDocument();
    expect(screen.queryByText('PageOne')).not.toBeInTheDocument();
  });

  it('keeps polling when the new run is not in the first refresh', async () => {
    let listCalls = 0;
    vi.mocked(scheduledJobsApi.listRuns).mockImplementation(async (_projectId, _jobId, page) => {
      listCalls += 1;
      if (listCalls <= 2) {
        return {
          items: [
            {
              id: 'run-old',
              triggeredBy: 'Schedule',
              startedUtc: '2026-07-15T17:30:00Z',
              status: 'Failed',
            },
          ],
          totalCount: 1,
          page,
          pageSize: 10,
        };
      }

      return {
        items: [
          {
            id: 'run-new',
            triggeredBy: 'Manual',
            startedUtc: '2026-07-15T18:00:00Z',
            status: 'Failed',
            errorMessage: 'Prompt is required.',
          },
        ],
        totalCount: 2,
        page: 1,
        pageSize: 10,
      };
    });
    vi.mocked(scheduledJobsApi.getRun).mockResolvedValue({
      id: 'run-new',
      triggeredBy: 'Manual',
      startedUtc: '2026-07-15T18:00:00Z',
      status: 'Failed',
      errorMessage: 'Prompt is required.',
      standardOutput: null,
      standardError: null,
    });

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

    window.dispatchEvent(new CustomEvent('scheduled-job-run-triggered', { detail: { jobId: 'job-1' } }));

    expect(await screen.findByText('Waiting for run to appear…')).toBeInTheDocument();

    await waitFor(() => {
      expect(scheduledJobsApi.listRuns.mock.calls.length).toBeGreaterThanOrEqual(3);
    });
    expect(await screen.findByText('Manual')).toBeInTheDocument();
    expect(await screen.findByText('Prompt is required.')).toBeInTheDocument();
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
