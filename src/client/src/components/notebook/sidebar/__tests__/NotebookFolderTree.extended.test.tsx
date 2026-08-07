import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { screen, fireEvent, waitFor, renderWithNotebookRoute } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { NotebookFolderTree } from '../NotebookFolderTree';
import { NotebookFolderTreeDto, NotebookSidebarSelectedItem } from '../../../../types/notebook';
import { notebookFilesApi } from '../../../../services/notebookFiles';

vi.mock('../../../../hooks/useLongPress', () => ({
  useLongPress: ({
    onLongPress,
    disabled,
  }: {
    onLongPress: (e: { clientX: number; clientY: number }) => void;
    disabled?: boolean;
  }) => ({
    onTouchStart: (e: React.TouchEvent) => {
      if (disabled) return;
      const touch = e.touches[0];
      onLongPress({ clientX: touch?.clientX ?? 0, clientY: touch?.clientY ?? 0 });
    },
    onTouchEnd: vi.fn(),
    onTouchMove: vi.fn(),
    onTouchCancel: vi.fn(),
  }),
}));

vi.mock('../../../../services/notebookFiles', () => ({
  notebookFilesApi: {
    uploadFiles: vi.fn(),
    getNotebookFileMarkdownContent: vi.fn(),
    getNotebookFileContent: vi.fn(),
  },
}));

vi.mock('../../../notebook/conversations/FullScreenEditor', () => ({
  default: ({ onSave, onCancel, title }: { onSave: (c: string) => void; onCancel: () => void; title?: string }) => (
    <div data-testid="fullscreen-editor">
      <span>{title}</span>
      <button type="button" onClick={() => onSave('# saved')}>Save MD</button>
      <button type="button" onClick={onCancel}>Cancel MD</button>
    </div>
  ),
}));

const mockTree: NotebookFolderTreeDto = {
  id: 'root',
  name: 'Notebook',
  relativePath: '',
  subFolders: [
    {
      id: 'folder-1',
      name: 'Docs',
      relativePath: 'Docs',
      subFolders: [
        {
          id: 'folder-empty',
          name: 'Empty',
          relativePath: 'Docs/Empty',
          subFolders: [],
          files: [],
        },
      ],
      files: [
        {
          id: 'file-md',
          fileName: 'notes.md',
          relativePath: 'Docs/notes.md',
          fileSize: 512,
          lastModifiedUtc: '2023-01-01T00:00:00Z',
          fileHash: 'md-hash',
          isIndexed: false,
          index: false,
        },
      ],
    },
    {
      id: 'folder-2',
      name: 'Assets',
      relativePath: 'Assets',
      subFolders: [],
      files: [],
    },
  ],
  files: [
    {
      id: 'file-root',
      fileName: 'readme.txt',
      relativePath: 'readme.txt',
      fileSize: 256,
      lastModifiedUtc: '2023-01-01T00:00:00Z',
      fileHash: 'root-hash',
      isIndexed: false,
      index: false,
    },
  ],
};

const renderTree = (
  overrides: Partial<React.ComponentProps<typeof NotebookFolderTree>> = {}
) => {
  const onItemSelect = vi.fn();
  const onCreateFolder = vi.fn().mockResolvedValue(undefined);
  const onRenameFolder = vi.fn().mockResolvedValue(undefined);
  const onDeleteFolder = vi.fn().mockResolvedValue(undefined);
  const onMoveFile = vi.fn().mockResolvedValue(undefined);
  const onDeleteFile = vi.fn().mockResolvedValue(undefined);
  const onRenameFile = vi.fn().mockResolvedValue(undefined);
  const onUploadToFolder = vi.fn();
  const onPublishToProject = vi.fn();
  const onPreviewFile = vi.fn();
  const onSetHomePage = vi.fn();

  const props = {
    tree: mockTree,
    notebookName: 'Test Notebook',
    selectedItem: null as NotebookSidebarSelectedItem | null,
    onItemSelect,
    onCreateFolder,
    onRenameFolder,
    onDeleteFolder,
    onMoveFile,
    onDeleteFile,
    onRenameFile,
    onUploadToFolder,
    onPublishToProject,
    onPreviewFile,
    onSetHomePage,
    canEdit: true,
    activeSection: 'notebookFiles' as const,
    onSectionActivate: vi.fn(),
    ...overrides,
  };

  const result = renderWithNotebookRoute(<NotebookFolderTree {...props} />, {
    route: '/projects/proj-1/notebooks/nb-1',
    projectId: 'proj-1',
    notebookId: 'nb-1',
  });

  return { ...result, props };
};

