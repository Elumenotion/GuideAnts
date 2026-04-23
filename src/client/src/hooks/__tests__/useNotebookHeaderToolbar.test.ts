import { renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
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
  it('loads aggregate toolbar data', async () => {
    const { result } = renderHook(() => useNotebookHeaderToolbar('n1', 'c1'));
    await waitFor(() => expect(result.current.isLoading).toBe(false));
    expect(api.notebooks.headerToolbar).toHaveBeenCalledWith('n1', 'c1');
    expect(result.current.data?.chat.summary).toBe('Chat ready');
  });
});
