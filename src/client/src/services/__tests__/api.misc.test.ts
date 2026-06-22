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
    statusText: 'OK',
    headers: {
      get: vi.fn((name: string) => (name.toLowerCase() === 'content-type' ? 'application/json' : null)),
    },
    json: vi.fn().mockResolvedValue(data),
    text: vi.fn().mockResolvedValue(JSON.stringify(data)),
  };
}

function noContent() {
  return { ok: true, status: 204, json: vi.fn(), text: vi.fn().mockResolvedValue('') };
}

describe('api callApi error handling', () => {
  beforeEach(() => {
    mockFetch.mockReset();
    mockBroadcastAuthExpired.mockReset();
  });

  it('extracts message from JSON error body and broadcasts on 401', async () => {
    mockFetch.mockResolvedValue({
      ok: false,
      status: 401,
      statusText: 'Unauthorized',
      headers: {
        get: vi.fn((name: string) => (name.toLowerCase() === 'content-type' ? 'application/json' : null)),
      },
      json: vi.fn().mockResolvedValue({ message: 'Session expired' }),
    });

    await expect(api.auth.me()).rejects.toMatchObject({
      status: 401,
      message: 'Session expired',
      code: undefined,
    });
    expect(mockBroadcastAuthExpired).toHaveBeenCalledWith('Session expired');
  });

  it('uses error.title and code from JSON body', async () => {
    mockFetch.mockResolvedValue({
      ok: false,
      status: 409,
      statusText: 'Conflict',
      headers: {
        get: vi.fn((name: string) => (name.toLowerCase() === 'content-type' ? 'application/json' : null)),
      },
      json: vi.fn().mockResolvedValue({ title: 'Conflict title', code: 'CONFLICT' }),
    });

    await expect(api.users.getCurrent()).rejects.toMatchObject({
      status: 409,
      message: 'Conflict title',
      code: 'CONFLICT',
    });
  });

  it('falls back to raw text for non-JSON errors', async () => {
    mockFetch.mockResolvedValue({
      ok: false,
      status: 500,
      statusText: 'Server Error',
      headers: { get: vi.fn().mockReturnValue('text/plain') },
      text: vi.fn().mockResolvedValue('plain error text'),
      json: vi.fn(),
    });

    await expect(api.test.secure()).rejects.toMatchObject({
      status: 500,
      message: 'plain error text',
    });
  });

  it('parses JSON via text() when response.text exists', async () => {
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      text: vi.fn().mockResolvedValue(JSON.stringify({ via: 'text' })),
      json: vi.fn(),
    });
    const result = await api.quickStart.create();
    expect(result).toEqual({ via: 'text' });
  });
});

describe('api.public', () => {
  beforeEach(() => mockFetch.mockReset());

  it('getPublishedGuideByFriendlyName returns guide on success', async () => {
    const guide = { id: 'g1', friendlyName: 'demo', requiresAuth: false };
    mockFetch.mockResolvedValue({ ok: true, json: vi.fn().mockResolvedValue(guide) });
    const result = await api.public.getPublishedGuideByFriendlyName('demo');
    expect(result).toEqual(guide);
    expect(mockFetch.mock.calls[0]?.[0]).toEqual(expect.stringContaining('/published/guides/by-name/demo'));
  });

  it('getPublishedGuideByFriendlyName throws on failure', async () => {
    mockFetch.mockResolvedValue({ ok: false, status: 404 });
    await expect(api.public.getPublishedGuideByFriendlyName('missing')).rejects.toMatchObject({ status: 404 });
  });
});

describe('api.auth mutations', () => {
  beforeEach(() => mockFetch.mockReset());

  it('register trims name and email', async () => {
    mockFetch.mockResolvedValue(jsonOk({ userId: 'u1' }));
    await api.auth.register({ name: ' Alice ', email: ' a@b.com ', password: 'pw' });
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining('/auth/register'),
      expect.objectContaining({
        body: JSON.stringify({ name: 'Alice', email: 'a@b.com', password: 'pw' }),
      }),
    );
  });

  it('changePassword sends POST', async () => {
    mockFetch.mockResolvedValue(noContent());
    await api.auth.changePassword({ currentPassword: 'a', newPassword: 'b' });
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining('/auth/change-password'),
      expect.objectContaining({ method: 'POST' }),
    );
  });
});

