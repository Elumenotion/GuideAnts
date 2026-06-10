import { vi } from 'vitest';
import type { ReactNode } from 'react';

/** Default stub values returned by useConversation when the stub is enabled. */
export function createStubUseConversationValue(overrides: Record<string, unknown> = {}) {
  return {
    sendMessage: vi.fn(),
    editAssistantMessage: vi.fn(),
    undoLastTurn: vi.fn(),
    setSelectedAssistant: vi.fn(),
    setDraftUserContent: vi.fn(),
    startEditingAssistant: vi.fn(),
    cancelEditingAssistant: vi.fn(),
    refresh: vi.fn(),
    assistants: [],
    conversationStarters: [],
    isEditLoading: false,
    isInitialized: true,
    isCancelling: false,
    isStreaming: false,
    messages: [],
    draftUserContent: '',
    pendingAttachments: [],
    addPendingAttachment: vi.fn(),
    removePendingAttachment: vi.fn(),
    handleStreamingEvent: vi.fn(),
    cancelStream: vi.fn(),
    ...overrides,
  };
}

/** Module shape for vi.mock('../contexts/ConversationContext', () => conversationContextStubModule). */
export const conversationContextStubModule = {
  ConversationProvider: ({ children }: { children: ReactNode }) => children,
  useConversation: () => createStubUseConversationValue(),
};
