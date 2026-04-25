import { describe, it, expect, vi, beforeEach } from 'vitest';
import { api } from '../api';

const mockFetch = vi.fn();

// @ts-ignore
global.fetch = mockFetch;

describe('api service', () => {
  beforeEach(() => {
    mockFetch.mockReset();
  });

  it('builds correct URL and returns JSON on success', async () => {
    const sample = { id: '123' };
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      json: vi.fn().mockResolvedValue(sample),
    });

    const result = await api.projects.getProjectDetails('123');

    expect(mockFetch).toHaveBeenCalledWith(expect.stringContaining('/projects/123/details'), expect.objectContaining({
      headers: expect.objectContaining({ 'Content-Type': 'application/json' })
    }));

    expect(result).toEqual(sample);
  });

  it('throws error on non-ok response', async () => {
    mockFetch.mockResolvedValue({ ok: false, status: 500, statusText: 'Server Error' });

    await expect(api.projects.getProjectDetails('999')).rejects.toThrow('API call failed');
  });

  it('returns undefined when API responds with 204 No Content', async () => {
    mockFetch.mockResolvedValue({ ok: true, status: 204, json: vi.fn() });

    const result = await api.projects.deleteProject('123');
    expect(result).toBeUndefined();
  });

  it('returns undefined when API responds with 200 and an empty body', async () => {
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      text: vi.fn().mockResolvedValue(''),
    });

    const result = await api.settings.loadLlamaModel('qwen-test');
    expect(result).toBeUndefined();
  });
});

describe('api.projects', () => {
  beforeEach(() => {
    mockFetch.mockReset();
  });

  it('create sends POST and returns project', async () => {
    const dto = { id: '1', title: 'T', description: '', created: '' };
    mockFetch.mockResolvedValue({ ok: true, status: 200, json: vi.fn().mockResolvedValue(dto) });
    const result = await api.projects.create({ title: 'T' });
    expect(result).toEqual(dto);
    expect(mockFetch).toHaveBeenCalledWith(expect.stringContaining('/projects'), expect.objectContaining({ method: 'POST' }));
  });

  it('updateProject sends PUT and returns project', async () => {
    const dto = { id: '1', title: 'T', description: '', created: '' };
    mockFetch.mockResolvedValue({ ok: true, status: 200, json: vi.fn().mockResolvedValue(dto) });
    const result = await api.projects.updateProject('1', { title: 'T' });
    expect(result).toEqual(dto);
    expect(mockFetch).toHaveBeenCalledWith(expect.stringContaining('/projects/1'), expect.objectContaining({ method: 'PUT' }));
  });

  it('copyProject sends POST and returns project', async () => {
    const dto = { id: '2', title: 'Copy', description: '', created: '' };
    mockFetch.mockResolvedValue({ ok: true, status: 200, json: vi.fn().mockResolvedValue(dto) });
    const result = await api.projects.copyProject('2');
    expect(result).toEqual(dto);
    expect(mockFetch).toHaveBeenCalledWith(expect.stringContaining('/projects/2/copy'), expect.objectContaining({ method: 'POST' }));
  });

  it('uploadFiles uploads files and returns results', async () => {
    const file = new File(['abc'], 'test.txt', { type: 'text/plain' });
    const resultObj = { id: 'f1' };
    mockFetch.mockResolvedValue({ ok: true, status: 200, json: vi.fn().mockResolvedValue(resultObj) });
    const result = await api.projects.uploadFiles('1', [file]);
    expect(result).toEqual([resultObj]);
    expect(mockFetch).toHaveBeenCalledWith(expect.stringContaining('/projects/1/files'), expect.objectContaining({ method: 'POST' }));
  });

  it('uploadFiles throws on error', async () => {
    const file = new File(['abc'], 'fail.txt', { type: 'text/plain' });
    mockFetch.mockResolvedValue({ ok: false, status: 400, statusText: 'Bad' });
    await expect(api.projects.uploadFiles('1', [file])).rejects.toThrow('Failed to upload file fail.txt');
  });

  it('getContentFile returns file dto', async () => {
    const fileDto = { id: 'f', name: 'n', contentType: 't', fileName: 'f', index: false, documentId: 'd', created: 'c', fileSize: 100, latestVersion: 1 };
    mockFetch.mockResolvedValue({ ok: true, status: 200, json: vi.fn().mockResolvedValue(fileDto) });
    const result = await api.projects.getContentFile('1', 'f');
    expect(result).toEqual(fileDto);
    expect(mockFetch).toHaveBeenCalledWith(expect.stringContaining('/projects/1/files/f'), expect.anything());
  });

  it('getContentFileContent returns blob and headers', async () => {
    const blob = new Blob(['test'], { type: 'text/plain' });
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      blob: vi.fn().mockResolvedValue(blob),
      headers: {
        get: vi.fn().mockImplementation((name) => {
          if (name === 'Content-Type') return 'text/plain';
          if (name === 'Content-Disposition') return 'attachment; filename="test.txt"';
          return null;
        }),
        entries: vi.fn().mockReturnValue([])
      }
    });
    const result = await api.projects.getContentFileContent('1', 'f');
    expect(result.blob).toBe(blob);
    expect(result.contentType).toBe('text/plain');
  });

  it('getContentFileContent throws on error', async () => {
    mockFetch.mockResolvedValue({
      ok: false,
      status: 404,
      statusText: 'Not Found',
      headers: {
        entries: vi.fn().mockReturnValue([])
      }
    });
    await expect(api.projects.getContentFileContent('1', 'f')).rejects.toThrow('Failed to fetch file content');
  });

  it('updateContentFile sends PATCH', async () => {
    const fileDto = { id: 'f', name: 'n', contentType: 't', fileName: 'f', index: false, documentId: 'd', created: 'c', fileSize: 100, latestVersion: 1 };
    mockFetch.mockResolvedValue({ ok: true, status: 200, json: vi.fn().mockResolvedValue(fileDto) });
    const result = await api.projects.updateContentFile('1', 'f', { index: true });
    expect(result).toEqual(fileDto);
    expect(mockFetch).toHaveBeenCalledWith(expect.stringContaining('/projects/1/files/f'), expect.objectContaining({ method: 'PATCH' }));
  });

  it('deleteContentFile sends DELETE', async () => {
    mockFetch.mockResolvedValue({ ok: true, status: 204, json: vi.fn() });
    await expect(api.projects.deleteContentFile('1', 'f')).resolves.toBeUndefined();
    expect(mockFetch).toHaveBeenCalledWith(expect.stringContaining('/projects/1/files/f'), expect.objectContaining({ method: 'DELETE' }));
  });
});

