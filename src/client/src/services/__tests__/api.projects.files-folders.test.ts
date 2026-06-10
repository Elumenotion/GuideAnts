import { describe, it, expect, vi, beforeEach } from 'vitest';
import { api } from '../api';
import { getCachedFile } from '../../utils/fileCache';

const mockFetch = vi.fn();

vi.mock('../../utils/fileCache', () => ({
  getCachedFile: vi.fn().mockResolvedValue(null),
  cacheFile: vi.fn().mockResolvedValue(undefined),
}));

const mockedGetCachedFile = vi.mocked(getCachedFile);

// @ts-ignore
global.fetch = mockFetch;

function jsonOk(data: unknown, status = 200) {
  return {
    ok: true,
    status,
    headers: {
      get: vi.fn((name: string) => (name.toLowerCase() === 'content-type' ? 'application/json' : null)),
      entries: vi.fn().mockReturnValue([]),
    },
    json: vi.fn().mockResolvedValue(data),
  };
}

function noContent() {
  return { ok: true, status: 204, json: vi.fn() };
}

function blobResponse(blob: Blob, headers: Record<string, string>) {
  return {
    ok: true,
    status: 200,
    blob: vi.fn().mockResolvedValue(blob),
    headers: {
      get: vi.fn((name: string) => headers[name] ?? headers[name.toLowerCase()] ?? null),
      entries: vi.fn().mockReturnValue(Object.entries(headers)),
    },
  };
}

