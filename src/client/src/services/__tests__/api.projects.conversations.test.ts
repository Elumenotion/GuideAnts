import { describe, it, expect, vi, beforeEach } from 'vitest';
import { api, CONVERSATION_STREAM_IDLE_TIMEOUT_MS } from '../api';

const mockFetch = vi.fn();

// @ts-ignore
global.fetch = mockFetch;

function jsonOk(data: unknown, status = 200) {
  return {
    ok: true,
    status,
    headers: {
      get: vi.fn((name: string) => (name.toLowerCase() === 'content-type' ? 'application/json' : null)),
    },
    json: vi.fn().mockResolvedValue(data),
  };
}

function noContent() {
  return { ok: true, status: 204, json: vi.fn() };
}

function sseResponse(chunks: string[]) {
  const encoder = new TextEncoder();
  let index = 0;
  return {
    ok: true,
    status: 200,
    json: vi.fn(),
    body: {
      getReader: () => ({
        read: vi.fn().mockImplementation(async () => {
          if (index >= chunks.length) {
            return { done: true, value: undefined };
          }
          const value = encoder.encode(chunks[index]);
          index += 1;
          return { done: false, value };
        }),
        releaseLock: vi.fn(),
      }),
    },
  };
}

describe('api.projects.notebooks.conversations (table-driven)', () => {
  const projectId = 'proj-1';
  const notebookId = 'nb-1';
  const convoId = 'convo-1';
  const messageId = 'msg-1';

  beforeEach(() => {
    mockFetch.mockReset();
  });

  const convoGetCases: Array<{ name: string; call: () => Promise<unknown>; urlPart: string }> = [
    {
      name: 'getAll',
      call: () => api.projects.notebooks.conversations.getAll(projectId, notebookId),
      urlPart: `/projects/${projectId}/notebooks/${notebookId}/conversations`,
    },
    {
      name: 'get',
      call: () => api.projects.notebooks.conversations.get(projectId, notebookId, convoId),
      urlPart: `/projects/${projectId}/notebooks/${notebookId}/conversations/${convoId}`,
    },
    {
      name: 'checkLlamaRuntime',
      call: () => api.projects.notebooks.conversations.checkLlamaRuntime(projectId, notebookId, 'asst-1'),
      urlPart: `/notebooks/${notebookId}/llama-runtime?assistantId=asst-1`,
    },
    {
      name: 'pollLlamaRuntimeOperation',
      call: () => api.projects.notebooks.conversations.pollLlamaRuntimeOperation(projectId, notebookId, 'op-1'),
      urlPart: `/notebooks/${notebookId}/llama-runtime/operations/op-1`,
    },
  ];

  it.each(convoGetCases)('$name hits correct URL', async ({ call, urlPart }) => {
    mockFetch.mockResolvedValue(jsonOk({}));
    await call();
    expect(mockFetch.mock.calls[0]?.[0]).toEqual(expect.stringContaining(urlPart));
  });

  const convoMutateCases: Array<{ name: string; call: () => Promise<unknown>; urlPart: string; method: string }> = [
    {
      name: 'create',
      call: () => api.projects.notebooks.conversations.create(projectId, notebookId, 'New chat'),
      urlPart: `/projects/${projectId}/notebooks/${notebookId}/conversations`,
      method: 'POST',
    },
    {
      name: 'rename',
      call: () => api.projects.notebooks.conversations.rename(projectId, notebookId, convoId, 'Renamed'),
      urlPart: `/projects/${projectId}/notebooks/${notebookId}/conversations/${convoId}`,
      method: 'PUT',
    },
    {
      name: 'generateTitle',
      call: () => api.projects.notebooks.conversations.generateTitle(projectId, notebookId, convoId),
      urlPart: `/projects/${projectId}/notebooks/${notebookId}/conversations/${convoId}/title/generate`,
      method: 'POST',
    },
    {
      name: 'loadLlamaRuntime',
      call: () => api.projects.notebooks.conversations.loadLlamaRuntime(projectId, notebookId, 'asst-1'),
      urlPart: `/notebooks/${notebookId}/llama-runtime/load`,
      method: 'POST',
    },
    {
      name: 'unloadLlamaRuntime',
      call: () => api.projects.notebooks.conversations.unloadLlamaRuntime(projectId, notebookId),
      urlPart: `/notebooks/${notebookId}/llama-runtime/unload`,
      method: 'POST',
    },
    {
      name: 'restartLlamaRuntime',
      call: () => api.projects.notebooks.conversations.restartLlamaRuntime(projectId, notebookId),
      urlPart: `/notebooks/${notebookId}/llama-runtime/restart`,
      method: 'POST',
    },
    {
      name: 'editMessage',
      call: () => api.projects.notebooks.conversations.editMessage(projectId, notebookId, convoId, messageId, 'edited'),
      urlPart: `/projects/${projectId}/notebooks/${notebookId}/conversations/${convoId}/messages/${messageId}`,
      method: 'PATCH',
    },
    {
      name: 'saveAs',
      call: () => api.projects.notebooks.conversations.saveAs(projectId, notebookId, convoId),
      urlPart: `/projects/${projectId}/notebooks/${notebookId}/conversations/${convoId}/save-as`,
      method: 'POST',
    },
  ];

  it.each(convoMutateCases)('$name sends $method', async ({ call, urlPart, method }) => {
    mockFetch.mockResolvedValue(method === 'PATCH' || method === 'PUT' ? noContent() : jsonOk({}));
    await call();
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining(urlPart),
      expect.objectContaining({ method }),
    );
  });

  it('delete conversation sends DELETE', async () => {
    mockFetch.mockResolvedValue(noContent());
    await api.projects.notebooks.conversations.delete(projectId, notebookId, convoId);
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining(`/conversations/${convoId}`),
      expect.objectContaining({ method: 'DELETE' }),
    );
  });

  it('undoLast sends DELETE to messages/last', async () => {
    mockFetch.mockResolvedValue(noContent());
    await api.projects.notebooks.conversations.undoLast(projectId, notebookId, convoId);
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining(`/conversations/${convoId}/messages/last`),
      expect.objectContaining({ method: 'DELETE' }),
    );
  });

  describe('sendMessageStream', () => {
    it('parses SSE events and calls onEvent then onComplete on [DONE]', async () => {
      const events: Array<{ type: string; data: unknown }> = [];
      const onComplete = vi.fn();
      const onError = vi.fn();

      mockFetch.mockResolvedValue(
        sseResponse([
          'event: token\n',
          'data: {"delta":"hi"}\n',
          '\n',
          'data: [DONE]\n',
          '\n',
        ]),
      );

      await api.projects.notebooks.conversations.sendMessageStream(
        projectId,
        notebookId,
        convoId,
        { instructions: 'hello' },
        (ev) => events.push(ev),
        onError,
        onComplete,
      );

      expect(events).toEqual([{ type: 'token', data: { delta: 'hi' } }]);
      expect(onComplete).toHaveBeenCalled();
      expect(onError).not.toHaveBeenCalled();
      const [, init] = mockFetch.mock.calls[0] ?? [];
      expect(mockFetch.mock.calls[0]?.[0]).toEqual(
        expect.stringContaining(`/conversations/${convoId}/messages`),
      );
      expect(init).toEqual(expect.objectContaining({ method: 'POST' }));
      const headers = init?.headers as Headers;
      expect(headers.get('Accept')).toBe('text/event-stream');
    });

    it('throws on non-ok response', async () => {
      mockFetch.mockResolvedValue({
        ok: false,
        status: 400,
        statusText: 'Bad Request',
        json: vi.fn().mockResolvedValue({ error: 'bad' }),
      });

      await expect(
        api.projects.notebooks.conversations.sendMessageStream(
          projectId,
          notebookId,
          convoId,
          { instructions: 'x' },
          vi.fn(),
          vi.fn(),
          vi.fn(),
        ),
      ).rejects.toMatchObject({ status: 400 });
    });

    it('throws when response has no body', async () => {
      mockFetch.mockResolvedValue({ ok: true, status: 200, body: null });
      await expect(
        api.projects.notebooks.conversations.sendMessageStream(
          projectId,
          notebookId,
          convoId,
          { instructions: 'x' },
          vi.fn(),
          vi.fn(),
          vi.fn(),
        ),
      ).rejects.toThrow('No response body for streaming');
    });

    it('calls onError when stream read fails', async () => {
      const onError = vi.fn();
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        body: {
          getReader: () => ({
            read: vi.fn().mockRejectedValue(new Error('read failed')),
            releaseLock: vi.fn(),
          }),
        },
      });

      await api.projects.notebooks.conversations.sendMessageStream(
        projectId,
        notebookId,
        convoId,
        { instructions: 'x' },
        vi.fn(),
        onError,
        vi.fn(),
      );

      expect(onError).toHaveBeenCalledWith(expect.objectContaining({ message: 'read failed' }));
    });

    it('calls onComplete when stream ends without [DONE]', async () => {
      const onComplete = vi.fn();
      const onError = vi.fn();
      mockFetch.mockResolvedValue(sseResponse([
        'event: complete\n',
        'data: {}\n',
        '\n',
      ]));
      await api.projects.notebooks.conversations.sendMessageStream(
        projectId,
        notebookId,
        convoId,
        { instructions: 'x' },
        vi.fn(),
        onError,
        onComplete,
      );
      expect(onComplete).toHaveBeenCalled();
      expect(onError).not.toHaveBeenCalled();
    });

    it('calls onError when the stream ends without a terminal event', async () => {
      const onComplete = vi.fn();
      const onError = vi.fn();
      mockFetch.mockResolvedValue(sseResponse(['data: {"delta":"hi"}\n', '\n']));
      await api.projects.notebooks.conversations.sendMessageStream(
        projectId,
        notebookId,
        convoId,
        { instructions: 'x' },
        vi.fn(),
        onError,
        onComplete,
      );
      expect(onError).toHaveBeenCalledWith(expect.objectContaining({
        message: expect.stringMatching(/ended without a reply/i),
      }));
      expect(onComplete).not.toHaveBeenCalled();
    });

    it('ignores invalid SSE JSON without failing the stream', async () => {
      const onComplete = vi.fn();
      mockFetch.mockResolvedValue(sseResponse([
        'data: not-json\n',
        '\n',
        'event: complete\n',
        'data: {}\n',
        '\n',
      ]));
      await api.projects.notebooks.conversations.sendMessageStream(
        projectId,
        notebookId,
        convoId,
        { instructions: 'x' },
        vi.fn(),
        vi.fn(),
        onComplete,
      );
      expect(onComplete).toHaveBeenCalled();
    });

    it('returns early on AbortError without calling onError', async () => {
      const onError = vi.fn();
      const abortController = new AbortController();
      abortController.abort();

      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        body: {
          getReader: () => ({
            read: vi.fn().mockRejectedValue(new DOMException('aborted', 'AbortError')),
            cancel: vi.fn(),
            releaseLock: vi.fn(),
          }),
        },
      });

      await api.projects.notebooks.conversations.sendMessageStream(
        projectId,
        notebookId,
        convoId,
        { instructions: 'x' },
        vi.fn(),
        onError,
        vi.fn(),
        abortController.signal,
      );

      expect(onError).not.toHaveBeenCalled();
    });

    it('calls onError when the stream goes silent', async () => {
      vi.useFakeTimers();
      const onError = vi.fn();
      const onComplete = vi.fn();
      const cancel = vi.fn().mockResolvedValue(undefined);
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        body: {
          getReader: () => ({
            read: () => new Promise(() => undefined),
            cancel,
            releaseLock: vi.fn(),
          }),
        },
      });

      try {
        const pending = api.projects.notebooks.conversations.sendMessageStream(
          projectId,
          notebookId,
          convoId,
          { instructions: 'x' },
          vi.fn(),
          onError,
          onComplete,
        );

        await vi.advanceTimersByTimeAsync(CONVERSATION_STREAM_IDLE_TIMEOUT_MS);
        await pending;

        expect(onError).toHaveBeenCalledWith(expect.objectContaining({
          name: 'StreamIdleTimeoutError',
          message: expect.stringMatching(/stopped sending data/i),
        }));
        expect(onComplete).not.toHaveBeenCalled();
        expect(cancel).toHaveBeenCalled();
      } finally {
        vi.useRealTimers();
      }
    });
  });
});
