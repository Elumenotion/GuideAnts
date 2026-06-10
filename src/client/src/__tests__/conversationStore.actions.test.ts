import { describe, it, expect, vi, beforeEach } from 'vitest';
import { useConversationStore } from '../store/conversationStore';
import { MessageDto } from '../types/conversation';
import * as apiModule from '../services/api';

function resetStore() {
  const { setState } = useConversationStore as unknown as {
    setState: (state: any) => void;
  };
  setState({ entries: {} });
}

const sampleMessages: MessageDto[] = [
  {
    id: 'm1',
    role: 'user',
    content: 'Hello',
    created: new Date().toISOString(),
    isEdited: false,
  },
  {
    id: 'm2',
    role: 'assistant',
    content: 'Hi there!',
    assistantName: 'Claude',
    created: new Date().toISOString(),
    isEdited: false,
  },
];

describe('conversationStore additional actions', () => {
  const projectId = 'proj-1';
  const notebookId = 'nb-1';
  const conversationId = 'convo-1';

  beforeEach(() => {
    resetStore();
    vi.restoreAllMocks();
  });

  it('sets status to error when fetch throws', async () => {
    const errorMessage = 'Network down';
    vi
      .spyOn(apiModule.api.projects.notebooks.conversations, 'get')
      .mockRejectedValueOnce(new Error(errorMessage));

    await useConversationStore.getState().actions.fetch(
      projectId,
      notebookId,
      conversationId,
    );

    const entry = useConversationStore.getState().entries[conversationId];
    expect(entry.status).toBe('error');
    expect(entry.error).toBe(errorMessage);
  });

  it('sets loading then idle with grouped turns on successful fetch', async () => {
    vi.spyOn(apiModule.api.projects.notebooks.conversations, 'get')
      .mockResolvedValueOnce({ messages: sampleMessages } as any);

    await useConversationStore.getState().actions.fetch(projectId, notebookId, conversationId);

    const entry = useConversationStore.getState().entries[conversationId];
    expect(entry.messages).toEqual(sampleMessages);
    expect(entry.status).toBe('idle');
    expect(entry.turns.length).toBeGreaterThan(0);
    expect(entry.error).toBeUndefined();
  });

  it('invalidate removes an entry from the cache', () => {
    useConversationStore.setState({
      entries: {
        [conversationId]: {
          messages: [],
          turns: [],
          currentTurn: null,
          turnBasedMode: false,
          status: 'idle',
        },
      },
    });

    useConversationStore.getState().actions.invalidate(conversationId);

    expect(useConversationStore.getState().entries).not.toHaveProperty(conversationId);
  });

  it('cancelStream aborts the controller and resets status', () => {
    const abortController = new AbortController();
    useConversationStore.setState({
      entries: {
        [conversationId]: {
          messages: [],
          turns: [],
          currentTurn: null,
          turnBasedMode: false,
          status: 'streaming',
          abortController,
        },
      },
    });

    const abortSpy = vi.spyOn(abortController, 'abort');

    useConversationStore.getState().actions.cancelStream(conversationId);

    expect(abortSpy).toHaveBeenCalledTimes(1);

    const updated = useConversationStore.getState().entries[conversationId];
    expect(updated.abortController).toBeUndefined();
    expect(updated.status).toBe('idle');
  });

  it('select returns empty entry for unknown conversation', () => {
    const entry = useConversationStore.getState().select('missing');
    expect(entry.messages).toEqual([]);
    expect(entry.status).toBe('idle');
    expect(entry.turnBasedMode).toBe(false);
  });

  it('enableTurnBasedMode groups messages into turns', () => {
    useConversationStore.setState({
      entries: {
        [conversationId]: {
          messages: sampleMessages,
          turns: [],
          currentTurn: null,
          turnBasedMode: false,
          status: 'idle',
        },
      },
    });

    useConversationStore.getState().actions.enableTurnBasedMode(conversationId);

    const entry = useConversationStore.getState().entries[conversationId];
    expect(entry.turnBasedMode).toBe(true);
    expect(entry.turns.length).toBeGreaterThan(0);
  });

  it('disableTurnBasedMode clears turn-based flag', () => {
    useConversationStore.setState({
      entries: {
        [conversationId]: {
          messages: sampleMessages,
          turns: [],
          currentTurn: null,
          turnBasedMode: true,
          status: 'idle',
        },
      },
    });

    useConversationStore.getState().actions.disableTurnBasedMode(conversationId);

    expect(useConversationStore.getState().entries[conversationId].turnBasedMode).toBe(false);
  });

  it('groupMessagesByTurns rebuilds turns from flat messages', () => {
    useConversationStore.setState({
      entries: {
        [conversationId]: {
          messages: sampleMessages,
          turns: [],
          currentTurn: null,
          turnBasedMode: false,
          status: 'idle',
        },
      },
    });

    useConversationStore.getState().actions.groupMessagesByTurns(conversationId);

    expect(useConversationStore.getState().entries[conversationId].turns.length).toBe(1);
  });

  it('addMessageToTurn creates and updates current turn', () => {
    useConversationStore.getState().actions.enableTurnBasedMode(conversationId);

    const message = {
      id: 'turn-msg-1',
      turnIndex: 0,
      messageSequence: 0,
      role: 'user' as const,
      content: 'New turn message',
      messageType: 'user_input' as const,
      timestamp: new Date(),
      isEdited: false,
    };

    useConversationStore.getState().actions.addMessageToTurn(conversationId, message);

    const entry = useConversationStore.getState().entries[conversationId];
    expect(entry.currentTurn?.messages).toContainEqual(expect.objectContaining({ id: 'turn-msg-1' }));
    expect(useConversationStore.getState().selectors.getCurrentTurn(conversationId)?.turnIndex).toBe(0);
  });

  it('addToolCallToTurn appends to current turn', () => {
    const turn = {
      turnIndex: 0,
      assistantName: 'Claude',
      messages: [],
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

    const toolCall = {
      id: 'tool-1',
      name: 'search',
      arguments: '{}',
      status: 'executing' as const,
      timestamp: new Date(),
    };

    useConversationStore.getState().actions.addToolCallToTurn(conversationId, toolCall);

    const updated = useConversationStore.getState().entries[conversationId].turns[0];
    expect(updated.toolCalls).toHaveLength(1);
    expect(updated.toolCalls[0].id).toBe('tool-1');
  });

  it('addToolResultToTurn appends to current turn', () => {
    const turn = {
      turnIndex: 0,
      assistantName: 'Claude',
      messages: [],
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

    const result = {
      toolCallId: 'tool-1',
      content: 'done',
      timestamp: new Date(),
    };

    useConversationStore.getState().actions.addToolResultToTurn(conversationId, result);

    expect(useConversationStore.getState().entries[conversationId].turns[0].toolResults).toHaveLength(1);
  });

  it('addFileToTurn appends attached file to current turn', () => {
    const turn = {
      turnIndex: 0,
      assistantName: 'Claude',
      messages: [],
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

    const file = {
      id: 'file-1',
      notebookFileId: 'nf-1',
      fileName: 'notes.pdf',
      fileType: 'document' as const,
      fileSize: 100,
      uploadedAt: new Date(),
      status: 'complete' as const,
    };

    useConversationStore.getState().actions.addFileToTurn(conversationId, file);

    expect(useConversationStore.getState().entries[conversationId].turns[0].attachedFiles).toHaveLength(1);
  });

  it('updateTurnUsage updates token usage on target turn', () => {
    const turn = {
      turnIndex: 0,
      assistantName: 'Claude',
      messages: [],
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

    useConversationStore.getState().actions.updateTurnUsage(conversationId, 0, {
      promptTokens: 10,
      completionTokens: 20,
      totalTokens: 30,
    });

    expect(useConversationStore.getState().entries[conversationId].turns[0].usage.totalTokens).toBe(30);
  });

  it('selectors return defaults for missing conversation', () => {
    expect(useConversationStore.getState().selectors.getTurns('missing')).toEqual([]);
    expect(useConversationStore.getState().selectors.getCurrentTurn('missing')).toBeNull();
    expect(useConversationStore.getState().selectors.isTurnBasedMode('missing')).toBe(false);
  });

  it('unimplemented send/edit/undo actions throw', async () => {
    const { actions } = useConversationStore.getState();
    await expect(actions.send(projectId, notebookId, conversationId, 'hi')).rejects.toThrow('not implemented');
    await expect(actions.edit(projectId, notebookId, conversationId, 'm1', 'x')).rejects.toThrow('not implemented');
    await expect(actions.undo(projectId, notebookId, conversationId)).rejects.toThrow('not implemented');
  });
}); 