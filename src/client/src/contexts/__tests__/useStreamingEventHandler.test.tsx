import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import type { MutableRefObject } from 'react';
import type { ExtendedConversationState, SendStreamState } from '../conversation/types';
import { useStreamingEventHandler } from '../conversation/useStreamingEventHandler';

vi.mock('../../services/api', () => ({
  api: {
    projects: {
      notebooks: {
        conversations: {
          refreshMessages: vi.fn(),
          pollLlamaRuntimeOperation: vi.fn(),
        },
      },
    },
  },
}));

function createState(overrides: Partial<ExtendedConversationState> = {}): ExtendedConversationState {
  return {
    messages: [],
    isStreaming: true,
    selectedAssistant: 'Claude',
    draftUserContent: '',
    streamingMode: 'sending',
    currentTurn: undefined,
    ...overrides,
  };
}

function mountHandler(
  stateOverrides: Partial<ExtendedConversationState> = {},
  depOverrides: {
    refreshConversation?: ReturnType<typeof vi.fn>;
    getActiveStreamTurnId?: () => string | null;
    sendStreamStateRef?: MutableRefObject<SendStreamState | null>;
    pendingStopRef?: MutableRefObject<boolean>;
  } = {},
) {
  const dispatch = vi.fn();
  const showToast = vi.fn();
  const setCurrentStreamController = vi.fn();
  const setActiveStreamTurnId = vi.fn();
  const loadNotebookFiles = vi.fn().mockResolvedValue(undefined);
  const refreshConversation = depOverrides.refreshConversation ?? vi.fn().mockResolvedValue(undefined);
  const state = createState(stateOverrides);

  const { result } = renderHook(() => useStreamingEventHandler(
    dispatch as any,
    state as any,
    {
      loadNotebookFiles,
      showToast,
      projectId: 'p1',
      notebookId: 'n1',
      conversationId: 'c1',
      setCurrentStreamController,
      setActiveStreamTurnId,
      refreshConversation,
      getActiveStreamTurnId: depOverrides.getActiveStreamTurnId,
      sendStreamStateRef: depOverrides.sendStreamStateRef,
      pendingStopRef: depOverrides.pendingStopRef,
    },
  ));

  return {
    handler: result.current,
    dispatch,
    showToast,
    setCurrentStreamController,
    setActiveStreamTurnId,
    loadNotebookFiles,
    refreshConversation,
    state,
  };
}

