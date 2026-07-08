import { describe, it, expect, vi, beforeEach } from 'vitest';

const mockBroadcastAuthExpired = vi.fn();

vi.mock('../authEvents', () => ({
  broadcastAuthExpired: (...args: unknown[]) => mockBroadcastAuthExpired(...args),
}));

vi.mock('../authService', () => ({
  withAuthFetchInit: (init: RequestInit) => ({ ...init, credentials: 'include' }),
  withAuthHeaders: () => new Headers(),
}));

import { scheduledJobsApi } from '../scheduledJobs';

const mockFetch = vi.fn();

describe('scheduledJobsApi', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // @ts-ignore
    global.fetch = mockFetch;
  });

  it('lists scheduled jobs for a project', async () => {
    const jobs = [{ id: 'job-1', name: 'Nightly' }];
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      json: vi.fn().mockResolvedValue(jobs),
    });

    await expect(scheduledJobsApi.list('project-1')).resolves.toEqual(jobs);
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining('/projects/project-1/scheduled-jobs'),
      expect.objectContaining({ credentials: 'include' }),
    );
  });

  it('creates and updates jobs with JSON bodies', async () => {
    const payload = {
      name: 'Daily sync',
      jobType: 'NewConversation' as const,
      notebookId: 'nb-1',
      isEnabled: true,
      timeZoneId: 'UTC',
      schedule: { frequency: 'Daily' as const, timeOfDay: '09:00' },
    };
    const detail = { id: 'job-2', ...payload };
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      json: vi.fn().mockResolvedValue(detail),
    });

    await expect(scheduledJobsApi.create('project-1', payload)).resolves.toEqual(detail);
    expect(mockFetch.mock.calls[0][1].method).toBe('POST');
    expect(mockFetch.mock.calls[0][1].body).toBe(JSON.stringify(payload));

    mockFetch.mockClear();
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      json: vi.fn().mockResolvedValue({ ...detail, name: 'Renamed' }),
    });

    await scheduledJobsApi.update('project-1', 'job-2', { ...payload, name: 'Renamed' });
    expect(mockFetch.mock.calls[0][1].method).toBe('PUT');
  });

  it('deletes jobs and treats 204 responses as void', async () => {
    mockFetch.mockResolvedValue({ ok: true, status: 204, json: vi.fn() });

    await expect(scheduledJobsApi.delete('project-1', 'job-1')).resolves.toBeUndefined();
    expect(mockFetch.mock.calls[0][1].method).toBe('DELETE');
  });

  it('runs jobs immediately and pages run history', async () => {
    mockFetch.mockResolvedValue({ ok: true, status: 202, json: vi.fn() });
    await expect(scheduledJobsApi.runNow('project-1', 'job-1')).resolves.toBeUndefined();

    const runs = { items: [], totalCount: 0, page: 2, pageSize: 10 };
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      json: vi.fn().mockResolvedValue(runs),
    });

    await expect(scheduledJobsApi.listRuns('project-1', 'job-1', 2, 10)).resolves.toEqual(runs);
    expect(mockFetch.mock.calls[1][0]).toContain('page=2&pageSize=10');
  });

  it('fetches a single run detail', async () => {
    const run = { id: 'run-1', status: 'Succeeded' };
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      json: vi.fn().mockResolvedValue(run),
    });

    await expect(scheduledJobsApi.getRun('project-1', 'job-1', 'run-1')).resolves.toEqual(run);
  });

  it('broadcasts auth expiry and surfaces API errors', async () => {
    mockFetch.mockResolvedValue({
      ok: false,
      status: 401,
      statusText: 'Unauthorized',
      json: vi.fn().mockResolvedValue({ message: 'Expired' }),
    });

    await expect(scheduledJobsApi.get('project-1', 'job-1')).rejects.toThrow('Expired');
    expect(mockBroadcastAuthExpired).toHaveBeenCalledWith('Authentication expired.');
  });

  it('falls back to status text when error JSON is invalid', async () => {
    mockFetch.mockResolvedValue({
      ok: false,
      status: 500,
      statusText: 'Server Error',
      json: vi.fn().mockRejectedValue(new Error('bad json')),
    });

    await expect(scheduledJobsApi.get('project-1', 'job-1')).rejects.toThrow('Server Error');
  });
});