describe('api.adminUsers (table-driven)', () => {
  beforeEach(() => mockFetch.mockReset());

  const cases: Array<{ name: string; call: () => Promise<unknown>; urlPart: string; method?: string }> = [
    { name: 'list no filters', call: () => api.adminUsers.list(), urlPart: '/admin/users/' },
    { name: 'list with filters', call: () => api.adminUsers.list({ role: 'Admin', status: 'active' }), urlPart: 'role=Admin' },
    { name: 'approve', call: () => api.adminUsers.approve('u1', 'Contributor'), urlPart: '/admin/users/u1/approve', method: 'POST' },
    { name: 'changeRole', call: () => api.adminUsers.changeRole('u1', 'Viewer'), urlPart: '/admin/users/u1/role', method: 'PUT' },
    { name: 'deactivate', call: () => api.adminUsers.deactivate('u1'), urlPart: '/admin/users/u1/deactivate', method: 'POST' },
    { name: 'reactivate', call: () => api.adminUsers.reactivate('u1'), urlPart: '/admin/users/u1/reactivate', method: 'POST' },
    { name: 'setPassword', call: () => api.adminUsers.setPassword('u1', 'secret'), urlPart: '/admin/users/u1/set-password', method: 'POST' },
  ];

  it.each(cases)('$name', async ({ call, urlPart, method }) => {
    mockFetch.mockResolvedValue(method ? jsonOk({ userId: 'u1' }) : jsonOk([]));
    await call();
    expect(mockFetch.mock.calls[0]?.[0]).toEqual(expect.stringContaining(urlPart));
    if (method) {
      expect(mockFetch.mock.calls[0]?.[1]).toEqual(expect.objectContaining({ method }));
    }
  });
});

describe('api.usage (table-driven)', () => {
  beforeEach(() => mockFetch.mockReset());

  const cases: Array<{ name: string; call: () => Promise<unknown>; urlPart: string }> = [
    { name: 'getSummary', call: () => api.usage.getSummary('2026-01-01', '2026-01-31', 'day'), urlPart: '/usage/summary?' },
    { name: 'getSummaryByProject', call: () => api.usage.getSummaryByProject('2026-01-01', '2026-01-31'), urlPart: '/usage/by-project?' },
    { name: 'getProjectSummary', call: () => api.usage.getProjectSummary('p1', '2026-01-01', '2026-01-31', 'week'), urlPart: '/usage/projects/p1/summary?' },
    {
      name: 'getDetails',
      call: () => api.usage.getDetails({ from: '2026-01-01', to: '2026-01-31', page: 2, pageSize: 50, category: 'chat' }),
      urlPart: '/usage/details?',
    },
    {
      name: 'getProjectDetails',
      call: () => api.usage.getProjectDetails('p1', { from: '2026-01-01', to: '2026-01-31', service: 'openai' }),
      urlPart: '/usage/projects/p1/details?',
    },
    { name: 'getBreakdown', call: () => api.usage.getBreakdown('2026-01-01', '2026-01-31'), urlPart: '/usage/breakdown?' },
    { name: 'getProjectBreakdown', call: () => api.usage.getProjectBreakdown('p1', '2026-01-01', '2026-01-31'), urlPart: '/usage/projects/p1/breakdown?' },
  ];

  it.each(cases)('$name hits usage endpoint', async ({ call, urlPart }) => {
    mockFetch.mockResolvedValue(jsonOk({}));
    await call();
    expect(mockFetch.mock.calls[0]?.[0]).toEqual(expect.stringContaining(urlPart));
  });
});

