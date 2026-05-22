import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  dispatchRuntimeStatusWindowEvent,
  getRuntimeBlockingMessage,
  getNotebookRuntimeReadyCache,
  clearNotebookRuntimeReadyCache,
  checkRuntimeStatus,
} from '../conversation/runtimeChecks';

vi.mock('../../services/api', () => ({
  api: {
    projects: {
      notebooks: {
        conversations: {
          checkLlamaRuntime: vi.fn(),
        },
      },
    },
  },
}));

import { api } from '../../services/api';
const mockCheckRuntime = vi.mocked(api.projects.notebooks.conversations.checkLlamaRuntime);

describe('runtimeChecks', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('dispatchRuntimeStatusWindowEvent', () => {
    let dispatchSpy: ReturnType<typeof vi.spyOn>;

    beforeEach(() => {
      dispatchSpy = vi.spyOn(window, 'dispatchEvent').mockImplementation(() => true);
    });

    afterEach(() => {
      dispatchSpy.mockRestore();
    });

    it('does nothing when state is undefined', () => {
      dispatchRuntimeStatusWindowEvent('a1', {});
      expect(dispatchSpy).not.toHaveBeenCalled();
    });

    it('does nothing when state is "ready"', () => {
      dispatchRuntimeStatusWindowEvent('a1', { state: 'ready' });
      expect(dispatchSpy).not.toHaveBeenCalled();
    });

    it('dispatches llama-runtime-loading when state is "loading"', () => {
      const op = { progress: 50 };
      dispatchRuntimeStatusWindowEvent('a1', { state: 'loading', activeOperation: op });
      expect(dispatchSpy).toHaveBeenCalledTimes(1);
      const evt = dispatchSpy.mock.calls[0][0] as CustomEvent;
      expect(evt.type).toBe('llama-runtime-loading');
      expect(evt.detail.operation).toBe(op);
    });

    it('dispatches llama-runtime-requires-load for other states', () => {
      dispatchRuntimeStatusWindowEvent('a1', { state: 'unloaded' });
      expect(dispatchSpy).toHaveBeenCalledTimes(1);
      const evt = dispatchSpy.mock.calls[0][0] as CustomEvent;
      expect(evt.type).toBe('llama-runtime-requires-load');
      expect(evt.detail.assistantId).toBe('a1');
    });
  });

  describe('getRuntimeBlockingMessage', () => {
    it('returns activeOperation.errorDetails when present', () => {
      expect(getRuntimeBlockingMessage({ activeOperation: { errorDetails: 'OOM' } })).toBe('OOM');
    });

    it('returns first conflict when errorDetails is absent', () => {
      expect(getRuntimeBlockingMessage({ conflicts: ['Model X is loaded'] })).toBe('Model X is loaded');
    });

    it('returns default message when no details', () => {
      expect(getRuntimeBlockingMessage({})).toBe('Local model runtime is not ready.');
    });
  });

  describe('getNotebookRuntimeReadyCache', () => {
    it('returns the same Set for the same notebookId', () => {
      const a = getNotebookRuntimeReadyCache('nb-1');
      const b = getNotebookRuntimeReadyCache('nb-1');
      expect(a).toBe(b);
    });

    it('returns different Sets for different notebookIds', () => {
      const a = getNotebookRuntimeReadyCache('nb-cache-a');
      const b = getNotebookRuntimeReadyCache('nb-cache-b');
      expect(a).not.toBe(b);
    });

    it('clears cache for one notebook without affecting others', () => {
      const a = getNotebookRuntimeReadyCache('nb-clear-a');
      const b = getNotebookRuntimeReadyCache('nb-clear-b');
      a.add('assistant-a');
      b.add('assistant-b');

      clearNotebookRuntimeReadyCache('nb-clear-a');

      expect(a.size).toBe(0);
      expect(b.has('assistant-b')).toBe(true);
    });
  });

  describe('checkRuntimeStatus', () => {
    const projectId = 'proj-1';
    const notebookId = 'nb-rt-1';
    const assistantId = 'asst-1';

    it('returns { state: "ready" } immediately when assistant is in the ready cache', async () => {
      const readyCache = new Set([assistantId]);
      const inflight = new Set<string>();
      const result = await checkRuntimeStatus(projectId, notebookId, assistantId, inflight, readyCache);
      expect(result).toEqual({ state: 'ready' });
      expect(mockCheckRuntime).not.toHaveBeenCalled();
    });

    it('returns null when a check for the same assistant is already in-flight', async () => {
      const inflight = new Set([`${notebookId}-${assistantId}`]);
      const result = await checkRuntimeStatus(projectId, notebookId, assistantId, inflight);
      expect(result).toBeNull();
      expect(mockCheckRuntime).not.toHaveBeenCalled();
    });

    it('calls the API, caches on ready, and returns status', async () => {
      mockCheckRuntime.mockResolvedValue({ state: 'ready' });
      const readyCache = new Set<string>();
      const inflight = new Set<string>();

      const result = await checkRuntimeStatus(projectId, notebookId, assistantId, inflight, readyCache);
      expect(result).toEqual({ state: 'ready' });
      expect(readyCache.has(assistantId)).toBe(true);
      expect(inflight.size).toBe(0);
    });

    it('dispatches window event when status is not ready', async () => {
      const dispatchSpy = vi.spyOn(window, 'dispatchEvent').mockImplementation(() => true);
      mockCheckRuntime.mockResolvedValue({ state: 'loading', activeOperation: { progress: 40 } });
      const inflight = new Set<string>();

      await checkRuntimeStatus(projectId, notebookId, assistantId, inflight);
      expect(dispatchSpy).toHaveBeenCalled();
      dispatchSpy.mockRestore();
    });

    it('cleans up inflight set even on success', async () => {
      mockCheckRuntime.mockResolvedValue({ state: 'ready' });
      const inflight = new Set<string>();
      await checkRuntimeStatus(projectId, notebookId, assistantId, inflight);
      expect(inflight.size).toBe(0);
    });

    it('handles 409 errors by dispatching event and returning runtimeStatus', async () => {
      const dispatchSpy = vi.spyOn(window, 'dispatchEvent').mockImplementation(() => true);
      const runtimeStatus = { state: 'conflict', conflicts: ['Model Y loaded'] };
      const error: any = new Error('Conflict');
      error.status = 409;
      error.body = { runtimeStatus };
      mockCheckRuntime.mockRejectedValue(error);
      const inflight = new Set<string>();

      const result = await checkRuntimeStatus(projectId, notebookId, assistantId, inflight);
      expect(result).toEqual(runtimeStatus);
      expect(dispatchSpy).toHaveBeenCalled();
      expect(inflight.size).toBe(0);
      dispatchSpy.mockRestore();
    });

    it('returns a synthetic status on 409 when body has no runtimeStatus', async () => {
      const error: any = new Error('Conflict');
      error.status = 409;
      error.body = {};
      error.message = 'Conflict';
      mockCheckRuntime.mockRejectedValue(error);
      const inflight = new Set<string>();

      const result = await checkRuntimeStatus(projectId, notebookId, assistantId, inflight);
      expect(result).toEqual({ state: 'failed', conflicts: ['Conflict'] });
      expect(inflight.size).toBe(0);
    });

    it('re-throws non-409 errors', async () => {
      const error = new Error('Network failure');
      mockCheckRuntime.mockRejectedValue(error);
      const inflight = new Set<string>();

      await expect(
        checkRuntimeStatus(projectId, notebookId, assistantId, inflight)
      ).rejects.toThrow('Network failure');
      expect(inflight.size).toBe(0);
    });
  });
});