describe('useStreamingEventHandler error branch', () => {
  let dispatchEventSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    dispatchEventSpy = vi.spyOn(window, 'dispatchEvent').mockImplementation(() => true);
  });

  afterEach(() => {
    dispatchEventSpy.mockRestore();
  });

  it('dispatches llama-runtime-crashed with OutOfMemory reason when code is local_llm_oom', () => {
    const { handler, showToast } = mountHandler();

    handler({
      type: 'error',
      data: {
        code: 'local_llm_oom',
        reason: 'OutOfMemory',
        message: 'The local model ran out of GPU memory...',
        innerMessage: 'CUDA error: out of memory',
        type: 'LlamaRuntimeCrashedException',
      },
    });

    expect(dispatchEventSpy).toHaveBeenCalledTimes(1);
    const event = dispatchEventSpy.mock.calls[0][0] as CustomEvent;
    expect(event.type).toBe('llama-runtime-crashed');
    expect(event.detail.reason).toBe('OutOfMemory');
    expect(event.detail.upstreamDetail).toBe('CUDA error: out of memory');
    expect(event.detail.code).toBe('local_llm_oom');
    // Crash branch must NOT also raise the generic error toast; the modal is the user surface.
    expect(showToast).not.toHaveBeenCalled();
  });

  it('dispatches llama-runtime-crashed with Crashed reason when code is local_llm_crashed', () => {
    const { handler, showToast } = mountHandler();

    handler({
      type: 'error',
      data: {
        code: 'local_llm_crashed',
        reason: 'Crashed',
        message: 'The local model returned HTTP 500 and must be restarted.',
        innerMessage: null,
        type: 'LlamaRuntimeCrashedException',
      },
    });

    expect(dispatchEventSpy).toHaveBeenCalledTimes(1);
    const event = dispatchEventSpy.mock.calls[0][0] as CustomEvent;
    expect(event.detail.reason).toBe('Crashed');
    expect(event.detail.code).toBe('local_llm_crashed');
    expect(showToast).not.toHaveBeenCalled();
  });

  it('dispatches llama-runtime-requires-load when code is local_llm_not_ready', () => {
    const { handler, showToast } = mountHandler();

    handler({
      type: 'error',
      data: {
        code: 'local_llm_not_ready',
        reason: 'NotReady',
        message: 'The local model runtime has no model loaded. Load a model to continue.',
        innerMessage: 'the server does not have a model loaded',
        type: 'LlamaRuntimeCrashedException',
      },
    });

    // NotReady routes through the existing "needs load" event — no crash modal, no toast,
    // no restart. The notebook-level handler opens LlamaRuntimeModal with a requires_load
    // status.
    expect(dispatchEventSpy).toHaveBeenCalledTimes(1);
    const event = dispatchEventSpy.mock.calls[0][0] as CustomEvent;
    expect(event.type).toBe('llama-runtime-requires-load');
    expect(event.detail.runtimeStatus).toEqual({ state: 'requires_load' });
    // assistantId intentionally omitted — the notebook page keeps its own ref of the last
    // target, so we let that survive rather than re-plumbing it through SSE.
    expect(event.detail.assistantId).toBeUndefined();
    expect(showToast).not.toHaveBeenCalled();
  });

  it('falls back to the generic error toast when no crash code is present', () => {
    const { handler, showToast } = mountHandler();

    handler({
      type: 'error',
      data: {
        message: 'Something went wrong',
        type: 'SomeOtherException',
      },
    });

    expect(dispatchEventSpy).not.toHaveBeenCalled();
    expect(showToast).toHaveBeenCalledWith(expect.objectContaining({
      type: 'error',
      title: 'Conversation Error',
    }));
  });

  it('shows recovery warning without opening the crash modal when local inference times out', () => {
    const { handler, showToast } = mountHandler();

    handler({
      type: 'error',
      data: {
        code: 'local_llm_timeout',
        message: 'The local model exceeded its inference deadline and is being recovered.',
        type: 'LlamaInferenceTimeoutException',
      },
    });

    expect(dispatchEventSpy).not.toHaveBeenCalled();
    expect(showToast).toHaveBeenCalledWith(expect.objectContaining({
      type: 'warning',
      title: 'Local Model Recovering',
    }));
  });

  it('shows warning toast for AttachmentNotReadyException', () => {
    const { handler, showToast } = mountHandler();

    handler({
      type: 'error',
      data: {
        message: 'File still processing',
        type: 'AttachmentNotReadyException',
      },
    });

    expect(showToast).toHaveBeenCalledWith(expect.objectContaining({
      type: 'warning',
      title: 'Attachment Still Processing',
    }));
  });

  it('includes action text in error display message', () => {
    const { handler, dispatch } = mountHandler();

    handler({
      type: 'error',
      data: {
        message: 'Quota exceeded',
        action: 'Upgrade your plan',
        type: 'QuotaException',
      },
    });

    expect(dispatch).toHaveBeenCalledWith({
      type: 'SET_STREAMING_ERROR',
      payload: 'Quota exceeded\n\nUpgrade your plan',
    });
  });
});