describe('api.links', () => {
  beforeEach(() => {
    mockFetch.mockReset();
  });

  it('create sends POST', async () => {
    const dto = { id: 'l', url: 'http://x', projectId: '1' };
    mockFetch.mockResolvedValue({ ok: true, status: 200, json: vi.fn().mockResolvedValue(dto) });
    const result = await api.links.create({ projectId: '1', url: 'http://x' });
    expect(result).toEqual(dto);
    expect(mockFetch).toHaveBeenCalledWith(expect.stringContaining('/projects/1/links'), expect.objectContaining({ method: 'POST' }));
  });

  it('update sends PUT', async () => {
    const dto = { id: 'l', url: 'http://x', projectId: '1' };
    mockFetch.mockResolvedValue({ ok: true, status: 200, json: vi.fn().mockResolvedValue(dto) });
    const result = await api.links.update('l', { url: 'http://x', projectId: '1' });
    expect(result).toEqual(dto);
    expect(mockFetch).toHaveBeenCalledWith(expect.stringContaining('/projects/1/links/l'), expect.objectContaining({ method: 'PUT' }));
  });

  it('delete sends DELETE', async () => {
    mockFetch.mockResolvedValue({ ok: true, status: 204, json: vi.fn() });
    await expect(api.links.delete('l', '1')).resolves.toBeUndefined();
    expect(mockFetch).toHaveBeenCalledWith(expect.stringContaining('/projects/1/links/l'), expect.objectContaining({ method: 'DELETE' }));
  });
});

describe('api.settings', () => {
  beforeEach(() => {
    mockFetch.mockReset();
  });

  it('getSections calls the settings sections endpoint', async () => {
    const dto = [
      {
        sectionName: 'OpenAI',
        displayName: 'OpenAI',
        displayOrder: 10,
        hasSecrets: true,
        readinessStatus: 'configured',
        missingFields: [],
      },
    ];
    mockFetch.mockResolvedValue({ ok: true, status: 200, json: vi.fn().mockResolvedValue(dto) });

    const result = await api.settings.getSections();

    expect(result).toEqual(dto);
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining('/settings/sections'),
      expect.objectContaining({ headers: expect.objectContaining({ 'Content-Type': 'application/json' }) })
    );
  });

  it('updateSection sends PUT with payload', async () => {
    const dto = {
      sectionName: 'OpenAI',
      displayName: 'OpenAI',
      schemaVersion: 1,
      rowVersion: 'abc',
      updatedUtc: '2026-01-01T00:00:00Z',
      payload: { apiKey: '********' },
      secretHasValue: { apiKey: true }
    };
    mockFetch.mockResolvedValue({ ok: true, status: 200, json: vi.fn().mockResolvedValue(dto) });

    const result = await api.settings.updateSection('OpenAI', { rowVersion: 'abc', payload: { apiKey: '********' } });

    expect(result).toEqual(dto);
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining('/settings/sections/OpenAI'),
      expect.objectContaining({ method: 'PUT' })
    );
  });

  it('deleteModel sends DELETE', async () => {
    mockFetch.mockResolvedValue({ ok: true, status: 204, json: vi.fn() });

    await expect(api.settings.deleteModel('gpt-5.4')).resolves.toBeUndefined();
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining('/settings/models/gpt-5.4'),
      expect.objectContaining({ method: 'DELETE' })
    );
  });

  it('rebuildEmbeddings sends POST and returns job payload', async () => {
    const dto = { jobId: '11111111-1111-1111-1111-111111111111', status: 'queued' };
    mockFetch.mockResolvedValue({ ok: true, status: 202, json: vi.fn().mockResolvedValue(dto) });

    const result = await api.settings.rebuildEmbeddings();

    expect(result).toEqual(dto);
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining('/settings/embeddings/rebuild'),
      expect.objectContaining({ method: 'POST' })
    );
  });
});