describe('api.guides and notebooks', () => {
  beforeEach(() => mockFetch.mockReset());

  const guideCases: Array<{ name: string; call: () => Promise<unknown>; urlPart: string }> = [
    { name: 'guides.list', call: () => api.guides.guides.list(), urlPart: '/guides' },
    { name: 'guides.get', call: () => api.guides.guides.get('g1'), urlPart: '/guides/g1' },
    { name: 'guides.create', call: () => api.guides.guides.create({ name: 'g' }), urlPart: '/guides' },
    { name: 'guides.update', call: () => api.guides.guides.update('g1', { name: 'g2' }), urlPart: '/guides/g1' },
    { name: 'guides.delete', call: () => api.guides.guides.delete('g1'), urlPart: '/guides/g1' },
    { name: 'guides.duplicate', call: () => api.guides.guides.duplicate('g1'), urlPart: '/guides/g1/duplicate' },
    { name: 'guides.validateRuntime', call: () => api.guides.guides.validateRuntime({}), urlPart: '/guides/runtime/validate' },
    { name: 'guides.publish', call: () => api.guides.guides.publish('g1', { friendlyName: 'demo' } as never), urlPart: '/guides/g1/publish' },
    { name: 'assistants.list', call: () => api.guides.assistants.list(), urlPart: '/assistants' },
    { name: 'assistants.get', call: () => api.guides.assistants.get('a1'), urlPart: '/assistants/a1' },
    { name: 'assistants.create', call: () => api.guides.assistants.create({ name: 'a' }), urlPart: '/assistants' },
    { name: 'assistants.retryMarkdown', call: () => api.guides.assistants.retryFileMarkdownExtraction('a1', 'f1'), urlPart: '/assistants/a1/files/f1/markdown/retry' },
    { name: 'catalogs.models', call: () => api.guides.catalogs.models(), urlPart: '/catalogs/models' },
    { name: 'catalogs.tools', call: () => api.guides.catalogs.tools(), urlPart: '/catalogs/tools' },
    { name: 'catalogs.globalAssistants', call: () => api.guides.catalogs.globalAssistants(), urlPart: '/catalogs/global-assistants' },
    { name: 'catalogs.globalAssistant', call: () => api.guides.catalogs.globalAssistant('ga1'), urlPart: '/catalogs/global-assistants/ga1' },
    { name: 'operations.get', call: () => api.guides.operations.get('op-1'), urlPart: '/operations/op-1' },
    { name: 'operations.update', call: () => api.guides.operations.update('op-1', { name: 'op' }), urlPart: '/operations/op-1' },
    { name: 'operations.preview', call: () => api.guides.operations.preview({ code: 'x' }), urlPart: '/operations/preview' },
    { name: 'headerToolbar', call: () => api.notebooks.headerToolbar('nb-1', 'c1'), urlPart: '/notebooks/nb-1/header-toolbar?conversationId=c1' },
    { name: 'chatReadiness', call: () => api.notebooks.chatReadiness('nb-1'), urlPart: '/notebooks/nb-1/header-toolbar/chat-readiness' },
    { name: 'chatReadiness with conversation', call: () => api.notebooks.chatReadiness('nb-1', 'c1'), urlPart: 'conversationId=c1' },
    {
      name: 'getUserConversations full query',
      call: () => api.conversations.getUserConversations({ page: 1, pageSize: 20, search: 'hi', sortBy: 'updated', sortOrder: 'desc' }),
      urlPart: '/conversations?',
    },
  ];

  it.each(guideCases)('$name', async ({ call, urlPart }) => {
    mockFetch.mockResolvedValue(jsonOk({}));
    await call();
    expect(mockFetch.mock.calls[0]?.[0]).toEqual(expect.stringContaining(urlPart));
  });

  it('guides.publish sends wireApiConfig payload when provided', async () => {
    mockFetch.mockResolvedValue(jsonOk({ id: 'pub-1' }));

    await api.guides.guides.publish('g1', {
      projectId: 'p1',
      wireApiConfig: {
        enabled: true,
        profile: 'balanced',
        endpointFlags: { models: true, embeddings: true },
        aliasMap: { guide: 'guide', embeddings: 'embeddings' },
        maxRequestSizes: { embeddingsBytes: 4096 },
      },
    });

    const request = mockFetch.mock.calls[0]?.[1];
    const body = JSON.parse((request?.body as string) ?? '{}');
    expect(body.wireApiConfig).toMatchObject({
      enabled: true,
      profile: 'balanced',
      endpointFlags: { models: true, embeddings: true },
      aliasMap: { guide: 'guide', embeddings: 'embeddings' },
      maxRequestSizes: { embeddingsBytes: 4096 },
    });
  });

  it('lineage download returns blob metadata', async () => {
    const blob = new Blob(['x'], { type: 'application/octet-stream' });
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      blob: vi.fn().mockResolvedValue(blob),
      headers: {
        get: vi.fn((name: string) => {
          if (name === 'Content-Type') return 'application/octet-stream';
          if (name === 'Content-Disposition') return 'attachment; filename=event.bin';
          return null;
        }),
      },
    });
    const result = await api.lineage.download('evt-1');
    expect(result).toMatchObject({ blob, contentType: 'application/octet-stream', fileName: 'event.bin' });
  });

  it('lineage download throws on failure', async () => {
    mockFetch.mockResolvedValue({ ok: false, status: 404, headers: { get: vi.fn() } });
    await expect(api.lineage.download('evt-1')).rejects.toThrow('Failed to download lineage file');
  });

  it('guides export returns blob', async () => {
    const blob = new Blob(['zip'], { type: 'application/zip' });
    mockFetch.mockResolvedValue({ ok: true, blob: vi.fn().mockResolvedValue(blob) });
    const result = await api.guides.guides.export('g1');
    expect(result).toBe(blob);
  });

  it('guides downloadClaudeSkill returns blob', async () => {
    const blob = new Blob(['zip'], { type: 'application/zip' });
    mockFetch.mockResolvedValue({ ok: true, blob: vi.fn().mockResolvedValue(blob) });
    const result = await api.guides.guides.downloadClaudeSkill('g1', 'pub-1');
    expect(result).toBe(blob);
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining('/guides/g1/publish/pub-1/claude-skill'),
      expect.any(Object)
    );
  });

  it('guides downloadClaudeSkill throws with server error message', async () => {
    mockFetch.mockResolvedValue({
      ok: false,
      json: vi.fn().mockResolvedValue({ error: 'mcp_not_enabled', message: 'MCP must be enabled' }),
    });
    await expect(api.guides.guides.downloadClaudeSkill('g1', 'pub-1')).rejects.toThrow(
      'MCP must be enabled'
    );
  });

  it('guides import returns parsed JSON', async () => {
    const file = new File(['{}'], 'guide.json', { type: 'application/json' });
    mockFetch.mockResolvedValue({ ok: true, json: vi.fn().mockResolvedValue({ id: 'g1' }) });
    const result = await api.guides.guides.import(file);
    expect(result).toEqual({ id: 'g1' });
  });

  it('guides import throws with server error message', async () => {
    const file = new File(['{}'], 'guide.json', { type: 'application/json' });
    mockFetch.mockResolvedValue({ ok: false, json: vi.fn().mockResolvedValue({ error: 'bad zip' }) });
    await expect(api.guides.guides.import(file)).rejects.toThrow('bad zip');
  });

  it('assistants getFileMarkdownContent returns blob metadata', async () => {
    const blob = new Blob(['# md'], { type: 'text/markdown' });
    mockFetch.mockResolvedValue({
      ok: true,
      blob: vi.fn().mockResolvedValue(blob),
      headers: {
        get: vi.fn((name: string) => {
          if (name === 'Content-Type') return 'text/markdown';
          if (name === 'Content-Disposition') return 'attachment; filename="a.md"';
          return null;
        }),
      },
    });
    const result = await api.guides.assistants.getFileMarkdownContent('a1', 'f1');
    expect(result).toMatchObject({ fileName: 'a.md', contentType: 'text/markdown' });
  });
});

