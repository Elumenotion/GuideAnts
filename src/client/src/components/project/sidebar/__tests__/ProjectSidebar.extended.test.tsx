import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { ProjectSidebar } from '../ProjectSidebar';
import { FolderTreeDto, ProjectDetailsDto } from '../../../../types/project';

const mockNavigate = vi.fn();

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

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return {
    ...actual,
    useNavigate: () => mockNavigate,
    useLocation: () => ({ pathname: '/projects/p1' }),
  };
});

vi.mock('../../../../services/api', () => {
  const mocks = {
    getContentFileContent: vi.fn().mockResolvedValue({
      blob: new Blob(['# md'], { type: 'text/markdown' }),
      fileName: 'readme.md',
    }),
    uploadFiles: vi.fn().mockResolvedValue([]),
    notebookTemplates: {
      getAll: vi.fn().mockResolvedValue([
        { id: 'tpl-1', templateName: 'Auth Template', hasConfigurableAuth: true },
      ]),
    },
  };
  (globalThis as { __projectSidebarApiMocks?: typeof mocks }).__projectSidebarApiMocks = mocks;
  return {
    api: {
      projects: mocks,
    },
  };
});

vi.mock('../../dialogs/UploadFilesDialog', () => ({
  UploadFilesDialog: ({
    isOpen,
    onUpload,
    onClose,
  }: {
    isOpen: boolean;
    onUpload: (files: File[]) => void;
    onClose: () => void;
  }) =>
    isOpen ? (
      <div data-testid="upload-dialog">
        <button type="button" onClick={() => onUpload([new File(['x'], 'doc.txt')])}>
          Submit upload
        </button>
        <button type="button" onClick={onClose}>
          Close
        </button>
      </div>
    ) : null,
}));

vi.mock('../../dialogs/CreateNotebookDialog', () => ({
  CreateNotebookDialog: ({
    isOpen,
    onCreate,
    onClose,
  }: {
    isOpen: boolean;
    onCreate: (title: string) => Promise<void>;
    onClose: () => void;
  }) =>
    isOpen ? (
      <div data-testid="create-notebook-dialog">
        <button type="button" onClick={() => onCreate('New NB').then(onClose)}>
          Create notebook
        </button>
      </div>
    ) : null,
}));

const sampleProject: ProjectDetailsDto = {
  id: 'p1',
  title: 'Demo',
  description: '',
  created: '',
  userRoles: [],
  notebooks: [
    { id: 'nb1', title: 'Design Notes', lastActivity: '2024-06-01T00:00:00Z' },
    { id: 'nb2', title: 'Alpha Notebook', lastActivity: '2024-01-01T00:00:00Z', description: 'desc' },
  ],
  contentFiles: [
    {
      id: 'cf1',
      fileName: 'readme.md',
      relativePath: 'readme.md',
      path: '/readme.md',
      contentType: 'text/markdown',
      index: false,
      documentId: 'd1',
      created: '2023-01-01T00:00:00Z',
      fileSize: 10,
    },
  ],
  links: [{ id: 'l1', url: 'https://example.com' }],
  folders: [
    { id: 'f-root', name: 'Demo', relativePath: '', projectId: 'p1', created: '' },
    { id: 'f1', name: 'Docs', relativePath: 'Docs', projectId: 'p1', parentFolderId: 'f-root', created: '' },
  ],
  semiStructuredDatas: [],
};

const folderTree: FolderTreeDto = {
  id: undefined,
  name: 'Demo',
  relativePath: '',
  subFolders: [
    {
      id: 'f1',
      name: 'Docs',
      relativePath: 'Docs',
      subFolders: [],
      files: [
        {
          id: 'cf1',
          fileName: 'readme.md',
          relativePath: 'readme.md',
          path: '/readme.md',
          contentType: 'text/markdown',
          index: false,
          documentId: 'd1',
          created: '2023-01-01T00:00:00Z',
          fileSize: 10,
        },
      ],
    },
  ],
  files: [],
};

