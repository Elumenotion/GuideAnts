import React, { FC } from 'react';
import { vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { ConversationProvider } from '../contexts/ConversationContext';
import { ToastProvider } from '../components/common/Toast';
import { MessageDto } from '../types/conversation';
import { waitFor } from '@testing-library/react';
import { expect } from 'vitest';

// Centralised holder for streaming callbacks captured by the sendMessageStream mock
export const mockStream = {
  onEvent: undefined as undefined | ((event: { type: string; data: any }) => void),
  onError: undefined as undefined | ((err: any) => void),
  onComplete: undefined as undefined | (() => void),
};

// Mock external dependencies that ConversationContext uses
vi.mock('../services/api', () => ({
  api: {
    projects: {
      notebooks: {
        conversations: {
          get: vi.fn().mockResolvedValue({
            messages: [
              {
                id: 'msg-1753388498852-495htgtar',
                role: 'user',
                content: 'Hello',
                created: '2025-07-24T20:21:38.852Z',
                isEdited: false,
              },
              {
                id: 'msg-1753388498852-79z7hzs27',
                role: 'assistant',
                content: 'Hi there!',
                created: '2025-07-24T20:21:38.852Z',
                isEdited: false,
                assistantName: 'Test Assistant',
              },
            ],
          }),
          // Streaming mock: capture callbacks for use in tests
          sendMessageStream: vi.fn().mockImplementation((_projectId, _notebookId, _conversationId, _payload, onEvent, onError, onComplete, _signal) => {
            mockStream.onEvent = onEvent;
            mockStream.onError = onError;
            mockStream.onComplete = onComplete;
            return Promise.resolve();
          }),
          editMessage: vi.fn().mockResolvedValue({}),
          undoLast: vi.fn().mockResolvedValue({}),
          getAll: vi.fn().mockResolvedValue([]),
        },
        getNotebook: vi.fn().mockResolvedValue({}),
      },
      notebookTemplates: {
        getAll: vi.fn().mockResolvedValue([
          {
            id: 'template-1',
            defaultAssistant: 'Claude',
            conversationStarters: ['Hello', 'How can I help?'],
          },
        ]),
        getAssistants: vi.fn().mockResolvedValue([
          { name: 'Claude', modelDeploymentId: 'claude-3' },
          { name: 'GPT-4', modelDeploymentId: 'gpt-4' },
        ]),
      },
      folders: {
        getFolderTree: vi.fn().mockResolvedValue({}),
      },
    },
  },
}));

// Ensure tests that import via a different relative path receive the same mock
vi.mock('../../services/api', () => vi.importActual('../services/api'));

vi.mock('../services/userService', () => ({
  userService: {
    getCurrentUser: vi.fn().mockResolvedValue({
      id: 'user-123',
      name: 'Test User',
      email: 'test@example.com',
    }),
    getUserById: vi.fn().mockResolvedValue({
      id: 'user-123',
      name: 'Test User',
      email: 'test@example.com',
    }),
  },
}));

vi.mock('../services/authService', () => ({
  authService: {
    getAccessToken: vi.fn().mockResolvedValue('mock-token'),
  },
}));

vi.mock('../contexts/NotebookContext', async (importOriginal) => {
  const actual = await importOriginal() as any;
  return {
    ...actual,
    useNotebook: () => ({
      loadNotebookFiles: vi.fn().mockResolvedValue(undefined),
    }),
  };
});

vi.mock('../components/common/Toast', () => ({
  useToast: () => ({
    showToast: vi.fn(),
  }),
  ToastProvider: ({ children }: { children: React.ReactNode }) => children,
}));

// Test data factories
export const createMockMessage = (overrides: Partial<MessageDto> = {}): MessageDto => ({
  id: `msg-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
  role: 'user',
  content: 'Test message content',
  created: new Date().toISOString(),
  isEdited: false,
  ...overrides,
});

export const createMockAssistantMessage = (overrides: Partial<MessageDto> = {}): MessageDto => ({
  ...createMockMessage(),
  role: 'assistant',
  assistantName: 'Test Assistant',
  ...overrides,
});

export const createMockToolCall = (overrides: any = {}) => ({
  id: `tool-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
  name: 'test_function',
  arguments: '{"param": "value"}',
  status: 'executing' as const,
  timestamp: new Date(),
  ...overrides,
});

export const createMockToolResult = (toolCallId: string, overrides: any = {}) => ({
  toolCallId,
  content: 'Tool execution result',
  isError: false,
  timestamp: new Date(),
  ...overrides,
});

export const createMockStreamingTurn = (overrides: any = {}) => ({
  id: `turn-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
  toolCalls: [],
  toolResults: [],
  assistantStepChunks: [],
  startTime: new Date(),
  isComplete: false,
  ...overrides,
});

// Wrapper component for testing ConversationProvider
export function renderConversationProvider(options: {
  projectId?: string;
  notebookId?: string;
  conversationId?: string;
  notebookTemplateId?: string;
  onPreviewFile?: (fileId: string) => void;
} = {}) {
  const {
    projectId = 'test-project',
    notebookId = 'test-notebook',
    conversationId = 'test-conversation',
    notebookTemplateId,
    onPreviewFile,
  } = options;

  const wrapper: FC<{ children: React.ReactNode }> = ({ children }) => {
    return React.createElement(ToastProvider, null,
      React.createElement(MemoryRouter, null,
        React.createElement(ConversationProvider, {
          projectId,
          notebookId,
          conversationId,
          guideId: notebookTemplateId,
          onPreviewFile,
          children,
        })
      )
    );
  };

  return wrapper;
}

// Mock SSE event sequences for testing streaming
export const createMockStreamingEvents = () => ({
  tokenSequence: [
    { type: 'token', data: { contentDelta: 'Hel' } },
    { type: 'token', data: { contentDelta: 'lo ' } },
    { type: 'token', data: { contentDelta: 'world!' } },
  ],
  
  assistantMessageWithTools: [
    {
      type: 'assistant_message',
      data: {
        content: 'I need to use a tool to help you.',
        tool_calls: [
          {
            id: 'tool-123',
            function: { name: 'search_web', arguments: '{"query": "test"}' },
          },
        ],
        timestamp: new Date().toISOString(),
      },
    },
  ],
  
  toolResult: [
    {
      type: 'tool_result',
      data: {
        toolCallId: 'tool-123',
        functionName: 'search_web',
        content: 'Search results: ...',
        timestamp: new Date().toISOString(),
      },
    },
  ],
  
  finalResponse: [
    {
      type: 'assistant_message',
      data: {
        content: 'Based on the search results, here is my response.',
        timestamp: new Date().toISOString(),
      },
    },
  ],
  
  complete: [{ type: 'complete', data: {} }],
  
  error: [
    {
      type: 'error',
      data: {
        message: 'Something went wrong during the conversation',
      },
    },
  ],
});

// Helper to simulate streaming events using the captured callbacks
export const emitStreamingEvents = async (events: Array<{ type: string; data: any }>) => {
  for (const e of events) {
    mockStream.onEvent?.(e);
    await Promise.resolve();
  }
};

// Initial state factory (unused by provider tests but kept for completeness)
export const createInitialState = (overrides: any = {}) => ({
  messages: [],
  isStreaming: false,
  selectedAssistant: null,
  draftUserContent: '',
  editingAssistantId: undefined,
  currentTurn: undefined,
  streamingProgress: {
    currentPhase: 'complete' as const,
    completedSteps: 0,
    totalSteps: 0,
  },
  isStreamingToolCalls: false,
  isStreamingThinking: false,
  assistants: [],
  conversationStarters: [],
  editError: undefined,
  isEditLoading: false,
  streamingError: undefined,
  _renderCounter: 0,
  _isInitialized: false,
  _justCompletedStreaming: false,
  _isCancelling: false,
  pendingAttachments: [],
  userProfiles: {},
  ...overrides,
});

// Helper: advance timers (for the 10 ms refresh) and flush microtasks
export const flushProviderQueues = async () => {
  await new Promise(res => setTimeout(res, 20));
  await Promise.resolve();
};

// Helper to ensure provider has fully initialized **and** the refresh ran
export const waitForInitialLoad = async (result: any) => {
  await waitFor(() => expect(result.current.isInitialized).toBe(true));
  await flushProviderQueues();
};