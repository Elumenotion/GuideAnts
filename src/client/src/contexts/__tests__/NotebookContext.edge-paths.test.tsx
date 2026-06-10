import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { NotebookProvider, useNotebook } from '../NotebookContext';
import { api } from '../../services/api';

const dispatchFlags = vi.hoisted(() => ({
  throwOnRemoveCell: false,
  throwOnAddCell: false,
  throwOnUpdateCell: false,
}));

vi.mock('react', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react')>();
  return {
    ...actual,
    useReducer: (reducer: Parameters<typeof actual.useReducer>[0], initialState: Parameters<typeof actual.useReducer>[1]) => {
      const [state, baseDispatch] = actual.useReducer(reducer, initialState);
      const dispatch = (action: { type?: string }) => {
        if (dispatchFlags.throwOnRemoveCell && action?.type === 'REMOVE_CELL') {
          throw new Error('dispatch failed');
        }
        if (dispatchFlags.throwOnAddCell && action?.type === 'ADD_CELL') {
          throw new Error('add cell failed');
        }
        if (dispatchFlags.throwOnUpdateCell && action?.type === 'UPDATE_CELL') {
          throw new Error('update cell failed');
        }
        return baseDispatch(action as never);
      };
      return [state, dispatch];
    },
  };
});

const mockShowToast = vi.fn();

vi.mock('../../services/api', () => ({
  api: {
    projects: {
      notebooks: {
        getNotebook: vi.fn(),
        getNotebookFolderTree: vi.fn(),
        uploadNotebookFiles: vi.fn(),
        createNotebookFolder: vi.fn(),
        renameNotebookItem: vi.fn(),
        deleteNotebookFileById: vi.fn(),
        renameNotebookFileById: vi.fn(),
        moveNotebookFileById: vi.fn(),
        copyFileFromProject: vi.fn(),
        setHomePageFile: vi.fn(),
        clearHomePage: vi.fn(),
        conversations: {
          getAll: vi.fn(),
          create: vi.fn(),
          rename: vi.fn(),
          delete: vi.fn(),
        },
      },
      notebookTemplates: {
        getAssistants: vi.fn(),
      },
    },
  },
}));

vi.mock('../../services/notebookFiles', () => ({
  notebookFilesApi: {
    delete: vi.fn(),
  },
}));

vi.mock('../../components/common/Toast', () => ({
  useToast: () => ({ showToast: mockShowToast }),
  ToastProvider: ({ children }: { children: React.ReactNode }) => children,
}));

const mockApi = api as unknown as {
  projects: {
    notebooks: {
      getNotebook: ReturnType<typeof vi.fn>;
      conversations: { getAll: ReturnType<typeof vi.fn> };
      getNotebookFolderTree: ReturnType<typeof vi.fn>;
    };
    notebookTemplates: { getAssistants: ReturnType<typeof vi.fn> };
  };
};

const PROJECT_ID = 'project-1';
const NOTEBOOK_ID = 'notebook-1';

const notebookFixture = {
  id: NOTEBOOK_ID,
  name: 'Test Notebook',
  guideId: 'guide-1',
  cells: [],
};

function wrapper({ children }: { children: React.ReactNode }) {
  return (
    <NotebookProvider projectId={PROJECT_ID} notebookId={NOTEBOOK_ID}>
      {children}
    </NotebookProvider>
  );
}

describe('NotebookContext edge paths', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    dispatchFlags.throwOnRemoveCell = false;
    dispatchFlags.throwOnAddCell = false;
    dispatchFlags.throwOnUpdateCell = false;
    mockApi.projects.notebooks.getNotebook.mockResolvedValue(notebookFixture);
    mockApi.projects.notebooks.conversations.getAll.mockResolvedValue([]);
    mockApi.projects.notebooks.getNotebookFolderTree.mockResolvedValue({});
    mockApi.projects.notebookTemplates.getAssistants.mockResolvedValue([]);
  });

  it('sets error when deleteCell dispatch throws', async () => {
    dispatchFlags.throwOnRemoveCell = true;
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.notebook).not.toBeNull());

    await act(async () => {
      await result.current.createCell({ type: 'markdown', content: '# temp' });
    });
    const cellId = result.current.notebook!.cells[0].id;

    await act(async () => {
      await result.current.deleteCell(cellId);
    });

    expect(consoleSpy).toHaveBeenCalledWith('Delete cell error:', expect.any(Error));
    expect(result.current.error).toBe('Failed to delete cell');
    consoleSpy.mockRestore();
  });

  it('sets error when createCell dispatch throws', async () => {
    dispatchFlags.throwOnAddCell = true;
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.notebook).not.toBeNull());

    await act(async () => {
      await result.current.createCell({ type: 'markdown', content: '# fail' });
    });

    expect(consoleSpy).toHaveBeenCalledWith('Create cell error:', expect.any(Error));
    expect(result.current.error).toBe('Failed to create cell');
    consoleSpy.mockRestore();
  });

  it('sets error when updateCell dispatch throws', async () => {
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.notebook).not.toBeNull());

    await act(async () => {
      await result.current.createCell({ type: 'markdown', content: '# ok' });
    });
    const cellId = result.current.notebook!.cells[0].id;
    dispatchFlags.throwOnUpdateCell = true;

    await act(async () => {
      await result.current.updateCell(cellId, { content: '# broken' });
    });

    expect(consoleSpy).toHaveBeenCalledWith('Update cell error:', expect.any(Error));
    expect(result.current.error).toBe('Failed to update cell');
    consoleSpy.mockRestore();
  });

  it('sets error when executeCell fails during mock execution', async () => {
    const realSetTimeout = global.setTimeout;
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.notebook).not.toBeNull());

    await act(async () => {
      await result.current.createCell({ type: 'code', content: 'print(1)', language: 'python' });
    });
    const codeCellId = result.current.notebook!.cells[0].id;

    const setTimeoutSpy = vi.spyOn(global, 'setTimeout').mockImplementation((handler, timeout, ...args) => {
      if (timeout === 1000) {
        throw new Error('timer failed');
      }
      return realSetTimeout(handler, timeout, ...args);
    });

    await act(async () => {
      await result.current.executeCell(codeCellId);
    });

    expect(consoleSpy).toHaveBeenCalledWith('Execute cell error:', expect.any(Error));
    expect(result.current.error).toBe('Failed to execute cell');
    expect(result.current.isExecuting).toBe(false);

    setTimeoutSpy.mockRestore();
    consoleSpy.mockRestore();
  });
});
