import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useConversationListPolling } from '../useConversationListPolling';
import { api } from '../../services/api';
import type { NotebookConversationDto } from '../../types/notebook';

vi.mock('../../services/api', () => ({
  api: {
    projects: {
      notebooks: {
        conversations: {
          getAll: vi.fn(),
        },
      },
    },
  },
}));

describe('useConversationListPolling', () => {
  const projectId = 'proj-1';
  const notebookId = 'nb-1';

  const mockConversations: NotebookConversationDto[] = [
    { id: 'c1', title: 'First', created: '2024-01-01T00:00:00Z' },
    { id: 'c2', title: 'Second', created: '2024-01-02T00:00:00Z' },
  ];

  beforeEach(() => {
    vi.clearAllMocks();
    (api.projects.notebooks.conversations.getAll as ReturnType<typeof vi.fn>).mockResolvedValue(
      mockConversations
    );
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('fetches conversations on mount', async () => {
    const { result } = renderHook(() =>
      useConversationListPolling({ projectId, notebookId, pollInterval: 5000 })
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(result.current.conversations).toEqual(mockConversations);
    expect(api.projects.notebooks.conversations.getAll).toHaveBeenCalledWith(
      projectId,
      notebookId
    );
    expect(result.current.lastUpdated).toBeInstanceOf(Date);
    expect(result.current.error).toBeNull();
    expect(result.current.isLoading).toBe(false);
  });

  it('polls on interval', async () => {
    vi.useFakeTimers();

    renderHook(() =>
      useConversationListPolling({ projectId, notebookId, pollInterval: 1000 })
    );

    await act(async () => {
      await Promise.resolve();
    });

    const initialCalls = (api.projects.notebooks.conversations.getAll as ReturnType<typeof vi.fn>)
      .mock.calls.length;

    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });

    expect(
      (api.projects.notebooks.conversations.getAll as ReturnType<typeof vi.fn>).mock.calls.length
    ).toBeGreaterThan(initialCalls);
  });

  it('does not poll when disabled', async () => {
    renderHook(() =>
      useConversationListPolling({
        projectId,
        notebookId,
        enabled: false,
      })
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(api.projects.notebooks.conversations.getAll).not.toHaveBeenCalled();
  });

  it('handles fetch errors', async () => {
    (api.projects.notebooks.conversations.getAll as ReturnType<typeof vi.fn>).mockRejectedValue(
      new Error('Network failure')
    );

    const { result } = renderHook(() =>
      useConversationListPolling({ projectId, notebookId })
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(result.current.error).toBe('Network failure');
    expect(result.current.isLoading).toBe(false);
  });

  it('refresh triggers a new fetch', async () => {
    const { result } = renderHook(() =>
      useConversationListPolling({ projectId, notebookId })
    );

    await act(async () => {
      await Promise.resolve();
    });

    const callsBefore = (api.projects.notebooks.conversations.getAll as ReturnType<typeof vi.fn>)
      .mock.calls.length;

    await act(async () => {
      result.current.refresh();
      await Promise.resolve();
    });

    expect(
      (api.projects.notebooks.conversations.getAll as ReturnType<typeof vi.fn>).mock.calls.length
    ).toBeGreaterThan(callsBefore);
  });
});
