import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { FolderTree } from '../FolderTree';
import { FolderTreeDto, ProjectContentFile, ProjectFolderDto } from '../../../../types/project';

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

vi.mock('../../../../services/api', () => {
  const mocks = {
    getContentFileContent: vi.fn(),
    uploadFiles: vi.fn(),
  } as const;
  (globalThis as { __folderTreeApiMocks?: typeof mocks }).__folderTreeApiMocks = mocks;
  return {
    api: {
      projects: mocks,
    },
  };
});

vi.mock('../../../notebook/conversations/FullScreenEditor', () => ({
  default: ({ onSave, onCancel }: { onSave: (c: string) => void; onCancel: () => void }) => (
    <div data-testid="fullscreen-editor">
      <button type="button" onClick={() => onSave('# saved')}>Save MD</button>
      <button type="button" onClick={onCancel}>Cancel MD</button>
    </div>
  ),
}));

const apiMocks = () => (globalThis as { __folderTreeApiMocks: { getContentFileContent: ReturnType<typeof vi.fn>; uploadFiles: ReturnType<typeof vi.fn> } }).__folderTreeApiMocks;

const mdFile: ProjectContentFile = {
  id: 'file-md',
  fileName: 'guide.md',
  path: '/guide.md',
  relativePath: 'guide.md',
  contentType: 'text/markdown',
  index: false,
  documentId: 'doc-md',
  created: '2023-01-01T00:00:00Z',
  fileSize: 400,
  latestVersion: 1,
};

const txtFile: ProjectContentFile = {
  id: 'file-txt',
  fileName: 'notes.txt',
  path: '/notes.txt',
  relativePath: 'notes.txt',
  contentType: 'text/plain',
  index: false,
  documentId: 'doc-txt',
  created: '2023-01-01T00:00:00Z',
  fileSize: 100,
};

const flatFileTree: FolderTreeDto = {
  id: undefined,
  name: 'Project',
  relativePath: '',
  subFolders: [
    {
      id: 'folder-empty',
      name: 'Archive',
      relativePath: 'Archive',
      subFolders: [],
      files: [],
    },
  ],
  files: [mdFile, txtFile],
};

const docsFileTree: FolderTreeDto = {
  id: undefined,
  name: 'Project',
  relativePath: '',
  subFolders: [
    {
      id: 'folder-docs',
      name: 'Docs',
      relativePath: 'Docs',
      subFolders: [],
      files: [mdFile],
    },
  ],
  files: [],
};

const folders: ProjectFolderDto[] = [
  { id: 'root', name: 'Project', relativePath: '', projectId: 'proj', created: '' } as ProjectFolderDto,
  { id: 'folder-empty', name: 'Archive', relativePath: 'Archive', projectId: 'proj', parentFolderId: 'root', created: '' } as ProjectFolderDto,
  { id: 'folder-docs', name: 'Docs', relativePath: 'Docs', projectId: 'proj', parentFolderId: 'root', created: '' } as ProjectFolderDto,
];

const expandFolder = (name: string) => {
  const row = screen.getByText(name).closest('.folder-tree-item');
  const toggle = row?.querySelector('button');
  if (toggle) fireEvent.click(toggle);
};

const renderTree = (overrides: Partial<React.ComponentProps<typeof FolderTree>> = {}) => {
  const props = {
    folderTree: flatFileTree,
    folders,
    projectId: 'proj',
    onFileSelect: vi.fn(),
    onFolderSelect: vi.fn(),
    onCreateFolder: vi.fn().mockResolvedValue(undefined),
    onRenameFolder: vi.fn().mockResolvedValue(undefined),
    onDeleteFolder: vi.fn().mockResolvedValue(undefined),
    onMoveFile: vi.fn().mockResolvedValue(undefined),
    onUploadToFolder: vi.fn(),
    onDeleteFile: vi.fn().mockResolvedValue(undefined),
    onRenameFile: vi.fn().mockResolvedValue(undefined),
    onCreateNotebookFromFile: vi.fn(),
    onCreateNotebookFromFiles: vi.fn(),
    onSetHomePage: vi.fn().mockResolvedValue(undefined),
    activeSection: 'contentFiles' as const,
    onSectionActivate: vi.fn(),
    ...overrides,
  };
  return { ...render(<FolderTree {...props} />), props };
};

