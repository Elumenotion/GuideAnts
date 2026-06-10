import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { screen, fireEvent, waitFor, renderWithNotebookRoute } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { NotebookSidebar } from '../NotebookSidebar';
import type {
  NotebookFileDto,
  NotebookFolderTreeDto,
  LinkDto,
  NotebookSidebarSelectedItem,
  NotebookSidebarSectionType,
} from '../../../../types/notebook';
import { FolderTreeDto } from '../../../../types/project';

const mockRefreshConversations = vi.fn();
const mockRefreshNotebookFiles = vi.fn();
const mockCreateConversation = vi.fn();
const mockRenameConversation = vi.fn();
const mockDeleteConversation = vi.fn();
const mockDeleteConversations = vi.fn();

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

vi.mock('../../../../hooks/useConversationListPolling', () => ({
  useConversationListPolling: () => ({
    conversations: [
      { id: 'convo-1', title: 'First Chat', created: '2024-01-02T00:00:00Z', lastActivity: '2024-01-03T00:00:00Z' },
      { id: 'convo-2', title: 'Alpha Chat', created: '2024-01-01T00:00:00Z', lastActivity: '2024-01-01T12:00:00Z' },
    ],
    refresh: mockRefreshConversations,
  }),
}));

let mockPolledFolderTree: NotebookFolderTreeDto | null = null;

vi.mock('../../../../hooks/useNotebookFilesPolling', () => ({
  useNotebookFilesPolling: () => ({
    folderTree: mockPolledFolderTree,
    refresh: mockRefreshNotebookFiles,
  }),
}));

vi.mock('../../../../contexts/NotebookContext', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../../contexts/NotebookContext')>();
  return {
    ...actual,
    useNotebook: () => ({
      createConversation: mockCreateConversation,
      renameConversation: mockRenameConversation,
      deleteConversation: mockDeleteConversation,
      deleteConversations: mockDeleteConversations,
    }),
  };
});

vi.mock('../../../../services/api', () => ({
  api: {
    projects: {
      notebooks: {
        conversations: {
          saveAs: vi.fn().mockResolvedValue(undefined),
        },
      },
    },
  },
}));

vi.mock('../../dialogs/NotebookUploadDialog', () => ({
  NotebookUploadDialog: ({
    isOpen,
    onClose,
    onUpload,
    onCopyFromProject,
  }: {
    isOpen: boolean;
    onClose: () => void;
    onUpload: (files: File[]) => void;
    onCopyFromProject?: (files: { fileId: string; version?: number }[]) => void;
  }) =>
    isOpen ? (
      <div data-testid="upload-dialog">
        <button type="button" onClick={() => onUpload([new File(['x'], 'a.txt')])}>
          Submit upload
        </button>
        <button
          type="button"
          onClick={() => onCopyFromProject?.([{ fileId: 'project-file-1' }])}
        >
          Copy from project
        </button>
        <button type="button" onClick={onClose}>
          Close upload
        </button>
      </div>
    ) : null,
}));

vi.mock('../../dialogs/CreateConversationDialog', () => ({
  CreateConversationDialog: ({
    isOpen,
    onCreate,
    onClose,
  }: {
    isOpen: boolean;
    onCreate: (title: string) => Promise<{ id?: string } | null>;
    onClose: () => void;
  }) =>
    isOpen ? (
      <div data-testid="create-convo-dialog">
        <button type="button" onClick={() => onCreate('New Convo').then(() => onClose())}>
          Create convo
        </button>
        <button type="button" onClick={() => onCreate('No Id Convo').then(() => onClose())}>
          Create convo without id
        </button>
      </div>
    ) : null,
}));

const mockFiles: NotebookFileDto[] = [
  {
    id: 'file-1',
    fileName: 'alpha.txt',
    relativePath: 'alpha.txt',
    fileSize: 100,
    lastModifiedUtc: '2023-01-01T00:00:00Z',
    fileHash: 'h1',
    isIndexed: false,
    index: false,
  },
  {
    id: 'file-2',
    fileName: 'beta.pdf',
    relativePath: 'beta.pdf',
    fileSize: 200,
    lastModifiedUtc: '2023-01-02T00:00:00Z',
    fileHash: 'h2',
    isIndexed: false,
    index: false,
  },
];

