import { describe, expect, it } from 'vitest';
import { reducer, initialState } from '../reducer';
import type { ExtendedConversationState } from '../types';
import type { MessageDto } from '../../../types/conversation';

function withTurn(state: ExtendedConversationState): ExtendedConversationState {
  return {
    ...state,
    currentTurn: {
      id: 'turn-1',
      toolCalls: [{
        id: 'tool-1',
        name: 'search',
        arguments: '{}',
        status: 'running',
        timestamp: new Date('2026-01-01T00:00:00Z'),
      }],
      toolResults: [],
      assistantStepChunks: [{ content: 'step chunk', timestamp: new Date() }],
      startTime: new Date(),
      isComplete: false,
    },
  };
}

describe('conversation reducer', () => {
  it('replaces, appends, and updates messages', () => {
    const message: MessageDto = {
      id: 'msg-1',
      role: 'user',
      content: 'Hello',
      created: '2026-01-01T00:00:00Z',
      isEdited: false,
    };

    const replaced = reducer(initialState, { type: 'SET_MESSAGES', payload: [message] });
    expect(replaced.messages).toHaveLength(1);

    const added = reducer(replaced, {
      type: 'ADD_MESSAGE',
      payload: { ...message, id: 'msg-2', content: 'Follow-up' },
    });
    expect(added.messages).toHaveLength(2);

    const updated = reducer(added, {
      type: 'UPDATE_MESSAGE',
      payload: { id: 'msg-2', updates: { content: 'Edited follow-up' } },
    });
    expect(updated.messages[1]?.content).toBe('Edited follow-up');
  });

  it('removes the last assistant turn up to the preceding user message', () => {
    const messages: MessageDto[] = [
      { id: 'u1', role: 'user', content: 'Q', created: '2026-01-01T00:00:00Z', isEdited: false },
      { id: 'a1', role: 'assistant', content: 'A', created: '2026-01-01T00:00:01Z', isEdited: false },
      { id: 't1', role: 'tool', content: 'tool', created: '2026-01-01T00:00:02Z', isEdited: false },
      { id: 'u2', role: 'user', content: 'Follow-up', created: '2026-01-01T00:00:03Z', isEdited: false },
    ];

    const next = reducer({ ...initialState, messages }, { type: 'REMOVE_LAST_TURN' });
    expect(next.messages.map((m) => m.id)).toEqual(['u1', 'a1', 't1']);
  });

  it('updates simple conversation metadata flags', () => {
    let state = reducer(initialState, { type: 'SET_STREAMING', payload: true });
    expect(state.isStreaming).toBe(true);

    state = reducer(state, { type: 'SET_ASSISTANT', payload: 'Guide' });
    expect(state.selectedAssistant).toBe('Guide');

    state = reducer(state, { type: 'SET_DRAFT', payload: 'draft text' });
    expect(state.draftUserContent).toBe('draft text');

    state = reducer(state, { type: 'SET_EDIT_ERROR', payload: 'edit failed' });
    expect(state.editError).toBe('edit failed');

    state = reducer(state, { type: 'SET_EDIT_LOADING', payload: true });
    expect(state.isEditLoading).toBe(true);

    state = reducer(state, { type: 'SET_ASSISTANTS', payload: [{ name: 'Guide' } as never] });
    expect(state.assistants).toHaveLength(1);

    state = reducer(state, { type: 'SET_CONVERSATION_STARTERS', payload: ['Hi'] });
    expect(state.conversationStarters).toEqual(['Hi']);

    state = reducer(state, { type: 'SET_INITIALIZED', payload: true });
    expect(state._isInitialized).toBe(true);

    state = reducer(state, { type: 'SET_CANCELLING', payload: true });
    expect(state._isCancelling).toBe(true);

    state = reducer(state, { type: 'SET_UNDOING', payload: true });
    expect(state._isUndoing).toBe(true);

    const template = { id: 'template-1', name: 'Default' } as never;
    state = reducer(state, { type: 'SET_NOTEBOOK_TEMPLATE', payload: template });
    expect(state.notebookTemplate).toEqual(template);

    state = reducer(state, { type: 'SET_STREAMING_ERROR', payload: 'stream failed' });
    expect(state.streamingError).toBe('stream failed');
  });

  it('preserves an existing turn when streaming mode changes to observing', () => {
    const existingTurn = withTurn(initialState).currentTurn!;
    const next = reducer(
      { ...initialState, currentTurn: existingTurn },
      { type: 'SET_STREAMING_MODE', payload: { mode: 'observing' } }
    );
    expect(next.currentTurn?.id).toBe(existingTurn.id);
  });

  it('manages pending attachments', () => {
    const attachment = {
      notebookFileId: 'file-1',
      fileName: 'notes.md',
      relativePath: 'notes.md',
      contentType: 'text/markdown',
    };

    const added = reducer(initialState, { type: 'ADD_ATTACHMENT', payload: attachment });
    expect(added.pendingAttachments).toHaveLength(1);

    const removed = reducer(added, { type: 'REMOVE_ATTACHMENT', payload: 'file-1' });
    expect(removed.pendingAttachments).toHaveLength(0);

    const cleared = reducer(added, { type: 'CLEAR_ATTACHMENTS' });
    expect(cleared.pendingAttachments).toEqual([]);
  });

  it('replaces pending attachments and skips duplicate ids or normalized paths', () => {
    const first = {
      notebookFileId: 'file-1',
      relativePath: 'Data/report.csv',
      fileName: 'report.csv',
      uploadType: 'text',
    };
    const withFirst = reducer(initialState, { type: 'ADD_ATTACHMENT', payload: first });
    const duplicateId = reducer(withFirst, {
      type: 'ADD_ATTACHMENT',
      payload: { ...first, relativePath: 'other/report.csv' },
    });
    const duplicatePath = reducer(duplicateId, {
      type: 'ADD_ATTACHMENT',
      payload: {
        ...first,
        notebookFileId: 'path:Data/report.csv',
        relativePath: '\\data\\REPORT.csv',
      },
    });

    expect(duplicatePath.pendingAttachments).toHaveLength(1);

    const replacement = reducer(duplicatePath, {
      type: 'SET_ATTACHMENTS',
      payload: [{
        notebookFileId: 'file-2',
        fileName: 'audio.mp3',
        uploadType: 'audio',
      }],
    });
    expect(replacement.pendingAttachments).toEqual([{
      notebookFileId: 'file-2',
      fileName: 'audio.mp3',
      uploadType: 'audio',
    }]);
  });

  it('records tool errors on the active turn', () => {
    const state = withTurn(initialState);
    const timestamp = new Date('2026-01-01T00:00:00Z');

    const next = reducer(state, {
      type: 'ADD_TOOL_ERROR',
      payload: { toolCallId: 'tool-1', content: 'tool failed', timestamp },
    });

    expect(next.currentTurn?.toolCalls[0]?.status).toBe('error');
    expect(next.currentTurn?.toolResults[0]).toMatchObject({
      toolCallId: 'tool-1',
      content: 'tool failed',
      isError: true,
    });
  });

  it('stores final responses and completes streaming turns', () => {
    const streamingMessage: MessageDto = {
      id: 'streaming-1',
      role: 'assistant',
      content: '',
      created: '2026-01-01T00:00:00Z',
      isEdited: false,
      assistantName: 'Guide',
    };

    const state = withTurn({
      ...initialState,
      selectedAssistant: 'Guide',
      streamingMode: 'observing',
      messages: [streamingMessage],
    });

    const withFinal = reducer(state, {
      type: 'ADD_FINAL_RESPONSE',
      payload: {
        role: 'assistant',
        content: 'Final answer',
        timestamp: new Date('2026-01-01T00:00:01Z'),
      },
    });
    expect(withFinal.currentTurn?.finalResponse?.content).toBe('Final answer');

    const completed = reducer(withFinal, { type: 'COMPLETE_STREAMING_TURN' });
    expect(completed.currentTurn).toBeUndefined();
    expect(completed.streamingMode).toBe('at-rest');
    expect(completed._justCompletedStreaming).toBe(true);
    expect(completed.messages.some((m) => m.id.startsWith('msg-'))).toBe(true);
  });

  it('switches streaming mode back to at-rest without creating a new turn', () => {
    const observing = reducer(initialState, {
      type: 'SET_STREAMING_MODE',
      payload: { mode: 'observing', activeUser: { userId: 'u1', userName: 'User' } },
    });
    const atRest = reducer(observing, {
      type: 'SET_STREAMING_MODE',
      payload: { mode: 'at-rest' },
    });
    expect(atRest.streamingMode).toBe('at-rest');
    expect(atRest.isStreaming).toBe(false);
  });

  it('creates a turn when entering observing mode without one', () => {
    const next = reducer(initialState, {
      type: 'SET_STREAMING_MODE',
      payload: { mode: 'observing', activeUser: { userId: 'u1', userName: 'User' } },
    });

    expect(next.streamingMode).toBe('observing');
    expect(next.currentTurn?.id).toBeTruthy();
    expect(next.activeStreamingUser).toEqual({ userId: 'u1', userName: 'User' });
  });

  it('appends structured token payloads to the streaming cell', () => {
    const streamingMessage: MessageDto = {
      id: 'streaming-2',
      role: 'assistant',
      content: 'Hello',
      created: '2026-01-01T00:00:00Z',
      isEdited: false,
      assistantName: 'Guide',
    };

    const next = reducer(
      { ...initialState, isStreaming: true, messages: [streamingMessage], selectedAssistant: 'Guide' },
      { type: 'APPEND_TOKEN', payload: JSON.stringify({ contentDelta: ' world' }) }
    );

    expect(next.messages[0]?.content).toBe('Hello world');
    expect(next.isStreamingThinking).toBe(true);
  });

  it('updates streaming progress and merges user profiles', () => {
    const progress = {
      currentPhase: 'tool' as const,
      completedSteps: 1,
      totalSteps: 2,
    };

    const withProgress = reducer(initialState, {
      type: 'UPDATE_STREAMING_PROGRESS',
      payload: progress,
    });
    expect(withProgress.streamingProgress).toEqual(progress);

    const withProfiles = reducer(initialState, {
      type: 'SET_USER_PROFILES',
      payload: { u1: { id: 'u1', displayName: 'User One' } as never },
    });
    expect(withProfiles.userProfiles?.u1).toBeTruthy();
  });

  it('stores active tool activity on the current streaming turn', () => {
    const currentTurn = {
      id: 'turn-activity',
      toolCalls: [],
      toolResults: [],
      startTime: new Date('2026-01-01T00:00:00Z'),
      isComplete: false,
    };
    const activity = {
      name: 'ReadWeb',
      status: 'running' as const,
      source: 'read_web',
      timestamp: new Date('2026-01-01T00:00:01Z'),
    };

    const next = reducer(
      { ...initialState, currentTurn },
      { type: 'SET_ACTIVE_TOOL_ACTIVITY', payload: activity }
    );

    expect(next.currentTurn?.activeToolActivity).toEqual(activity);
  });

  it('stores agent invocation activity as the active crew and clears the active tool', () => {
    const currentTurn = {
      id: 'turn-activity-crew',
      toolCalls: [],
      toolResults: [],
      activeToolActivity: {
        name: 'ReadWeb',
        status: 'running' as const,
        source: 'read_web',
        timestamp: new Date('2026-01-01T00:00:01Z'),
      },
      startTime: new Date('2026-01-01T00:00:00Z'),
      isComplete: false,
    };
    const activity = {
      name: 'Search',
      status: 'running' as const,
      source: 'agent_invocation',
      timestamp: new Date('2026-01-01T00:00:02Z'),
    };

    const next = reducer(
      { ...initialState, currentTurn },
      { type: 'SET_ACTIVE_TOOL_ACTIVITY', payload: activity }
    );

    expect(next.currentTurn?.activeCrewActivity).toEqual(activity);
    expect(next.currentTurn?.activeToolActivity).toBeUndefined();
  });

  it('creates a streaming turn when active tool activity arrives during an active stream', () => {
    const activity = {
      name: 'ReadWeb',
      status: 'running' as const,
      source: 'read_web',
      timestamp: new Date('2026-01-01T00:00:01Z'),
    };

    const next = reducer(
      { ...initialState, isStreaming: true, streamingMode: 'observing' },
      { type: 'SET_ACTIVE_TOOL_ACTIVITY', payload: activity }
    );

    expect(next.currentTurn).toBeDefined();
    expect(next.currentTurn?.activeToolActivity).toEqual(activity);
    expect(next.streamingProgress.currentPhase).toBe('tool_execution');
    expect(next.isStreamingToolCalls).toBe(true);
  });

  it('ignores active tool activity when there is no active stream', () => {
    const next = reducer(initialState, {
      type: 'SET_ACTIVE_TOOL_ACTIVITY',
      payload: {
        name: 'ReadWeb',
        status: 'running',
        timestamp: new Date('2026-01-01T00:00:01Z'),
      },
    });

    expect(next).toBe(initialState);
  });

  it('returns the existing state for unknown actions', () => {
    const next = reducer(initialState, { type: 'UNKNOWN_ACTION' as never });
    expect(next).toBe(initialState);
  });

  it('ignores APPEND_TOKEN when not streaming', () => {
    const next = reducer(
      { ...initialState, selectedAssistant: 'Guide' },
      { type: 'APPEND_TOKEN', payload: 'orphan token' }
    );
    expect(next.messages).toHaveLength(0);
    expect(next.isStreaming).toBe(false);
  });

  it('throws when creating a streaming cell without a selected assistant', () => {
    expect(() =>
      reducer(
        { ...initialState, isStreaming: true },
        { type: 'APPEND_TOKEN', payload: 'orphan token' }
      )
    ).toThrow('No assistant selected');
  });

  it('creates a streaming assistant cell when appending the first token', () => {
    const next = reducer(
      { ...initialState, isStreaming: true, selectedAssistant: 'Guide' },
      { type: 'APPEND_TOKEN', payload: 'First token' }
    );

    expect(next.messages).toHaveLength(1);
    expect(next.messages[0]?.content).toBe('First token');
    expect(next.messages[0]?.id).toMatch(/^streaming-/);
  });

  it('clears streaming cells and marks pending cell clear on the active turn', () => {
    const streamingMessage: MessageDto = {
      id: 'streaming-3',
      role: 'assistant',
      content: 'partial',
      created: '2026-01-01T00:00:00Z',
      isEdited: false,
      assistantName: 'Guide',
    };

    const cleared = reducer(
      { ...initialState, messages: [streamingMessage] },
      { type: 'CLEAR_STREAMING_CELL' }
    );
    expect(cleared.messages[0]?.content).toBe('');

    const pendingClear = reducer(withTurn(initialState), { type: 'SET_PENDING_CELL_CLEAR' });
    expect(pendingClear.currentTurn?.pendingCellClear).toBe(true);
  });

  it('finalizes and converts streaming message ids', () => {
    const streamingMessage: MessageDto = {
      id: 'streaming-4',
      role: 'assistant',
      content: 'draft',
      created: '2026-01-01T00:00:00Z',
      isEdited: false,
      assistantName: 'Guide',
    };

    const finalized = reducer(
      { ...initialState, messages: [streamingMessage] },
      { type: 'FINALIZE_STREAMING_MESSAGE', payload: { content: 'finalized' } }
    );
    expect(finalized.messages[0]?.content).toBe('finalized');

    const converted = reducer(
      { ...initialState, messages: [streamingMessage] },
      { type: 'CONVERT_STREAMING_IDS' }
    );
    expect(converted.messages[0]?.id).toMatch(/^msg-/);
  });

  it('uses the last assistant step chunk when completing an empty streaming cell', () => {
    const streamingMessage: MessageDto = {
      id: 'streaming-5',
      role: 'assistant',
      content: '',
      created: '2026-01-01T00:00:00Z',
      isEdited: false,
      assistantName: 'Guide',
    };

    const completed = reducer(
      withTurn({
        ...initialState,
        selectedAssistant: 'Guide',
        messages: [streamingMessage],
      }),
      { type: 'COMPLETE_STREAMING_TURN' }
    );

    expect(completed.messages.some((m) => m.content === 'step chunk')).toBe(true);
  });

  it('merges tool calls and records tool results during streaming', () => {
    const streamingMessage: MessageDto = {
      id: 'streaming-6',
      role: 'assistant',
      content: 'thinking',
      created: '2026-01-01T00:00:00Z',
      isEdited: false,
      assistantName: 'Guide',
    };

    const withToolCalls = reducer(
      {
        ...withTurn({ ...initialState, messages: [streamingMessage] }),
      },
      {
        type: 'SET_TOOL_CALLS',
        payload: [
          {
            id: 'tool-2',
            name: 'lookup',
            arguments: '{}',
            status: 'running',
            timestamp: new Date('2026-01-01T00:00:02Z'),
          },
        ],
      }
    );

    expect(withToolCalls.currentTurn?.toolCalls.map((tc) => tc.id)).toEqual(['tool-1', 'tool-2']);
    expect(withToolCalls.isStreamingToolCalls).toBe(true);

    const withResult = reducer(withToolCalls, {
      type: 'ADD_TOOL_RESULT',
      payload: {
        toolCallId: 'tool-2',
        content: 'lookup result',
        isError: false,
        timestamp: new Date('2026-01-01T00:00:03Z'),
      },
    });
    expect(withResult.currentTurn?.toolResults).toHaveLength(1);
    expect(withResult.currentTurn?.toolCalls.find((tc) => tc.id === 'tool-2')?.status).toBe('completed');
  });

  it('ensures tool calls on a new or existing turn', () => {
    const created = reducer(initialState, {
      type: 'ENSURE_TOOL_CALL',
      payload: { id: 'tool-new', name: 'search', arguments: '{}', status: 'running', timestamp: new Date() },
    });
    expect(created.currentTurn?.toolCalls).toHaveLength(1);

    const unchanged = reducer(created, {
      type: 'ENSURE_TOOL_CALL',
      payload: { id: 'tool-new', name: 'search', arguments: '{}', status: 'running', timestamp: new Date() },
    });
    expect(unchanged).toBe(created);

    const extended = reducer(created, {
      type: 'ENSURE_TOOL_CALL',
      payload: { id: 'tool-next', name: 'fetch', arguments: '{}', status: 'running', timestamp: new Date() },
    });
    expect(extended.currentTurn?.toolCalls).toHaveLength(2);
  });

  it('updates edit and streaming flags', () => {
    const justCompleted = reducer(initialState, { type: 'SET_JUST_COMPLETED_STREAMING', payload: true });
    expect(justCompleted._justCompletedStreaming).toBe(true);
    expect(justCompleted.isStreaming).toBe(false);

    const editing = reducer(initialState, { type: 'SET_EDITING', payload: 'assistant-1' });
    expect(editing.editingAssistantId).toBe('assistant-1');
  });

  it('starts a streaming turn with initial progress', () => {
    const next = reducer(initialState, { type: 'START_STREAMING_TURN' });
    expect(next.isStreaming).toBe(true);
    expect(next.currentTurn?.toolCalls).toEqual([]);
    expect(next.streamingProgress?.currentPhase).toBe('thinking');
  });

  it('ignores empty append token payloads', () => {
    const streamingMessage: MessageDto = {
      id: 'streaming-empty',
      role: 'assistant',
      content: 'partial',
      created: '2026-01-01T00:00:00Z',
      isEdited: false,
      assistantName: 'Guide',
    };

    const base = { ...initialState, isStreaming: true, messages: [streamingMessage] };
    const next = reducer(base, { type: 'APPEND_TOKEN', payload: JSON.stringify({ contentDelta: '' }) });
    expect(next).toBe(base);
  });

  it('replaces streaming cell content when pending clear is set', () => {
    const streamingMessage: MessageDto = {
      id: 'streaming-clear',
      role: 'assistant',
      content: 'old chunk',
      created: '2026-01-01T00:00:00Z',
      isEdited: false,
      assistantName: 'Guide',
    };

    const baseState = withTurn({ ...initialState, isStreaming: true, messages: [streamingMessage] });
    const pendingClear = {
      ...baseState,
      currentTurn: {
        ...baseState.currentTurn!,
        pendingCellClear: true,
      },
    };
    const next = reducer(pendingClear, { type: 'APPEND_TOKEN', payload: 'fresh chunk' });

    expect(next.messages[0]?.content).toBe('fresh chunk');
    expect(next.currentTurn?.pendingCellClear).toBe(false);
  });

  it('leaves non-streaming messages unchanged when finalizing', () => {
    const userMessage: MessageDto = {
      id: 'msg-user',
      role: 'user',
      content: 'Question',
      created: '2026-01-01T00:00:00Z',
      isEdited: false,
    };

    const next = reducer(
      { ...initialState, messages: [userMessage] },
      { type: 'FINALIZE_STREAMING_MESSAGE', payload: { content: 'ignored' } }
    );
    expect(next.messages[0]).toEqual(userMessage);
  });

  it('ignores pending cell clear when no active turn exists', () => {
    const next = reducer(initialState, { type: 'SET_PENDING_CELL_CLEAR' });
    expect(next).toBe(initialState);
  });

  it('includes tool messages when completing a turn with tool results', () => {
    const streamingMessage: MessageDto = {
      id: 'streaming-tools',
      role: 'assistant',
      content: 'Working',
      created: '2026-01-01T00:00:00Z',
      isEdited: false,
      assistantName: 'Guide',
    };

    const withResult = reducer(
      withTurn({ ...initialState, messages: [streamingMessage], selectedAssistant: 'Guide' }),
      {
        type: 'ADD_TOOL_RESULT',
        payload: {
          toolCallId: 'tool-1',
          content: 'tool output',
          isError: false,
          timestamp: new Date('2026-01-01T00:00:01Z'),
        },
      }
    );

    const completed = reducer(withResult, { type: 'COMPLETE_STREAMING_TURN' });
    expect(completed.messages.some((m) => m.role === 'tool' && m.content === 'tool output')).toBe(true);
  });

  it('ignores tool results when no active turn exists', () => {
    const next = reducer(initialState, {
      type: 'ADD_TOOL_RESULT',
      payload: {
        toolCallId: 'missing',
        content: 'ignored',
        isError: false,
        timestamp: new Date(),
      },
    });
    expect(next).toBe(initialState);
  });
});