describe('api.settings.localModels.listOutcome', () => {
  beforeEach(() => {
    mockFetch.mockReset();
  });

  it('returns available with parsed JSON on 200', async () => {
    const payload = { items: [] };
    mockFetch.mockResolvedValue({
      status: 200,
      statusText: 'OK',
      url: 'http://localhost/api/settings/services/SpeechSynthesis/local-models',
      headers: new Headers({ 'content-type': 'application/json' }),
      text: vi.fn().mockResolvedValue(JSON.stringify(payload)),
    });
    const result = await api.settings.localModels.listOutcome('SpeechSynthesis');
    expect(result).toEqual({ kind: 'available', payload });
  });

  it('surfaces upstream proxy failures verbatim from 502 envelope', async () => {
    const envelope = {
      error: 'Upstream http://guideants-ai:80/sd/admin/bundles returned 404 Not Found.',
      upstreamTarget: 'http://guideants-ai:80/sd/admin/bundles',
      upstreamStatus: 404,
      upstreamStatusText: 'Not Found',
      upstreamContentType: 'application/json',
      upstreamBody: '{"detail":"Not Found"}',
    };
    mockFetch.mockResolvedValue({
      status: 502,
      statusText: 'Bad Gateway',
      url: 'http://localhost/api/settings/services/ImageGeneration/local-models',
      headers: new Headers({ 'content-type': 'application/json' }),
      text: vi.fn().mockResolvedValue(JSON.stringify(envelope)),
    });
    const result = await api.settings.localModels.listOutcome('ImageGeneration');
    expect(result.kind).toBe('error');
    if (result.kind !== 'error') throw new Error('expected error');
    expect(result.message).toBe(envelope.error);
    expect(result.upstream).toEqual({
      upstreamTarget: envelope.upstreamTarget,
      upstreamStatus: envelope.upstreamStatus,
      upstreamStatusText: envelope.upstreamStatusText,
      upstreamContentType: envelope.upstreamContentType,
      upstreamBody: envelope.upstreamBody,
    });
  });

  it('returns an error carrying the raw body when the response is not a proxy envelope', async () => {
    mockFetch.mockResolvedValue({
      status: 500,
      statusText: 'Server Error',
      url: 'http://localhost/api/settings/services/SpeechTranscription/local-models',
      headers: new Headers({ 'content-type': 'text/plain' }),
      text: vi.fn().mockResolvedValue('boom'),
    });
    const result = await api.settings.localModels.listOutcome('SpeechTranscription');
    expect(result.kind).toBe('error');
    if (result.kind !== 'error') throw new Error('expected error');
    expect(result.message).toContain('500');
    expect(result.message).toContain('Server Error');
    expect(result.message).toContain('boom');
    expect(result.upstream).toBeUndefined();
  });

  it('returns an error on network failure', async () => {
    mockFetch.mockRejectedValue(new Error('fetch blew up'));
    const result = await api.settings.localModels.listOutcome('ImageGeneration');
    expect(result).toEqual({ kind: 'error', message: 'fetch blew up' });
  });
});

describe('api.settings.chatDefaults', () => {
  beforeEach(() => {
    mockFetch.mockReset();
  });

  it('get calls /settings/chat-defaults', async () => {
    const dto = {
      defaultModelId: 'gpt-test',
      overrideAllChatModels: false,
      temperature: 0.7,
      topP: 0.9,
      reasoningEffort: null,
      samplingParametersJson: null,
      rowVersion: 'abc',
    };
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      json: vi.fn().mockResolvedValue(dto),
    });

    const result = await api.settings.chatDefaults.get();

    expect(result).toEqual(dto);
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining('/settings/chat-defaults'),
      expect.objectContaining({
        headers: expect.objectContaining({ 'Content-Type': 'application/json' }),
      })
    );
  });

  it('update sends PUT with body', async () => {
    const updated = {
      defaultModelId: 'm1',
      overrideAllChatModels: true,
      temperature: 0.5,
      topP: 1,
      reasoningEffort: 'medium',
      samplingParametersJson: null,
      rowVersion: 'rv2',
    };
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      json: vi.fn().mockResolvedValue(updated),
    });

    const payload = {
      rowVersion: 'rv1',
      defaultModelId: 'm1',
      overrideAllChatModels: true,
      temperature: 0.5,
      topP: 1,
      reasoningEffort: 'medium',
      samplingParametersJson: null,
    };

    const result = await api.settings.chatDefaults.update(payload);

    expect(result).toEqual(updated);
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining('/settings/chat-defaults'),
      expect.objectContaining({
        method: 'PUT',
        body: JSON.stringify(payload),
      })
    );
  });
});