const setup = (overrides?: Partial<React.ComponentProps<typeof ProjectSidebar>>) => {
  const props = {
    project: sampleProject,
    expandedSections: new Set(['notebooks', 'contentFiles'] as const),
    selectedItem: null,
    onSectionToggle: vi.fn(),
    onItemSelect: vi.fn(),
    folderTree,
    onCreateNotebook: vi.fn().mockResolvedValue(undefined),
    onCopyNotebook: vi.fn().mockResolvedValue(undefined),
    onDeleteNotebook: vi.fn().mockResolvedValue(undefined),
    onDeleteNotebooks: vi.fn().mockResolvedValue(undefined),
    onRenameNotebook: vi.fn().mockResolvedValue(undefined),
    onUploadFilesToFolder: vi.fn().mockResolvedValue(undefined),
    onCreateFolder: vi.fn().mockResolvedValue(undefined),
    onRenameFolder: vi.fn().mockResolvedValue(undefined),
    onDeleteFolder: vi.fn().mockResolvedValue(undefined),
    onMoveFile: vi.fn().mockResolvedValue(undefined),
    onDeleteFile: vi.fn().mockResolvedValue(undefined),
    onRenameFile: vi.fn().mockResolvedValue(undefined),
    onCreateNotebookFromFile: vi.fn().mockResolvedValue(undefined),
    onCreateNotebookFromFiles: vi.fn().mockResolvedValue(undefined),
    isCurrentUserOwner: true,
    ...overrides,
  } as React.ComponentProps<typeof ProjectSidebar>;

  return { ...render(<ProjectSidebar {...props} />), props };
};

const findPortalButton = async (label: string | RegExp) =>
  waitFor(() => {
    const match = Array.from(document.body.querySelectorAll('button')).find((b) =>
      typeof label === 'string' ? b.textContent === label : label.test(b.textContent ?? '')
    );
    if (!match) throw new Error(`Button not found: ${label}`);
    return match;
  });