describe('api.projects files and folders (table-driven)', () => {
  const projectId = 'proj-1';
  const fileId = 'file-1';
  const folderId = 'folder-1';

  beforeEach(() => {
    mockFetch.mockReset();
    mockedGetCachedFile.mockResolvedValue(null);
  });

  const fileGetCases: Array<{ name: string; call: () => Promise<unknown>; urlPart: string }> = [
    { name: 'getUserProjects', call: () => api.projects.getUserProjects(), urlPart: '/projects' },
    { name: 'getProject', call: () => api.projects.getProject(projectId), urlPart: `/projects/${projectId}` },
    { name: 'getContentFile', call: () => api.projects.getContentFile(projectId, fileId), urlPart: `/projects/${projectId}/files/${fileId}` },
    { name: 'getFileHistory', call: () => api.projects.getFileHistory(projectId, fileId), urlPart: `/projects/${projectId}/files/${fileId}/history` },
    { name: 'getContentFileMarkdownShadow', call: () => api.projects.getContentFileMarkdownShadow(projectId, fileId), urlPart: `/projects/${projectId}/files/${fileId}/markdown` },
    {
      name: 'getContentFileMarkdownShadow versioned',
      call: () => api.projects.getContentFileMarkdownShadow(projectId, fileId, 2),
      urlPart: `/projects/${projectId}/files/${fileId}/versions/2/markdown`,
    },
  ];

  it.each(fileGetCases)('$name hits correct URL', async ({ call, urlPart }) => {
    mockFetch.mockResolvedValue(jsonOk({}));
    await call();
    expect(mockFetch.mock.calls[0]?.[0]).toEqual(expect.stringContaining(urlPart));
  });

  const folderCases: Array<{ name: string; call: () => Promise<unknown>; urlPart: string; method?: string }> = [
    { name: 'getFolderTree', call: () => api.projects.folders.getFolderTree(projectId), urlPart: `/projects/${projectId}/folders/tree` },
    { name: 'getFolders', call: () => api.projects.folders.getFolders(projectId), urlPart: `/projects/${projectId}/folders` },
    { name: 'getFolder', call: () => api.projects.folders.getFolder(projectId, folderId), urlPart: `/projects/${projectId}/folders/${folderId}` },
    {
      name: 'createFolder',
      call: () => api.projects.folders.createFolder(projectId, { name: 'Docs', parentFolderId: null }),
      urlPart: `/projects/${projectId}/folders`,
      method: 'POST',
    },
    {
      name: 'updateFolder',
      call: () => api.projects.folders.updateFolder(projectId, folderId, { name: 'Renamed' }),
      urlPart: `/projects/${projectId}/folders/${folderId}`,
      method: 'PUT',
    },
    {
      name: 'moveFolder',
      call: () => api.projects.folders.moveFolder(projectId, folderId, { destinationFolderId: 'dest' }),
      urlPart: `/projects/${projectId}/folders/${folderId}/move`,
      method: 'PATCH',
    },
  ];

  it.each(folderCases)('$name calls folders endpoint', async ({ call, urlPart, method }) => {
    mockFetch.mockResolvedValue(method === 'POST' || method === 'PUT' || method === 'PATCH' ? jsonOk({}) : jsonOk([]));
    await call();
    const init = mockFetch.mock.calls[0]?.[1];
    expect(mockFetch.mock.calls[0]?.[0]).toEqual(expect.stringContaining(urlPart));
    if (method) {
      expect(init).toEqual(expect.objectContaining({ method }));
    }
  });

  it('deleteFolder sends DELETE', async () => {
    mockFetch.mockResolvedValue(noContent());
    await api.projects.folders.deleteFolder(projectId, folderId);
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining(`/projects/${projectId}/folders/${folderId}`),
      expect.objectContaining({ method: 'DELETE' }),
    );
  });

  it('renameContentFile sends PATCH with newName', async () => {
    mockFetch.mockResolvedValue(jsonOk({ id: fileId, fileName: 'renamed.txt' }));
    await api.projects.renameContentFile(projectId, fileId, 'renamed.txt');
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining(`/projects/${projectId}/files/${fileId}/rename`),
      expect.objectContaining({
        method: 'PATCH',
        body: JSON.stringify({ newName: 'renamed.txt' }),
      }),
    );
  });

  it.each([
    ['', null],
    ['undefined', null],
    [undefined, null],
    ['folder-dest', 'folder-dest'],
  ])('moveContentFile normalizes destinationFolderId %j to %j', async (input, expected) => {
    mockFetch.mockResolvedValue(jsonOk({ id: fileId }));
    await api.projects.moveContentFile(projectId, fileId, { destinationFolderId: input as string | undefined });
    const body = JSON.parse(mockFetch.mock.calls[0]?.[1]?.body as string);
    expect(body.destinationFolderId).toBe(expected);
  });

  it('deleteContentFile enriches file-in-use 400 errors', async () => {
    const errBody = { notebooksUsingFile: ['nb-1'], message: 'File is in use' };
    mockFetch.mockResolvedValue({
      ok: false,
      status: 400,
      statusText: 'Bad Request',
      headers: {
        get: vi.fn((name: string) => (name.toLowerCase() === 'content-type' ? 'application/json' : null)),
      },
      json: vi.fn().mockResolvedValue(errBody),
    });

    try {
      await api.projects.deleteContentFile(projectId, fileId);
      expect.fail('expected throw');
    } catch (error: unknown) {
      expect(error).toMatchObject({
        status: 400,
        isFileInUse: true,
        notebooksUsingFile: ['nb-1'],
        message: 'File is in use',
      });
    }
  });

  it('uploadFiles with folderId appends folderId to form', async () => {
    const file = new File(['x'], 'a.txt', { type: 'text/plain' });
    mockFetch.mockResolvedValue(jsonOk({ id: 'f1' }));
    await api.projects.uploadFiles(projectId, [file], folderId);
    const body = mockFetch.mock.calls[0]?.[1]?.body as FormData;
    expect(body.get('folderId')).toBe(folderId);
  });

  it('getContentFileContent returns cached blob without network fetch', async () => {
    const blob = new Blob(['cached'], { type: 'text/plain' });
    mockedGetCachedFile.mockResolvedValue({ blob, contentType: 'text/plain', fileName: 'cached.txt' });
    const result = await api.projects.getContentFileContent(projectId, fileId);
    expect(result).toEqual({ blob, contentType: 'text/plain', fileName: 'cached.txt' });
    expect(mockFetch).not.toHaveBeenCalled();
  });

  it('getContentFileContent fetches blob with version query', async () => {
    const blob = new Blob(['data'], { type: 'text/plain' });
    mockFetch.mockResolvedValue(
      blobResponse(blob, {
        'Content-Type': 'text/plain',
        'Content-Disposition': "attachment; filename*=UTF-8''hello%20world.txt",
      }),
    );
    const result = await api.projects.getContentFileContent(projectId, fileId, 3);
    expect(mockFetch.mock.calls[0]?.[0]).toEqual(expect.stringContaining('?v=3'));
    expect(result.blob).toBe(blob);
    expect(result.fileName).toBe('hello world.txt');
  });

  it('getContentFileMarkdownContent returns blob on success', async () => {
    const blob = new Blob(['# md'], { type: 'text/markdown' });
    mockFetch.mockResolvedValue(
      blobResponse(blob, {
        'Content-Type': 'text/markdown',
        'Content-Disposition': 'attachment; filename="doc.md"',
      }),
    );
    const result = await api.projects.getContentFileMarkdownContent(projectId, fileId);
    expect(result).toMatchObject({ contentType: 'text/markdown', fileName: 'doc.md' });
  });

  it('getContentFileMarkdownContent throws when not ok', async () => {
    mockFetch.mockResolvedValue({ ok: false, status: 404, headers: { get: vi.fn() } });
    await expect(api.projects.getContentFileMarkdownContent(projectId, fileId)).rejects.toThrow(
      'Failed to fetch markdown content',
    );
  });

  it('project link CRUD endpoints', async () => {
    mockFetch.mockResolvedValue(jsonOk({ id: 'l1' }));
    await api.projects.addLink(projectId, 'https://example.com');
    expect(mockFetch.mock.calls[0]?.[0]).toEqual(expect.stringContaining(`/projects/${projectId}/links`));

    mockFetch.mockResolvedValue(jsonOk({ id: 'l1' }));
    await api.projects.updateLink(projectId, 'l1', { url: 'https://new.example.com' });
    expect(mockFetch.mock.calls[1]?.[0]).toEqual(expect.stringContaining(`/projects/${projectId}/links/l1`));

    mockFetch.mockResolvedValue(noContent());
    await api.projects.deleteLink(projectId, 'l1');
    expect(mockFetch.mock.calls[2]?.[1]).toEqual(expect.objectContaining({ method: 'DELETE' }));
  });
});
