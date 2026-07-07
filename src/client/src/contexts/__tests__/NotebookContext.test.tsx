import React from 'react';

import { describe, it, expect, vi, beforeEach } from 'vitest';

import { renderHook, waitFor, act } from '@testing-library/react';

import { NotebookProvider, useNotebook } from '../NotebookContext';

import { api } from '../../services/api';

import { notebookFilesApi } from '../../services/notebookFiles';



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

      getNotebookFolderTree: ReturnType<typeof vi.fn>;

      uploadNotebookFiles: ReturnType<typeof vi.fn>;

      createNotebookFolder: ReturnType<typeof vi.fn>;

      renameNotebookItem: ReturnType<typeof vi.fn>;

      deleteNotebookFileById: ReturnType<typeof vi.fn>;

      renameNotebookFileById: ReturnType<typeof vi.fn>;

      moveNotebookFileById: ReturnType<typeof vi.fn>;

      copyFileFromProject: ReturnType<typeof vi.fn>;

      setHomePageFile: ReturnType<typeof vi.fn>;

      clearHomePage: ReturnType<typeof vi.fn>;

      conversations: {

        getAll: ReturnType<typeof vi.fn>;

        create: ReturnType<typeof vi.fn>;

        rename: ReturnType<typeof vi.fn>;

        delete: ReturnType<typeof vi.fn>;

      };

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