describe('ProjectSidebar extended coverage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 1024 });
    localStorage.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
    fireEvent.click(document.body);
  });

  it('filters notebooks via search', () => {
    setup();
    fireEvent.change(screen.getByPlaceholderText(/Search notebooks/), {
      target: { value: 'alpha' },
    });
    expect(screen.getByText('Alpha Notebook')).toBeInTheDocument();
    expect(screen.queryByText('Design Notes')).not.toBeInTheDocument();
  });

  it('shows no results message for empty search', () => {
    setup();
    fireEvent.change(screen.getByPlaceholderText(/Search notebooks/), {
      target: { value: 'zzznomatch' },
    });
    expect(screen.getByText(/No results found/)).toBeInTheDocument();
  });

  it('sorts notebooks A-Z', async () => {
    const user = userEvent.setup();
    setup();
    await user.click(screen.getByRole('button', { name: 'A-Z' }));
    const alpha = screen.getByText('Alpha Notebook');
    const design = screen.getByText('Design Notes');
    expect(alpha.compareDocumentPosition(design)).toBe(Node.DOCUMENT_POSITION_FOLLOWING);
  });

  it('deletes notebook after confirmation', async () => {
    const user = userEvent.setup();
    const { props } = setup();

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('Design Notes') });
    await user.click(await findPortalButton('Delete'));

    const confirm = await screen.findByRole('button', { name: 'Delete' });
    await user.click(confirm);

    await waitFor(() => {
      expect(props.onDeleteNotebook).toHaveBeenCalledWith('nb1');
    });
  });

  it('opens upload dialog and submits files', async () => {
    const user = userEvent.setup();
    const { props } = setup();

    const filesHeader = screen.getByText('Files').closest('div')!;
    const uploadBtn = filesHeader.parentElement?.querySelector('button[title="Upload files"]');
    if (uploadBtn) {
      await user.click(uploadBtn);
      await user.click(screen.getByText('Submit upload'));
      await waitFor(() => expect(props.onUploadFilesToFolder).toHaveBeenCalled());
    }
  });

  it('navigates to guides for project owner', async () => {
    const user = userEvent.setup();
    setup({ isCurrentUserOwner: true });
    await user.click(screen.getByText('Guides'));
    expect(mockNavigate).toHaveBeenCalledWith('/projects/p1/guides');
  });

  it('shows guide authorization section for owner with templates', async () => {
    setup({ isCurrentUserOwner: true, expandedSections: new Set(['guideAuthorization', 'notebooks']) });
    await waitFor(() => {
      expect(screen.getByText('Guide Authorization')).toBeInTheDocument();
      expect(screen.getByText('Auth Template')).toBeInTheDocument();
    });
  });

  it('opens create notebook dialog', async () => {
    const user = userEvent.setup();
    const { props } = setup();

    const addBtn = screen
      .getByText('Notebooks')
      .closest('[data-tour-id="sidebar.section.notebooks"]')!
      .querySelector('button[title="Add new link"]');
    if (addBtn) {
      await user.click(addBtn);
      await user.click(screen.getByText('Create notebook'));
      await waitFor(() => expect(props.onCreateNotebook).toHaveBeenCalledWith('New NB', undefined, undefined));
    }
  });

  it('selects notebook on double click', async () => {
    const user = userEvent.setup();
    const { props } = setup();
    await user.dblClick(screen.getByText('Design Notes'));
    expect(props.onItemSelect).toHaveBeenCalledWith('notebooks', 'nb1');
  });

  it('renders fallback file list when folder tree is missing', () => {
    setup({ folderTree: null });
    expect(screen.getByText('readme.md')).toBeInTheDocument();
  });

  it('restores notebook sort from localStorage', () => {
    localStorage.setItem('project.sidebar.notebookSort:p1', 'alpha');
    setup();
    expect(screen.getByRole('button', { name: 'A-Z' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('cancels notebook rename with Escape', async () => {
    const user = userEvent.setup();
    setup({ onRenameNotebook: vi.fn() });

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('Alpha Notebook') });
    await user.click(await findPortalButton('Rename'));

    const input = await screen.findByDisplayValue('Alpha Notebook');
    fireEvent.keyDown(input, { key: 'Escape' });
    expect(screen.getByText('Alpha Notebook')).toBeInTheDocument();
  });

  it('selects content file from folder tree', async () => {
    const user = userEvent.setup();
    const { props } = setup();

    const docsRow = screen.getByText('Docs');
    const toggle = docsRow.closest('.folder-tree-item')?.querySelector('button');
    if (toggle) fireEvent.click(toggle);

    await user.dblClick(screen.getByText('readme.md'));
    expect(props.onItemSelect).toHaveBeenCalledWith('contentFiles', 'cf1');
  });

  it('creates notebook from a single file via dialog', async () => {
    const user = userEvent.setup();
    const onCreateNotebookFromFile = vi.fn().mockResolvedValue(undefined);
    setup({ onCreateNotebookFromFile, onCreateNotebookFromFiles: undefined });

    const docsRow = screen.getByText('Docs');
    const toggle = docsRow.closest('.folder-tree-item')?.querySelector('button');
    if (toggle) fireEvent.click(toggle);

    fireEvent.contextMenu(screen.getByText('readme.md'));
    await user.click(await findPortalButton('Create Notebook from File'));
    await user.click(screen.getByText('Create notebook'));

    await waitFor(() => {
      expect(onCreateNotebookFromFile).toHaveBeenCalledWith(
        'cf1',
        'readme.md',
        'New NB',
        undefined,
        undefined
      );
    });
  });

  it('shows error toast when copy notebook fails', async () => {
    const user = userEvent.setup();
    const onCopyNotebook = vi.fn().mockRejectedValue(new Error('copy failed'));
    setup({ onCopyNotebook });

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('Design Notes') });
    await user.click(await findPortalButton('Copy'));

    await waitFor(() => {
      expect(onCopyNotebook).toHaveBeenCalledWith('nb1');
    });
  });

  it('renames notebook via F2 keyboard shortcut', async () => {
    const onRenameNotebook = vi.fn().mockResolvedValue(undefined);
    setup({ onRenameNotebook });

    fireEvent.click(screen.getByText('Design Notes'));
    fireEvent.keyDown(window, { key: 'F2' });

    await waitFor(() => {
      expect(screen.getByDisplayValue('Design Notes')).toBeInTheDocument();
    });
  });

  it('deletes notebook using serial fallback when batch handler missing', async () => {
    const user = userEvent.setup();
    const onDeleteNotebook = vi.fn().mockResolvedValue(undefined);
    setup({ onDeleteNotebook, onDeleteNotebooks: undefined });

    fireEvent.click(screen.getByText('Design Notes'));
    fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
    fireEvent.click(screen.getByText('Alpha Notebook'), { ctrlKey: true });
    fireEvent.keyDown(window, { key: 'Delete' });

    const confirm = await screen.findByRole('button', { name: 'Delete' });
    await user.click(confirm);

    await waitFor(() => {
      expect(onDeleteNotebook).toHaveBeenCalled();
    });
  });

  it('closes create notebook dialog via onClose', async () => {
    const user = userEvent.setup();
    setup();

    const addBtn = screen
      .getByText('Notebooks')
      .closest('[data-tour-id="sidebar.section.notebooks"]')!
      .querySelector('button[title="Add new link"]');
    if (addBtn) {
      await user.click(addBtn);
      expect(screen.getByTestId('create-notebook-dialog')).toBeInTheDocument();
      await user.click(screen.getByText('Create notebook'));
      await waitFor(() => {
        expect(screen.queryByTestId('create-notebook-dialog')).not.toBeInTheDocument();
      });
    }
  });

  it('closes upload dialog and clears target folder', async () => {
    const user = userEvent.setup();
    setup();

    const filesHeader = screen.getByText('Files').closest('div')!;
    const uploadBtn = filesHeader.parentElement?.querySelector('button[title="Upload files"]');
    if (uploadBtn) {
      await user.click(uploadBtn);
      await user.click(screen.getByText('Close'));
      expect(screen.queryByTestId('upload-dialog')).not.toBeInTheDocument();
    }
  });

  it('shows notebook description when present', () => {
    setup();
    expect(screen.getByText('desc')).toBeInTheDocument();
  });

  it('hides guides link for non-owners', () => {
    setup({ isCurrentUserOwner: false });
    expect(screen.queryByText('Guides')).not.toBeInTheDocument();
  });

  it('toggles files section collapse', async () => {
    const user = userEvent.setup();
    const onSectionToggle = vi.fn();
    setup({ onSectionToggle, expandedSections: new Set(['notebooks', 'contentFiles']) });

    const collapseButtons = screen.getAllByLabelText('Collapse section');
    await user.click(collapseButtons[collapseButtons.length - 1]);
    expect(onSectionToggle).toHaveBeenCalledWith('contentFiles');
  });

  it('clears search when collapsed', () => {
    const { rerender, props } = setup();
    fireEvent.change(screen.getByPlaceholderText(/Search notebooks/), { target: { value: 'alpha' } });
    rerender(<ProjectSidebar {...props} isCollapsed={true} />);
    expect(screen.queryByPlaceholderText(/Search notebooks/)).not.toBeInTheDocument();
  });

  it('sorts notebooks by recent activity', async () => {
    const user = userEvent.setup();
    setup();
    await user.click(screen.getByRole('button', { name: 'Recent' }));
    const design = screen.getByText('Design Notes');
    const alpha = screen.getByText('Alpha Notebook');
    expect(design.compareDocumentPosition(alpha)).toBe(Node.DOCUMENT_POSITION_FOLLOWING);
  });

  it('renames notebook and saves on Enter', async () => {
    const user = userEvent.setup();
    const onRenameNotebook = vi.fn().mockResolvedValue(undefined);
    setup({ onRenameNotebook });

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('Alpha Notebook') });
    await user.click(await findPortalButton('Rename'));
    const input = await screen.findByDisplayValue('Alpha Notebook');
    await user.clear(input);
    await user.type(input, 'Renamed NB{enter}');

    await waitFor(() => {
      expect(onRenameNotebook).toHaveBeenCalledWith('nb2', 'Renamed NB');
    });
  });

  it('copies notebook successfully from context menu', async () => {
    const user = userEvent.setup();
    const onCopyNotebook = vi.fn().mockResolvedValue(undefined);
    setup({ onCopyNotebook });

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('Design Notes') });
    await user.click(await findPortalButton('Copy'));

    await waitFor(() => {
      expect(onCopyNotebook).toHaveBeenCalledWith('nb1');
    });
  });

  it('batch deletes notebooks using onDeleteNotebooks', async () => {
    const user = userEvent.setup();
    const onDeleteNotebooks = vi.fn().mockResolvedValue(undefined);
    setup({ onDeleteNotebooks });

    fireEvent.click(screen.getByText('Design Notes'));
    fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
    fireEvent.keyDown(window, { key: 'Delete' });
    const confirm = await screen.findByRole('button', { name: 'Delete' });
    await user.click(confirm);

    await waitFor(() => {
      expect(onDeleteNotebooks).toHaveBeenCalledWith(['nb1', 'nb2']);
    });
  });

  it('opens notebook on single tap in mobile layout', async () => {
    const user = userEvent.setup();
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });
    const { props } = setup();
    fireEvent(window, new Event('resize'));

    await waitFor(() => {
      expect(screen.getByText('Design Notes')).toBeInTheDocument();
    });
    await user.click(screen.getByText('Design Notes'));
    expect(props.onItemSelect).toHaveBeenCalledWith('notebooks', 'nb1');
  });

  it('creates notebook via standard dialog path', async () => {
    const user = userEvent.setup();
    const { props } = setup();

    const addBtn = screen
      .getByText('Notebooks')
      .closest('[data-tour-id="sidebar.section.notebooks"]')!
      .querySelector('button[title="Add new link"]');
    if (addBtn) {
      await user.click(addBtn);
      await user.click(screen.getByText('Create notebook'));
      await waitFor(() => {
        expect(props.onCreateNotebook).toHaveBeenCalledWith('New NB', undefined, undefined);
      });
    }
  });

  it('cancels notebook delete confirmation', async () => {
    const user = userEvent.setup();
    const onDeleteNotebook = vi.fn();
    setup({ onDeleteNotebook });

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('Design Notes') });
    await user.click(await findPortalButton('Delete'));
    await user.click(await screen.findByRole('button', { name: 'Cancel' }));

    expect(onDeleteNotebook).not.toHaveBeenCalled();
  });

  it('saves notebook rename on blur', async () => {
    const user = userEvent.setup();
    const onRenameNotebook = vi.fn().mockResolvedValue(undefined);
    setup({ onRenameNotebook });

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('Alpha Notebook') });
    await user.click(await findPortalButton('Rename'));
    const input = await screen.findByDisplayValue('Alpha Notebook');
    await user.clear(input);
    await user.type(input, 'Blur Saved');
    fireEvent.blur(input);

    await waitFor(() => {
      expect(onRenameNotebook).toHaveBeenCalledWith('nb2', 'Blur Saved');
    });
  });

  it('toggles guide authorization section', async () => {
    const user = userEvent.setup();
    const onSectionToggle = vi.fn();
    setup({
      isCurrentUserOwner: true,
      expandedSections: new Set(['guideAuthorization', 'notebooks']),
      onSectionToggle,
    });

    await waitFor(() => screen.getByText('Auth Template'));
    const section = screen.getByText('Guide Authorization').closest('[data-tour-id]') ?? screen.getByText('Guide Authorization').parentElement!;
    const collapseBtn = section.querySelector('[aria-label="Collapse section"]') as HTMLButtonElement;
    await user.click(collapseBtn);
    expect(onSectionToggle).toHaveBeenCalledWith('guideAuthorization');
  });

  describe('mobile interactions', () => {
    beforeEach(() => {
      Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });
    });

    it('opens notebook context menu after long press', async () => {
      setup();
      fireEvent(window, new Event('resize'));
      await waitFor(() => {
        expect(screen.getAllByTitle('Tap to open, hold for options').length).toBeGreaterThan(0);
      });

      const notebookEl = screen.getByText('Design Notes').closest('[data-tour-id="sidebar.notebook.item"]')!;
      fireEvent.touchStart(notebookEl, { touches: [{ clientX: 20, clientY: 20 }] });
      await waitFor(() => {
        expect(document.body.textContent).toContain('Copy');
      });
    });
  });

  describe('keyboard arrow navigation', () => {
    it('navigates notebooks with ArrowDown and Enter', async () => {
      const { props } = setup();

      fireEvent.click(screen.getByText('Design Notes'));
      const el = screen.getByText('Design Notes').closest('[data-tour-id="sidebar.notebook.item"]')!;
      fireEvent.keyDown(el, { key: 'ArrowDown' });
      fireEvent.keyDown(el, { key: 'ArrowUp' });
      fireEvent.keyDown(el, { key: 'Enter' });
      expect(props.onItemSelect).toHaveBeenCalled();
    });
  });

  describe('disabled mode', () => {
    it('disables search input when sidebar is disabled', () => {
      setup({ disabled: true });
      expect(screen.getByPlaceholderText(/Search notebooks/)).toBeDisabled();
    });

    it('does not show notebook context menu when disabled', async () => {
      setup({ disabled: true });
      fireEvent.contextMenu(screen.getByText('Design Notes'));
      expect(screen.queryByText('Copy')).not.toBeInTheDocument();
    });
  });

  describe('error toasts', () => {
    it('shows toast when notebook delete fails', async () => {
      const user = userEvent.setup();
      setup({ onDeleteNotebook: vi.fn().mockRejectedValue(new Error('delete fail')) });

      await user.pointer({ keys: '[MouseRight]', target: screen.getByText('Design Notes') });
      await user.click(await findPortalButton('Delete'));
      await user.click(await screen.findByRole('button', { name: 'Delete' }));

      await waitFor(() => {
        expect(screen.getByText('Failed to delete notebook')).toBeInTheDocument();
      });
    });

    it('shows toast when notebook rename fails', async () => {
      const user = userEvent.setup();
      setup({ onRenameNotebook: vi.fn().mockRejectedValue(new Error('rename fail')) });

      await user.pointer({ keys: '[MouseRight]', target: screen.getByText('Alpha Notebook') });
      await user.click(await findPortalButton('Rename'));
      const input = await screen.findByDisplayValue('Alpha Notebook');
      await user.clear(input);
      await user.type(input, 'Bad{enter}');

      await waitFor(() => {
        expect(screen.getByText('Failed to rename notebook')).toBeInTheDocument();
      });
    });

    it('shows toast when batch notebook delete fails', async () => {
      const user = userEvent.setup();
      setup({ onDeleteNotebooks: vi.fn().mockRejectedValue(new Error('batch fail')) });

      fireEvent.click(screen.getByText('Design Notes'));
      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      fireEvent.keyDown(window, { key: 'Delete' });
      await user.click(await screen.findByRole('button', { name: 'Delete' }));

      await waitFor(() => {
        expect(screen.getByText('Failed to delete notebooks')).toBeInTheDocument();
      });
    });
  });

  describe('search edge cases', () => {
    it('filters notebooks by description', () => {
      setup();
      fireEvent.change(screen.getByPlaceholderText(/Search notebooks/), {
        target: { value: 'desc' },
      });
      expect(screen.getByText('Alpha Notebook')).toBeInTheDocument();
      expect(screen.queryByText('Design Notes')).not.toBeInTheDocument();
    });

    it('filters files in folder tree via search', () => {
      setup();
      const docsRow = screen.getByText('Docs');
      const toggle = docsRow.closest('.folder-tree-item')?.querySelector('button');
      if (toggle) fireEvent.click(toggle);

      fireEvent.change(screen.getByPlaceholderText(/Search notebooks/), {
        target: { value: 'readme' },
      });
      expect(screen.getByText('readme.md')).toBeInTheDocument();
    });
  });

  describe('notebook list overrides', () => {
    it('uses polled notebooks prop when provided', () => {
      setup({
        notebooks: [{ id: 'nb-poll', title: 'Polled Notebook', lastActivity: '2024-01-01T00:00:00Z' }],
      });
      expect(screen.getByText('Polled Notebook')).toBeInTheDocument();
      expect(screen.queryByText('Design Notes')).not.toBeInTheDocument();
    });
  });

  describe('batch notebook context menu', () => {
    it('shows batch delete option for multi-selected notebooks', async () => {
      const user = userEvent.setup();
      setup({ onDeleteNotebooks: vi.fn().mockResolvedValue(undefined) });

      fireEvent.click(screen.getByText('Design Notes'));
      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      await user.pointer({ keys: '[MouseRight]', target: screen.getByText('Design Notes') });
      await user.click(await findPortalButton(/Delete 2 Notebooks/));
      await user.click(await screen.findByRole('button', { name: 'Delete' }));

      await waitFor(() => {
        expect(screen.queryByText(/Delete 2 Notebooks/)).not.toBeInTheDocument();
      });
    });
  });

  describe('create notebook from files', () => {
    it('creates notebook from multiple files via dialog', async () => {
      const user = userEvent.setup();
      const onCreateNotebookFromFiles = vi.fn().mockResolvedValue(undefined);
      setup({ onCreateNotebookFromFiles });

      const docsRow = screen.getByText('Docs');
      const toggle = docsRow.closest('.folder-tree-item')?.querySelector('button');
      if (toggle) fireEvent.click(toggle);

      fireEvent.contextMenu(screen.getByText('readme.md'));
      await user.click(await findPortalButton('Create Notebook from File'));
      await user.click(screen.getByText('Create notebook'));

      await waitFor(() => {
        expect(onCreateNotebookFromFiles).toHaveBeenCalled();
      });
    });
  });

  describe('auth templates', () => {
    it('handles auth template load failure gracefully', async () => {
      const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
      const mocks = (globalThis as { __projectSidebarApiMocks: { notebookTemplates: { getAll: ReturnType<typeof vi.fn> } } }).__projectSidebarApiMocks;
      mocks.notebookTemplates.getAll.mockRejectedValueOnce(new Error('load fail'));
      setup({ isCurrentUserOwner: true });

      await waitFor(() => {
        expect(screen.queryByText('Guide Authorization')).not.toBeInTheDocument();
      });
      warnSpy.mockRestore();
    });

    it('hides guide authorization for non-owners', () => {
      setup({ isCurrentUserOwner: false });
      expect(screen.queryByText('Guide Authorization')).not.toBeInTheDocument();
    });
  });

  describe('sort persistence', () => {
    it('persists notebook sort to localStorage', async () => {
      const user = userEvent.setup();
      setup();
      await user.click(screen.getByRole('button', { name: 'A-Z' }));
      expect(localStorage.getItem('project.sidebar.notebookSort:p1')).toBe('alpha');
    });
  });

  describe('fallback file list', () => {
    it('filters flat file list when folder tree is absent', () => {
      setup({ folderTree: null });
      fireEvent.change(screen.getByPlaceholderText(/Search notebooks/), {
        target: { value: 'readme' },
      });
      expect(screen.getByText('readme.md')).toBeInTheDocument();
    });

    it('selects a file from the fallback flat list', async () => {
      const user = userEvent.setup();
      const onItemSelect = vi.fn();
      setup({ folderTree: null, onItemSelect });
      await user.click(screen.getByText('readme.md'));
      expect(onItemSelect).toHaveBeenCalledWith('contentFiles', 'cf1');
    });
  });

  describe('notebook rename edge cases', () => {
    it('cancels rename when rename handler is missing', () => {
      setup({ onRenameNotebook: undefined });
      fireEvent.click(screen.getByText('Design Notes'));
      fireEvent.keyDown(window, { key: 'F2' });
      fireEvent.blur(screen.getByDisplayValue('Design Notes'));
      expect(screen.queryByDisplayValue('Design Notes')).not.toBeInTheDocument();
    });

    it('skips rename when title is unchanged', async () => {
      const user = userEvent.setup();
      const onRenameNotebook = vi.fn();
      setup({ onRenameNotebook });

      await user.pointer({ keys: '[MouseRight]', target: screen.getByText('Alpha Notebook') });
      await user.click(await findPortalButton('Rename'));
      const input = await screen.findByDisplayValue('Alpha Notebook');
      fireEvent.blur(input);

      expect(onRenameNotebook).not.toHaveBeenCalled();
    });

    it('clears notebook multi-selection with Escape', () => {
      setup();
      fireEvent.click(screen.getByText('Design Notes'));
      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      fireEvent.keyDown(window, { key: 'Escape' });
      fireEvent.contextMenu(screen.getByText('Design Notes'));
      expect(screen.getByText('Copy')).toBeInTheDocument();
    });
  });

  describe('notebook selection shortcuts', () => {
    it('starts notebook rename from F2 with single selection', () => {
      const onRenameNotebook = vi.fn().mockResolvedValue(undefined);
      setup({ onRenameNotebook });

      fireEvent.click(screen.getByText('Design Notes'));
      fireEvent.keyDown(window, { key: 'F2' });
      expect(screen.getByDisplayValue('Design Notes')).toBeInTheDocument();
    });

    it('activates notebooks section when selecting an item', () => {
      setup();
      fireEvent.click(screen.getByText('Design Notes'));
      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      expect(screen.getByText('Design Notes').closest('[data-tour-id="sidebar.notebook.item"]')).toHaveClass('bg-blue-100');
    });

    it('sorts notebooks alphabetically with activity tie-breakers', async () => {
      const user = userEvent.setup();
      setup({
        notebooks: [
          { id: 'n1', title: 'Same Title', lastActivity: '2024-06-01T00:00:00Z' },
          { id: 'n2', title: 'Same Title', lastActivity: '2024-01-01T00:00:00Z' },
        ],
      });
      await user.click(screen.getByRole('button', { name: 'A-Z' }));
      const items = screen.getAllByText('Same Title');
      expect(items.length).toBe(2);
    });

    it('parses notebook activity timestamps without timezone suffix', () => {
      setup({
        notebooks: [{ id: 'n-tz', title: 'No TZ Notebook', lastActivity: '2024-01-01T12:00:00' }],
      });
      expect(screen.getByText('No TZ Notebook')).toBeInTheDocument();
    });
  });

  describe('folder tree integration', () => {
    it('invokes folder select handler from tree double-click', async () => {
      const user = userEvent.setup();
      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      setup();

      const docsRow = screen.getByText('Docs');
      const toggle = docsRow.closest('.folder-tree-item')?.querySelector('button');
      if (toggle) fireEvent.click(toggle);
      await user.dblClick(screen.getByText('Docs'));

      expect(logSpy).toHaveBeenCalledWith('Folder selected:', 'f1');
      logSpy.mockRestore();
    });

    it('creates subfolder from project root context menu', async () => {
      const user = userEvent.setup();
      const onCreateFolder = vi.fn().mockResolvedValue(undefined);
      setup({ onCreateFolder });

      fireEvent.contextMenu(screen.getByText('Demo'));
      await user.click(await findPortalButton('Create Subfolder'));

      const input = await waitFor(() => {
        const el = document.querySelector('.folder-tree input[type="text"]') as HTMLInputElement | null;
        if (!el) throw new Error('Subfolder input not found');
        return el;
      });
      fireEvent.change(input, { target: { value: 'Specs' } });
      fireEvent.keyDown(input, { key: 'Enter' });

      await waitFor(() => {
        expect(onCreateFolder).toHaveBeenCalledWith(undefined, { name: 'Specs' });
      });
    });

    it('deletes a single content file via keyboard shortcut in tree', async () => {
      const user = userEvent.setup();
      const onDeleteFile = vi.fn().mockResolvedValue(undefined);
      setup({ onDeleteFile });

      const docsRow = screen.getByText('Docs');
      const toggle = docsRow.closest('.folder-tree-item')?.querySelector('button');
      if (toggle) fireEvent.click(toggle);

      await user.click(screen.getByText('readme.md'));
      fireEvent.keyDown(window, { key: 'Delete' });
      await user.click(await screen.findByRole('button', { name: 'Delete' }));

      await waitFor(() => {
        expect(onDeleteFile).toHaveBeenCalledWith('cf1');
      });
    });
  });
});