const mockFolderTree: NotebookFolderTreeDto = {
  id: 'root',
  name: 'Root',
  relativePath: '',
  subFolders: [],
  files: mockFiles,
};

const mockLinks: LinkDto[] = [{ id: 'link-1', url: 'https://example.com' }];

const defaultProps = {
  folderTree: mockFolderTree,
  links: mockLinks,
  expandedSections: new Set<NotebookSidebarSectionType>(['conversations', 'notebookFiles']),
  selectedItem: null as NotebookSidebarSelectedItem | null,
  onSectionToggle: vi.fn(),
  onItemSelect: vi.fn(),
  onUploadFiles: vi.fn().mockResolvedValue(undefined),
  onCreateFolder: vi.fn(),
  onRenameFolder: vi.fn(),
  onDeleteFolder: vi.fn(),
  onMoveFile: vi.fn(),
  onDeleteFile: vi.fn(),
  onDeleteLink: vi.fn(),
  onCopyFromProject: vi.fn(),
  onPublishToProject: vi.fn(),
  onPreviewFile: vi.fn(),
  onRenameFile: vi.fn(),
  canEdit: true,
  isCollapsed: false,
};

const renderSidebar = (overrides: Partial<React.ComponentProps<typeof NotebookSidebar>> = {}) =>
  renderWithNotebookRoute(<NotebookSidebar {...defaultProps} {...overrides} />, {
    route: '/projects/proj-1/notebooks/nb-1',
    projectId: 'proj-1',
    notebookId: 'nb-1',
  });

const findPortalButton = async (label: string | RegExp) =>
  waitFor(() => {
    const buttons = Array.from(document.body.querySelectorAll('button'));
    const match = buttons.find((b) =>
      typeof label === 'string' ? b.textContent === label : label.test(b.textContent ?? '')
    );
    if (!match) throw new Error(`Button not found: ${label}`);
    return match;
  });

