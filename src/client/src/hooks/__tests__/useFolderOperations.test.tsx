import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useFolderOperations } from '../useFolderOperations';
import { api } from '../../services/api';
import { ProjectFolderDto, FolderTreeDto, CreateFolderDto, UpdateFolderDto, MoveFolderDto, MoveFileDto } from '../../types/project';

vi.mock('../../services/api', () => ({
  api: {
    projects: {
      folders: {
        getFolderTree: vi.fn(),
        getFolders: vi.fn(),
        createFolder: vi.fn(),
        updateFolder: vi.fn(),
        deleteFolder: vi.fn(),
        moveFolder: vi.fn(),
      },
      moveContentFile: vi.fn(),
      uploadFiles: vi.fn(),
    }
  }
}));

describe('useFolderOperations', () => {
  const projectId = '123';
  
  const mockFolder: ProjectFolderDto = {
    id: 'folder-1',
    name: 'Test Folder',
    relativePath: 'Test Folder',
    projectId,
    created: '2023-01-01T00:00:00Z'
  };

  const mockFolderTree: FolderTreeDto = {
    id: undefined,
    name: 'Test Project',
    relativePath: '',
    subFolders: [{
      id: 'folder-1',
      name: 'Test Folder',
      relativePath: 'Test Folder',
      subFolders: [],
      files: []
    }],
    files: []
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('fetchFolderTree', () => {
    it('fetches folder tree successfully', async () => {
      (api.projects.folders.getFolderTree as any).mockResolvedValue(mockFolderTree);

      const { result } = renderHook(() => useFolderOperations(projectId));

      await act(async () => {
        await result.current.fetchFolderTree();
      });

      expect(result.current.folderTree).toEqual(mockFolderTree);
      expect(result.current.isLoading).toBe(false);
      expect(result.current.error).toBeNull();
      expect(api.projects.folders.getFolderTree).toHaveBeenCalledWith(projectId);
    });

    it('handles fetch error', async () => {
      const errorMessage = 'Failed to fetch folder tree';
      (api.projects.folders.getFolderTree as any).mockRejectedValue(new Error(errorMessage));

      const { result } = renderHook(() => useFolderOperations(projectId));

      await act(async () => {
        try {
          await result.current.fetchFolderTree();
        } catch (error) {
          // Expected to throw
        }
      });

      expect(result.current.error).toBe(errorMessage);
      expect(result.current.folderTree).toBeNull();
    });

    it('does not fetch when projectId is empty', async () => {
      const { result } = renderHook(() => useFolderOperations(''));

      await act(async () => {
        await result.current.fetchFolderTree();
      });

      expect(api.projects.folders.getFolderTree).not.toHaveBeenCalled();
    });
  });

  describe('fetchFolders', () => {
    it('fetches folders list successfully', async () => {
      const mockFolders = [mockFolder];
      (api.projects.folders.getFolders as any).mockResolvedValue(mockFolders);

      const { result } = renderHook(() => useFolderOperations(projectId));

      await act(async () => {
        await result.current.fetchFolders();
      });

      expect(result.current.folders).toEqual(mockFolders);
      expect(result.current.isLoading).toBe(false);
      expect(result.current.error).toBeNull();
      expect(api.projects.folders.getFolders).toHaveBeenCalledWith(projectId);
    });
  });

  describe('createFolder', () => {
    it('creates folder successfully', async () => {
      const createDto: CreateFolderDto = { name: 'New Folder' };
      (api.projects.folders.createFolder as any).mockResolvedValue(mockFolder);
      (api.projects.folders.getFolderTree as any).mockResolvedValue(mockFolderTree);
      (api.projects.folders.getFolders as any).mockResolvedValue([mockFolder]);

      const { result } = renderHook(() => useFolderOperations(projectId));

      let createdFolder: ProjectFolderDto;
      await act(async () => {
        createdFolder = await result.current.createFolder(createDto);
      });

      expect(createdFolder!).toEqual(mockFolder);
      expect(api.projects.folders.createFolder).toHaveBeenCalledWith(projectId, createDto);
      expect(api.projects.folders.getFolderTree).toHaveBeenCalled();
      expect(api.projects.folders.getFolders).toHaveBeenCalled();
    });

    it('throws error when projectId is empty', async () => {
      const { result } = renderHook(() => useFolderOperations(''));

      await act(async () => {
        await expect(result.current.createFolder({ name: 'Test' })).rejects.toThrow('Project ID is required');
      });
    });

    it('handles create error', async () => {
      const errorMessage = 'Folder already exists';
      (api.projects.folders.createFolder as any).mockRejectedValue(new Error(errorMessage));

      const { result } = renderHook(() => useFolderOperations(projectId));

      await act(async () => {
        await expect(result.current.createFolder({ name: 'Duplicate' })).rejects.toThrow(errorMessage);
      });

      expect(result.current.error).toBe(errorMessage);
    });
  });

  describe('updateFolder', () => {
    it('updates folder successfully', async () => {
      const updateDto: UpdateFolderDto = { name: 'Updated Folder' };
      const updatedFolder = { ...mockFolder, name: 'Updated Folder' };
      (api.projects.folders.updateFolder as any).mockResolvedValue(updatedFolder);
      (api.projects.folders.getFolderTree as any).mockResolvedValue(mockFolderTree);
      (api.projects.folders.getFolders as any).mockResolvedValue([updatedFolder]);

      const { result } = renderHook(() => useFolderOperations(projectId));

      let resultFolder: ProjectFolderDto;
      await act(async () => {
        resultFolder = await result.current.updateFolder('folder-1', updateDto);
      });

      expect(resultFolder!).toEqual(updatedFolder);
      expect(api.projects.folders.updateFolder).toHaveBeenCalledWith(projectId, 'folder-1', updateDto);
    });
  });

  describe('deleteFolder', () => {
    it('deletes folder successfully', async () => {
      (api.projects.folders.deleteFolder as any).mockResolvedValue(undefined);
      (api.projects.folders.getFolderTree as any).mockResolvedValue(mockFolderTree);
      (api.projects.folders.getFolders as any).mockResolvedValue([]);

      const { result } = renderHook(() => useFolderOperations(projectId));

      await act(async () => {
        await result.current.deleteFolder('folder-1');
      });

      expect(api.projects.folders.deleteFolder).toHaveBeenCalledWith(projectId, 'folder-1');
    });

    it('handles delete error', async () => {
      const errorMessage = 'Cannot delete non-empty folder';
      (api.projects.folders.deleteFolder as any).mockRejectedValue(new Error(errorMessage));

      const { result } = renderHook(() => useFolderOperations(projectId));

      await act(async () => {
        await expect(result.current.deleteFolder('folder-1')).rejects.toThrow(errorMessage);
      });

      expect(result.current.error).toBe(errorMessage);
    });
  });

  describe('moveFolder', () => {
    it('moves folder successfully', async () => {
      const moveDto: MoveFolderDto = { newParentFolderId: 'parent-folder' };
      (api.projects.folders.moveFolder as any).mockResolvedValue(mockFolder);
      (api.projects.folders.getFolderTree as any).mockResolvedValue(mockFolderTree);
      (api.projects.folders.getFolders as any).mockResolvedValue([mockFolder]);

      const { result } = renderHook(() => useFolderOperations(projectId));

      let movedFolder: ProjectFolderDto;
      await act(async () => {
        movedFolder = await result.current.moveFolder('folder-1', moveDto);
      });

      expect(movedFolder!).toEqual(mockFolder);
      expect(api.projects.folders.moveFolder).toHaveBeenCalledWith(projectId, 'folder-1', moveDto);
    });
  });

  describe('moveFile', () => {
    it('moves file successfully', async () => {
      const moveDto: MoveFileDto = { destinationFolderId: 'folder-1' };
      (api.projects.moveContentFile as any).mockResolvedValue(undefined);
      (api.projects.folders.getFolderTree as any).mockResolvedValue(mockFolderTree);

      const { result } = renderHook(() => useFolderOperations(projectId));

      await act(async () => {
        await result.current.moveFile('file-1', moveDto);
      });

      expect(api.projects.moveContentFile).toHaveBeenCalledWith(projectId, 'file-1', moveDto);
      expect(api.projects.folders.getFolderTree).toHaveBeenCalled();
    });
  });

  describe('uploadFiles', () => {
    it('uploads files successfully', async () => {
      const mockFiles = [new File(['content'], 'test.txt', { type: 'text/plain' })];
      const uploadResults = [{ id: 'file-1', fileName: 'test.txt' }];
      (api.projects.uploadFiles as any).mockResolvedValue(uploadResults);
      (api.projects.folders.getFolderTree as any).mockResolvedValue(mockFolderTree);

      const { result } = renderHook(() => useFolderOperations(projectId));

      let results: any[];
      await act(async () => {
        results = await result.current.uploadFiles(mockFiles, 'folder-1');
      });

      expect(results!).toEqual(uploadResults);
      expect(api.projects.uploadFiles).toHaveBeenCalledWith(projectId, mockFiles, 'folder-1');
      expect(api.projects.folders.getFolderTree).toHaveBeenCalled();
    });

    it('uploads files to root when no folder specified', async () => {
      const mockFiles = [new File(['content'], 'test.txt', { type: 'text/plain' })];
      const uploadResults = [{ id: 'file-1', fileName: 'test.txt' }];
      (api.projects.uploadFiles as any).mockResolvedValue(uploadResults);
      (api.projects.folders.getFolderTree as any).mockResolvedValue(mockFolderTree);

      const { result } = renderHook(() => useFolderOperations(projectId));

      await act(async () => {
        await result.current.uploadFiles(mockFiles);
      });

      expect(api.projects.uploadFiles).toHaveBeenCalledWith(projectId, mockFiles, undefined);
    });
  });

  describe('error handling', () => {
    it('sets and clears error state correctly', async () => {
      (api.projects.folders.getFolderTree as any).mockRejectedValue(new Error('Network error'));

      const { result } = renderHook(() => useFolderOperations(projectId));

      // Trigger error
      await act(async () => {
        try {
          await result.current.fetchFolderTree();
        } catch (error) {
          // Expected to throw
        }
      });

      expect(result.current.error).toBe('Network error');

      // Clear error with successful operation
      (api.projects.folders.getFolderTree as any).mockResolvedValue(mockFolderTree);
      await act(async () => {
        await result.current.fetchFolderTree();
      });

      expect(result.current.error).toBeNull();
    });
  });

  describe('loading states', () => {
    it('sets loading state during operations', async () => {
      let resolvePromise: (value: any) => void;
      const promise = new Promise((resolve) => {
        resolvePromise = resolve;
      });
      (api.projects.folders.getFolderTree as any).mockReturnValue(promise);

      const { result } = renderHook(() => useFolderOperations(projectId));

      // Start operation
      act(() => {
        result.current.fetchFolderTree();
      });

      expect(result.current.isLoading).toBe(true);

      // Complete operation
      await act(async () => {
        resolvePromise!(mockFolderTree);
        await promise;
      });

      expect(result.current.isLoading).toBe(false);
    });
  });
}); 