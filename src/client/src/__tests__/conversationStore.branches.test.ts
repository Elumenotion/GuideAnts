import { describe, it, expect, vi, beforeEach } from 'vitest';
import { useConversationStore } from '../store/conversationStore';
import { MessageDto } from '../types/conversation';
import * as apiModule from '../services/api';

function resetStore() {
  const { setState } = useConversationStore as unknown as {
    setState: (state: { entries: Record<string, never> }) => void;
  };
  setState({ entries: {} });
}

describe('conversationStore branch coverage', () => {
  const conversationId = 'convo-branches';

  beforeEach(() => {
    resetStore();
    vi.restoreAllMocks();
  });

  it('groups legacy file references, attachments, and assistant-only messages', async () => {
    const messages: MessageDto[] = [
      {
        id: 'file-ref',
        role: 'user',
        content: 'diagram.png',
        messageContentType: 'FileReference',
        attachedNotebookFileId: 'nf-1',
        created: new Date().toISOString(),
        isEdited: false,
      },
      {
        id: 'assistant-first',
        role: 'assistant',
        content: 'Hello before any user turn',
        created: new Date().toISOString(),
        isEdited: false,
      },
      {
        id: 'user-1',
        role: 'user',
        content: 'Question',
        attachments: [
          {
            notebookFileId: 'nf-2',
            fileName: 'notes.pdf',
            fileType: 'document',
            fileSize: 42,
          },
        ],
        created: new Date().toISOString(),
        isEdited: false,
      },
      {
        id: 'user-legacy',
        role: 'user',
        content: 'Legacy attach',
        attachedNotebookFileId: 'nf-legacy',
        created: new Date().toISOString(),
        isEdited: false,
      },
    ];

    vi.spyOn(apiModule.api.projects.notebooks.conversations, 'get')
      .mockResolvedValueOnce({ messages } as never);

    await useConversationStore.getState().actions.fetch('p', 'n', conversationId);

    const entry = useConversationStore.getState().entries[conversationId];
    expect(entry.turns.length).toBeGreaterThan(0);
    expect(entry.turns.some((turn) => turn.attachedFiles.length > 0)).toBe(true);
  });

  it('uses fallback error message when fetch rejects a non-Error value', async () => {
    vi.spyOn(apiModule.api.projects.notebooks.conversations, 'get')
      .mockRejectedValueOnce('plain failure');

    await useConversationStore.getState().actions.fetch('p', 'n', conversationId);

    const entry = useConversationStore.getState().entries[conversationId];
    expect(entry.status).toBe('error');
    expect(entry.error).toBe('Failed to load conversation');
  });

  it('handles fetch responses with missing messages array', async () => {
    vi.spyOn(apiModule.api.projects.notebooks.conversations, 'get')
      .mockResolvedValueOnce({} as never);

    await useConversationStore.getState().actions.fetch('p', 'n', conversationId);

    expect(useConversationStore.getState().entries[conversationId].messages).toEqual([]);
  });

  it('cancelStream preserves non-streaming status', () => {
    useConversationStore.setState({
      entries: {
        [conversationId]: {
          messages: [],
          turns: [],
          currentTurn: null,
          turnBasedMode: false,
          status: 'loading',
          abortController: new AbortController(),
        },
      },
    });

    useConversationStore.getState().actions.cancelStream(conversationId);

    expect(useConversationStore.getState().entries[conversationId].status).toBe('loading');
  });

  it('addMessageToTurn appends to an existing turn', () => {
    const existingMessage = {
      id: 'msg-1',
      turnIndex: 0,
      messageSequence: 0,
      role: 'user' as const,
      content: 'First',
      messageType: 'user_input' as const,
      timestamp: new Date(),
      isEdited: false,
    };
    const turn = {
      turnIndex: 0,
      assistantName: 'assistant',
      messages: [existingMessage],
      toolCalls: [],
      toolResults: [],
      attachedFiles: [],
      usage: { promptTokens: 0, completionTokens: 0, totalTokens: 0 },
      timestamp: new Date(),
      isStreaming: false,
    };

    useConversationStore.setState({
      entries: {
        [conversationId]: {
          messages: [],
          turns: [turn],
          currentTurn: turn,
          turnBasedMode: true,
          status: 'idle',
        },
      },
    });

    useConversationStore.getState().actions.addMessageToTurn(conversationId, {
      id: 'msg-2',
      turnIndex: 0,
      messageSequence: 1,
      role: 'assistant',
      content: 'Second',
      messageType: 'final_response',
      timestamp: new Date(),
      isEdited: false,
    });

    const updatedTurn = useConversationStore.getState().entries[conversationId].turns[0];
    expect(updatedTurn.messages).toHaveLength(2);
    expect(updatedTurn.messages[1].content).toBe('Second');
  });

  it('ignores tool and file mutations when there is no current turn', () => {
    useConversationStore.getState().actions.addToolCallToTurn(conversationId, {
      id: 'tool-1',
      name: 'search',
      arguments: '{}',
      status: 'executing',
      timestamp: new Date(),
    });
    useConversationStore.getState().actions.addToolResultToTurn(conversationId, {
      toolCallId: 'tool-1',
      content: 'done',
      timestamp: new Date(),
    });
    useConversationStore.getState().actions.addFileToTurn(conversationId, {
      id: 'file-1',
      notebookFileId: 'nf-1',
      fileName: 'notes.txt',
      fileType: 'other',
      fileSize: 1,
      uploadedAt: new Date(),
      status: 'complete',
    });

    const entry = useConversationStore.getState().entries[conversationId];
    expect(entry.turns).toEqual([]);
    expect(entry.currentTurn).toBeNull();
  });

  it('no-ops updateTurnUsage when turn index is missing', () => {
    const turn = {
      turnIndex: 0,
      assistantName: 'assistant',
      messages: [],
      toolCalls: [],
      toolResults: [],
      attachedFiles: [],
      usage: { promptTokens: 1, completionTokens: 2, totalTokens: 3 },
      timestamp: new Date(),
      isStreaming: false,
    };

    useConversationStore.setState({
      entries: {
        [conversationId]: {
          messages: [],
          turns: [turn],
          currentTurn: turn,
          turnBasedMode: true,
          status: 'idle',
        },
      },
    });

    useConversationStore.getState().actions.updateTurnUsage(conversationId, 99, {
      promptTokens: 9,
      completionTokens: 9,
      totalTokens: 18,
    });

    expect(useConversationStore.getState().entries[conversationId].turns[0].usage.totalTokens).toBe(3);
  });
});