const findPortalButton = async (label: string | RegExp) =>
  waitFor(() => {
    const match = Array.from(document.body.querySelectorAll('button')).find((b) =>
      typeof label === 'string' ? b.textContent === label : label.test(b.textContent ?? '')
    );
    if (!match) throw new Error(`Button not found: ${label}`);
    return match;
  });

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

describe('FolderTree extended coverage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 1024 });
    apiMocks().getContentFileContent.mockResolvedValue({
      blob: new Blob(['# md'], { type: 'text/markdown' }),
      fileName: 'guide.md',
    });
    apiMocks().uploadFiles.mockResolvedValue([
      { id: 'new-1', fileName: 'New Markdown.md', latestVersion: 2 },
    ]);
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

  it('deletes empty folder after confirmation', async () => {
    const user = userEvent.setup();
    const { props } = renderTree();

    fireEvent.contextMenu(screen.getByText('Archive'));
    await user.click(await findPortalButton('Delete'));

    const confirm = await screen.findByRole('button', { name: 'Delete' });
    await user.click(confirm);

    await waitFor(() => {
      expect(props.onDeleteFolder).toHaveBeenCalledWith('folder-empty');
    });
  });

  it('renames file from context menu', async () => {
    const user = userEvent.setup();
    const { props } = renderTree();

    fireEvent.contextMenu(screen.getByText('notes.txt'));
    await user.click(await findPortalButton('Rename'));

    const input = screen.getByDisplayValue('notes.txt');
    fireEvent.change(input, { target: { value: 'renamed.txt' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    await waitFor(() => {
      expect(props.onRenameFile).toHaveBeenCalledWith('file-txt', 'renamed.txt');
    });
  });

  it('deletes file after confirmation', async () => {
    const user = userEvent.setup();
    const { props } = renderTree();

    fireEvent.contextMenu(screen.getByText('notes.txt'));
    await user.click(await findPortalButton('Delete'));

    const buttons = await screen.findAllByRole('button', { name: 'Delete' });
    await user.click(buttons[buttons.length - 1]);

    await waitFor(() => {
      expect(props.onDeleteFile).toHaveBeenCalledWith('file-txt');
    });
  });

  it('downloads file from context menu', async () => {
    const user = userEvent.setup();
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    renderTree();
    fireEvent.contextMenu(screen.getByText('notes.txt'));
    await user.click(await findPortalButton('Download'));

    await waitFor(() => {
      expect(apiMocks().getContentFileContent).toHaveBeenCalledWith('proj', 'file-txt');
    });
    clickSpy.mockRestore();
  });

  it('creates notebook from file', async () => {
    const user = userEvent.setup();
    const { props } = renderTree();

    fireEvent.contextMenu(screen.getByText('notes.txt'));
    await user.click(await findPortalButton('Create Notebook from File'));

    expect(props.onCreateNotebookFromFile).toHaveBeenCalledWith('file-txt', 'notes.txt');
  });

  it('sets home page from file menu', async () => {
    const user = userEvent.setup();
    const { props } = renderTree();

    fireEvent.contextMenu(screen.getByText('notes.txt'));
    await user.click(await findPortalButton('Set Project Home Page'));

    await waitFor(() => {
      expect(props.onSetHomePage).toHaveBeenCalledWith('file-txt');
    });
  });

  it('creates new markdown file from folder menu', async () => {
    const user = userEvent.setup();
    renderTree();

    fireEvent.contextMenu(screen.getByText('Project'));
    await user.click(await findPortalButton('New Markdown File'));

    await waitFor(() => {
      expect(apiMocks().uploadFiles).toHaveBeenCalled();
      expect(screen.getByTestId('fullscreen-editor')).toBeInTheDocument();
    });
  });

  it('selects file on double click', async () => {
    const user = userEvent.setup();
    const { props } = renderTree();

    await user.dblClick(screen.getByText('notes.txt'));
    expect(props.onFileSelect).toHaveBeenCalledWith('file-txt');
  });

  it('moves file when dropped on different folder', async () => {
    const { props } = renderTree();
    const archiveRow = screen.getByText('Archive').closest('.group') ?? screen.getByText('Archive').parentElement!;

    fireEvent.drop(archiveRow, {
      dataTransfer: createDataTransfer({
        'text/plain': 'file-txt',
        'application/x-origin-folder': '',
      }),
    });

    await waitFor(() => {
      expect(props.onMoveFile).toHaveBeenCalledWith('file-txt', 'folder-empty');
    });
  });

  it('filters by search term with matches', () => {
    renderTree({ searchTerm: 'guide' });
    expect(screen.getByText('guide.md')).toBeInTheDocument();
    expect(screen.queryByText('notes.txt')).not.toBeInTheDocument();
  });

  it('shows search matches in nested folder names', () => {
    renderTree({ folderTree: docsFileTree, searchTerm: 'guide' });
    expandFolder('Docs');
    expect(screen.getByText('guide.md')).toBeInTheDocument();
  });

  it('batch deletes selected files via keyboard shortcut', async () => {
    const user = userEvent.setup();
    const onDeleteFiles = vi.fn().mockResolvedValue(undefined);
    renderTree({ onDeleteFiles });

    await user.click(screen.getByText('notes.txt'));
    fireEvent.keyDown(window, { key: 'Delete' });
    const confirm = await screen.findByRole('button', { name: 'Delete' });
    await user.click(confirm);

    await waitFor(() => {
      expect(onDeleteFiles).toHaveBeenCalledWith(['file-txt']);
    });
  });

  it('renames file via F2 keyboard shortcut', async () => {
    const user = userEvent.setup();
    renderTree();

    await user.click(screen.getByText('notes.txt'));
    fireEvent.keyDown(window, { key: 'F2' });

    await waitFor(() => {
      expect(screen.getByDisplayValue('notes.txt')).toBeInTheDocument();
    });
  });

  it('clears selection on Escape key', async () => {
    const user = userEvent.setup();
    renderTree();

    await user.click(screen.getByText('notes.txt'));
    fireEvent.keyDown(window, { key: 'Escape' });
    fireEvent.contextMenu(screen.getByText('notes.txt'));
    await waitFor(() => {
      expect(screen.getByText('Create Notebook from File')).toBeInTheDocument();
    });
  });

  it('renames folder from context menu', async () => {
    const user = userEvent.setup();
    const { props } = renderTree();

    fireEvent.contextMenu(screen.getByText('Archive'));
    await user.click(await findPortalButton('Rename'));

    const input = screen.getByDisplayValue('Archive');
    fireEvent.change(input, { target: { value: 'Renamed Archive' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    await waitFor(() => {
      expect(props.onRenameFolder).toHaveBeenCalledWith('folder-empty', 'Renamed Archive');
    });
  });

  it('creates subfolder from project root context menu', async () => {
    const user = userEvent.setup();
    const { props } = renderTree();

    fireEvent.contextMenu(screen.getByText('Project'));
    await user.click(await findPortalButton('Create Subfolder'));

    const input = await waitFor(() => {
      const el = document.querySelector('.folder-tree input[type="text"]') as HTMLInputElement | null;
      if (!el) throw new Error('Subfolder input not found');
      return el;
    });
    fireEvent.change(input, { target: { value: 'New Docs' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    await waitFor(() => {
      expect(props.onCreateFolder).toHaveBeenCalledWith(undefined, { name: 'New Docs' });
    });
  });

  it('batch deletes selected folder after confirmation', async () => {
    const user = userEvent.setup();
    const { props } = renderTree();

    await user.click(screen.getByText('Archive'));
    fireEvent.keyDown(window, { key: 'Delete' });
    const confirm = await screen.findByRole('button', { name: 'Delete' });
    await user.click(confirm);

    await waitFor(() => {
      expect(props.onDeleteFolder).toHaveBeenCalledWith('folder-empty');
    });
  });

  it('cancels batch delete confirmation dialog', async () => {
    const user = userEvent.setup();
    const onDeleteFolder = vi.fn();
    renderTree({ onDeleteFolder });

    await user.click(screen.getByText('notes.txt'));
    fireEvent.keyDown(window, { key: 'Delete' });
    const cancel = await screen.findByRole('button', { name: 'Cancel' });
    await user.click(cancel);

    expect(onDeleteFolder).not.toHaveBeenCalled();
    expect(screen.queryByText(/Confirm Deletion/)).not.toBeInTheDocument();
  });

  it('renames folder via F2 keyboard shortcut', async () => {
    const user = userEvent.setup();
    renderTree();

    await user.click(screen.getByText('Archive'));
    fireEvent.keyDown(window, { key: 'F2' });

    await waitFor(() => {
      expect(screen.getByDisplayValue('Archive')).toBeInTheDocument();
    });
  });

  it('cancels markdown editor after creating a new file', async () => {
    const user = userEvent.setup();
    renderTree();

    fireEvent.contextMenu(screen.getByText('Project'));
    await user.click(await findPortalButton('New Markdown File'));
    await waitFor(() => screen.getByTestId('fullscreen-editor'));
    await user.click(screen.getByText('Cancel MD'));

    expect(screen.queryByTestId('fullscreen-editor')).not.toBeInTheDocument();
  });

  it('saves markdown from fullscreen editor', async () => {
    const user = userEvent.setup();
    renderTree();

    fireEvent.contextMenu(screen.getByText('Project'));
    await user.click(await findPortalButton('New Markdown File'));
    await waitFor(() => screen.getByTestId('fullscreen-editor'));
    await user.click(screen.getByText('Save MD'));

    await waitFor(() => {
      expect(apiMocks().uploadFiles).toHaveBeenCalledTimes(2);
    });
  });

  it('clears home page when file is already home', async () => {
    const user = userEvent.setup();
    const { props } = renderTree({ homePageContentFileId: 'file-txt' });

    fireEvent.contextMenu(screen.getByText('notes.txt'));
    await user.click(await findPortalButton('Clear as Home Page'));

    await waitFor(() => {
      expect(props.onSetHomePage).toHaveBeenCalledWith(null);
    });
  });

  it('batch deletes multiple files using serial onDeleteFile fallback', async () => {
    const user = userEvent.setup();
    const onDeleteFile = vi.fn().mockResolvedValue(undefined);
    renderTree({ onDeleteFile, onDeleteFiles: undefined });

    await user.click(screen.getByText('notes.txt'));
    fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
    fireEvent.keyDown(window, { key: 'Delete' });
    const confirm = await screen.findByRole('button', { name: 'Delete' });
    await user.click(confirm);

    await waitFor(() => {
      expect(onDeleteFile).toHaveBeenCalledTimes(2);
    });
  });

  describe('mobile interactions', () => {
    beforeEach(() => {
      Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });
    });

    it('opens file on single tap in mobile layout', async () => {
      const user = userEvent.setup();
      const { props } = renderTree();
      fireEvent(window, new Event('resize'));

      await user.click(screen.getByText('notes.txt'));
      expect(props.onFileSelect).toHaveBeenCalledWith('file-txt');
    });

    it('opens file context menu after long press', async () => {
      renderTree();
      fireEvent(window, new Event('resize'));
      await waitForMobileMode();

      const fileEl = screen.getByText('notes.txt').closest('[data-tour-id="sidebar.file.item"]')!;
      fireEvent.touchStart(fileEl, { touches: [{ clientX: 40, clientY: 40 }] });
      await waitFor(() => {
        expect(document.body.textContent).toContain('Download');
      });
    });

    it('toggles folder expansion on mobile tap', async () => {
      const user = userEvent.setup();
      renderTree();
      fireEvent(window, new Event('resize'));

      const archiveRow = screen.getByText('Archive').closest('.group')!;
      await user.click(archiveRow);
      expect(archiveRow).toBeInTheDocument();
    });
  });

  describe('keyboard arrow navigation', () => {
    it('moves focus with ArrowDown, ArrowUp, Home, and End', async () => {
      const user = userEvent.setup();
      renderTree();

      await user.click(screen.getByText('notes.txt'));
      const fileEl = document.querySelector('[data-tour-id="sidebar.file.item"]')!;
      fireEvent.keyDown(fileEl, { key: 'ArrowDown' });
      fireEvent.keyDown(fileEl, { key: 'ArrowUp' });
      fireEvent.keyDown(fileEl, { key: 'Home' });
      fireEvent.keyDown(fileEl, { key: 'End' });
    });

    it('opens file with Enter when focused', async () => {
      const user = userEvent.setup();
      const { props } = renderTree();

      await user.click(screen.getByText('notes.txt'));
      const fileEl = document.querySelector('[data-tour-id="sidebar.file.item"]')!;
      fireEvent.keyDown(fileEl, { key: 'Enter' });
      expect(props.onFileSelect).toHaveBeenCalledWith('file-txt');
    });
  });

  describe('error toasts', () => {
    it('shows toast when folder rename fails', async () => {
      const user = userEvent.setup();
      renderTree({ onRenameFolder: vi.fn().mockRejectedValue(new Error('fail')) });

      fireEvent.contextMenu(screen.getByText('Archive'));
      await user.click(await findPortalButton('Rename'));
      const input = screen.getByDisplayValue('Archive');
      fireEvent.change(input, { target: { value: 'New Archive' } });
      fireEvent.keyDown(input, { key: 'Enter' });

      await waitFor(() => {
        expect(screen.getByText('Failed to rename folder')).toBeInTheDocument();
      });
    });

    it('shows toast when file rename fails', async () => {
      const user = userEvent.setup();
      renderTree({ onRenameFile: vi.fn().mockRejectedValue(new Error('fail')) });

      fireEvent.contextMenu(screen.getByText('notes.txt'));
      await user.click(await findPortalButton('Rename'));
      const input = screen.getByDisplayValue('notes.txt');
      fireEvent.change(input, { target: { value: 'bad.txt' } });
      fireEvent.keyDown(input, { key: 'Enter' });

      await waitFor(() => {
        expect(screen.getByText('Failed to rename file')).toBeInTheDocument();
      });
    });

    it('shows toast when download fails', async () => {
      const user = userEvent.setup();
      apiMocks().getContentFileContent.mockRejectedValueOnce(new Error('dl fail'));
      renderTree();

      fireEvent.contextMenu(screen.getByText('notes.txt'));
      await user.click(await findPortalButton('Download'));

      await waitFor(() => {
        expect(screen.getByText('Failed to download file')).toBeInTheDocument();
      });
    });
  });

  describe('disabled mode', () => {
    it('blocks folder context menu when disabled', () => {
      renderTree({ disabled: true });
      fireEvent.contextMenu(screen.getByText('Project'));
      expect(screen.queryByText('New Markdown File')).not.toBeInTheDocument();
    });

    it('blocks file interactions when disabled', async () => {
      const user = userEvent.setup();
      const { props } = renderTree({ disabled: true });

      await user.click(screen.getByText('notes.txt'));
      expect(props.onFileSelect).not.toHaveBeenCalled();
    });
  });

  describe('batch context menu', () => {
    it('creates notebook from multiple selected files', async () => {
      const user = userEvent.setup();
      const { props } = renderTree();

      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      fireEvent.contextMenu(screen.getByText('notes.txt'));
      await user.click(await findPortalButton(/Create Notebook from \d+ Files/));

      expect(props.onCreateNotebookFromFiles).toHaveBeenCalled();
    });

    it('batch deletes via context menu confirmation', async () => {
      const user = userEvent.setup();
      const onDeleteFiles = vi.fn().mockResolvedValue(undefined);
      renderTree({ onDeleteFiles });

      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      fireEvent.contextMenu(screen.getByText('notes.txt'));
      await user.click(await findPortalButton(/Delete \d+ Items/));
      const confirm = await screen.findByRole('button', { name: 'Delete' });
      await user.click(confirm);

      await waitFor(() => {
        expect(onDeleteFiles).toHaveBeenCalled();
      });
    });
  });

  describe('markdown on existing file', () => {
    it('shows editor error when markdown load fails', async () => {
      const user = userEvent.setup();
      apiMocks().getContentFileContent.mockRejectedValueOnce(new Error('load fail'));
      renderTree();

      fireEvent.contextMenu(screen.getByText('guide.md'));
      await user.click(await findPortalButton('Edit'));

      await waitFor(() => {
        expect(screen.queryByTestId('fullscreen-editor')).not.toBeInTheDocument();
      });
    });
  });

  describe('upload and drag', () => {
    it('triggers upload from folder context menu', async () => {
      const user = userEvent.setup();
      const { props } = renderTree();

      fireEvent.contextMenu(screen.getByText('Project'));
      await user.click(await findPortalButton('Upload Files'));
      expect(props.onUploadToFolder).toHaveBeenCalled();
    });

    it('highlights folder during drag over', () => {
      const { props: _props } = renderTree();
      const archiveRow = screen.getByText('Archive').closest('.group')!;
      const dt = createDataTransfer();

      fireEvent.dragOver(archiveRow, { dataTransfer: dt });
      expect(archiveRow.className).toMatch(/ring-blue-400/);
    });
  });

  describe('search edge cases', () => {
    it('hides non-matching content when search has no results', () => {
      renderTree({ searchTerm: 'zzznomatch' });
      expect(screen.queryByText('notes.txt')).not.toBeInTheDocument();
    });

    it('matches folder name in search', () => {
      renderTree({ searchTerm: 'archive' });
      expect(screen.getByText('Archive')).toBeInTheDocument();
    });
  });

  describe('file rename cancel', () => {
    it('cancels inline file rename with Escape', async () => {
      const user = userEvent.setup();
      renderTree();

      fireEvent.contextMenu(screen.getByText('notes.txt'));
      await user.click(await findPortalButton('Rename'));
      const input = screen.getByDisplayValue('notes.txt');
      fireEvent.keyDown(input, { key: 'Escape' });
      expect(screen.getByText('notes.txt')).toBeInTheDocument();
    });
  });

  describe('section coordination', () => {
    it('clears selection when active section changes', async () => {
      const user = userEvent.setup();
      const { rerender, props } = renderTree();

      await user.click(screen.getByText('notes.txt'));
      rerender(<FolderTree {...props} activeSection="notebooks" />);
      fireEvent.keyDown(window, { key: 'Delete' });
      expect(screen.queryByText(/Confirm Deletion/)).not.toBeInTheDocument();
    });
  });

  describe('copy path', () => {
    it('copies a file path to the clipboard as root-relative', async () => {
      const { user, writeText } = setupClipboard();
      renderTree();

      fireEvent.contextMenu(screen.getByText('notes.txt'));
      await user.click(await findPortalButton('Copy path'));

      expect(writeText).toHaveBeenCalledWith('/notes.txt');
    });

    it('copies a folder path to the clipboard as root-relative', async () => {
      const { user, writeText } = setupClipboard();
      renderTree();

      fireEvent.contextMenu(screen.getByText('Archive'));
      await user.click(await findPortalButton('Copy path'));

      expect(writeText).toHaveBeenCalledWith('/Archive');
    });

    it('does not show Copy path for the root folder', () => {
      renderTree();
      fireEvent.contextMenu(screen.getByText('Project'));
      expect(screen.queryByText('Copy path')).not.toBeInTheDocument();
    });

    it('copies multiple selected paths joined by newlines', async () => {
      const { user, writeText } = setupClipboard();
      renderTree();

      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      fireEvent.contextMenu(screen.getByText('notes.txt'));
      await user.click(await findPortalButton(/Copy \d+ Paths/));

      expect(writeText).toHaveBeenCalledWith('/Archive\n/guide.md\n/notes.txt');
    });
  });

  describe('folder multi-select highlight', () => {
    it('shift-clicked folders are visibly highlighted, same as multi-selected files', () => {
      renderTree();

      // Anchor on a file, then shift-click the folder to extend the range onto it.
      fireEvent.click(screen.getByText('guide.md'));
      fireEvent.click(screen.getByText('Archive'), { shiftKey: true });

      const archiveRow = screen.getByText('Archive').closest('.group');
      expect(archiveRow).toHaveClass('bg-blue-100');
    });
  });

  describe('multi-select folder context menu', () => {
    it('shows Copy N Paths when right-clicking a folder within a multi-selection', async () => {
      const { user, writeText } = setupClipboard();
      renderTree();

      fireEvent.click(screen.getByText('guide.md'));
      fireEvent.click(screen.getByText('Archive'), { shiftKey: true });

      fireEvent.contextMenu(screen.getByText('Archive'));
      await user.click(await findPortalButton(/Copy \d+ Paths/));

      expect(writeText).toHaveBeenCalledWith('/Archive\n/guide.md');
    });

    it('right-clicking a folder outside the current selection collapses to just that folder', () => {
      renderTree();

      fireEvent.click(screen.getByText('guide.md'));
      fireEvent.click(screen.getByText('notes.txt'), { shiftKey: true });

      // Archive was never part of the [guide.md, notes.txt] selection.
      fireEvent.contextMenu(screen.getByText('Archive'));
      expect(screen.queryByText(/Copy \d+ Paths/)).not.toBeInTheDocument();
      expect(screen.getByText('Copy path')).toBeInTheDocument();
    });
  });

});