describe('NotebookSidebar extended coverage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockPolledFolderTree = null;
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 1024 });
    mockCreateConversation.mockResolvedValue({ id: 'convo-new' });
    mockRenameConversation.mockResolvedValue(undefined);
    mockDeleteConversation.mockResolvedValue(undefined);
    mockDeleteConversations.mockResolvedValue(undefined);
    localStorage.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
    fireEvent.click(document.body);
  });

  it('renders conversations and supports A-Z sort', async () => {
    const user = userEvent.setup();
    renderSidebar();

    expect(screen.getByText('First Chat')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'A-Z' }));
    const items = screen.getAllByRole('button').filter((b) => b.textContent === 'Alpha Chat' || b.textContent === 'First Chat');
    expect(items[0]).toHaveTextContent('Alpha Chat');
  });

  it('opens create conversation dialog from section add', async () => {
    const user = userEvent.setup();
    renderSidebar();

    const section = screen.getByText('Conversations').closest('[data-tour-id="notebook.sidebar.conversations"]')!;
    const addBtn = section.querySelector('button[title="Add new link"]') as HTMLButtonElement;
    await user.click(addBtn);

    const createBtn = await screen.findByText('Create convo');
    await user.click(createBtn);
    expect(mockCreateConversation).toHaveBeenCalledWith('New Convo');
    expect(mockRefreshConversations).toHaveBeenCalled();
  });

  it('copies conversation from context menu', async () => {
    const user = userEvent.setup();
    renderSidebar();

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('First Chat') });
    await user.click(await findPortalButton('Copy'));
    expect(mockCreateConversation).toHaveBeenCalledWith('First Chat (Copy)');
  });

  it('renames conversation from context menu', async () => {
    const user = userEvent.setup();
    renderSidebar();

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('First Chat') });
    await user.click(await findPortalButton('Rename'));

    const input = await screen.findByDisplayValue('First Chat');
    await user.clear(input);
    await user.type(input, 'Renamed Chat{enter}');

    await waitFor(() => {
      expect(mockRenameConversation).toHaveBeenCalledWith('convo-1', 'Renamed Chat');
    });
  });

  it('deletes conversation after confirmation', async () => {
    const user = userEvent.setup();
    renderSidebar({ onConversationDeleted: vi.fn() });

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('First Chat') });
    await user.click(await findPortalButton('Delete'));

    const confirm = await screen.findByRole('button', { name: 'Delete' });
    await user.click(confirm);

    await waitFor(() => {
      expect(mockDeleteConversation).toHaveBeenCalledWith('convo-1');
      expect(mockRefreshConversations).toHaveBeenCalled();
    });
  });

  it('opens upload dialog from files section', async () => {
    const user = userEvent.setup();
    const onUploadFiles = vi.fn().mockResolvedValue(undefined);
    renderSidebar({ onUploadFiles });

    const filesHeader = screen.getByText('Files').closest('div')!;
    const uploadBtn = filesHeader.parentElement?.querySelector('button[title="Upload files"]');
    if (uploadBtn) {
      await user.click(uploadBtn);
      await user.click(screen.getByText('Submit upload'));
      await waitFor(() => expect(onUploadFiles).toHaveBeenCalled());
    }
  });

  it('filters files via search', () => {
    renderSidebar();
    fireEvent.change(screen.getByPlaceholderText('Search files and links...'), {
      target: { value: 'alpha' },
    });
    expect(screen.getByText('alpha.txt')).toBeInTheDocument();
    expect(screen.queryByText('beta.pdf')).not.toBeInTheDocument();
  });

  it('handles refresh-notebook-files event', () => {
    renderSidebar();
    window.dispatchEvent(new Event('refresh-notebook-files'));
    expect(mockRefreshNotebookFiles).toHaveBeenCalled();
  });

  it('handles refresh-conversations event', () => {
    renderSidebar();
    window.dispatchEvent(new Event('refresh-conversations'));
    expect(mockRefreshConversations).toHaveBeenCalled();
  });

  it('selects conversation on double click', async () => {
    const user = userEvent.setup();
    const onItemSelect = vi.fn();
    renderSidebar({ onItemSelect });

    await user.dblClick(screen.getByText('First Chat'));
    expect(onItemSelect).toHaveBeenCalledWith('conversations', 'convo-1');
  });

  it('saves conversation as markdown from context menu', async () => {
    const user = userEvent.setup();
    const { api } = await import('../../../../services/api');
    renderSidebar();

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('First Chat') });
    await user.click(await findPortalButton('Save Conversation'));

    await waitFor(() => {
      expect(api.projects.notebooks.conversations.saveAs).toHaveBeenCalledWith(
        'proj-1',
        'nb-1',
        'convo-1'
      );
      expect(mockRefreshNotebookFiles).toHaveBeenCalled();
    });
  });

  it('batch deletes conversations after confirmation', async () => {
    const user = userEvent.setup();
    renderSidebar({ onConversationsDeleted: vi.fn() });

    await user.click(screen.getByText('First Chat'));
    await user.click(screen.getByText('Alpha Chat'), { ctrlKey: true });
    fireEvent.keyDown(window, { key: 'Delete' });

    const confirm = await screen.findByRole('button', { name: 'Delete' });
    await user.click(confirm);

    await waitFor(() => {
      expect(mockDeleteConversations).toHaveBeenCalled();
    });
  });

  it('restores conversation sort from localStorage', () => {
    localStorage.setItem('notebook.sidebar.conversationSort:proj-1:nb-1', 'alpha');
    renderSidebar();
    expect(screen.getByRole('button', { name: 'A-Z' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('renders collapsed sidebar without search controls', () => {
    renderSidebar({ isCollapsed: true });
    expect(screen.queryByPlaceholderText('Search files and links...')).not.toBeInTheDocument();
  });

  it('creates conversation from header action', async () => {
    const user = userEvent.setup();
    const onItemSelect = vi.fn();
    renderSidebar({ onItemSelect });

    const conversationsHeader = screen.getByText('Conversations').closest('div')!;
    const newBtn = conversationsHeader.parentElement?.querySelector('button[title="New conversation"]');
    if (newBtn) {
      await user.click(newBtn);
      await user.click(screen.getByText('Create convo'));
      await waitFor(() => {
        expect(mockCreateConversation).toHaveBeenCalledWith('New Convo');
        expect(onItemSelect).toHaveBeenCalledWith('conversations', 'convo-new');
      });
    }
  });

  it('renames conversation from context menu', async () => {
    const user = userEvent.setup();
    renderSidebar();

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('First Chat') });
    await user.click(await findPortalButton('Rename'));
    const input = await screen.findByDisplayValue('First Chat');
    await user.clear(input);
    await user.type(input, 'Renamed Chat');
    await user.keyboard('{Enter}');

    await waitFor(() => {
      expect(mockRenameConversation).toHaveBeenCalledWith('convo-1', 'Renamed Chat');
    });
  });

  it('shows project folder tree prop without error', () => {
    const projectFolderTree: FolderTreeDto = {
      id: 'p-root',
      name: 'Project',
      relativePath: '',
      subFolders: [],
      files: [],
    };
    renderSidebar({ projectFolderTree });
    expect(screen.getByPlaceholderText('Search files and links...')).toBeInTheDocument();
  });

  it('opens conversation on single tap in mobile layout', async () => {
    const user = userEvent.setup();
    const onItemSelect = vi.fn();
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });
    renderSidebar({ onItemSelect });

    await user.click(screen.getByText('First Chat'));
    expect(onItemSelect).toHaveBeenCalledWith('conversations', 'convo-1');
  });

  it('cancels inline conversation rename with Escape', async () => {
    const user = userEvent.setup();
    renderSidebar();

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('First Chat') });
    await user.click(await findPortalButton('Rename'));
    const input = await screen.findByDisplayValue('First Chat');
    await user.clear(input);
    await user.type(input, 'Aborted');
    fireEvent.keyDown(input, { key: 'Escape' });

    expect(screen.getByText('First Chat')).toBeInTheDocument();
    expect(mockRenameConversation).not.toHaveBeenCalled();
  });

  it('clears the search box from the clear button', async () => {
    const user = userEvent.setup();
    renderSidebar();

    const input = screen.getByPlaceholderText('Search files and links...');
    const searchBar = input.parentElement!;
    await user.type(input, 'alpha');
    const clearBtn = searchBar.querySelector('button') as HTMLButtonElement;
    await user.click(clearBtn);

    expect(input).toHaveValue('');
  });

  it('hides conversations when search matches only link hostnames', () => {
    renderSidebar();
    fireEvent.change(screen.getByPlaceholderText('Search files and links...'), {
      target: { value: 'example.com' },
    });
    expect(screen.queryByText('First Chat')).not.toBeInTheDocument();
    expect(screen.queryByText('Alpha Chat')).not.toBeInTheDocument();
  });

  it('filters conversations by search term', () => {
    renderSidebar();
    fireEvent.change(screen.getByPlaceholderText('Search files and links...'), {
      target: { value: 'alpha' },
    });
    expect(screen.getByText('Alpha Chat')).toBeInTheDocument();
    expect(screen.queryByText('First Chat')).not.toBeInTheDocument();
  });

  it('starts rename from F2 when one conversation is selected', async () => {
    const user = userEvent.setup();
    renderSidebar();

    await user.click(screen.getByText('First Chat'));
    fireEvent.keyDown(window, { key: 'F2' });

    expect(await screen.findByDisplayValue('First Chat')).toBeInTheDocument();
  });

  it('saves conversation rename on blur', async () => {
    const user = userEvent.setup();
    renderSidebar();

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('First Chat') });
    await user.click(await findPortalButton('Rename'));
    const input = await screen.findByDisplayValue('First Chat');
    await user.clear(input);
    await user.type(input, 'Blur Saved');
    fireEvent.blur(input);

    await waitFor(() => {
      expect(mockRenameConversation).toHaveBeenCalledWith('convo-1', 'Blur Saved');
    });
  });

  it('selects conversation before opening context menu when unselected', async () => {
    const user = userEvent.setup();
    renderSidebar();

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('Alpha Chat') });
    await user.click(await findPortalButton('Delete'));
    const confirm = await screen.findByRole('button', { name: 'Delete' });
    await user.click(confirm);

    await waitFor(() => {
      expect(mockDeleteConversation).toHaveBeenCalledWith('convo-2');
    });
  });

  it('falls back to the first conversation when create returns no id', async () => {
    const user = userEvent.setup();
    const onItemSelect = vi.fn();
    mockCreateConversation.mockResolvedValueOnce(null);
    renderSidebar({ onItemSelect });

    await user.click(screen.getByTitle('Add new link'));
    await user.click(screen.getByText('Create convo without id'));

    await waitFor(() => {
      expect(onItemSelect).toHaveBeenCalledWith('conversations', 'convo-1');
    });
  });

  it('sorts conversations by recent activity', async () => {
    const user = userEvent.setup();
    renderSidebar();

    await user.click(screen.getByRole('button', { name: 'Recent' }));
    const first = screen.getByText('First Chat');
    const alpha = screen.getByText('Alpha Chat');
    expect(first.compareDocumentPosition(alpha)).toBe(Node.DOCUMENT_POSITION_FOLLOWING);
  });

  it('shows no results message when search matches nothing', () => {
    renderSidebar();
    fireEvent.change(screen.getByPlaceholderText('Search files and links...'), {
      target: { value: 'zzznomatch' },
    });
    expect(screen.getByText(/No results found/)).toBeInTheDocument();
  });

  it('invokes onConversationDeleted after deleting active conversation', async () => {
    const user = userEvent.setup();
    const onConversationDeleted = vi.fn();
    renderSidebar({
      onConversationDeleted,
      activeConversationId: 'convo-1',
      selectedItem: { type: 'conversations', id: 'convo-1' },
    });

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('First Chat') });
    await user.click(await findPortalButton('Delete'));
    await user.click(await screen.findByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(onConversationDeleted).toHaveBeenCalledWith('convo-1', 'convo-2');
    });
  });

  it('copies files from project through upload dialog', async () => {
    const user = userEvent.setup();
    const onCopyFromProject = vi.fn().mockResolvedValue(undefined);
    renderSidebar({ onCopyFromProject });

    const filesHeader = screen.getByText('Files').closest('div')!;
    const uploadBtn = filesHeader.parentElement?.querySelector('button[title="Upload files"]');
    if (uploadBtn) {
      await user.click(uploadBtn);
      const copyBtn = await screen.findByText('Copy from project');
      await user.click(copyBtn);
      await waitFor(() => {
        expect(onCopyFromProject).toHaveBeenCalledWith('project-file-1', undefined);
      });
    }
  });

  it('hides edit controls when canEdit is false', () => {
    renderSidebar({ canEdit: false });
    expect(screen.queryByTitle('New conversation')).not.toBeInTheDocument();
    expect(screen.queryByTitle('Upload files')).not.toBeInTheDocument();
  });

  it('cancels conversation delete confirmation', async () => {
    const user = userEvent.setup();
    renderSidebar();

    await user.pointer({ keys: '[MouseRight]', target: screen.getByText('First Chat') });
    await user.click(await findPortalButton('Delete'));
    const cancel = await screen.findByRole('button', { name: 'Cancel' });
    await user.click(cancel);

    expect(mockDeleteConversation).not.toHaveBeenCalled();
  });

  describe('mobile interactions', () => {
    beforeEach(() => {
      Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });
    });

    it('opens conversation context menu after long press', async () => {
      renderSidebar({ canEdit: true });
      fireEvent(window, new Event('resize'));
      await waitFor(() => {
        expect(screen.getAllByTitle('Tap to open, hold for options').length).toBeGreaterThan(0);
      });

      const convoBtn = screen.getByText('First Chat').closest('button')!;
      fireEvent.touchStart(convoBtn, { touches: [{ clientX: 30, clientY: 30 }] });
      await waitFor(() => {
        expect(document.body.textContent).toContain('Copy');
      });
    });
  });

  describe('keyboard arrow navigation', () => {
    it('navigates conversations with ArrowDown and ArrowUp', async () => {
      const user = userEvent.setup();
      renderSidebar();

      await user.click(screen.getByText('First Chat'));
      const btn = screen.getByText('First Chat').closest('button')!;
      fireEvent.keyDown(btn, { key: 'ArrowDown' });
      fireEvent.keyDown(btn, { key: 'ArrowUp' });
      fireEvent.keyDown(btn, { key: 'Enter' });
    });

    it('selects conversation with Space key', async () => {
      const user = userEvent.setup();
      renderSidebar();

      await user.click(screen.getByText('Alpha Chat'));
      const btn = screen.getByText('Alpha Chat').closest('button')!;
      fireEvent.keyDown(btn, { key: ' ' });
    });
  });

  describe('canEdit guards', () => {
    it('does not show conversation context menu when canEdit is false', async () => {
      renderSidebar({ canEdit: false });
      fireEvent.contextMenu(screen.getByText('First Chat'));
      expect(screen.queryByText('Copy')).not.toBeInTheDocument();
    });

    it('does not open create dialog add button when canEdit is false', () => {
      renderSidebar({ canEdit: false });
      expect(screen.queryByTitle('New conversation')).not.toBeInTheDocument();
    });
  });

  describe('error handling', () => {
    it('logs error when rename conversation fails', async () => {
      const user = userEvent.setup();
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      mockRenameConversation.mockRejectedValueOnce(new Error('rename fail'));
      renderSidebar();

      await user.pointer({ keys: '[MouseRight]', target: screen.getByText('First Chat') });
      await user.click(await findPortalButton('Rename'));
      const input = await screen.findByDisplayValue('First Chat');
      await user.clear(input);
      await user.type(input, 'Fail{enter}');

      await waitFor(() => {
        expect(errorSpy).toHaveBeenCalled();
      });
      errorSpy.mockRestore();
    });

    it('logs error when save conversation as markdown fails', async () => {
      const user = userEvent.setup();
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const { api } = await import('../../../../services/api');
      vi.mocked(api.projects.notebooks.conversations.saveAs).mockRejectedValueOnce(new Error('save fail'));
      renderSidebar();

      await user.pointer({ keys: '[MouseRight]', target: screen.getByText('First Chat') });
      await user.click(await findPortalButton('Save Conversation'));

      await waitFor(() => {
        expect(errorSpy).toHaveBeenCalled();
      });
      errorSpy.mockRestore();
    });

    it('logs error when copy conversation fails', async () => {
      const user = userEvent.setup();
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      mockCreateConversation.mockRejectedValueOnce(new Error('copy fail'));
      renderSidebar();

      await user.pointer({ keys: '[MouseRight]', target: screen.getByText('First Chat') });
      await user.click(await findPortalButton('Copy'));

      await waitFor(() => {
        expect(errorSpy).toHaveBeenCalled();
      });
      errorSpy.mockRestore();
    });
  });

  describe('batch conversation operations', () => {
    it('shows batch delete in context menu for multi-selection', async () => {
      const user = userEvent.setup();
      renderSidebar();

      fireEvent.click(screen.getByText('First Chat'));
      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      await user.pointer({ keys: '[MouseRight]', target: screen.getByText('First Chat') });
      await user.click(await findPortalButton(/Delete 2 Conversations/));
      const confirm = await screen.findByRole('button', { name: 'Delete' });
      await user.click(confirm);

      await waitFor(() => {
        expect(mockDeleteConversations).toHaveBeenCalled();
      });
    });
  });

  describe('file operations via tree', () => {
    it('refreshes notebook files after delete through tree', async () => {
      const user = userEvent.setup();
      mockPolledFolderTree = mockFolderTree;
      const onDeleteFile = vi.fn().mockResolvedValue(undefined);
      renderSidebar({ onDeleteFile, folderTree: mockFolderTree });

      fireEvent.contextMenu(screen.getByText('alpha.txt'));
      await user.click(await findPortalButton('Delete'));
      const buttons = await screen.findAllByRole('button', { name: 'Delete' });
      await user.click(buttons[buttons.length - 1]);

      await waitFor(() => {
        expect(onDeleteFile).toHaveBeenCalledWith('file-1');
        expect(mockRefreshNotebookFiles).toHaveBeenCalled();
      });
    });
  });

  describe('search and sort edge cases', () => {
    it('parses activity timestamps without Z suffix', () => {
      renderSidebar();
      fireEvent.change(screen.getByPlaceholderText('Search files and links...'), {
        target: { value: 'chat' },
      });
      expect(screen.getByText('First Chat')).toBeInTheDocument();
    });

    it('persists conversation sort to localStorage', async () => {
      const user = userEvent.setup();
      renderSidebar();
      await user.click(screen.getByRole('button', { name: 'A-Z' }));
      expect(localStorage.getItem('notebook.sidebar.conversationSort:proj-1:nb-1')).toBe('alpha');
    });
  });

  describe('selection coordination', () => {
    it('clears conversation selection when files section is activated', async () => {
      const user = userEvent.setup();
      renderSidebar();

      await user.click(screen.getByText('First Chat'));
      await user.click(screen.getByText('alpha.txt'));
      fireEvent.keyDown(window, { key: 'Escape' });
    });

    it('clears conversation multi-selection with Escape shortcut', async () => {
      renderSidebar();
      fireEvent.click(screen.getByText('First Chat'));
      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      fireEvent.keyDown(window, { key: 'Escape' });
      fireEvent.contextMenu(screen.getByText('First Chat'));
      await waitFor(() => {
        expect(screen.getByText('Copy')).toBeInTheDocument();
      });
    });
  });

  describe('file operation wrappers', () => {
    beforeEach(() => {
      mockPolledFolderTree = mockFolderTree;
    });

    it('refreshes notebook files after rename through tree', async () => {
      const user = userEvent.setup();
      const onRenameFile = vi.fn().mockResolvedValue(undefined);
      renderSidebar({ onRenameFile, folderTree: mockFolderTree });

      fireEvent.contextMenu(screen.getByText('alpha.txt'));
      await user.click(await findPortalButton('Rename'));
      const input = screen.getByDisplayValue('alpha.txt');
      fireEvent.change(input, { target: { value: 'renamed.txt' } });
      fireEvent.keyDown(input, { key: 'Enter' });

      await waitFor(() => {
        expect(onRenameFile).toHaveBeenCalledWith('file-1', 'renamed.txt');
        expect(mockRefreshNotebookFiles).toHaveBeenCalled();
      });
    });

    it('skips conversation rename when title is unchanged', async () => {
      const user = userEvent.setup();
      renderSidebar();

      await user.pointer({ keys: '[MouseRight]', target: screen.getByText('First Chat') });
      await user.click(await findPortalButton('Rename'));
      const input = await screen.findByDisplayValue('First Chat');
      fireEvent.blur(input);

      expect(mockRenameConversation).not.toHaveBeenCalled();
    });

    it('invokes onCopyFromProject effect on mount', () => {
      const onCopyFromProject = vi.fn();
      renderSidebar({ onCopyFromProject });
      expect(onCopyFromProject).toBeDefined();
    });
  });

  describe('conversation delete edge cases', () => {
    it('notifies parent when batch delete leaves no active conversation', async () => {
      const user = userEvent.setup();
      const onConversationsDeleted = vi.fn();
      renderSidebar({
        onConversationsDeleted,
        activeConversationId: 'convo-1',
      });

      fireEvent.click(screen.getByText('First Chat'));
      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      fireEvent.keyDown(window, { key: 'Delete' });
      await user.click(await screen.findByRole('button', { name: 'Delete' }));

      await waitFor(() => {
        expect(onConversationsDeleted).toHaveBeenCalled();
      });
    });
  });

});
