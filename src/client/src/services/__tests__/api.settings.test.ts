import { describe, it, expect, vi, beforeEach } from 'vitest';
import { api } from '../api';

const mockFetch = vi.fn();
const mockBroadcastAuthExpired = vi.fn();

vi.mock('../authEvents', () => ({
  broadcastAuthExpired: (...args: unknown[]) => mockBroadcastAuthExpired(...args),
}));

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

function fetchAuthText(status: number, body: string, contentType = 'application/json') {
  return {
    status,
    statusText: status === 200 ? 'OK' : 'Error',
    url: 'http://localhost/api/test',
    headers: new Headers({ 'content-type': contentType }),
    text: vi.fn().mockResolvedValue(body),
  };
}

describe('api.settings (table-driven)', () => {
  beforeEach(() => {
    mockFetch.mockReset();
    mockBroadcastAuthExpired.mockReset();
  });

  const getCases: Array<{
    name: string;
    call: () => Promise<unknown>;
    urlPart: string;
    sample: unknown;
  }> = [
    { name: 'getSections', call: () => api.settings.getSections(), urlPart: '/settings/sections', sample: [] },
    { name: 'getSchema', call: () => api.settings.getSchema(), urlPart: '/settings/schema', sample: { version: 1 } },
    { name: 'getReadiness', call: () => api.settings.getReadiness(), urlPart: '/settings/readiness', sample: { ready: true } },
    { name: 'getSection', call: () => api.settings.getSection('OpenAI'), urlPart: '/settings/sections/OpenAI', sample: { sectionName: 'OpenAI' } },
    { name: 'getModels', call: () => api.settings.getModels(), urlPart: '/settings/models', sample: [] },
    { name: 'getRuntimeProfiles', call: () => api.settings.getRuntimeProfiles(), urlPart: '/settings/runtime-profiles', sample: [] },
    { name: 'getRuntimeProfile', call: () => api.settings.getRuntimeProfile('rp-1'), urlPart: '/settings/runtime-profiles/rp-1', sample: { id: 'rp-1' } },
    { name: 'getLlamaInventory', call: () => api.settings.getLlamaInventory(), urlPart: '/settings/llama/runtime/inventory', sample: [] },
    { name: 'getLlamaRuntimeStatus', call: () => api.settings.getLlamaRuntimeStatus(), urlPart: '/settings/llama/runtime/status', sample: [] },
    { name: 'getOverview', call: () => api.settings.getOverview(), urlPart: '/settings/overview', sample: {} },
    { name: 'chatDefaults.get', call: () => api.settings.chatDefaults.get(), urlPart: '/settings/chat-defaults', sample: { defaultModelId: 'm1' } },
    { name: 'routing.getChatTargetsPreflight', call: () => api.settings.routing.getChatTargetsPreflight(), urlPart: '/settings/routing/chat-targets/preflight', sample: [] },
    { name: 'routing.getChatTargetReadiness', call: () => api.settings.routing.getChatTargetReadiness('model/id', true), urlPart: '/settings/routing/chat-targets/model%2Fid/readiness?strict=true', sample: { ready: true } },
    { name: 'connections.getUsage', call: () => api.settings.connections.getUsage('OpenAI'), urlPart: '/settings/connections/OpenAI/usage', sample: {} },
    { name: 'infrastructure.listDependencies', call: () => api.settings.infrastructure.listDependencies(), urlPart: '/settings/infrastructure/dependencies', sample: [] },
    { name: 'services.get', call: () => api.settings.services.get('SpeechSynthesis'), urlPart: '/settings/services/SpeechSynthesis', sample: {} },
    { name: 'services.getReadiness', call: () => api.settings.services.getReadiness('SpeechSynthesis'), urlPart: '/settings/services/SpeechSynthesis/readiness', sample: {} },
    { name: 'localModels.list', call: () => api.settings.localModels.list('SpeechSynthesis'), urlPart: '/settings/services/SpeechSynthesis/local-models', sample: {} },
    { name: 'localModels.get', call: () => api.settings.localModels.get('SpeechSynthesis', 'model-ref'), urlPart: '/settings/services/SpeechSynthesis/local-models/model-ref', sample: {} },
    { name: 'localModels.getOperation', call: () => api.settings.localModels.getOperation('SpeechSynthesis', 'op-1'), urlPart: '/settings/services/SpeechSynthesis/local-models/operations/op-1', sample: {} },
    { name: 'getDownloadStatus', call: () => api.settings.getDownloadStatus('dl-1'), urlPart: '/settings/llama/downloads/dl-1', sample: { status: 'done' } },
  ];

  it.each(getCases)('$name calls correct endpoint and returns payload', async ({ call, urlPart, sample }) => {
    mockFetch.mockResolvedValue(jsonOk(sample));
    const result = await call();
    expect(result).toEqual(sample);
    expect(mockFetch.mock.calls[0]?.[0]).toEqual(expect.stringContaining(urlPart));
  });

  const mutateCases: Array<{
    name: string;
    call: () => Promise<unknown>;
    urlPart: string;
    method: string;
    body?: unknown;
  }> = [
    {
      name: 'updateSection',
      call: () => api.settings.updateSection('OpenAI', { rowVersion: 'rv', payload: { key: 'v' } }),
      urlPart: '/settings/sections/OpenAI',
      method: 'PUT',
      body: { rowVersion: 'rv', payload: { key: 'v' } },
    },
    {
      name: 'addModel',
      call: () => api.settings.addModel({ provider: 'openai', modelId: 'gpt-test' } as never),
      urlPart: '/settings/models:add',
      method: 'POST',
    },
    {
      name: 'updateModel',
      call: () => api.settings.updateModel('m1', { displayName: 'M' } as never),
      urlPart: '/settings/models/m1',
      method: 'PUT',
    },
    {
      name: 'createRuntimeProfile',
      call: () => api.settings.createRuntimeProfile({ name: 'p' } as never),
      urlPart: '/settings/runtime-profiles',
      method: 'POST',
    },
    {
      name: 'updateRuntimeProfile',
      call: () => api.settings.updateRuntimeProfile('rp-1', { name: 'p2' } as never),
      urlPart: '/settings/runtime-profiles/rp-1',
      method: 'PUT',
    },
    {
      name: 'rebuildEmbeddings',
      call: () => api.settings.rebuildEmbeddings(),
      urlPart: '/settings/embeddings/rebuild',
      method: 'POST',
    },
    {
      name: 'loadLlamaModel',
      call: () => api.settings.loadLlamaModel('router-1'),
      urlPart: '/settings/llama/runtime/load',
      method: 'POST',
    },
    {
      name: 'unloadLlamaModel',
      call: () => api.settings.unloadLlamaModel('router-1'),
      urlPart: '/settings/llama/runtime/unload',
      method: 'POST',
    },
    {
      name: 'chatDefaults.update',
      call: () => api.settings.chatDefaults.update({ rowVersion: 'rv', defaultModelId: 'm1' } as never),
      urlPart: '/settings/chat-defaults',
      method: 'PUT',
    },
    {
      name: 'infrastructure.updateDependency',
      call: () => api.settings.infrastructure.updateDependency('key', 'value'),
      urlPart: '/settings/infrastructure/dependencies/key',
      method: 'PUT',
    },
    {
      name: 'infrastructure.probe',
      call: () => api.settings.infrastructure.probe([{ key: 'k', target: 't' }]),
      urlPart: '/settings/infrastructure/probes',
      method: 'POST',
    },
    {
      name: 'services.updateActiveProvider',
      call: () => api.settings.services.updateActiveProvider('SpeechSynthesis', 'prov-1'),
      urlPart: '/settings/services/SpeechSynthesis/active-provider',
      method: 'PUT',
    },
    {
      name: 'services.updateProviderFields',
      call: () => api.settings.services.updateProviderFields('SpeechSynthesis', 'prov-1', { apiKey: 'x' }),
      urlPart: '/settings/services/SpeechSynthesis/providers/prov-1',
      method: 'PUT',
    },
    {
      name: 'localModels.startDownload',
      call: () => api.settings.localModels.startDownload('SpeechSynthesis', { repo: 'o/r' }),
      urlPart: '/settings/services/SpeechSynthesis/local-models/downloads',
      method: 'POST',
    },
    {
      name: 'localModels.cancelOperation',
      call: () => api.settings.localModels.cancelOperation('SpeechSynthesis', 'op-1'),
      urlPart: '/settings/services/SpeechSynthesis/local-models/operations/op-1/cancel',
      method: 'POST',
    },
    {
      name: 'localModels.selectActive',
      call: () => api.settings.localModels.selectActive('SpeechSynthesis', 'model-ref'),
      urlPart: '/settings/services/SpeechSynthesis/local-models/model-ref/select-active',
      method: 'POST',
    },
    {
      name: 'localModels.load',
      call: () => api.settings.localModels.load('SpeechTranscription', { model_id: 'whisper' }),
      urlPart: '/settings/services/SpeechTranscription/local-models/load',
      method: 'POST',
    },
  ];

  it.each(mutateCases)('$name sends $method to correct endpoint', async ({ call, urlPart, method }) => {
    mockFetch.mockResolvedValue(jsonOk({ ok: true }));
    await call();
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining(urlPart),
      expect.objectContaining({ method }),
    );
  });

  const deleteCases: Array<{ name: string; call: () => Promise<unknown>; urlPart: string }> = [
    { name: 'deleteModel', call: () => api.settings.deleteModel('m1'), urlPart: '/settings/models/m1' },
    { name: 'deleteRuntimeProfile', call: () => api.settings.deleteRuntimeProfile('rp-1'), urlPart: '/settings/runtime-profiles/rp-1' },
    { name: 'deleteLlamaRouterEntry', call: () => api.settings.deleteLlamaRouterEntry('router-1'), urlPart: '/settings/llama/router/entries/router-1' },
    { name: 'localModels.remove', call: () => api.settings.localModels.remove('SpeechSynthesis', 'ref'), urlPart: '/settings/services/SpeechSynthesis/local-models/ref' },
  ];

  it.each(deleteCases)('$name sends DELETE', async ({ call, urlPart }) => {
    mockFetch.mockResolvedValue(noContent());
    await expect(call()).resolves.toBeUndefined();
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining(urlPart),
      expect.objectContaining({ method: 'DELETE' }),
    );
  });

  describe('browseHuggingFaceRepository', () => {
    it.each([
      ['meta-llama/Llama-3', 'meta-llama', 'Llama-3'],
      ['https://huggingface.co/meta-llama/Llama-3/tree/main', 'meta-llama', 'Llama-3'],
      ['https://www.huggingface.co/org/model-name', 'org', 'model-name'],
    ])('parses %s and calls browse endpoint', async (input, owner, repo) => {
      const listing = { files: [] };
      mockFetch.mockResolvedValue(jsonOk(listing));

      const result = await api.settings.browseHuggingFaceRepository(input, { serviceOrigin: 'SpeechSynthesis' });

      expect(result).toEqual(listing);
      const [url, init] = mockFetch.mock.calls[0] ?? [];
      expect(url).toEqual(expect.stringContaining(`/settings/huggingface/repositories/${owner}/${repo}/files`));
      const headers = init?.headers as Headers;
      expect(headers.get('X-Service-Origin')).toBe('SpeechSynthesis');
    });

    it.each(['', 'invalid', 'onlyowner'])('rejects invalid repository %j with REPO_INVALID', async (input) => {
      await expect(api.settings.browseHuggingFaceRepository(input)).rejects.toMatchObject({
        code: 'REPO_INVALID',
      });
      expect(mockFetch).not.toHaveBeenCalled();
    });
  });

  describe('localModels.listOutcome', () => {
    it('returns available on 200 with JSON', async () => {
      const payload = { models: ['a'] };
      mockFetch.mockResolvedValue(fetchAuthText(200, JSON.stringify(payload)));
      const result = await api.settings.localModels.listOutcome('SpeechSynthesis');
      expect(result).toEqual({ kind: 'available', payload });
    });

    it('returns error on invalid JSON body', async () => {
      mockFetch.mockResolvedValue(fetchAuthText(200, 'not-json'));
      const result = await api.settings.localModels.listOutcome('SpeechSynthesis');
      expect(result.kind).toBe('error');
    });

    it('returns error with upstream envelope on 502', async () => {
      const envelope = {
        error: 'upstream failed',
        upstreamTarget: 'http://upstream',
        upstreamStatus: 404,
        upstreamStatusText: 'Not Found',
        upstreamContentType: 'application/json',
        upstreamBody: '{}',
      };
      mockFetch.mockResolvedValue(fetchAuthText(502, JSON.stringify(envelope)));
      const result = await api.settings.localModels.listOutcome('ImageGeneration');
      expect(result).toMatchObject({ kind: 'error', message: 'upstream failed' });
    });

    it('returns error with parsed.error when no upstream fields', async () => {
      mockFetch.mockResolvedValue(fetchAuthText(502, JSON.stringify({ error: 'bad gateway' })));
      const result = await api.settings.localModels.listOutcome('ImageGeneration');
      expect(result).toEqual({ kind: 'error', message: 'bad gateway' });
    });

    it('returns network error on fetch rejection', async () => {
      mockFetch.mockRejectedValue(new Error('offline'));
      const result = await api.settings.localModels.listOutcome('SpeechSynthesis');
      expect(result).toEqual({ kind: 'error', message: 'offline' });
    });
  });

  describe('localModels.runtimeReadinessOutcome', () => {
    it('returns available on 503 (service not loaded)', async () => {
      const payload = { ready: false };
      mockFetch.mockResolvedValue(fetchAuthText(503, JSON.stringify(payload)));
      const result = await api.settings.localModels.runtimeReadinessOutcome('SpeechTranscription');
      expect(result).toEqual({ kind: 'available', payload });
    });

    it('returns generic error for unexpected status', async () => {
      mockFetch.mockResolvedValue(fetchAuthText(500, 'internal', 'text/plain'));
      const result = await api.settings.localModels.runtimeReadinessOutcome('SpeechTranscription');
      expect(result.kind).toBe('error');
      if (result.kind === 'error') {
        expect(result.message).toContain('500');
        expect(result.message).toContain('internal');
      }
    });
  });

  describe('localModels.catalogOutcome', () => {
    it('returns available on 200 with JSON', async () => {
      const payload = { version: 1, entries: [{ id: 'chatterbox', displayName: 'Chatterbox' }] };
      mockFetch.mockResolvedValue(fetchAuthText(200, JSON.stringify(payload)));
      const result = await api.settings.localModels.catalogOutcome('SpeechSynthesis');
      expect(result).toEqual({ kind: 'available', payload });
      expect(mockFetch.mock.calls[0]?.[0]).toEqual(
        expect.stringContaining('/settings/services/SpeechSynthesis/local-models/catalog')
      );
    });

    it('returns error on invalid JSON body', async () => {
      mockFetch.mockResolvedValue(fetchAuthText(200, 'not-json'));
      const result = await api.settings.localModels.catalogOutcome('Embeddings');
      expect(result.kind).toBe('error');
    });

    it('returns network error on fetch rejection', async () => {
      mockFetch.mockRejectedValue(new Error('offline'));
      const result = await api.settings.localModels.catalogOutcome('SpeechTranscription');
      expect(result).toEqual({ kind: 'error', message: 'offline' });
    });
  });
});
