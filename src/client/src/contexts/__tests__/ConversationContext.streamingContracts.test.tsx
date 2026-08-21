import { describe, it, expect, beforeEach, vi } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import React from 'react';
import { MemoryRouter } from 'react-router';

vi.unmock('../ConversationContext');

const mockShowToast = vi.fn();
let capturedOnError: ((err: Error) => void) | undefined;
let capturedOnEvent: ((event: { type: string; data: any }) => void) | undefined;

vi.mock('../../services/api', () => ({
  api: {
    projects: {
      notebooks: {
        conversations: {
          get: vi.fn().mockResolvedValue({ messages: [], assistantName: 'Demo Guide' }),
          sendMessageStream: vi.fn().mockImplementation(
            async (_p, _n, _c, _payload, onEvent, onError) => {
              capturedOnEvent = onEvent;
              capturedOnError = onError;
              return Promise.resolve();
            },
          ),
          editMessage: vi.fn().mockResolvedValue({}),
          undoLast: vi.fn().mockResolvedValue({}),
          getAll: vi.fn().mockResolvedValue([]),
          cancelTurn: vi.fn().mockResolvedValue(undefined),
        },
        getNotebook: vi.fn().mockResolvedValue({}),
      },
      notebookTemplates: {
        getAll: vi.fn().mockResolvedValue([]),
        getAssistants: vi.fn().mockResolvedValue([]),
      },
      assistants: {
        getConversationStarters: vi.fn().mockResolvedValue([]),
      },
      folders: {
        getFolderTree: vi.fn().mockResolvedValue({}),
      },
    },
  },
}));

vi.mock('../../utils/notebookAuth', () => ({
  ensureValidTokensForTemplate: vi.fn().mockResolvedValue({ needsAuth: false, missingProviders: [] }),
}));

vi.mock('../conversation/runtimeChecks', () => ({
  checkRuntimeStatus: vi.fn().mockResolvedValue({ state: 'ready' }),
  getRuntimeBlockingMessage: vi.fn().mockReturnValue('Runtime not ready'),
  dispatchRuntimeStatusWindowEvent: vi.fn(),
  getNotebookRuntimeReadyCache: vi.fn(() => new Set<string>()),
  clearNotebookRuntimeReadyCache: vi.fn(),
}));

vi.mock('../NotebookContext', () => ({
  useNotebook: () => ({
    loadNotebookFiles: vi.fn().mockResolvedValue(undefined),
  }),
  NotebookProvider: ({ children }: { children: React.ReactNode }) => children,
}));

vi.mock('../../components/common/Toast', () => ({
  useToast: () => ({ showToast: mockShowToast }),
  ToastProvider: ({ children }: { children: React.ReactNode }) => children,
}));

vi.mock('../../services/userService', () => ({
  userService: {
    getCurrentUser: vi.fn().mockResolvedValue({ id: 'user-1', name: 'Test User', email: 'test@example.com' }),
    getUserById: vi.fn().mockResolvedValue({ id: 'user-1', name: 'Test User', email: 'test@example.com' }),
  },
}));

import { ConversationProvider, useConversation } from '../ConversationContext';
import { api } from '../../services/api';

const defaultAssistants = [
  { name: 'Demo Guide', model: 'gpt-4', avatarUrl: '/a.png', id: 'assistant-1' },
];

function renderProvider() {
  return ({ children }: { children: React.ReactNode }) => (
    <MemoryRouter>
      <ConversationProvider
        projectId="test-project"
        notebookId="test-notebook"
        conversationId="test-conversation"
        guideId="guide-1"
        assistants={defaultAssistants}
      >
        {children}
      </ConversationProvider>
    </MemoryRouter>
  );
}

describe('ConversationContext streaming contracts', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    capturedOnError = undefined;
    capturedOnEvent = undefined;
    vi.mocked(api.projects.notebooks.conversations.get).mockResolvedValue({
      messages: [],
      assistantName: 'Demo Guide',
    } as any);
  });

  it('retains partial content and surfaces streamingError on idle timeout', async () => {
    const { result } = renderHook(() => useConversation(), { wrapper: renderProvider() });
    await waitFor(() => expect(result.current.isInitialized).toBe(true));

    await act(async () => {
      await result.current.sendMessage('hello');
    });

    await act(async () => {
      capturedOnEvent?.({ type: 'assistant_message', data: { contentDelta: 'partial streamed thinking' } });
    });

    await act(async () => {
      capturedOnError?.(Object.assign(
        new Error('The conversation stream stopped sending data. The server is no longer answering this request.'),
        { name: 'StreamIdleTimeoutError' },
      ));
    });

    await waitFor(() => {
      expect(result.current.isStreaming).toBe(false);
      expect(result.current.streamingError).toMatch(/stopped sending data/i);
    });

    const assistant = result.current.messages.find(m => m.role.toLowerCase() === 'assistant');
    expect(assistant?.content).toContain('partial streamed thinking');
    expect(mockShowToast).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Chat Request Failed',
    }));
  });

  it('retains partial content without streamingError on explicit cancel', async () => {
    const { result } = renderHook(() => useConversation(), { wrapper: renderProvider() });
    await waitFor(() => expect(result.current.isInitialized).toBe(true));

    await act(async () => {
      await result.current.sendMessage('hello');
    });

    await act(async () => {
      capturedOnEvent?.({ type: 'turn_created', data: { turnId: 'turn-stop-1' } });
      capturedOnEvent?.({ type: 'assistant_message', data: { contentDelta: 'partial before cancel' } });
    });

    await act(async () => {
      result.current.cancelStream();
    });

    expect(api.projects.notebooks.conversations.cancelTurn).toHaveBeenCalledWith(
      'test-project',
      'test-notebook',
      'test-conversation',
      'turn-stop-1',
    );
    expect(result.current.isCancelling).toBe(true);
    expect(result.current.isStreaming).toBe(true);

    await act(async () => {
      capturedOnEvent?.({ type: 'cancelled', data: { status: 'cancelled' } });
    });

    await waitFor(() => {
      expect(result.current.isStreaming).toBe(false);
      expect(result.current.streamingError).toBeUndefined();
    });

    const assistant = result.current.messages.find(m => m.role.toLowerCase() === 'assistant');
    expect(assistant?.content).toContain('partial before cancel');
    expect(mockShowToast).not.toHaveBeenCalled();
  });

  it('posts cancelTurn when Stop is clicked before turn_created arrives', async () => {
    const { result } = renderHook(() => useConversation(), { wrapper: renderProvider() });
    await waitFor(() => expect(result.current.isInitialized).toBe(true));

    await act(async () => {
      await result.current.sendMessage('hello');
    });

    await act(async () => {
      result.current.cancelStream();
    });

    expect(api.projects.notebooks.conversations.cancelTurn).not.toHaveBeenCalled();
    expect(result.current.isCancelling).toBe(true);
    expect(result.current.isStreaming).toBe(true);

    await act(async () => {
      capturedOnEvent?.({ type: 'turn_created', data: { turnId: 'turn-late' } });
    });

    expect(api.projects.notebooks.conversations.cancelTurn).toHaveBeenCalledWith(
      'test-project',
      'test-notebook',
      'test-conversation',
      'turn-late',
    );
  });
});
