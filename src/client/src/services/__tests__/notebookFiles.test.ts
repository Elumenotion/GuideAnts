import { describe, it, expect, vi, beforeEach } from 'vitest';

// Mock the file-cache helpers used inside notebookFiles.ts
vi.mock('../../utils/fileCache', () => ({
  getCachedFile: vi.fn(),
  cacheFile: vi.fn(),
  deleteCachedFile: vi.fn(),
}));

// Provide a global fetch mock before loading the module-under-test
const mockFetch = vi.fn();
global.fetch = mockFetch as any;

import { getCachedFile, cacheFile, deleteCachedFile } from '../../utils/fileCache';
import { notebookFilesApi } from '../notebookFiles';

const mockedGetCached = vi.mocked(getCachedFile);
const mockedCacheFile = vi.mocked(cacheFile);
const mockedDeleteCached = vi.mocked(deleteCachedFile);

describe('notebookFilesApi', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockedCacheFile.mockResolvedValue(undefined);
    mockedDeleteCached.mockResolvedValue(undefined);
  });

  describe('getNotebookFileContent', () => {
    it('should return cached file when available', async () => {
      const mockCachedFile = {
        blob: new Blob(['cached content'], { type: 'text/plain' }),
        contentType: 'text/plain',
        fileName: 'test.txt',
        updatedAt: Date.now()
      };

      mockedGetCached.mockResolvedValue(mockCachedFile);

      const result = await notebookFilesApi.getNotebookFileContent('project-1', 'notebook-1', 'test.txt');

      expect(mockedGetCached).toHaveBeenCalledWith('project-1', 'notebook-1:test.txt');
      expect(mockFetch).not.toHaveBeenCalled();
      expect(result).toBe(mockCachedFile.blob);
    });

    it('should fetch from network and cache when not cached', async () => {
      const mockBlob = new Blob(['network content'], { type: 'text/plain' });
      const mockResponse = {
        ok: true,
        status: 200,
        blob: vi.fn().mockResolvedValue(mockBlob),
        headers: {
          get: vi.fn().mockReturnValue('text/plain')
        }
      };

      mockedGetCached.mockResolvedValue(null);
      mockFetch.mockResolvedValue(mockResponse);

      const result = await notebookFilesApi.getNotebookFileContent('project-1', 'notebook-1', 'test.txt');

      expect(mockedGetCached).toHaveBeenCalledWith('project-1', 'notebook-1:test.txt');
      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/projects/project-1/notebooks/notebook-1/files/content?path=test.txt'),
        expect.objectContaining({ headers: expect.any(Headers) })
      );
      expect(mockedCacheFile).toHaveBeenCalledWith('project-1', 'notebook-1:test.txt', {
        blob: mockBlob,
        contentType: 'text/plain',
        fileName: 'test.txt'
      });
      expect(result).toBe(mockBlob);
    });

    it('should handle cache errors gracefully', async () => {
      const mockBlob = new Blob(['network content'], { type: 'text/plain' });
      const mockResponse = {
        ok: true,
        status: 200,
        blob: vi.fn().mockResolvedValue(mockBlob),
        headers: {
          get: vi.fn().mockReturnValue('text/plain')
        }
      };

      mockedGetCached.mockRejectedValue(new Error('Cache error'));
      mockFetch.mockResolvedValue(mockResponse);

      const consoleSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});

      const result = await notebookFilesApi.getNotebookFileContent('project-1', 'notebook-1', 'test.txt');

      expect(consoleSpy).toHaveBeenCalledWith('IndexedDB cache read failed – falling back to network', expect.any(Error));
      expect(mockFetch).toHaveBeenCalled();
      expect(result).toBe(mockBlob);

      consoleSpy.mockRestore();
    });

    it('should handle network errors', async () => {
      mockedGetCached.mockResolvedValue(null);
      mockFetch.mockResolvedValue({
        ok: false,
        status: 404,
        statusText: 'Not Found'
      });

      await expect(
        notebookFilesApi.getNotebookFileContent('project-1', 'notebook-1', 'nonexistent.txt')
      ).rejects.toThrow('File not found');
    });
  });

  describe('CRUD endpoints', () => {
    it('listFiles calls notebook files endpoint', async () => {
      const files = [{ id: 'f1', relativePath: 'a.txt' }];
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        json: vi.fn().mockResolvedValue(files),
      });

      const result = await notebookFilesApi.listFiles('project-1', 'notebook-1');

      expect(result).toEqual(files);
      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/projects/project-1/notebooks/notebook-1/files'),
        expect.any(Object),
      );
    });

    it('getFolderTree calls tree endpoint', async () => {
      const tree = { id: 'root', name: 'root', children: [] };
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        json: vi.fn().mockResolvedValue(tree),
      });

      const result = await notebookFilesApi.getFolderTree('project-1', 'notebook-1');
      expect(result).toEqual(tree);
    });

    it('createFolder posts folder payload', async () => {
      const tree = { id: 'root', name: 'root', children: [] };
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        json: vi.fn().mockResolvedValue(tree),
      });

      await notebookFilesApi.createFolder('project-1', 'notebook-1', {
        parentRelativePath: '',
        folderName: 'docs',
      });

      expect(mockFetch).toHaveBeenCalledWith(
        expect.stringContaining('/files/create-folder'),
        expect.objectContaining({ method: 'POST' }),
      );
    });

    it('move clears cache for old path', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        json: vi.fn().mockResolvedValue(undefined),
      });

      await notebookFilesApi.move('project-1', 'notebook-1', 'old/path.txt', 'new-folder');

      expect(mockedDeleteCached).toHaveBeenCalledWith('project-1', 'notebook-1:old/path.txt');
    });

    it('copyFromProject clears cache for copied file', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        json: vi.fn().mockResolvedValue({ relativePath: 'imports/file.txt' }),
      });

      await notebookFilesApi.copyFromProject('project-1', 'notebook-1', 'source-file');

      expect(mockedDeleteCached).toHaveBeenCalledWith('project-1', 'notebook-1:imports/file.txt');
    });

    it('returns undefined for 204 responses', async () => {
      mockFetch.mockResolvedValue({
        ok: true,
        status: 204,
      });

      const result = await notebookFilesApi.sync('project-1', 'notebook-1');
      expect(result).toBeUndefined();
    });
  });

  describe('getNotebookFileContent branches', () => {
    it('refetches when cached hash does not match current hash', async () => {
      const staleBlob = new Blob(['stale'], { type: 'text/plain' });
      mockedGetCached.mockResolvedValue({
        blob: staleBlob,
        contentType: 'text/plain',
        fileName: 'test.txt',
        fileHash: 'old-hash',
      });

      const freshBlob = new Blob(['fresh'], { type: 'text/plain' });
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        blob: vi.fn().mockResolvedValue(freshBlob),
        headers: { get: vi.fn().mockReturnValue('text/plain') },
      });

      const result = await notebookFilesApi.getNotebookFileContent(
        'project-1',
        'notebook-1',
        'test.txt',
        'new-hash',
      );

      expect(mockFetch).toHaveBeenCalled();
      expect(result).toBe(freshBlob);
    });

    it('forces network fetch when forceNetwork is true', async () => {
      const cachedBlob = new Blob(['cached'], { type: 'text/plain' });
      mockedGetCached.mockResolvedValue({
        blob: cachedBlob,
        contentType: 'text/plain',
        fileName: 'test.txt',
      });

      const networkBlob = new Blob(['network'], { type: 'text/plain' });
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        blob: vi.fn().mockResolvedValue(networkBlob),
        headers: { get: vi.fn().mockReturnValue('text/plain') },
      });

      const result = await notebookFilesApi.getNotebookFileContent(
        'project-1',
        'notebook-1',
        'test.txt',
        undefined,
        true,
      );

      expect(mockFetch).toHaveBeenCalled();
      expect(result).toBe(networkBlob);
    });
  });

  describe('markdown content', () => {
    it('returns cached markdown when hash matches', async () => {
      const cached = {
        blob: new Blob(['# md'], { type: 'text/markdown' }),
        contentType: 'text/markdown',
        fileName: 'doc.md',
        fileHash: 'hash-1',
      };
      mockedGetCached.mockResolvedValue(cached);

      const result = await notebookFilesApi.getNotebookFileMarkdownContent(
        'project-1',
        'notebook-1',
        'file-1',
        'hash-1',
      );

      expect(result.blob).toBe(cached.blob);
      expect(mockFetch).not.toHaveBeenCalled();
    });

    it('parses filename from content-disposition on network fetch', async () => {
      mockedGetCached.mockResolvedValue(null);
      const blob = new Blob(['# hello'], { type: 'text/markdown' });
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        blob: vi.fn().mockResolvedValue(blob),
        headers: {
          get: vi.fn((header: string) => {
            if (header === 'Content-Type') return 'text/markdown';
            if (header === 'Content-Disposition') return 'attachment; filename="exported.md"';
            return null;
          }),
        },
      });

      const result = await notebookFilesApi.getNotebookFileMarkdownContent(
        'project-1',
        'notebook-1',
        'file-1',
        'hash-2',
      );

      expect(result.fileName).toBe('exported.md');
      expect(mockedCacheFile).toHaveBeenCalled();
    });
  });

  describe('error and auth branches', () => {
    it('broadcasts auth expiry on 401 API responses', async () => {
      const authSpy = vi.spyOn(await import('../authEvents'), 'broadcastAuthExpired');
      mockFetch.mockResolvedValue({
        ok: false,
        status: 401,
        statusText: 'Unauthorized',
      });

      await expect(notebookFilesApi.listFiles('project-1', 'notebook-1')).rejects.toThrow('API call failed');
      expect(authSpy).toHaveBeenCalledWith('Authentication expired.');
      authSpy.mockRestore();
    });

    it('throws generic API error for non-404 content failures', async () => {
      mockedGetCached.mockResolvedValue(null);
      mockFetch.mockResolvedValue({
        ok: false,
        status: 500,
        statusText: 'Server Error',
      });

      await expect(
        notebookFilesApi.getNotebookFileContent('project-1', 'notebook-1', 'broken.txt'),
      ).rejects.toThrow('API call failed: Server Error');
    });

    it('logs and rethrows network failures from callNotebookApi', async () => {
      const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      mockFetch.mockRejectedValueOnce(new Error('network down'));

      await expect(notebookFilesApi.getFolderTree('project-1', 'notebook-1')).rejects.toThrow('network down');
      expect(consoleSpy).toHaveBeenCalledWith('Notebook API call error:', expect.any(Error));
      consoleSpy.mockRestore();
    });

    it('broadcasts auth expiry when upload returns 401', async () => {
      const authSpy = vi.spyOn(await import('../authEvents'), 'broadcastAuthExpired');
      mockFetch.mockResolvedValue({
        ok: false,
        status: 401,
        statusText: 'Unauthorized',
      });

      const files = [new File(['content'], 'test.txt', { type: 'text/plain' })];
      await expect(
        notebookFilesApi.uploadFiles('project-1', 'notebook-1', files, '', false),
      ).rejects.toThrow('Failed to upload files');
      expect(authSpy).toHaveBeenCalledWith('Authentication expired.');
      authSpy.mockRestore();
    });

    it('warns when cache write fails after network fetch', async () => {
      const consoleSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
      mockedGetCached.mockResolvedValue(null);
      mockedCacheFile.mockRejectedValueOnce(new Error('cache write failed'));
      const mockBlob = new Blob(['network content'], { type: 'text/plain' });
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        blob: vi.fn().mockResolvedValue(mockBlob),
        headers: { get: vi.fn().mockReturnValue('text/plain') },
      });

      const result = await notebookFilesApi.getNotebookFileContent('project-1', 'notebook-1', 'test.txt');
      expect(result).toBe(mockBlob);
      await new Promise((resolve) => setTimeout(resolve, 0));
      expect(consoleSpy).toHaveBeenCalledWith('Cache write failed', expect.any(Error));
      consoleSpy.mockRestore();
    });
  });

  describe('additional endpoints', () => {
    it('fetches origin file info', async () => {
      const info = { contentFileId: 'cf-1', fileName: 'source.txt' };
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        json: vi.fn().mockResolvedValue(info),
      });

      const result = await notebookFilesApi.getOriginFileInfo('project-1', 'notebook-1', 'version-1');
      expect(result).toEqual(info);
    });

    it('publishes notebook file to project', async () => {
      const publishResult = { contentFileId: 'cf-2' };
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        json: vi.fn().mockResolvedValue(publishResult),
      });

      const result = await notebookFilesApi.publishToProject('project-1', 'notebook-1', {
        notebookFileId: 'nf-1',
        destinationFolderId: 'folder-1',
      });

      expect(result).toEqual(publishResult);
    });

    it('logs placeholder warning for clearNotebookCache', async () => {
      const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
      await notebookFilesApi.clearNotebookCache('project-1', 'notebook-1');
      expect(warnSpy).toHaveBeenCalledWith('clearNotebookCache not yet implemented');
      warnSpy.mockRestore();
    });
  });

  describe('markdown content branches', () => {
    it('warns when markdown cache read fails', async () => {
      const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
      mockedGetCached.mockRejectedValueOnce(new Error('cache read failed'));
      const blob = new Blob(['# md'], { type: 'text/markdown' });
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        blob: vi.fn().mockResolvedValue(blob),
        headers: {
          get: vi.fn((header: string) => {
            if (header === 'Content-Type') return 'text/markdown';
            if (header === 'Content-Disposition') return "attachment; filename*=UTF-8''encoded%20name.md";
            return null;
          }),
        },
      });

      const result = await notebookFilesApi.getNotebookFileMarkdownContent(
        'project-1',
        'notebook-1',
        'file-1',
        'hash-1',
      );

      expect(result.fileName).toBe('encoded name.md');
      expect(warnSpy).toHaveBeenCalledWith('Cache read failed for markdown', expect.any(Error));
      warnSpy.mockRestore();
    });

    it('falls back when UTF-8 filename decoding fails', async () => {
      mockedGetCached.mockResolvedValue(null);
      const blob = new Blob(['# md'], { type: 'text/markdown' });
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        blob: vi.fn().mockResolvedValue(blob),
        headers: {
          get: vi.fn((header: string) => {
            if (header === 'Content-Type') return 'text/markdown';
            if (header === 'Content-Disposition') return "attachment; filename*=UTF-8''%E0%A4%A";
            return null;
          }),
        },
      });

      const result = await notebookFilesApi.getNotebookFileMarkdownContent(
        'project-1',
        'notebook-1',
        'file-1',
        'hash-2',
      );

      expect(result.fileName).toBe('%E0%A4%A');
    });

    it('throws when markdown content fetch fails', async () => {
      mockFetch.mockResolvedValue({
        ok: false,
        status: 500,
        statusText: 'Server Error',
      });

      await expect(
        notebookFilesApi.getNotebookFileMarkdownContent('project-1', 'notebook-1', 'file-1'),
      ).rejects.toThrow('Failed to fetch markdown content');
    });

    it('skips markdown cache write when current hash is omitted', async () => {
      const blob = new Blob(['# md'], { type: 'text/markdown' });
      mockFetch.mockResolvedValue({
        ok: true,
        status: 200,
        blob: vi.fn().mockResolvedValue(blob),
        headers: {
          get: vi.fn((header: string) => {
            if (header === 'Content-Type') return 'text/markdown';
            return null;
          }),
        },
      });

      const result = await notebookFilesApi.getNotebookFileMarkdownContent(
        'project-1',
        'notebook-1',
        'file-1',
      );

      expect(result.fileName).toBe('markdown-content.md');
      expect(mockedCacheFile).not.toHaveBeenCalled();
    });
  });

  describe('cache invalidation', () => {
    it('should clear cache when uploading files', async () => {
      const mockResponse = {
        ok: true,
        status: 200,
        json: vi.fn().mockResolvedValue([{ id: 'file-1', relativePath: 'folder/test.txt' }])
      };
      mockFetch.mockResolvedValue(mockResponse);

      const files = [new File(['content'], 'test.txt', { type: 'text/plain' })];
      await notebookFilesApi.uploadFiles('project-1', 'notebook-1', files, 'folder', false);

      expect(mockedDeleteCached).toHaveBeenCalledWith('project-1', 'notebook-1:folder/test.txt');
    });

    it('should clear cache when renaming files', async () => {
      const mockResponse = {
        ok: true,
        status: 200,
        json: vi.fn().mockResolvedValue(undefined)
      };
      mockFetch.mockResolvedValue(mockResponse);

      await notebookFilesApi.rename('project-1', 'notebook-1', 'old.txt', 'new.txt');

      expect(mockedDeleteCached).toHaveBeenCalledWith('project-1', 'notebook-1:old.txt');
    });

    it('clearNotebookFileCache deletes specific cache key', async () => {
      await notebookFilesApi.clearNotebookFileCache('project-1', 'notebook-1', 'notes.md');
      expect(mockedDeleteCached).toHaveBeenCalledWith('project-1', 'notebook-1:notes.md');
    });

    it('should clear cache when deleting files', async () => {
      const mockResponse = {
        ok: true,
        status: 200,
        json: vi.fn().mockResolvedValue(undefined)
      };
      mockFetch.mockResolvedValue(mockResponse);

      await notebookFilesApi.delete('project-1', 'notebook-1', 'test.txt');

      expect(mockedDeleteCached).toHaveBeenCalledWith('project-1', 'notebook-1:test.txt');
    });
  });
});
