import { renderHook, waitFor } from '@testing-library/react';
import { act } from 'react';
import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';
import { useNotebookHeaderToolbar } from '../useNotebookHeaderToolbar';
import { api } from '../../services/api';

const toastMocks = vi.hoisted(() => ({
  showToast: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  api: {
    notebooks: {
      headerToolbar: vi.fn(async () => ({
        generatedUtc: new Date().toISOString(),
        chat: {
          status: 'ready',
          summary: 'Chat ready',
          conversationId: 'c1',
          selectedAssistantName: 'assistant',
          effectiveModelId: 'gpt-5-mini',
          effectiveModelDisplayName: 'GPT-5 mini',
          effectiveProvider: 'azure-openai',
          overrideAllChatModels: false,
          supportsLocalRuntimePower: false,
          localRuntimeOn: false,
          modelOptions: [],
          blockers: [],
          inProgressOperationId: null,
          inProgressState: null,
        },
        services: [],
      })),
    },
  },
}));

vi.mock('../../components/common/Toast', () => ({
  useToast: () => toastMocks,
}));

describe('useNotebookHeaderToolbar', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('loads aggregate toolbar data', async () => {
    const { result } = renderHook(() => useNotebookHeaderToolbar('n1', 'c1'));
    await waitFor(() => expect(result.current.isLoading).toBe(false));
    expect(api.notebooks.headerToolbar).toHaveBeenCalledWith('n1', 'c1');
    expect(result.current.data?.chat.summary).toBe('Chat ready');
  });

  it('does not keep polling when there is no active operation', async () => {
    renderHook(() => useNotebookHeaderToolbar('n1', 'c1'));
    await waitFor(() => expect(api.notebooks.headerToolbar).toHaveBeenCalledTimes(1));

    await act(async () => {
      vi.advanceTimersByTime(60_000);
    });

    expect(api.notebooks.headerToolbar).toHaveBeenCalledTimes(1);
  });

  it('polls while a toolbar operation is in progress', async () => {
    vi.mocked(api.notebooks.headerToolbar).mockResolvedValue({
      generatedUtc: new Date().toISOString(),
      chat: {
        status: 'degraded',
        summary: 'Loading local runtime',
        conversationId: 'c1',
        selectedAssistantName: 'assistant',
        effectiveModelId: 'llama-1',
        effectiveModelDisplayName: 'Llama 1',
        effectiveProvider: 'llama-cpp',
        overrideAllChatModels: false,
        supportsLocalRuntimePower: true,
        localRuntimeOn: false,
        modelOptions: [],
        blockers: [],
        inProgressOperationId: 'op-1',
        inProgressState: 'loading',
      },
      services: [],
    } as any);

    renderHook(() => useNotebookHeaderToolbar('n1', 'c1'));
    await waitFor(() => expect(api.notebooks.headerToolbar).toHaveBeenCalledTimes(1));

    await act(async () => {
      vi.advanceTimersByTime(2_100);
    });

    await waitFor(() => expect(api.notebooks.headerToolbar).toHaveBeenCalledTimes(2));
  });
});
