import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import type { ExtendedConversationState } from '../conversation/types';
import { useStreamingEventHandler } from '../conversation/useStreamingEventHandler';

const mockGenerateTitle = vi.fn().mockResolvedValue(undefined);

vi.mock('../../services/api', () => ({
  api: {
    projects: {
      notebooks: {
        conversations: {
          refreshMessages: vi.fn(),
          pollLlamaRuntimeOperation: vi.fn(),
          generateTitle: (...args: unknown[]) => mockGenerateTitle(...args),
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

function mountHandler(stateOverrides: Partial<ExtendedConversationState> = {}) {
  const dispatch = vi.fn();
  const showToast = vi.fn();
  const setCurrentStreamController = vi.fn();
  const loadNotebookFiles = vi.fn().mockResolvedValue(undefined);
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
    },
  ));

  return { handler: result.current, dispatch, showToast, setCurrentStreamController, loadNotebookFiles, state };
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

  it('handles complete event and refreshes notebook files', async () => {
    const { handler, dispatch, loadNotebookFiles } = mountHandler({
      messages: [{ id: 'streaming-1', role: 'assistant', content: 'partial', created: '', isEdited: false } as any],
    });

    handler({ type: 'complete', data: {} });

    expect(dispatch).toHaveBeenCalledWith({ type: 'COMPLETE_STREAMING_TURN' });
    expect(loadNotebookFiles).toHaveBeenCalled();
    expect(dispatchEventSpy).toHaveBeenCalledWith(expect.objectContaining({ type: 'refresh-notebook-files' }));
  });

  it('auto-generates title on first completed assistant turn', async () => {
    const { handler } = mountHandler({ messages: [] });

    handler({ type: 'complete', data: {} });

    await vi.waitFor(() => {
      expect(mockGenerateTitle).toHaveBeenCalledWith('p1', 'n1', 'c1');
    });
  });

  it('skips title generation when prior assistant messages exist', async () => {
    const { handler } = mountHandler({
      messages: [{ id: 'msg-1', role: 'assistant', content: 'prior', created: '', isEdited: false } as any],
    });

    handler({ type: 'complete', data: {} });

    await new Promise((r) => setTimeout(r, 10));
    expect(mockGenerateTitle).not.toHaveBeenCalled();
  });

  it('handles cancelled event', () => {
    const { handler, dispatch } = mountHandler();

    handler({ type: 'cancelled', data: {} });

    expect(dispatch).toHaveBeenCalledWith({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
    expect(dispatch).toHaveBeenCalledWith({ type: 'COMPLETE_STREAMING_TURN' });
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
      payload: expect.objectContaining({ toolCallId: 'tc-1', content: 'results' }),
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
});