describe('api.projects externalAuth and notebooks', () => {
  beforeEach(() => mockFetch.mockReset());

  const projectId = 'p1';
  const notebookId = 'nb1';

  const cases: Array<{ name: string; call: () => Promise<unknown>; urlPart: string; method?: string }> = [
    { name: 'externalAuth.list', call: () => api.projects.externalAuth.list(projectId), urlPart: '/external-auth' },
    { name: 'externalAuth.save', call: () => api.projects.externalAuth.save(projectId, 'prov', { authType: 'header' }), urlPart: '/external-auth/prov', method: 'PUT' },
    { name: 'externalAuth.delete', call: () => api.projects.externalAuth.delete(projectId, 'prov'), urlPart: '/external-auth/prov', method: 'DELETE' },
    { name: 'oauth.callback', call: () => api.projects.externalAuth.oauth.callback(projectId, 'prov', { code: 'c', state: 's' }), urlPart: '/oauth/callback', method: 'POST' },
    { name: 'createNotebook', call: () => api.projects.createNotebook(projectId, { title: 'nb' }), urlPart: '/notebooks', method: 'POST' },
    { name: 'updateNotebook', call: () => api.projects.updateNotebook(projectId, notebookId, { title: 'nb2' }), urlPart: `/notebooks/${notebookId}`, method: 'PUT' },
    { name: 'copyNotebook', call: () => api.projects.copyNotebook(projectId, { title: 'copy', sourceNotebookId: notebookId }), urlPart: '/notebooks/copy', method: 'POST' },
    { name: 'createNotebookFromFile', call: () => api.projects.createNotebookFromFile(projectId, { title: 'nb', contentFileId: 'f1' }), urlPart: '/notebooks/create-from-file', method: 'POST' },
    { name: 'setHomePage', call: () => api.projects.setHomePage(projectId, 'f1'), urlPart: '/homepage/f1', method: 'POST' },
    { name: 'clearHomePage', call: () => api.projects.clearHomePage(projectId), urlPart: '/homepage', method: 'DELETE' },
    { name: 'notebookTemplates.getAll', call: () => api.projects.notebookTemplates.getAll(projectId), urlPart: '/notebook-templates?projectId=' },
    { name: 'assistants.getConversationStarters', call: () => api.projects.assistants.getConversationStarters('asst', projectId), urlPart: '/assistants/conversation-starters/asst' },
    { name: 'deleteNotebookItem', call: () => api.projects.notebooks.deleteNotebookItem(projectId, notebookId, '/tmp'), urlPart: 'path=%2Ftmp', method: 'DELETE' },
    { name: 'deleteNotebookFileById', call: () => api.projects.notebooks.deleteNotebookFileById(projectId, notebookId, 'f1'), urlPart: `/files/f1`, method: 'DELETE' },
    { name: 'renameNotebookFileById', call: () => api.projects.notebooks.renameNotebookFileById(projectId, notebookId, 'f1', 'new.txt'), urlPart: '/files/f1/rename', method: 'PATCH' },
    { name: 'moveNotebookFileById', call: () => api.projects.notebooks.moveNotebookFileById(projectId, notebookId, 'f1', '/dest'), urlPart: '/files/f1/move', method: 'PATCH' },
    { name: 'copyFileFromProject', call: () => api.projects.notebooks.copyFileFromProject(projectId, notebookId, 'cf1', 2, '/dest'), urlPart: '/files/copy-from-project', method: 'POST' },
    { name: 'setHomePageFile', call: () => api.projects.notebooks.setHomePageFile(projectId, notebookId, 'f1'), urlPart: '/homepage/f1', method: 'POST' },
    { name: 'clearNotebookHomePage', call: () => api.projects.notebooks.clearHomePage(projectId, notebookId), urlPart: `/notebooks/${notebookId}/homepage`, method: 'DELETE' },
  ];

  it.each(cases)('$name', async ({ call, urlPart, method }) => {
    mockFetch.mockResolvedValue(method ? (method === 'DELETE' ? noContent() : jsonOk({})) : jsonOk([]));
    await call();
    expect(mockFetch.mock.calls[0]?.[0]).toEqual(expect.stringContaining(urlPart));
    if (method) {
      expect(mockFetch.mock.calls[0]?.[1]).toEqual(expect.objectContaining({ method }));
    }
  });
});

