import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useNotebookFilesPolling } from '../useNotebookFilesPolling';
import { api } from '../../services/api';
import type { NotebookFolderTreeDto } from '../../types/notebook';

vi.mock('../../services/api', () => ({
  api: {
    projects: {
      notebooks: {
        getNotebookFolderTree: vi.fn(),
      },
    },
  },
}));

const mockTree: NotebookFolderTreeDto = {
  name: 'root',
  relativePath: '',
  subFolders: [],
  files: [
    {
      id: 'f1',
      fileName: 'notes.md',
      relativePath: 'notes.md',
      fileSize: 100,
      fileHash: 'abc',
    } as NotebookFolderTreeDto['files'][number],
  ],
};

describe('useNotebookFilesPolling', () => {
  const projectId = 'proj-1';
  const notebookId = 'nb-1';

  beforeEach(() => {
    vi.clearAllMocks();
    (api.projects.notebooks.getNotebookFolderTree as ReturnType<typeof vi.fn>).mockResolvedValue(
      mockTree
    );
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('loads folder tree on mount with loading state', async () => {
    const { result } = renderHook(() =>
      useNotebookFilesPolling({ projectId, notebookId, pollInterval: 5000 })
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(result.current.folderTree).toEqual(mockTree);
    expect(result.current.isLoading).toBe(false);
    expect(result.current.lastUpdated).toBeInstanceOf(Date);
  });

  it('skips state update when tree is unchanged', async () => {
    vi.useFakeTimers();

    const { result } = renderHook(() =>
      useNotebookFilesPolling({ projectId, notebookId, pollInterval: 1000 })
    );

    await act(async () => {
      await Promise.resolve();
    });

    const treeRef = result.current.folderTree;

    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });

    expect(result.current.folderTree).toBe(treeRef);
  });

  it('updates when tree changes', async () => {
    vi.useFakeTimers();

    const updatedTree: NotebookFolderTreeDto = {
      ...mockTree,
      files: [
        ...mockTree.files,
        {
          id: 'f2',
          fileName: 'extra.md',
          relativePath: 'extra.md',
          fileSize: 50,
          fileHash: 'def',
        } as NotebookFolderTreeDto['files'][number],
      ],
    };

    const { result } = renderHook(() =>
      useNotebookFilesPolling({ projectId, notebookId, pollInterval: 1000 })
    );

    await act(async () => {
      await Promise.resolve();
    });

    (api.projects.notebooks.getNotebookFolderTree as ReturnType<typeof vi.fn>).mockResolvedValue(
      updatedTree
    );

    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });

    expect(result.current.folderTree?.files).toHaveLength(2);
  });

  it('does not poll when disabled or missing ids', async () => {
    renderHook(() =>
      useNotebookFilesPolling({
        projectId: '',
        notebookId: '',
        enabled: false,
      })
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(api.projects.notebooks.getNotebookFolderTree).not.toHaveBeenCalled();
  });

  it('handles errors', async () => {
    (api.projects.notebooks.getNotebookFolderTree as ReturnType<typeof vi.fn>).mockRejectedValue(
      new Error('Tree fetch failed')
    );

    const { result } = renderHook(() =>
      useNotebookFilesPolling({ projectId, notebookId })
    );

    await act(async () => {
      await Promise.resolve();
    });

    expect(result.current.error).toBe('Tree fetch failed');
  });

  it('refresh shows loading state', async () => {
    const { result } = renderHook(() =>
      useNotebookFilesPolling({ projectId, notebookId })
    );

    await act(async () => {
      await Promise.resolve();
    });

    await act(async () => {
      result.current.refresh();
      await Promise.resolve();
    });

    expect(api.projects.notebooks.getNotebookFolderTree).toHaveBeenCalled();
    expect(result.current.isLoading).toBe(false);
  });

  it('updates when file metadata changes', async () => {
    vi.useFakeTimers();

    const { result } = renderHook(() =>
      useNotebookFilesPolling({ projectId, notebookId, pollInterval: 1000 })
    );

    await act(async () => {
      await Promise.resolve();
    });

    const changedTree: NotebookFolderTreeDto = {
      ...mockTree,
      files: [{ ...mockTree.files[0], fileHash: 'changed-hash' }],
    };

    (api.projects.notebooks.getNotebookFolderTree as ReturnType<typeof vi.fn>).mockResolvedValue(
      changedTree
    );

    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });

    expect(result.current.folderTree?.files[0].fileHash).toBe('changed-hash');
  });

  it('updates when subfolder count changes', async () => {
    vi.useFakeTimers();

    const { result } = renderHook(() =>
      useNotebookFilesPolling({ projectId, notebookId, pollInterval: 1000 })
    );

    await act(async () => {
      await Promise.resolve();
    });

    const withSubfolder: NotebookFolderTreeDto = {
      ...mockTree,
      subFolders: [
        {
          name: 'docs',
          relativePath: 'docs',
          subFolders: [],
          files: [],
        },
      ],
    };

    (api.projects.notebooks.getNotebookFolderTree as ReturnType<typeof vi.fn>).mockResolvedValue(
      withSubfolder
    );

    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });

    expect(result.current.folderTree?.subFolders).toHaveLength(1);
  });

  it('ignores results after unmount aborts the fetch', async () => {
    let resolveTree: (tree: NotebookFolderTreeDto) => void = () => {};
    (api.projects.notebooks.getNotebookFolderTree as ReturnType<typeof vi.fn>).mockImplementation(
      () =>
        new Promise((resolve) => {
          resolveTree = resolve;
        })
    );

    const { unmount } = renderHook(() =>
      useNotebookFilesPolling({ projectId, notebookId })
    );

    unmount();

    await act(async () => {
      resolveTree(mockTree);
      await Promise.resolve();
    });
  });

  it('shares one poller across multiple hook instances for the same notebook', async () => {
    vi.useFakeTimers();

    renderHook(() => useNotebookFilesPolling({ projectId, notebookId, pollInterval: 1000 }));
    renderHook(() => useNotebookFilesPolling({ projectId, notebookId, pollInterval: 1000 }));

    await act(async () => {
      await Promise.resolve();
    });

    expect(api.projects.notebooks.getNotebookFolderTree).toHaveBeenCalledTimes(1);

    await act(async () => {
      vi.advanceTimersByTime(1000);
      await Promise.resolve();
    });

    expect(api.projects.notebooks.getNotebookFolderTree).toHaveBeenCalledTimes(2);
  });
});