describe('useStreamingEventHandler streaming branches', () => {
  let dispatchEventSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    vi.clearAllMocks();
    dispatchEventSpy = vi.spyOn(window, 'dispatchEvent').mockImplementation(() => true);
  });

  afterEach(() => {
    dispatchEventSpy.mockRestore();
  });

  it('ignores events with no data', () => {
    const { handler, dispatch } = mountHandler();

    handler({ type: 'token', data: undefined });

    expect(dispatch).not.toHaveBeenCalled();
  });

  it('does not let an observer turn_created replace the local sending turn', () => {
    const sendStreamStateRef = {
      current: {
        snapshot: { draft: 'hello', pendingAttachments: [] },
        turnId: 'local-turn',
      },
    } as MutableRefObject<SendStreamState | null>;
    const { handler, setActiveStreamTurnId } = mountHandler({}, { sendStreamStateRef });

    handler({ type: 'turn_created', data: { turnId: 'remote-turn' } }, 'observer');

    expect(setActiveStreamTurnId).not.toHaveBeenCalled();
  });

  it('accepts turn_created from the local sending stream', () => {
    const sendStreamStateRef = {
      current: {
        snapshot: { draft: 'hello', pendingAttachments: [] },
        turnId: null,
      },
    } as MutableRefObject<SendStreamState | null>;
    const { handler, setActiveStreamTurnId } = mountHandler({}, { sendStreamStateRef });

    handler({ type: 'turn_created', data: { turnId: 'local-turn' } }, 'send');

    expect(setActiveStreamTurnId).toHaveBeenCalledWith('local-turn');
  });

  it('handles complete event; the send-side owner refreshes files, the handler only for observers', async () => {
    // File-tree refresh split (turn-terminal ownership): the send-side owner
    // (useConversationActions onComplete) refreshes the tree for every local terminal, so the
    // handler must NOT double-refresh on a local (send / no-source) complete…
    const { handler, dispatch, loadNotebookFiles, setActiveStreamTurnId } = mountHandler({
      messages: [{ id: 'streaming-1', role: 'assistant', content: 'partial', created: '', isEdited: false } as any],
    });

    handler({ type: 'complete', data: {} });

    expect(dispatch).toHaveBeenCalledWith({ type: 'COMPLETE_STREAMING_TURN' });
    expect(setActiveStreamTurnId).toHaveBeenCalledWith(null);
    expect(loadNotebookFiles).not.toHaveBeenCalled();

    // …but an observer (other-client) complete has no send-side owner, so the handler
    // refreshes the tree then (loadNotebookFiles emits refresh-notebook-files itself).
    const { handler: obsHandler, loadNotebookFiles: obsLoad } = mountHandler();
    obsHandler({ type: 'complete', data: {} }, 'observer');
    expect(obsLoad).toHaveBeenCalled();
  });

  it('complete never dispatches composer state — the action layer owns it', () => {
    // Regression for the stale-closure freeze: the handler used to gate CLEAR_ATTACHMENTS on
    // render-captured state.streamingMode === 'sending', which was always false for a
    // locally-sent turn. P2 moves composer terminal state to the action layer's onComplete,
    // so the handler must not touch draft/attachments/snapshot — for sending OR observing
    // mounts, regardless of what closure state it was captured with.
    const sendStreamStateRef = {
      current: {
        snapshot: {
          draft: 'draft',
          pendingAttachments: [{
            notebookFileId: 'file-1',
            fileName: 'notes.md',
            uploadType: 'text',
          }],
        },
        turnId: 'turn-1',
      },
    } as MutableRefObject<SendStreamState | null>;
    const local = mountHandler({}, { sendStreamStateRef });

    local.handler({ type: 'complete', data: {} });

    expect(local.dispatch).toHaveBeenCalledWith({ type: 'COMPLETE_STREAMING_TURN' });
    expect(local.dispatch).not.toHaveBeenCalledWith({ type: 'CLEAR_ATTACHMENTS' });
    expect(local.dispatch).not.toHaveBeenCalledWith({ type: 'SET_DRAFT', payload: 'draft' });
    expect(local.dispatch).not.toHaveBeenCalledWith({ type: 'SET_ATTACHMENTS', payload: expect.anything() });
    // The snapshot is intact for the action-layer owner to consume.
    expect(sendStreamStateRef.current).not.toBeNull();

    // Same guarantee for an observing mount (other client's complete broadcast).
    const observer = mountHandler({ streamingMode: 'observing' }, { sendStreamStateRef });
    observer.handler({ type: 'complete', data: {} });
    expect(observer.dispatch).not.toHaveBeenCalledWith({ type: 'CLEAR_ATTACHMENTS' });
    expect(observer.dispatch).not.toHaveBeenCalledWith({ type: 'SET_DRAFT', payload: expect.anything() });
  });

  it('does not unlock on a terminal SSE event while Stop is pending', () => {
    const pendingStopRef = { current: true } as MutableRefObject<boolean>;
    const { handler, dispatch, setActiveStreamTurnId } = mountHandler(
      {},
      { pendingStopRef },
    );

    handler({ type: 'cancelled', data: {} });

    expect(dispatch).not.toHaveBeenCalledWith({ type: 'COMPLETE_STREAMING_TURN' });
    expect(dispatch).not.toHaveBeenCalledWith({ type: 'SET_STREAMING', payload: false });
    expect(setActiveStreamTurnId).not.toHaveBeenCalledWith(null);
    expect(pendingStopRef.current).toBe(true);
  });

  it('finalizes conversation state at pending_client_tool without touching the composer', () => {
    // The pending_client_tool composer policy (default: restore draft + chips, re-enable at
    // rest) is applied by the action layer's onComplete('pending_client_tool'). The handler
    // only finalizes the streamed cell / turn — so the snapshot survives for the owner and no
    // draft/attachment dispatch happens here. This is the D2 fix: the draft is no longer
    // silently dropped by a stale closure, and the policy is swappable (see P4).
    const sendStreamStateRef = {
      current: {
        snapshot: {
          draft: 'follow-up text',
          pendingAttachments: [{
            notebookFileId: 'file-1',
            fileName: 'notes.md',
            uploadType: 'text',
          }],
        },
        turnId: 'turn-1',
      },
    } as MutableRefObject<SendStreamState | null>;
    const { handler, dispatch } = mountHandler({}, { sendStreamStateRef });

    handler({ type: 'pending_client_tool', data: {} });

    expect(dispatch).toHaveBeenCalledWith({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
    expect(dispatch).toHaveBeenCalledWith({ type: 'COMPLETE_STREAMING_TURN' });
    expect(dispatch).not.toHaveBeenCalledWith({ type: 'SET_DRAFT', payload: expect.anything() });
    expect(dispatch).not.toHaveBeenCalledWith({ type: 'CLEAR_ATTACHMENTS' });
    expect(dispatch).not.toHaveBeenCalledWith({ type: 'SET_ATTACHMENTS', payload: expect.anything() });
    expect(sendStreamStateRef.current).not.toBeNull();
  });

  it('refreshes conversations on complete so sidebar picks up server-set title', () => {
    const { handler } = mountHandler({ messages: [] });

    handler({ type: 'complete', data: {} });

    expect(dispatchEventSpy).toHaveBeenCalledWith(expect.objectContaining({ type: 'refresh-conversations' }));
  });

  it('handles cancelled event', () => {
    const refreshConversation = vi.fn().mockResolvedValue(undefined);
    const { handler, dispatch, setActiveStreamTurnId } = mountHandler({}, { refreshConversation });

    handler({ type: 'cancelled', data: {} });

    expect(dispatch).toHaveBeenCalledWith({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
    expect(dispatch).toHaveBeenCalledWith({ type: 'COMPLETE_STREAMING_TURN' });
    expect(setActiveStreamTurnId).toHaveBeenCalledWith(null);
    expect(refreshConversation).toHaveBeenCalledWith({ force: true });
  });

  it('processes tool_result with tool call and result', () => {
    const { handler, dispatch } = mountHandler();

    handler({
      type: 'tool_result',
      data: {
        toolCallId: 'tc-1',
        functionName: 'search_web',
        content: 'results',
        arguments: '{"q":"test"}',
        timestamp: new Date().toISOString(),
      },
    });

    expect(dispatch).toHaveBeenCalledWith({
      type: 'ENSURE_TOOL_CALL',
      payload: expect.objectContaining({ id: 'tc-1', name: 'search_web' }),
    });
    expect(dispatch).toHaveBeenCalledWith({
      type: 'ADD_TOOL_RESULT',
      payload: expect.objectContaining({ toolCallId: 'tc-1', content: 'results', isError: false }),
    });
  });

  it('marks ERROR tool_result as isError', () => {
    const { handler, dispatch } = mountHandler();

    handler({
      type: 'tool_result',
      data: {
        toolCallId: 'tc-cancel',
        functionName: 'sandbox_tool',
        content: 'ERROR: Operation was cancelled',
      },
    });

    expect(dispatch).toHaveBeenCalledWith({
      type: 'ADD_TOOL_RESULT',
      payload: expect.objectContaining({
        toolCallId: 'tc-cancel',
        content: 'ERROR: Operation was cancelled',
        isError: true,
      }),
    });
  });

  it('skips tool_result without toolCallId', () => {
    const { handler, dispatch } = mountHandler();

    handler({ type: 'tool_result', data: { content: 'orphan' } });

    expect(dispatch).not.toHaveBeenCalled();
  });

  it('refreshes files after GeneratePodcast tool result', () => {
    const { handler, loadNotebookFiles } = mountHandler();

    handler({
      type: 'tool_result',
      data: { toolCallId: 'tc-pod', functionName: 'GeneratePodcast', content: 'done' },
    });

    expect(loadNotebookFiles).toHaveBeenCalled();
  });

  it('appends tokens and tool calls from assistant_message', () => {
    const { handler, dispatch } = mountHandler();

    handler({
      type: 'assistant_message',
      data: {
        contentDelta: 'Hello',
        tool_calls: [{ id: 'tc-2', function: { name: 'calc', arguments: '{}' } }],
        timestamp: new Date().toISOString(),
      },
    });

    expect(dispatch).toHaveBeenCalledWith({
      type: 'APPEND_TOKEN',
      payload: { contentDelta: 'Hello' },
    });
    expect(dispatch).toHaveBeenCalledWith({
      type: 'SET_TOOL_CALLS',
      payload: [expect.objectContaining({ id: 'tc-2', name: 'calc' })],
    });
  });

  it('stores tool activity from streaming_progress without appending visible content', () => {
    const { handler, dispatch } = mountHandler({ currentTurn: {
      id: 'turn-1',
      toolCalls: [],
      toolResults: [],
      startTime: new Date(),
      isComplete: false,
    } as any });

    handler({
      type: 'streaming_progress',
      data: {
        toolActivity: {
          name: 'ReadWeb',
          status: 'running',
          toolCallId: 'tc-read',
          timestamp: '2026-06-16T15:42:10.162Z',
        },
      },
    });

    expect(dispatch).toHaveBeenCalledWith({
      type: 'SET_ACTIVE_TOOL_ACTIVITY',
      payload: expect.objectContaining({
        name: 'ReadWeb',
        status: 'running',
        toolCallId: 'tc-read',
        timestamp: expect.any(Date),
      }),
    });
    expect(dispatch).not.toHaveBeenCalledWith(expect.objectContaining({ type: 'APPEND_TOKEN' }));
  });

  it('creates observer placeholder on first assistant_message in observing mode', () => {
    const { handler, dispatch } = mountHandler({ streamingMode: 'observing', messages: [] });

    handler({ type: 'assistant_message', data: { contentDelta: 'Hi' } });

    expect(dispatch).toHaveBeenCalledWith(expect.objectContaining({
      type: 'ADD_MESSAGE',
      payload: expect.objectContaining({ role: 'assistant', streaming: true }),
    }));
  });

  it('finalizes message event with content', () => {
    const { handler, dispatch } = mountHandler();

    handler({
      type: 'message',
      data: { role: 'assistant', content: 'Final answer', timestamp: new Date().toISOString() },
    });

    expect(dispatch).toHaveBeenCalledWith(expect.objectContaining({ type: 'FINALIZE_STREAMING_MESSAGE' }));
    expect(dispatch).toHaveBeenCalledWith(expect.objectContaining({ type: 'ADD_FINAL_RESPONSE' }));
  });

  it('ignores message event without content', () => {
    const { handler, dispatch } = mountHandler();

    handler({ type: 'message', data: { role: 'assistant' } });

    expect(dispatch).not.toHaveBeenCalled();
  });

  it('dispatches ADD_TOOL_ERROR for tool_error events', () => {
    const { handler, dispatch } = mountHandler();

    handler({ type: 'tool_error', data: { toolCallId: 'tc-err', content: 'boom' } });

    expect(dispatch).toHaveBeenCalledWith({
      type: 'ADD_TOOL_ERROR',
      payload: expect.objectContaining({ toolCallId: 'tc-err', content: 'boom' }),
    });
  });

  it('handles unknown event types with legacy content field', () => {
    const { handler, dispatch } = mountHandler();

    handler({ type: 'legacy_token', data: { content: 'chunk' } });

    expect(dispatch).toHaveBeenCalledWith({
      type: 'APPEND_TOKEN',
      payload: { contentDelta: 'chunk' },
    });
  });

  it('ignores error events for a non-active turn', () => {
    const { handler, dispatch, showToast } = mountHandler(
      {},
      { getActiveStreamTurnId: () => 'turn-live' },
    );

    handler({
      type: 'error',
      data: {
        turnId: 'turn-orphan',
        message: 'An error occurred while saving the entity changes.',
        type: 'DbUpdateException',
      },
    });

    expect(dispatch).not.toHaveBeenCalled();
    expect(showToast).not.toHaveBeenCalled();
  });

  it('ignores assistant_message deltas for a non-active turn', () => {
    const { handler, dispatch } = mountHandler(
      {},
      { getActiveStreamTurnId: () => 'turn-live' },
    );

    handler({
      type: 'assistant_message',
      data: { turnId: 'turn-other', contentDelta: 'Nope' },
    });

    expect(dispatch).not.toHaveBeenCalled();
  });

  it('ignores tool results for a non-active turn', () => {
    const { handler, dispatch } = mountHandler(
      {},
      { getActiveStreamTurnId: () => 'turn-live' },
    );

    handler({
      type: 'tool_result',
      data: {
        turnId: 'turn-other',
        toolCallId: 'tc-old',
        functionName: 'ReadWeb',
        content: 'late result',
      },
    });

    expect(dispatch).not.toHaveBeenCalled();
  });

  it('ignores completion for a non-active turn', () => {
    const { handler, dispatch, setActiveStreamTurnId } = mountHandler(
      {},
      { getActiveStreamTurnId: () => 'turn-live' },
    );

    handler({ type: 'complete', data: { turnId: 'turn-other' } });

    expect(dispatch).not.toHaveBeenCalled();
    expect(setActiveStreamTurnId).not.toHaveBeenCalled();
  });

  it('surfaces the streaming error for the active turn without aborting or touching the composer', () => {
    // The transport-driven action owner finalizes the composer; the handler only raises the
    // visible error and finalizes conversation state. It no longer aborts the stream (the
    // transport owns that lifecycle) and no longer restores draft/attachments (the action
    // layer's onComplete('error') does, via the turnId persistence oracle).
    const sendStreamStateRef = {
      current: {
        snapshot: { draft: 'kept', pendingAttachments: [] },
        turnId: 'turn-live',
      },
    } as MutableRefObject<SendStreamState | null>;
    const { handler, dispatch } = mountHandler(
      {},
      { getActiveStreamTurnId: () => 'turn-live', sendStreamStateRef },
    );

    handler({
      type: 'error',
      data: {
        turnId: 'turn-live',
        message: 'real failure',
        code: null,
      },
    });

    expect(dispatch).toHaveBeenCalledWith({
      type: 'SET_STREAMING_ERROR',
      payload: 'real failure',
    });
    expect(dispatch).not.toHaveBeenCalledWith({ type: 'SET_DRAFT', payload: expect.anything() });
    expect(dispatch).not.toHaveBeenCalledWith({ type: 'CLEAR_ATTACHMENTS' });
    expect(dispatch).not.toHaveBeenCalledWith({ type: 'SET_ATTACHMENTS', payload: expect.anything() });
    expect(sendStreamStateRef.current).not.toBeNull();
  });
});