const findPortalButton = async (label: string | RegExp) => {
  return waitFor(() => {
    const buttons = Array.from(document.body.querySelectorAll('button'));
    const match = buttons.find((b) =>
      typeof label === 'string' ? b.textContent === label : label.test(b.textContent ?? '')
    );
    if (!match) throw new Error(`Button not found: ${label}`);
    return match;
  });
};

const waitForMobileMode = async () => {
  await waitFor(() => {
    expect(screen.getAllByTitle('Tap to open, hold for options').length).toBeGreaterThan(0);
  });
};

const createDataTransfer = (data: Record<string, string> = {}) => {
  const store = { ...data };
  return {
    data: store,
    setData: vi.fn((format: string, value: string) => {
      store[format] = value;
    }),
    getData: vi.fn((format: string) => store[format] || ''),
    effectAllowed: 'move',
    dropEffect: 'move',
  } as unknown as DataTransfer;
};

describe('NotebookFolderTree extended coverage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 1024 });
    vi.mocked(notebookFilesApi.uploadFiles).mockResolvedValue([
      {
        id: 'new-md',
        fileName: 'New Markdown.md',
        relativePath: 'New Markdown.md',
        fileSize: 10,
        lastModifiedUtc: '2023-01-01T00:00:00Z',
        fileHash: 'x',
        isIndexed: false,
        index: false,
      },
    ]);
    vi.mocked(notebookFilesApi.getNotebookFileMarkdownContent).mockResolvedValue({
      blob: new Blob(['# Hello'], { type: 'text/markdown' }),
    });
    vi.mocked(notebookFilesApi.getNotebookFileContent).mockResolvedValue(new Blob(['fallback']));
  });

  afterEach(() => {
    vi.useRealTimers();
    fireEvent.click(document.body);
  });

  // userEvent.setup() installs its own navigator.clipboard stub internally,
  // so our mock must be defined *after* that call or it gets overwritten.
  const setupClipboard = () => {
    const user = userEvent.setup();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    return { user, writeText };
  };

  describe('folder context menu', () => {
    it('renames folder from context menu', async () => {
      const user = userEvent.setup();
      const { props } = renderTree();

      fireEvent.contextMenu(screen.getByText('Docs'));
      await user.click(await findPortalButton('Rename'));

      const input = screen.getByDisplayValue('Docs');
      await user.clear(input);
      await user.type(input, 'Documents');
      fireEvent.keyDown(input, { key: 'Enter' });

      await waitFor(() => {
        expect(props.onRenameFolder).toHaveBeenCalledWith('Docs', 'Documents');
      });
    });

    it('cancels folder rename with Escape', async () => {
      const user = userEvent.setup();
      renderTree();

      fireEvent.contextMenu(screen.getByText('Docs'));
      await user.click(await findPortalButton('Rename'));

      const input = screen.getByDisplayValue('Docs');
      fireEvent.change(input, { target: { value: 'X' } });
      fireEvent.keyDown(input, { key: 'Escape' });

      expect(screen.getByText('Docs')).toBeInTheDocument();
    });

    it('creates subfolder from context menu', async () => {
      const user = userEvent.setup();
      const { props } = renderTree();

      fireEvent.contextMenu(screen.getByText('Docs'));
      await user.click(await findPortalButton('Create Subfolder'));

      const input = screen.getByPlaceholderText('Folder name...');
      await user.type(input, 'New Folder');
      fireEvent.keyDown(input, { key: 'Enter' });

      await waitFor(() => {
        expect(props.onCreateFolder).toHaveBeenCalledWith('Docs', { name: 'New Folder' });
      });
    });

    it('triggers upload from folder context menu', async () => {
      const user = userEvent.setup();
      const { props } = renderTree();

      fireEvent.contextMenu(screen.getByText('Docs'));
      await user.click(await findPortalButton('Upload Files'));

      expect(props.onUploadToFolder).toHaveBeenCalledWith('Docs');
    });

    it('deletes empty folder after confirmation', async () => {
      const user = userEvent.setup();
      const { props } = renderTree();

      fireEvent.contextMenu(screen.getByText('Assets'));
      await user.click(await findPortalButton('Delete'));

      const confirmBtn = await screen.findByRole('button', { name: 'Delete' });
      await user.click(confirmBtn);

      await waitFor(() => {
        expect(props.onDeleteFolder).toHaveBeenCalledWith('Assets');
      });
    });

    it('creates markdown file from folder menu', async () => {
      const user = userEvent.setup();
      renderTree();

      fireEvent.contextMenu(screen.getByText('Docs'));
      await user.click(await findPortalButton('New Markdown File'));

      await waitFor(() => {
        expect(notebookFilesApi.uploadFiles).toHaveBeenCalled();
        expect(screen.getByTestId('fullscreen-editor')).toBeInTheDocument();
      });
    });
  });

  describe('file context menu', () => {
    it('previews file from context menu', async () => {
      const user = userEvent.setup();
      const { props } = renderTree();

      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton('Preview'));

      expect(props.onPreviewFile).toHaveBeenCalledWith(
        expect.objectContaining({ id: 'file-root', fileName: 'readme.txt' })
      );
    });

    it('publishes file to project', async () => {
      const user = userEvent.setup();
      const { props } = renderTree();

      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton('Publish to Project'));

      expect(props.onPublishToProject).toHaveBeenCalledWith([
        expect.objectContaining({ id: 'file-root' }),
      ]);
    });

    it('renames file inline from context menu', async () => {
      const user = userEvent.setup();
      const { props } = renderTree();

      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton('Rename'));

      const input = screen.getByDisplayValue('readme.txt');
      fireEvent.change(input, { target: { value: 'updated.txt' } });
      fireEvent.keyDown(input, { key: 'Enter' });

      await waitFor(() => {
        expect(props.onRenameFile).toHaveBeenCalledWith('file-root', 'updated.txt');
      });
    });

    it('deletes file after confirmation', async () => {
      const user = userEvent.setup();
      const { props } = renderTree();

      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton('Delete'));

      const confirmButtons = await screen.findAllByRole('button', { name: 'Delete' });
      await user.click(confirmButtons[confirmButtons.length - 1]);

      await waitFor(() => {
        expect(props.onDeleteFile).toHaveBeenCalledWith('file-root');
      });
    });

    it('downloads file via API', async () => {
      const user = userEvent.setup();
      const appendSpy = vi.spyOn(document.body, 'appendChild');
      const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

      renderTree();
      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton('Download'));

      await waitFor(() => {
        expect(notebookFilesApi.getNotebookFileContent).toHaveBeenCalledWith(
          'proj-1',
          'nb-1',
          'readme.txt',
          'root-hash'
        );
      });

      appendSpy.mockRestore();
      clickSpy.mockRestore();
    });

    it('sets home page from file menu', async () => {
      const user = userEvent.setup();
      const onSetHomePage = vi.fn();
      renderTree({ onSetHomePage });

      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton('Set as Notebook Home Page'));
      expect(onSetHomePage).toHaveBeenCalledWith('file-root');
    });

    it('clears home page when file is already home', async () => {
      const user = userEvent.setup();
      const onSetHomePage = vi.fn();
      renderTree({ onSetHomePage, homePageFileId: 'file-root' });

      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton('Clear as Home Page'));
      expect(onSetHomePage).toHaveBeenCalledWith(null);
    });

    it('shows batch delete confirmation when Delete key pressed with selection', async () => {
      const user = userEvent.setup();
      renderTree();

      await user.click(screen.getByText('readme.txt'));
      fireEvent.keyDown(document, { key: 'Delete' });

      await waitFor(() => {
        expect(screen.getByText(/Confirm Deletion/)).toBeInTheDocument();
      });
    });
  });

  describe('drag and drop', () => {
    it('highlights folder on drag over and moves file on drop', async () => {
      const { props } = renderTree();
      const assetsRow = screen.getByText('Assets').closest('.group') ?? screen.getByText('Assets').parentElement!;
      const dt = createDataTransfer();

      fireEvent.dragOver(assetsRow, { dataTransfer: dt });
      expect(assetsRow.className).toMatch(/ring-blue-400/);

      fireEvent.dragLeave(assetsRow, { dataTransfer: dt, relatedTarget: document.body });
      expect(assetsRow.className).not.toMatch(/ring-blue-400/);

      fireEvent.drop(assetsRow, {
        dataTransfer: createDataTransfer({
          'text/plain': 'file-root',
          'application/x-origin-folder': '',
        }),
      });

      await waitFor(() => {
        expect(props.onMoveFile).toHaveBeenCalledWith('file-root', 'Assets');
      });
    });

    it('starts file drag with metadata', () => {
      renderTree();
      const fileEl = screen.getByText('readme.txt').closest('[draggable="true"]')!;
      const dt = createDataTransfer();

      fireEvent.dragStart(fileEl, { dataTransfer: dt });
      expect(dt.setData).toHaveBeenCalledWith('text/plain', 'file-root');
      fireEvent.dragEnd(fileEl, { dataTransfer: dt });
    });
  });

  describe('search and selection', () => {
    it('filters tree by search term', () => {
      renderTree({ searchTerm: 'notes' });
      expect(screen.getByText('notes.md')).toBeInTheDocument();
      expect(screen.queryByText('readme.txt')).not.toBeInTheDocument();
    });

    it('selects file on double click', async () => {
      const user = userEvent.setup();
      const onPreviewFile = vi.fn();
      renderTree({ onPreviewFile });

      await user.dblClick(screen.getByText('readme.txt'));
      expect(onPreviewFile).toHaveBeenCalledWith(expect.objectContaining({ id: 'file-root' }));
    });

    it('responds to select-notebook-file event', async () => {
      renderTree();
      window.dispatchEvent(
        new CustomEvent('select-notebook-file', { detail: { relativePath: 'Docs/notes.md' } })
      );

      await waitFor(() => {
        const fileEl = document.querySelector('[data-file-id="file-md"]');
        expect(fileEl).toBeTruthy();
      });
    });
  });

  describe('read-only mode', () => {
    // Mirrors how NotebookSidebar actually wires these props: it passes
    // undefined for edit-only handlers when canEdit is false, rather than
    // relying on the folder menu itself to stay closed.
    const readOnlyOverrides = {
      canEdit: false,
      onCreateFolder: undefined,
      onRenameFolder: undefined,
      onDeleteFolder: undefined,
      onUploadToFolder: undefined,
    } as const;

    it('skips edit actions when canEdit is false', () => {
      renderTree(readOnlyOverrides);
      fireEvent.contextMenu(screen.getByText('Docs'));
      expect(screen.queryByText('Rename')).not.toBeInTheDocument();
      expect(screen.queryByText('Create Subfolder')).not.toBeInTheDocument();
      expect(screen.queryByText('Upload Files')).not.toBeInTheDocument();
      expect(screen.queryByText('New Markdown File')).not.toBeInTheDocument();
    });

    it('still opens the folder menu with Copy path when canEdit is false', () => {
      renderTree(readOnlyOverrides);
      fireEvent.contextMenu(screen.getByText('Docs'));
      expect(screen.getByText('Copy path')).toBeInTheDocument();
    });
  });

  describe('hover actions', () => {
    it('creates subfolder from hover button', () => {
      renderTree();
      const docs = screen.getByText('Docs');
      fireEvent.mouseEnter(docs);

      const container = docs.closest('.folder-tree-item');
      const createBtn = container?.querySelector('[title="Create subfolder"]') as HTMLElement;
      fireEvent.click(createBtn);

      expect(screen.getByPlaceholderText('Folder name...')).toBeInTheDocument();
    });
  });

  describe('keyboard shortcuts', () => {
    it('confirms batch delete and calls onDeleteFile', async () => {
      const user = userEvent.setup();
      const { props } = renderTree();

      await user.click(screen.getByText('readme.txt'));
      fireEvent.keyDown(window, { key: 'Delete' });
      const confirm = await screen.findByRole('button', { name: 'Delete' });
      await user.click(confirm);

      await waitFor(() => {
        expect(props.onDeleteFile).toHaveBeenCalledWith('file-root');
      });
    });

    it('triggers rename via F2 when one item selected', async () => {
      const user = userEvent.setup();
      renderTree();

      await user.click(screen.getByText('readme.txt'));
      fireEvent.keyDown(window, { key: 'F2' });

      await waitFor(() => {
        expect(screen.getByDisplayValue('readme.txt')).toBeInTheDocument();
      });
    });

    it('selects all items with Ctrl+A', async () => {
      renderTree();
      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await waitFor(() => {
        expect(screen.getByText(/Publish \d+ File/)).toBeInTheDocument();
      });
    });
  });

  describe('markdown editor save flow', () => {
    it('saves markdown from fullscreen editor after creating file', async () => {
      const user = userEvent.setup();
      renderTree();

      fireEvent.contextMenu(screen.getByText('Docs'));
      await user.click(await findPortalButton('New Markdown File'));

      await waitFor(() => screen.getByTestId('fullscreen-editor'));
      await user.click(screen.getByText('Save MD'));

      await waitFor(() => {
        expect(notebookFilesApi.uploadFiles).toHaveBeenCalledTimes(2);
      });
    });
  });

  describe('empty tree and missing file selection', () => {
    it('renders empty state when tree is null', () => {
      renderTree({ tree: null });
      expect(screen.getByText('No files available')).toBeInTheDocument();
    });

    it('warns when select-notebook-file targets a missing path', async () => {
      const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
      renderTree();

      window.dispatchEvent(
        new CustomEvent('select-notebook-file', { detail: { relativePath: 'missing/file.txt' } })
      );

      await waitFor(() => {
        expect(warnSpy).toHaveBeenCalledWith('File not found for path: missing/file.txt');
      });
      warnSpy.mockRestore();
    });
  });

  describe('markdown editor on existing files', () => {
    it('loads existing markdown via API when Edit is chosen', async () => {
      const user = userEvent.setup();
      renderTree({ searchTerm: 'notes' });

      fireEvent.contextMenu(screen.getByText('notes.md'));
      await user.click(await findPortalButton('Edit'));

      await waitFor(() => {
        expect(notebookFilesApi.getNotebookFileMarkdownContent).toHaveBeenCalledWith(
          'proj-1',
          'nb-1',
          'file-md',
        );
      });
    });

    it('falls back to raw content when markdown fetch fails', async () => {
      const user = userEvent.setup();
      vi.mocked(notebookFilesApi.getNotebookFileMarkdownContent).mockRejectedValueOnce(new Error('no md'));
      renderTree({ searchTerm: 'notes' });

      fireEvent.contextMenu(screen.getByText('notes.md'));
      await user.click(await findPortalButton('Edit'));

      await waitFor(() => {
        expect(notebookFilesApi.getNotebookFileContent).toHaveBeenCalledWith(
          'proj-1',
          'nb-1',
          'Docs/notes.md',
          'md-hash',
        );
      });
    });

  });

  describe('batch folder operations', () => {
    it('batch deletes folder and file selection', async () => {
      const user = userEvent.setup();
      const { props } = renderTree();

      await user.click(screen.getByText('readme.txt'));
      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      fireEvent.keyDown(window, { key: 'Delete' });
      const confirm = await screen.findByRole('button', { name: 'Delete' });
      await user.click(confirm);

      await waitFor(() => {
        expect(props.onDeleteFile).toHaveBeenCalled();
      });
    });

    it('cancels batch delete confirmation', async () => {
      const user = userEvent.setup();
      const onDeleteFile = vi.fn();
      renderTree({ onDeleteFile });

      await user.click(screen.getByText('readme.txt'));
      fireEvent.keyDown(window, { key: 'Delete' });
      const cancel = await screen.findByRole('button', { name: 'Cancel' });
      await user.click(cancel);

      expect(onDeleteFile).not.toHaveBeenCalled();
    });

    it('renames folder via F2 keyboard shortcut', async () => {
      const user = userEvent.setup();
      renderTree();

      await user.click(screen.getByText('Docs'));
      fireEvent.keyDown(window, { key: 'F2' });

      await waitFor(() => {
        expect(screen.getByDisplayValue('Docs')).toBeInTheDocument();
      });
    });
  });

  describe('mobile interactions', () => {
    beforeEach(() => {
      Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });
    });

    it('opens file on single tap in mobile layout', async () => {
      const user = userEvent.setup();
      const onPreviewFile = vi.fn();
      renderTree({ onPreviewFile });
      fireEvent(window, new Event('resize'));

      await user.click(screen.getByText('readme.txt'));
      expect(onPreviewFile).toHaveBeenCalledWith(expect.objectContaining({ id: 'file-root' }));
    });

    it('opens file context menu after long press', async () => {
      renderTree();
      fireEvent(window, new Event('resize'));
      await waitForMobileMode();

      const fileEl = screen.getByText('readme.txt').closest('[data-tour-id="notebook.sidebar.file.item"]')!;
      fireEvent.touchStart(fileEl, { touches: [{ clientX: 50, clientY: 50 }] });
      await waitFor(() => {
        expect(document.body.textContent).toContain('Download');
      });
    });

    it('selects file on long press when not already selected', async () => {
      renderTree();
      fireEvent(window, new Event('resize'));
      await waitForMobileMode();

      const fileEl = screen.getByText('readme.txt').closest('[data-tour-id="notebook.sidebar.file.item"]')!;
      fireEvent.touchStart(fileEl, { touches: [{ clientX: 60, clientY: 60 }] });
      await waitFor(() => {
        expect(document.body.textContent).toContain('Download');
      });
    });
  });

  describe('keyboard arrow navigation', () => {
    it('moves focus with ArrowDown and ArrowUp', async () => {
      const user = userEvent.setup();
      renderTree();

      await user.click(screen.getByText('readme.txt'));
      const fileEl = document.querySelector('[data-file-id="file-root"]')!;
      fireEvent.keyDown(fileEl, { key: 'ArrowDown' });
      fireEvent.keyDown(fileEl, { key: 'ArrowUp' });
      fireEvent.keyDown(fileEl, { key: 'Home' });
      fireEvent.keyDown(fileEl, { key: 'End' });
    });

    it('opens focused file with Enter', async () => {
      const user = userEvent.setup();
      const onPreviewFile = vi.fn();
      renderTree({ onPreviewFile });

      await user.click(screen.getByText('readme.txt'));
      const fileEl = document.querySelector('[data-file-id="file-root"]')!;
      fireEvent.keyDown(fileEl, { key: 'Enter' });
      expect(onPreviewFile).toHaveBeenCalledWith(expect.objectContaining({ id: 'file-root' }));
    });

    it('toggles folder expansion when Enter on folder row', async () => {
      const user = userEvent.setup();
      renderTree({ searchTerm: 'notes' });

      await user.click(screen.getByText('notes.md'));
      fireEvent.keyDown(window, { key: 'ArrowUp' });
    });
  });

  describe('error toasts', () => {
    it('shows toast when folder rename fails', async () => {
      const user = userEvent.setup();
      renderTree({ onRenameFolder: vi.fn().mockRejectedValue(new Error('fail')) });

      fireEvent.contextMenu(screen.getByText('Docs'));
      await user.click(await findPortalButton('Rename'));
      const input = screen.getByDisplayValue('Docs');
      fireEvent.change(input, { target: { value: 'Renamed' } });
      fireEvent.keyDown(input, { key: 'Enter' });

      await waitFor(() => {
        expect(screen.getByText('Failed to rename folder')).toBeInTheDocument();
      });
    });

    it('shows toast when folder delete fails', async () => {
      const user = userEvent.setup();
      renderTree({ onDeleteFolder: vi.fn().mockRejectedValue(new Error('fail')) });

      fireEvent.contextMenu(screen.getByText('Assets'));
      await user.click(await findPortalButton('Delete'));
      const confirm = await screen.findByRole('button', { name: 'Delete' });
      await user.click(confirm);

      await waitFor(() => {
        expect(screen.getByText('Failed to delete folder')).toBeInTheDocument();
      });
    });

    it('shows toast when file delete fails', async () => {
      const user = userEvent.setup();
      renderTree({ onDeleteFile: vi.fn().mockRejectedValue(new Error('fail')) });

      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton('Delete'));
      const buttons = await screen.findAllByRole('button', { name: 'Delete' });
      await user.click(buttons[buttons.length - 1]);

      await waitFor(() => {
        expect(screen.getByText('Failed to delete file')).toBeInTheDocument();
      });
    });

    it('shows toast when file rename fails', async () => {
      const user = userEvent.setup();
      renderTree({ onRenameFile: vi.fn().mockRejectedValue(new Error('fail')) });

      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton('Rename'));
      const input = screen.getByDisplayValue('readme.txt');
      fireEvent.change(input, { target: { value: 'renamed.txt' } });
      fireEvent.keyDown(input, { key: 'Enter' });

      await waitFor(() => {
        expect(screen.getByText('Failed to rename file')).toBeInTheDocument();
      });
    });

    it('shows toast when download fails', async () => {
      const user = userEvent.setup();
      vi.mocked(notebookFilesApi.getNotebookFileContent).mockRejectedValueOnce(new Error('dl fail'));
      renderTree();

      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton('Download'));

      await waitFor(() => {
        expect(screen.getByText('Failed to download file')).toBeInTheDocument();
      });
    });

    it('shows toast when subfolder creation fails', async () => {
      const user = userEvent.setup();
      renderTree({ onCreateFolder: vi.fn().mockRejectedValue(new Error('fail')) });

      fireEvent.contextMenu(screen.getByText('Docs'));
      await user.click(await findPortalButton('Create Subfolder'));
      const input = screen.getByPlaceholderText('Folder name...');
      await user.type(input, 'Bad{enter}');

      await waitFor(() => {
        expect(screen.getByText('Failed to create subfolder')).toBeInTheDocument();
      });
    });
  });

  describe('batch context menu operations', () => {
    it('publishes multiple selected files from context menu', async () => {
      const user = userEvent.setup();
      const { props } = renderTree();

      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton(/Publish \d+ File/));

      expect(props.onPublishToProject).toHaveBeenCalled();
    });

    it('batch downloads with partial failure toast', async () => {
      const user = userEvent.setup();
      vi.mocked(notebookFilesApi.getNotebookFileContent)
        .mockResolvedValueOnce(new Blob(['a']))
        .mockRejectedValueOnce(new Error('fail'));

      renderTree();
      const docsRow = screen.getByText('Docs').closest('.group');
      const docsToggle = docsRow?.querySelector('button');
      expect(docsToggle).toBeInTheDocument();
      if (docsToggle) {
        await user.click(docsToggle);
      }
      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton(/Download \d+ File/));

      await waitFor(() => {
        expect(screen.getByText('Some downloads failed')).toBeInTheDocument();
      });
    });

    it('deletes multiple items from batch context menu', async () => {
      const user = userEvent.setup();
      const onDeleteFile = vi.fn().mockResolvedValue(undefined);
      renderTree({ onDeleteFile });

      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton(/Delete \d+ Items/));

      await waitFor(() => {
        expect(onDeleteFile).toHaveBeenCalled();
      });
    });
  });

  describe('search edge cases', () => {
    it('shows parent folder when nested file matches search', () => {
      renderTree({ searchTerm: 'notes' });
      expect(screen.getByText('Docs')).toBeInTheDocument();
      expect(screen.getByText('notes.md')).toBeInTheDocument();
    });

    it('hides all content when search has no matches', () => {
      renderTree({ searchTerm: 'zzznomatch' });
      expect(screen.queryByText('readme.txt')).not.toBeInTheDocument();
      expect(screen.queryByText('Docs')).not.toBeInTheDocument();
    });

    it('matches folder name in search', () => {
      renderTree({ searchTerm: 'assets' });
      expect(screen.getByText('Assets')).toBeInTheDocument();
    });
  });

  describe('file rename cancel', () => {
    it('cancels inline file rename with Escape', async () => {
      const user = userEvent.setup();
      renderTree();

      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton('Rename'));
      const input = screen.getByDisplayValue('readme.txt');
      fireEvent.keyDown(input, { key: 'Escape' });
      expect(screen.getByText('readme.txt')).toBeInTheDocument();
    });
  });

  describe('markdown editor lifecycle', () => {
    it('shows editor error when markdown create upload fails', async () => {
      const user = userEvent.setup();
      vi.mocked(notebookFilesApi.uploadFiles).mockRejectedValueOnce(new Error('create fail'));
      renderTree();

      fireEvent.contextMenu(screen.getByText('Docs'));
      await user.click(await findPortalButton('New Markdown File'));

      await waitFor(() => {
        expect(screen.queryByTestId('fullscreen-editor')).not.toBeInTheDocument();
      });
    });
  });

  describe('section coordination', () => {
    it('clears selection when active section changes', async () => {
      const user = userEvent.setup();
      const { rerender, props } = renderTree();

      await user.click(screen.getByText('readme.txt'));
      rerender(
        <NotebookFolderTree {...props} activeSection="conversations" />
      );
      fireEvent.keyDown(window, { key: 'Delete' });
      expect(screen.queryByText(/Confirm Deletion/)).not.toBeInTheDocument();
    });
  });

  describe('local empty folder tracking', () => {
    it('shows locally created empty subfolder after create', async () => {
      const user = userEvent.setup();
      renderTree();

      fireEvent.contextMenu(screen.getByText('Docs'));
      await user.click(await findPortalButton('Create Subfolder'));
      const input = screen.getByPlaceholderText('Folder name...');
      await user.type(input, 'LocalEmpty{enter}');

      await waitFor(() => {
        expect(screen.getByText('LocalEmpty')).toBeInTheDocument();
      });
    });
  });

  describe('copy path', () => {
    it('copies a file path to the clipboard scoped under the notebook name', async () => {
      const { user, writeText } = setupClipboard();
      renderTree();

      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton('Copy path'));

      expect(writeText).toHaveBeenCalledWith('/Test Notebook/readme.txt');
    });

    it('copies a folder path to the clipboard scoped under the notebook name', async () => {
      const { user, writeText } = setupClipboard();
      renderTree();

      fireEvent.contextMenu(screen.getByText('Docs'));
      await user.click(await findPortalButton('Copy path'));

      expect(writeText).toHaveBeenCalledWith('/Test Notebook/Docs');
    });

    it('does not show Copy path for the root folder', () => {
      renderTree();
      fireEvent.contextMenu(screen.getByText('Test Notebook'));
      expect(screen.queryByText('Copy path')).not.toBeInTheDocument();
    });

    it('copies multiple selected paths joined by newlines, each scoped under the notebook name', async () => {
      const { user, writeText } = setupClipboard();
      renderTree();

      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton(/Copy \d+ Paths?/));

      expect(writeText).toHaveBeenCalledWith('/Test Notebook/Assets\n/Test Notebook/Docs\n/Test Notebook/readme.txt');
    });

    it('falls back to a bare root-relative path when notebookName is not provided', async () => {
      const { user, writeText } = setupClipboard();
      renderTree({ notebookName: undefined });

      fireEvent.contextMenu(screen.getByText('readme.txt'));
      await user.click(await findPortalButton('Copy path'));

      expect(writeText).toHaveBeenCalledWith('/readme.txt');
    });
  });

  describe('folder row click behavior', () => {
    it('plain click on a folder row still toggles its expansion', () => {
      renderTree();

      expect(screen.queryByText('notes.md')).not.toBeInTheDocument();
      fireEvent.click(screen.getByText('Docs'));
      expect(screen.getByText('notes.md')).toBeInTheDocument();
    });

    it('shift-click selects a range of folders without toggling expansion', async () => {
      renderTree();

      // Anchor on Assets (a childless folder, so its own expand state is unobservable),
      // then shift-click Docs to extend the range selection.
      fireEvent.click(screen.getByText('Assets'));
      fireEvent.click(screen.getByText('Docs'), { shiftKey: true });

      // Docs must stay collapsed - the shift-click should only select, not expand it.
      expect(screen.queryByText('notes.md')).not.toBeInTheDocument();

      // Both folders should now be selected: Delete should offer to remove 2 items.
      fireEvent.keyDown(window, { key: 'Delete' });
      await waitFor(() => {
        expect(screen.getByText(/Confirm Deletion/)).toBeInTheDocument();
      });
      expect(screen.getByText(/2 items/)).toBeInTheDocument();
    });

    it('shift-clicked folders are visibly highlighted, same as multi-selected files', () => {
      renderTree();

      fireEvent.click(screen.getByText('Assets'));
      fireEvent.click(screen.getByText('Docs'), { shiftKey: true });

      const assetsRow = screen.getByText('Assets').closest('.group');
      const docsRow = screen.getByText('Docs').closest('.group');
      expect(assetsRow).toHaveClass('bg-blue-100');
      expect(docsRow).toHaveClass('bg-blue-100');
    });

    it('ctrl-click selects a folder without toggling expansion', () => {
      renderTree();

      fireEvent.click(screen.getByText('Docs'), { ctrlKey: true });

      expect(screen.queryByText('notes.md')).not.toBeInTheDocument();
    });
  });

  describe('multi-select folder context menu', () => {
    it('shows Copy N Paths when right-clicking a folder within a multi-selection', async () => {
      const { user, writeText } = setupClipboard();
      renderTree();

      fireEvent.click(screen.getByText('Assets'));
      fireEvent.click(screen.getByText('readme.txt'), { ctrlKey: true });

      fireEvent.contextMenu(screen.getByText('Assets'));
      await user.click(await findPortalButton(/Copy \d+ Paths?/));

      expect(writeText).toHaveBeenCalledWith('/Test Notebook/Assets\n/Test Notebook/readme.txt');
    });

    it('right-clicking a folder outside the current selection collapses to just that folder', () => {
      renderTree();

      fireEvent.click(screen.getByText('readme.txt'));
      fireEvent.click(screen.getByText('Docs'), { ctrlKey: true });

      // Assets was never part of the [readme.txt, Docs] selection.
      fireEvent.contextMenu(screen.getByText('Assets'));
      expect(screen.queryByText(/Copy \d+ Paths?/)).not.toBeInTheDocument();
      expect(screen.getByText('Copy path')).toBeInTheDocument();
    });
  });

});
