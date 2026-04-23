import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useStreamingEventHandler } from '../conversation/useStreamingEventHandler';

// Mock the API surface the hook touches; we don't care about its behavior in these tests —
// we're only exercising the SSE 'error' branch, which doesn't call the API.
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

function mountHandler() {
  const dispatch = vi.fn();
  const showToast = vi.fn();
  const setCurrentStreamController = vi.fn();

  const { result } = renderHook(() => useStreamingEventHandler(
    dispatch as any,
    {} as any,
    {
      loadNotebookFiles: vi.fn(),
      loadConversations: vi.fn(),
      conversations: [],
      showToast,
      projectId: 'p1',
      notebookId: 'n1',
      conversationId: 'c1',
      setCurrentStreamController,
    },
  ));

  return { handler: result.current, dispatch, showToast, setCurrentStreamController };
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
});