describe('NotebookContext', () => {

  beforeEach(() => {

    vi.clearAllMocks();

    mockApi.projects.notebooks.getNotebook.mockResolvedValue(notebookFixture);

    mockApi.projects.notebooks.conversations.getAll.mockResolvedValue([]);

    mockApi.projects.notebooks.getNotebookFolderTree.mockResolvedValue({});

    mockApi.projects.notebookTemplates.getAssistants.mockResolvedValue([

      { id: 'a1', name: 'Helper', avatarUrl: '/api/avatars/a1.png', model: 'gpt-4' },

    ]);

  });



  it('throws when useNotebook is used outside NotebookProvider', () => {

    expect(() => renderHook(() => useNotebook())).toThrow(

      'useNotebook must be used within a NotebookProvider',

    );

  });



  it('loads notebook and conversations on mount', async () => {

    const { result } = renderHook(() => useNotebook(), { wrapper });



    await waitFor(() => {

      expect(result.current.isLoading).toBe(false);

    });



    expect(mockApi.projects.notebooks.getNotebook).toHaveBeenCalledWith(

      PROJECT_ID,

      NOTEBOOK_ID,

    );

    expect(mockApi.projects.notebooks.conversations.getAll).toHaveBeenCalledWith(

      PROJECT_ID,

      NOTEBOOK_ID,

    );

    expect(result.current.notebook?.name).toBe('Test Notebook');

    expect(result.current.projectId).toBe(PROJECT_ID);

    expect(result.current.notebookId).toBe(NOTEBOOK_ID);

  });



  it('loads assistants when notebook has a guideId', async () => {

    const { result } = renderHook(() => useNotebook(), { wrapper });



    await waitFor(() => {

      expect(result.current.assistants).toHaveLength(1);

    });



    expect(mockApi.projects.notebookTemplates.getAssistants).toHaveBeenCalledWith(

      'guide-1',

      PROJECT_ID,

    );

    expect(result.current.assistants[0].name).toBe('Helper');

  });



  it('resolves relative assistant avatar URLs against API base', async () => {

    mockApi.projects.notebookTemplates.getAssistants.mockResolvedValue([

      { id: 'a1', name: 'Helper', avatarUrl: '/api/avatars/a1.png', modelDeploymentId: 'gpt-4' },

    ]);



    const { result } = renderHook(() => useNotebook(), { wrapper });



    await waitFor(() => {

      expect(result.current.assistants[0].avatarUrl).toContain('projectId=project-1');

    });

  });



  it('sets assistants error when assistant load fails', async () => {

    mockApi.projects.notebookTemplates.getAssistants.mockRejectedValueOnce(new Error('assistants down'));



    const { result } = renderHook(() => useNotebook(), { wrapper });



    await waitFor(() => {

      expect(result.current.assistantsError).toBe('assistants down');

      expect(result.current.isLoadingAssistants).toBe(false);

    });

  });



  it('skips assistant load when notebook has no guideId', async () => {

    mockApi.projects.notebooks.getNotebook.mockResolvedValueOnce({

      ...notebookFixture,

      guideId: undefined,

    });



    const { result } = renderHook(() => useNotebook(), { wrapper });



    await waitFor(() => {

      expect(result.current.isLoading).toBe(false);

    });



    expect(mockApi.projects.notebookTemplates.getAssistants).not.toHaveBeenCalled();

    expect(result.current.assistants).toEqual([]);

  });



  it('sets error when notebook load fails', async () => {

    mockApi.projects.notebooks.getNotebook.mockRejectedValueOnce(new Error('Network down'));



    const { result } = renderHook(() => useNotebook(), { wrapper });



    await waitFor(() => {

      expect(result.current.error).toBe('Network down');

    });

    expect(result.current.isLoading).toBe(false);

  });



  it('sets conversations error when conversation load fails', async () => {

    mockApi.projects.notebooks.conversations.getAll.mockRejectedValueOnce(new Error('convos failed'));



    const { result } = renderHook(() => useNotebook(), { wrapper });



    await waitFor(() => {

      expect(result.current.conversationsError).toBe('convos failed');

      expect(result.current.isLoadingConversations).toBe(false);

    });

  });



  it('creates, updates, executes, and deletes cells locally', async () => {

    const { result } = renderHook(() => useNotebook(), { wrapper });



    await waitFor(() => {

      expect(result.current.notebook).not.toBeNull();

    });



    await act(async () => {

      await result.current.createCell({

        type: 'markdown',

        content: '# Hello',

      });

    });



    expect(result.current.notebook?.cells).toHaveLength(1);



    const cellId = result.current.notebook!.cells[0].id;



    await act(async () => {

      await result.current.updateCell(cellId, { content: '# Updated' });

    });

    expect(result.current.notebook?.cells[0].content).toBe('# Updated');



    await act(async () => {

      await result.current.createCell({

        type: 'code',

        content: 'print(1)',

        language: 'python',

      });

    });



    const codeCellId = result.current.notebook!.cells.find((c) => c.type === 'code')!.id;

    await act(async () => {

      await result.current.executeCell(codeCellId);

    });

    await waitFor(() => {
      expect(result.current.isExecuting).toBe(false);
      expect(result.current.notebook?.cells.find((c) => c.id === codeCellId)?.output).toContain('print(1)');
    }, { timeout: 3000 });



    await act(async () => {

      await result.current.deleteCell(cellId);

    });



    expect(result.current.notebook?.cells).toHaveLength(1);

  });



  it('manages selected cell, selected item, and expanded sections', async () => {

    const { result } = renderHook(() => useNotebook(), { wrapper });



    await waitFor(() => expect(result.current.notebook).not.toBeNull());



    await act(async () => {

      await result.current.createCell({ type: 'text', content: 'x' });

    });

    const cell = result.current.notebook!.cells[0];

    act(() => {

      result.current.setSelectedCell(cell);

      result.current.setSelectedItem({ type: 'cells', id: cell.id });

      result.current.toggleSection('cells');

    });



    expect(result.current.selectedCell).toEqual(cell);

    expect(result.current.selectedItem).toEqual({ type: 'cells', id: cell.id });

    expect(result.current.expandedSections.has('cells')).toBe(false);

  });



  it('uploads files and refreshes folder tree', async () => {

    const file = new File(['hello'], 'hello.txt', { type: 'text/plain' });

    const { result } = renderHook(() => useNotebook(), { wrapper });



    await waitFor(() => expect(result.current.isLoading).toBe(false));



    await act(async () => {

      await result.current.uploadFiles([file], 'folder-a');

    });



    expect(mockApi.projects.notebooks.uploadNotebookFiles).toHaveBeenCalledWith(

      PROJECT_ID,

      NOTEBOOK_ID,

      [file],

      'folder-a',

      false,

    );

    expect(mockApi.projects.notebooks.getNotebookFolderTree).toHaveBeenCalled();

  });



  it('creates and renames folders', async () => {

    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));



    await act(async () => {

      await result.current.createFolder('parent', { name: 'child' });

      await result.current.renameFolder('parent/child', 'renamed');

    });



    expect(mockApi.projects.notebooks.createNotebookFolder).toHaveBeenCalledWith(

      PROJECT_ID,

      NOTEBOOK_ID,

      'parent/child',

    );

    expect(mockApi.projects.notebooks.renameNotebookItem).toHaveBeenCalledWith(

      PROJECT_ID,

      NOTEBOOK_ID,

      'parent/child',

      'renamed',

    );

  });



  it('deletes folder and shows toast on 409 conflict', async () => {

    vi.mocked(notebookFilesApi.delete).mockRejectedValueOnce({

      status: 409,

      body: { message: 'Folder not empty' },

    });



    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));



    await act(async () => {

      await result.current.deleteFolder('parent/child');

    });



    expect(mockShowToast).toHaveBeenCalledWith(

      expect.objectContaining({ title: 'Cannot delete folder', message: 'Folder not empty' }),

    );

  });



  it('deletes, renames, and moves files by id', async () => {

    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));



    await act(async () => {

      await result.current.deleteFile('file-1');

      await result.current.renameFile('file-1', 'renamed.txt');

      await result.current.moveFile('file-1', 'docs');

    });



    expect(mockApi.projects.notebooks.deleteNotebookFileById).toHaveBeenCalledWith(

      PROJECT_ID,

      NOTEBOOK_ID,

      'file-1',

    );

    expect(mockApi.projects.notebooks.renameNotebookFileById).toHaveBeenCalledWith(

      PROJECT_ID,

      NOTEBOOK_ID,

      'file-1',

      'renamed.txt',

    );

    expect(mockApi.projects.notebooks.moveNotebookFileById).toHaveBeenCalledWith(

      PROJECT_ID,

      NOTEBOOK_ID,

      'file-1',

      'docs',

    );

  });



  it('shows toast when file delete returns 409', async () => {

    mockApi.projects.notebooks.deleteNotebookFileById.mockRejectedValueOnce({

      status: 409,

      body: { message: 'File in use' },

    });



    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));



    await act(async () => {

      await result.current.deleteFile('file-1');

    });



    expect(mockShowToast).toHaveBeenCalledWith(

      expect.objectContaining({ title: 'Cannot delete file', message: 'File in use' }),

    );

  });



  it('copies file from project and refreshes tree', async () => {

    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));



    await act(async () => {

      await result.current.copyFromProject('content-file-1', 2);

    });



    expect(mockApi.projects.notebooks.copyFileFromProject).toHaveBeenCalledWith(

      PROJECT_ID,

      NOTEBOOK_ID,

      'content-file-1',

      2,

    );

    expect(mockApi.projects.notebooks.getNotebookFolderTree).toHaveBeenCalled();

  });



  it('creates, renames, and deletes conversations', async () => {

    mockApi.projects.notebooks.conversations.create.mockResolvedValue({

      id: 'convo-1',

      title: 'New chat',

    });

    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));



    await act(async () => {

      const created = await result.current.createConversation('New chat');

      expect(created?.id).toBe('convo-1');

      await result.current.renameConversation('convo-1', 'Renamed chat');

      await result.current.deleteConversation('convo-1');

    });



    expect(mockApi.projects.notebooks.conversations.rename).toHaveBeenCalledWith(

      PROJECT_ID,

      NOTEBOOK_ID,

      'convo-1',

      'Renamed chat',

    );

    expect(result.current.conversations).toHaveLength(0);

  });



  it('deletes multiple conversations in bulk', async () => {

    mockApi.projects.notebooks.conversations.getAll.mockResolvedValue([

      { id: 'c1', title: 'One' },

      { id: 'c2', title: 'Two' },

    ]);



    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.conversations).toHaveLength(2));



    await act(async () => {

      await result.current.deleteConversations(['c1', 'c2']);

    });



    expect(result.current.conversations).toHaveLength(0);

  });



  it('sets and clears home page file then refreshes notebook', async () => {

    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));



    await act(async () => {

      await result.current.setHomePageFile('home-file-id');

      await result.current.clearHomePage();

    });



    expect(mockApi.projects.notebooks.setHomePageFile).toHaveBeenCalledWith(

      PROJECT_ID,

      NOTEBOOK_ID,

      'home-file-id',

    );

    expect(mockApi.projects.notebooks.clearHomePage).toHaveBeenCalledWith(PROJECT_ID, NOTEBOOK_ID);

    expect(mockApi.projects.notebooks.getNotebook).toHaveBeenCalledTimes(3);

  });



  it('resets state when notebookId is cleared', async () => {

    const { result, rerender } = renderHook(() => useNotebook(), {

      wrapper: ({ children }) => (

        <NotebookProvider projectId={PROJECT_ID} notebookId={undefined}>

          {children}

        </NotebookProvider>

      ),

    });



    await waitFor(() => {

      expect(result.current.notebook).toBeNull();

      expect(result.current.isLoading).toBe(true);

    });



    rerender();

    expect(mockApi.projects.notebooks.getNotebook).not.toHaveBeenCalled();

  });

  it('propagates conversation delete failures', async () => {
    mockApi.projects.notebooks.conversations.delete.mockRejectedValueOnce(new Error('Delete denied'));

    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    await expect(
      act(async () => {
        await result.current.deleteConversation('convo-1');
      }),
    ).rejects.toThrow('Delete denied');
  });

  it('propagates bulk conversation delete failures', async () => {
    mockApi.projects.notebooks.conversations.getAll.mockResolvedValue([
      { id: 'c1', title: 'One' },
      { id: 'c2', title: 'Two' },
    ]);
    mockApi.projects.notebooks.conversations.delete.mockRejectedValueOnce(new Error('Bulk delete denied'));

    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.conversations).toHaveLength(2));

    await expect(
      act(async () => {
        await result.current.deleteConversations(['c1', 'c2']);
      }),
    ).rejects.toThrow('Bulk delete denied');
  });

  it('refreshes notebook files on demand', async () => {
    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    mockApi.projects.notebooks.getNotebookFolderTree.mockClear();

    await act(async () => {
      await result.current.loadNotebookFiles();
    });

    expect(mockApi.projects.notebooks.getNotebookFolderTree).toHaveBeenCalled();
  });

  it('refreshes notebook details on demand', async () => {
    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    mockApi.projects.notebooks.getNotebook.mockClear();

    await act(async () => {
      await result.current.refreshNotebook();
    });

    expect(mockApi.projects.notebooks.getNotebook).toHaveBeenCalledWith(PROJECT_ID, NOTEBOOK_ID);
  });

  it('logs moveCell placeholder calls', async () => {
    const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    act(() => {
      result.current.moveCell('cell-1', 2);
    });

    expect(logSpy).toHaveBeenCalledWith('Move cell:', 'cell-1', 'to index:', 2);
    logSpy.mockRestore();
  });

  it('propagates conversation create and rename failures', async () => {
    mockApi.projects.notebooks.conversations.create.mockRejectedValueOnce(new Error('Create denied'));
    mockApi.projects.notebooks.conversations.rename.mockRejectedValueOnce(new Error('Rename denied'));

    const { result } = renderHook(() => useNotebook(), { wrapper });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    await expect(
      act(async () => {
        await result.current.createConversation('New chat');
      }),
    ).rejects.toThrow('Create denied');

    await expect(
      act(async () => {
        await result.current.renameConversation('convo-1', 'Renamed');
      }),
    ).rejects.toThrow('Rename denied');
  });

});


