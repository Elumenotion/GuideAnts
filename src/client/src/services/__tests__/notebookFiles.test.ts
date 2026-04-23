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
        expect.stringContaining('/projects/project-1/notebooks/notebook-1/files/content?path=test.txt')
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