describe('api.projects.notebooks file ops', () => {
  beforeEach(() => mockFetch.mockReset());

  const projectId = 'p1';
  const notebookId = 'nb1';

  const cases: Array<{ name: string; call: () => Promise<unknown>; urlPart: string; method?: string }> = [
    { name: 'getNotebookFiles', call: () => api.projects.notebooks.getNotebookFiles(projectId, notebookId), urlPart: `/notebooks/${notebookId}/files` },
    { name: 'getNotebookFolderTree', call: () => api.projects.notebooks.getNotebookFolderTree(projectId, notebookId), urlPart: `/notebooks/${notebookId}/files/tree` },
    { name: 'createNotebookFolder', call: () => api.projects.notebooks.createNotebookFolder(projectId, notebookId, '/docs'), urlPart: '/files/create-folder', method: 'POST' },
    { name: 'renameNotebookItem', call: () => api.projects.notebooks.renameNotebookItem(projectId, notebookId, '/a', 'b'), urlPart: '/files/rename', method: 'PATCH' },
    { name: 'moveNotebookItem', call: () => api.projects.notebooks.moveNotebookItem(projectId, notebookId, '/a', '/b'), urlPart: '/files/move', method: 'PATCH' },
    { name: 'syncFiles', call: () => api.projects.notebooks.syncFiles(projectId, notebookId), urlPart: '/files/sync', method: 'POST' },
    { name: 'getNotebook', call: () => api.projects.notebooks.getNotebook(projectId, notebookId), urlPart: `/notebooks/${notebookId}` },
  ];

  it.each(cases)('$name', async ({ call, urlPart, method }) => {
    mockFetch.mockResolvedValue(method ? noContent() : jsonOk({}));
    await call();
    expect(mockFetch.mock.calls[0]?.[0]).toEqual(expect.stringContaining(urlPart));
    if (method) {
      expect(mockFetch.mock.calls[0]?.[1]).toEqual(expect.objectContaining({ method }));
    }
  });

  it('uploadNotebookFiles posts multipart form', async () => {
    const file = new File(['x'], 'n.txt', { type: 'text/plain' });
    mockFetch.mockResolvedValue(jsonOk({ uploaded: 1 }));
    await api.projects.notebooks.uploadNotebookFiles(projectId, notebookId, [file], '/docs', true);
    const body = mockFetch.mock.calls[0]?.[1]?.body as FormData;
    expect(body.get('targetRelativePath')).toBe('/docs');
    expect(body.get('index')).toBe('true');
  });
});
