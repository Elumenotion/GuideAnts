import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render as rtlRender, screen, waitFor, fireEvent, act } from '@testing-library/react';
// Prevent global ConversationContext stub from overriding this file's provider mock.
vi.mock('../../test/enableConversationContextStub', () => ({}));
import { render, renderWithNotebookRoute } from '@/test/test-utils';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { ToastProvider } from '../../components/common/Toast';

import NotebookDetails from '../NotebookDetails';
import { checkNotebookAuthRequirements } from '../../utils/notebookAuth';
import { api } from '../../services/api';
import { notebookFilesApi } from '../../services/notebookFiles';
import { useRegisterTour } from '../../tour/useRegisterTour';

const projectId = 'proj-1';
const notebookId = 'nb-1';
const route = `/projects/${projectId}/notebooks/${notebookId}`;

let mockProjectContext: Record<string, unknown>;
let mockNotebookContext: Record<string, unknown>;
let mockAuth: { role: string; status: string };
let mockWorkspaceControls: {
  chat: Record<string, unknown> | null;
  chatIsLoading: boolean;
  toolbar: Record<string, unknown> | null;
  toolbarIsLoading: boolean;
  refresh: ReturnType<typeof vi.fn>;
  inFlight: boolean;
  setInFlight: ReturnType<typeof vi.fn>;
};
let mockFolderTree: Record<string, unknown>;
let mockFileTreeLastUpdated: Date;
let mockPreviewByPathTarget = 'doc.md';
const mockShowToast = vi.fn();
const mockRefreshProject = vi.fn();
const mockNavigate = vi.fn();
const capturedTourSteps: Record<string, { onHighlight?: () => void; onDeselect?: () => void }[]> = {};

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return {
    ...actual,
    useNavigate: () => mockNavigate,
  };
});

vi.mock('../../contexts/ProjectContext', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../contexts/ProjectContext')>();
  return {
    ...actual,
    ProjectProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
    useProject: () => mockProjectContext,
  };
});

vi.mock('../../contexts/NotebookContext', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../contexts/NotebookContext')>();
  return {
    ...actual,
    NotebookProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
    useNotebook: () => mockNotebookContext,
  };
});

vi.mock('../../contexts/AuthContext', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../contexts/AuthContext')>();
  return {
    ...actual,
    useAuth: () => mockAuth,
  };
});

vi.mock('../../components/common/Toast', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../components/common/Toast')>();
  return {
    ...actual,
    useToast: () => ({ showToast: mockShowToast }),
  };
});

vi.mock('../../hooks/useNotebookFilesPolling', () => ({
  useNotebookFilesPolling: () => ({
    folderTree: mockFolderTree,
    lastUpdated: mockFileTreeLastUpdated,
  }),
}));

vi.mock('../../hooks/useNotebookWorkspaceControls', () => ({
  useNotebookWorkspaceControls: () => mockWorkspaceControls,
}));

vi.mock('../../tour/useRegisterTour', () => ({
  useRegisterTour: vi.fn((screenId: string, steps: { onHighlight?: () => void; onDeselect?: () => void }[]) => {
    capturedTourSteps[screenId] = steps;
  }),
}));

vi.mock('../../utils/notebookAuth', () => ({
  checkNotebookAuthRequirements: vi.fn(),
}));

vi.mock('../../services/notebookFiles', () => ({
  notebookFilesApi: {
    getOriginFileInfo: vi.fn().mockResolvedValue({
      fileName: 'orig.md',
      folderPath: 'Docs/orig.md',
      contentFileId: 'cf-1',
      versionNumber: 1,
    }),
    publishToProject: vi.fn().mockResolvedValue({ contentFileId: 'published-1' }),
    getNotebookFileContent: vi.fn().mockImplementation(() =>
      Promise.resolve({ text: () => Promise.resolve('# Home') } as Blob)
    ),
  },
}));

vi.mock('../../services/api', () => ({
  api: {
    projects: {
      notebookTemplates: {
        getById: vi.fn(),
      },
      notebooks: {
        conversations: {
          checkLlamaRuntime: vi.fn().mockResolvedValue({ state: 'ready' }),
          get: vi.fn().mockResolvedValue({ id: 'conv-123' }),
          pollLlamaRuntimeOperation: vi.fn(),
          loadLlamaRuntime: vi.fn(),
          restartLlamaRuntime: vi.fn(),
        },
      },
    },
  },
}));

vi.mock('../../components/layouts/NotebookLayout', () => ({
  NotebookLayout: (props: {
    isMobileSidebarOpen?: boolean;
    onMobileSidebarToggle?: () => void;
    onMobileSidebarClose?: () => void;
    onBack?: () => void;
    onEdit?: () => void;
    canEdit?: boolean;
    sidebar?: React.ReactNode;
    content?: React.ReactNode;
    notebook?: { title?: string };
    headerCenter?: React.ReactNode;
  }) => (
    <div
      data-testid="notebook-layout"
      data-mobile-sidebar-open={String(props.isMobileSidebarOpen)}
      data-can-edit={String(props.canEdit)}
    >
      <span data-testid="notebook-title">{props.notebook?.title}</span>
      <div data-testid="header-center">{props.headerCenter}</div>
      <button type="button" data-testid="layout-back" onClick={props.onBack}>
        Back
      </button>
      {props.onEdit && (
        <button type="button" data-testid="layout-edit" onClick={props.onEdit}>
          Edit
        </button>
      )}
      <button type="button" data-testid="mobile-sidebar-toggle" onClick={props.onMobileSidebarToggle}>
        Toggle Sidebar
      </button>
      <button type="button" data-testid="mobile-sidebar-close" onClick={props.onMobileSidebarClose}>
        Close Sidebar
      </button>
      <div data-testid="sidebar-slot">{props.sidebar}</div>
      <div data-testid="content-slot">{props.content}</div>
    </div>
  ),
}));

vi.mock('../../components/layouts/SidebarContainer', () => ({
  SidebarContainer: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));

vi.mock('../../components/notebook/sidebar/NotebookSidebar', () => ({
  NotebookSidebar: (props: {
    onPreviewFile?: (file: unknown) => void;
    onPublishToProject?: (files: unknown[]) => void;
    onItemSelect?: (type: string, id: string) => void;
    onSectionToggle?: (section: string) => void;
    onConversationDeleted?: (deletedId: string, nextId: string | null) => void;
    onConversationsDeleted?: (deletedIds: string[], nextId: string | null) => void;
    onSetHomePage?: (fileId: string | null) => void;
  }) => (
    <div data-testid="sidebar">
      <button
        type="button"
        data-testid="trigger-preview"
        onClick={() =>
          props.onPreviewFile?.({
            id: 'file-1',
            fileName: 'preview.md',
            relativePath: 'preview.md',
            fileHash: 'hash-preview',
            fileSize: 12,
            lastModifiedUtc: '2024-01-01T00:00:00Z',
            isIndexed: false,
          })
        }
      >
        Preview File
      </button>
      <button
        type="button"
        data-testid="trigger-publish"
        onClick={() =>
          props.onPublishToProject?.([
            {
              id: 'file-1',
              fileName: 'publish.md',
              relativePath: 'publish.md',
              fileHash: 'hash-publish',
              fileSize: 8,
              lastModifiedUtc: '2024-01-01T00:00:00Z',
              isIndexed: false,
            },
          ])
        }
      >
        Publish File
      </button>
      <button
        type="button"
        data-testid="select-conversation"
        onClick={() => props.onItemSelect?.('conversations', 'conv-selected')}
      >
        Select Conversation
      </button>
      <button
        type="button"
        data-testid="select-notebook-file"
        onClick={() => props.onItemSelect?.('notebookFiles', 'file-1')}
      >
        Select File
      </button>
      <button
        type="button"
        data-testid="toggle-section"
        onClick={() => props.onSectionToggle?.('conversations')}
      >
        Toggle Section
      </button>
      <button
        type="button"
        data-testid="delete-conversation"
        onClick={() => props.onConversationDeleted?.('conv-active', 'conv-next')}
      >
        Delete Conversation
      </button>
      <button
        type="button"
        data-testid="delete-conversations"
        onClick={() => props.onConversationsDeleted?.(['conv-a', 'conv-b'], null)}
      >
        Delete Conversations
      </button>
      <button
        type="button"
        data-testid="set-home-page"
        onClick={() => props.onSetHomePage?.('file-1')}
      >
        Set Home Page
      </button>
      <button
        type="button"
        data-testid="clear-home-page"
        onClick={() => props.onSetHomePage?.(null)}
      >
        Clear Home Page
      </button>
      <button
        type="button"
        data-testid="publish-with-origin"
        onClick={() =>
          props.onPublishToProject?.([
            {
              id: 'file-origin',
              fileName: 'origin.md',
              relativePath: 'origin.md',
              fileHash: 'hash-origin',
              fileSize: 8,
              lastModifiedUtc: '2024-01-01T00:00:00Z',
              isIndexed: false,
              originContentFileVersionId: 'origin-v1',
            },
          ])
        }
      >
        Publish With Origin
      </button>
      <button
        type="button"
        data-testid="delete-inactive-conversation"
        onClick={() => props.onConversationDeleted?.('conv-other', 'conv-next-2')}
      >
        Delete Inactive Conversation
      </button>
      <button
        type="button"
        data-testid="bulk-delete-with-next"
        onClick={() => props.onConversationsDeleted?.(['conv-x'], 'conv-y')}
      >
        Bulk Delete With Next
      </button>
    </div>
  ),
}));

vi.mock('../../components/notebook/content/NotebookContent', () => ({
  NotebookContent: () => <div data-testid="notebook-content">Cells View</div>,
}));

vi.mock('../../components/LoadingSpinner', () => ({
  default: ({ message }: { message?: string }) => <div data-testid="loading">{message}</div>,
}));

vi.mock('../../components/ErrorScreen', () => ({
  default: ({
    title,
    error,
    onRetry,
    onBack,
  }: {
    title?: string;
    error?: string;
    onRetry?: () => void;
    onBack?: () => void;
  }) => (
    <div data-testid="error">
      {title}:{error}
      {onRetry && (
        <button type="button" data-testid="error-retry" onClick={onRetry}>
          Retry
        </button>
      )}
      {onBack && (
        <button type="button" data-testid="error-back" onClick={onBack}>
          Back
        </button>
      )}
    </div>
  ),
}));

vi.mock('../../components/notebook/header-toolbar/NotebookServiceToolbar', () => ({
  NotebookServiceToolbar: () => <div data-testid="service-toolbar" />,
}));

vi.mock('../../components/notebook/content/FilePreviewOverlay', () => ({
  FilePreviewOverlay: ({
    file,
    onClose,
    onNavigate,
    isEmbedded,
  }: {
    file: { fileName: string };
    onClose: () => void;
    onNavigate?: (path: string) => void;
    isEmbedded?: boolean;
  }) => (
    <div data-testid={isEmbedded ? 'embedded-preview' : 'file-preview-overlay'}>
      <span>{file.fileName}</span>
      <button type="button" onClick={onClose}>
        Close Preview
      </button>
      {onNavigate && (
        <button type="button" data-testid="preview-navigate" onClick={() => onNavigate('doc.md')}>
          Navigate Preview
        </button>
      )}
    </div>
  ),
}));

vi.mock('../../components/notebook/dialogs/PublishToProjectDialog', () => ({
  PublishToProjectDialog: ({
    isOpen,
    notebookFiles,
    onClose,
    onPublish,
    onComplete,
  }: {
    isOpen: boolean;
    notebookFiles: { fileName: string }[];
    onClose?: () => void;
    onPublish?: (data: Record<string, unknown>) => Promise<string>;
    onComplete?: (ids: string[]) => void;
  }) =>
    isOpen ? (
      <div data-testid="publish-dialog">
        Publish {notebookFiles.map((f) => f.fileName).join(', ')}
        <button type="button" data-testid="publish-close" onClick={onClose}>
          Close Publish
        </button>
        <button
          type="button"
          data-testid="publish-submit"
          onClick={() => onPublish?.({ fileId: notebookFiles[0]?.fileName ?? 'f' })}
        >
          Submit Publish
        </button>
        <button type="button" data-testid="publish-complete" onClick={() => onComplete?.(['pub-file-1'])}>
          Complete Publish
        </button>
        <button type="button" data-testid="publish-complete-empty" onClick={() => onComplete?.([])}>
          Complete Publish Empty
        </button>
      </div>
    ) : null,
}));

vi.mock('../../components/notebook/auth/NotebookAuthInterstitial', () => ({
  NotebookAuthInterstitial: ({ onAuthComplete }: { onAuthComplete?: () => void }) => (
    <div data-testid="auth-interstitial">
      Auth Required
      <button type="button" data-testid="auth-complete" onClick={onAuthComplete}>
        Complete Auth
      </button>
    </div>
  ),
}));

vi.mock('../../contexts/ConversationContext', () => ({
  ConversationProvider: ({
    children,
    onPreviewFileByPath,
    onPreviewFile,
  }: {
    children: React.ReactNode;
    onPreviewFileByPath?: (path: string) => void;
    onPreviewFile?: (fileId: string) => void;
  }) => (
    <div data-testid="conversation-provider">
      {children}
      <button
        type="button"
        data-testid="preview-by-path"
        onClick={() => onPreviewFileByPath?.(mockPreviewByPathTarget)}
      >
        Preview By Path
      </button>
      <button type="button" data-testid="preview-by-id" onClick={() => onPreviewFile?.('file-1')}>
        Preview By Id
      </button>
    </div>
  ),
}));

vi.mock('../../components/notebook/conversations/ConversationPanel', () => ({
  ConversationPanel: ({
    conversationId,
    onNewConversation,
    isRuntimeLoading,
    isChatModelMissing,
  }: {
    conversationId: string;
    onNewConversation?: (id: string) => void;
    isRuntimeLoading?: boolean;
    isChatModelMissing?: boolean;
  }) => (
    <div
      data-testid="conversation-panel"
      data-runtime-loading={String(isRuntimeLoading)}
      data-chat-model-missing={String(isChatModelMissing)}
    >
      Conversation: {conversationId}
      <button type="button" data-testid="new-conversation" onClick={() => onNewConversation?.('new-conv-1')}>
        New Conversation
      </button>
    </div>
  ),
}));

vi.mock('../../components/notebook/conversations/LlamaRuntimeModal', () => ({
  LlamaRuntimeModal: (props: {
    isOpen: boolean;
    onStartLoad?: () => void;
    onClose?: () => void;
  }) =>
    props.isOpen ? (
      <div data-testid="llama-runtime-modal">
        <button type="button" onClick={props.onStartLoad}>
          Start Load
        </button>
        <button type="button" onClick={props.onClose}>
          Close Runtime Modal
        </button>
      </div>
    ) : null,
}));

vi.mock('../../components/notebook/conversations/LlamaCrashedModal', () => ({
  LlamaCrashedModal: (props: {
    isOpen: boolean;
    onClose?: () => void;
    onRestart?: () => Promise<void>;
    onAfterRestart?: () => void;
  }) =>
    props.isOpen ? (
      <div data-testid="llama-crashed-modal">
        <button type="button" onClick={props.onClose}>
          Dismiss Crash
        </button>
        <button type="button" onClick={() => props.onRestart?.()}>
          Restart Runtime
        </button>
        <button type="button" onClick={props.onAfterRestart}>
          After Restart
        </button>
      </div>
    ) : null,
}));

vi.mock('../../components/notebook/conversations/NoChatModelDialog', () => ({
  NoChatModelDialog: (props: { isOpen: boolean; onClose?: () => void; onGoToSettings?: () => void }) =>
    props.isOpen ? (
      <div data-testid="no-chat-model-dialog">
        <button type="button" onClick={props.onClose}>
          Dismiss No Model
        </button>
        <button type="button" data-testid="go-to-settings" onClick={props.onGoToSettings}>
          Go To Settings
        </button>
      </div>
    ) : null,
}));

vi.mock('../../components/common/MarkdownViewer', () => ({
  default: ({ text }: { text: string }) => <div data-testid="markdown-viewer">{text}</div>,
}));

function createTextBlob(content: string): Blob {
  return { text: () => Promise.resolve(content) } as Blob;
}

function defaultWorkspaceControls() {
  return {
    chat: { effectiveModelId: 'model-1' },
    chatIsLoading: false,
    toolbar: null,
    toolbarIsLoading: false,
    refresh: vi.fn(),
    inFlight: false,
    setInFlight: vi.fn(),
  };
}

function defaultFolderTree() {
  return {
    name: 'root',
    relativePath: '',
    subFolders: [],
    files: [
      {
        id: 'file-1',
        fileName: 'doc.md',
        relativePath: 'doc.md',
        fileHash: 'hash-1',
        fileSize: 10,
        lastModifiedUtc: '2024-01-01T00:00:00Z',
        isIndexed: false,
      },
      {
        id: 'home-md',
        fileName: 'home.md',
        relativePath: 'home.md',
        fileHash: 'hash-home',
        fileSize: 20,
        lastModifiedUtc: '2024-01-01T00:00:00Z',
        isIndexed: false,
      },
      {
        id: 'home-pdf',
        fileName: 'home.pdf',
        relativePath: 'home.pdf',
        fileHash: 'hash-pdf',
        fileSize: 30,
        lastModifiedUtc: '2024-01-01T00:00:00Z',
        isIndexed: false,
      },
    ],
  };
}

function createReadyContexts(overrides?: {
  project?: Record<string, unknown>;
  notebook?: Record<string, unknown>;
  notebookContext?: Record<string, unknown>;
}) {
  mockProjectContext = {
    project: {
      id: projectId,
      title: 'Test Project',
      notebooks: [{ id: notebookId, title: 'My Notebook', guideId: 'guide-1' }],
      folders: [],
      links: [],
      ...overrides?.project,
    },
    canEdit: () => true,
    isLoading: false,
    error: null,
    refreshProject: mockRefreshProject,
    folderTree: null,
  };

  mockNotebookContext = {
    notebook: {
      id: notebookId,
      title: 'My Notebook',
      guideId: 'guide-1',
      ...overrides?.notebook,
    },
    isLoading: false,
    error: null,
    filesError: null,
    assistants: [{ id: 'assistant-1', name: 'Helper' }],
    uploadFiles: vi.fn(),
    createFolder: vi.fn(),
    deleteFolder: vi.fn(),
    renameFolder: vi.fn(),
    deleteFile: vi.fn(),
    renameFile: vi.fn(),
    moveFile: vi.fn(),
    copyFromProject: vi.fn(),
    setHomePageFile: vi.fn(),
    clearHomePage: vi.fn(),
    ...(overrides?.notebookContext ?? {}),
  };
}

function renderNotebook(path = route) {
  return renderWithNotebookRoute(<NotebookDetails />, {
    route: path,
    projectId,
    notebookId,
  });
}

function renderNotebookWithNavState(path: string, state: Record<string, unknown>) {
  return rtlRender(
    <ToastProvider>
      <MemoryRouter initialEntries={[{ pathname: path, state }]}>
        <Routes>
          <Route path="/projects/:projectId/notebooks/:notebookId/*" element={<NotebookDetails />} />
        </Routes>
      </MemoryRouter>
    </ToastProvider>
  );
}

describe('NotebookDetails page', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    Object.keys(capturedTourSteps).forEach((k) => delete capturedTourSteps[k]);
    window.history.replaceState({}, '', route);
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 1024 });
    mockAuth = { role: 'User', status: 'authenticated' };
    mockWorkspaceControls = defaultWorkspaceControls();
    mockFolderTree = defaultFolderTree();
    mockFileTreeLastUpdated = new Date('2024-06-01T12:00:00Z');
    mockPreviewByPathTarget = 'doc.md';

    createReadyContexts();
    vi.mocked(api.projects.notebookTemplates.getById).mockResolvedValue({
      id: 'guide-1',
      templateName: 'Guide Template',
      authProviders: [],
    } as never);
    vi.mocked(checkNotebookAuthRequirements).mockResolvedValue({
      needsAuth: false,
      requiredProviders: [],
      missingProviders: [],
    });
    vi.mocked(api.projects.notebooks.conversations.get).mockResolvedValue({ id: 'conv-123' } as never);
  });

  afterEach(() => {
    window.history.replaceState({}, '', '/');
  });

  it('renders error when params missing', () => {
    render(
      <Routes>
        <Route path="*" element={<NotebookDetails />} />
      </Routes>,
      { initialRoute: '/invalid', projectId, notebookId }
    );
    expect(screen.getByTestId('error')).toHaveTextContent('Invalid URL');
  });

  it('calls history.back from invalid URL error screen', () => {
    const backSpy = vi.fn();
    vi.spyOn(window.history, 'back').mockImplementation(backSpy);
    render(
      <Routes>
        <Route path="*" element={<NotebookDetails />} />
      </Routes>,
      { initialRoute: '/invalid', projectId, notebookId }
    );
    fireEvent.click(screen.getByTestId('error-back'));
    expect(backSpy).toHaveBeenCalled();
    vi.mocked(window.history.back).mockRestore();
  });

  it('shows loading spinner while project or notebook is loading', () => {
    createReadyContexts();
    mockProjectContext.isLoading = true;

    renderNotebook();
    expect(screen.getByTestId('loading')).toHaveTextContent('Loading notebook...');
  });

  it('shows loading spinner until auth check completes', async () => {
    let resolveTemplate: (value: unknown) => void;
    vi.mocked(api.projects.notebookTemplates.getById).mockImplementation(
      () =>
        new Promise((resolve) => {
          resolveTemplate = resolve;
        }) as never
    );

    renderNotebook();
    expect(screen.getByTestId('loading')).toBeInTheDocument();

    resolveTemplate!({ id: 'guide-1', templateName: 'Guide', authProviders: [] });
    await waitFor(() => {
      expect(screen.getByTestId('notebook-layout')).toBeInTheDocument();
    });
  });

  it('renders auth interstitial when OAuth is required', async () => {
    vi.mocked(checkNotebookAuthRequirements).mockResolvedValue({
      needsAuth: true,
      requiredProviders: [],
      missingProviders: [],
    });

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('auth-interstitial')).toBeInTheDocument();
    });
    expect(screen.queryByTestId('notebook-layout')).not.toBeInTheDocument();
  });

  it('initializes active conversation from ?c= URL param', async () => {
    window.history.replaceState({}, '', `${route}?c=conv-123`);

    renderNotebook(`${route}?c=conv-123`);

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toHaveTextContent('Conversation: conv-123');
    });
  });

  it('syncs conversation id to URL when selecting a conversation', async () => {
    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('notebook-layout')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('select-conversation'));

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toHaveTextContent('Conversation: conv-selected');
    });
    expect(window.location.search).toContain('c=conv-selected');
  });

  it('opens publish dialog when sidebar triggers publish', async () => {
    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('sidebar')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('trigger-publish'));

    await waitFor(() => {
      expect(screen.getByTestId('publish-dialog')).toHaveTextContent('publish.md');
    });
  });

  it('opens file preview overlay when sidebar triggers preview', async () => {
    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('sidebar')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('trigger-preview'));

    await waitFor(() => {
      expect(screen.getByTestId('file-preview-overlay')).toHaveTextContent('preview.md');
    });

    fireEvent.click(screen.getByText('Close Preview'));
    await waitFor(() => {
      expect(screen.queryByTestId('file-preview-overlay')).not.toBeInTheDocument();
    });
  });

  it('toggles mobile sidebar open state', async () => {
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('notebook-layout')).toHaveAttribute('data-mobile-sidebar-open', 'false');
    });

    fireEvent.click(screen.getByTestId('mobile-sidebar-toggle'));
    expect(screen.getByTestId('notebook-layout')).toHaveAttribute('data-mobile-sidebar-open', 'true');

    fireEvent.click(screen.getByTestId('mobile-sidebar-close'));
    expect(screen.getByTestId('notebook-layout')).toHaveAttribute('data-mobile-sidebar-open', 'false');
  });

  it('closes mobile sidebar when selecting an item on mobile', async () => {
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('sidebar')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('mobile-sidebar-toggle'));
    expect(screen.getByTestId('notebook-layout')).toHaveAttribute('data-mobile-sidebar-open', 'true');

    fireEvent.click(screen.getByTestId('select-conversation'));
    expect(screen.getByTestId('notebook-layout')).toHaveAttribute('data-mobile-sidebar-open', 'false');
  });

  it('renders notebook layout with default cells view when no conversation is active', async () => {
    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('notebook-title')).toHaveTextContent('My Notebook');
      expect(screen.getByTestId('notebook-content')).toBeInTheDocument();
    });
  });

  it('shows error screen when project or notebook context reports an error', async () => {
    createReadyContexts();
    mockNotebookContext.error = 'Notebook load failed';

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('error')).toHaveTextContent('Failed to Load Notebook');
      expect(screen.getByTestId('error')).toHaveTextContent('Notebook load failed');
    });
  });

  it('shows error screen when filesError is set', async () => {
    createReadyContexts();
    mockNotebookContext.filesError = 'Files unavailable';

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('error')).toHaveTextContent('Files unavailable');
    });
  });

  it('shows project not found when project context is empty', async () => {
    createReadyContexts();
    mockProjectContext.project = null;

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('error')).toHaveTextContent('Project Not Found');
    });
  });

  it('shows notebook loading spinner while notebook context is loading', () => {
    createReadyContexts();
    mockProjectContext.isLoading = false;
    mockNotebookContext.isLoading = true;

    renderNotebook();
    expect(screen.getByTestId('loading')).toHaveTextContent('Loading notebook...');
  });

  it('applies conversation id from navigation state when URL has no c param', async () => {
    renderNotebookWithNavState(route, { conversationId: 'nav-conv-99' });

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toHaveTextContent('Conversation: nav-conv-99');
    });
  });

  it('clears conversation from URL when selecting a non-conversation sidebar item', async () => {
    window.history.replaceState({}, '', `${route}?c=conv-123`);
    renderNotebook(`${route}?c=conv-123`);

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('select-notebook-file'));

    await waitFor(() => {
      expect(screen.queryByTestId('conversation-panel')).not.toBeInTheDocument();
      expect(screen.getByTestId('notebook-content')).toBeInTheDocument();
    });
    expect(window.location.search).not.toContain('c=');
  });

  it('navigates to next conversation when active conversation is deleted', async () => {
    window.history.replaceState({}, '', `${route}?c=conv-active`);
    renderNotebook(`${route}?c=conv-active`);

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toHaveTextContent('conv-active');
    });

    fireEvent.click(screen.getByTestId('delete-conversation'));

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toHaveTextContent('conv-next');
    });
  });

  it('clears conversation when bulk delete removes active conversation', async () => {
    window.history.replaceState({}, '', `${route}?c=conv-a`);
    renderNotebook(`${route}?c=conv-a`);

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toHaveTextContent('conv-a');
    });

    fireEvent.click(screen.getByTestId('delete-conversations'));

    await waitFor(() => {
      expect(screen.queryByTestId('conversation-panel')).not.toBeInTheDocument();
    });
  });

  it('re-checks auth after oauthSuccess query param and cleans URL', async () => {
    vi.mocked(checkNotebookAuthRequirements)
      .mockResolvedValueOnce({
        needsAuth: false,
        requiredProviders: [],
        missingProviders: [],
      })
      .mockResolvedValueOnce({
        needsAuth: true,
        requiredProviders: [],
        missingProviders: [],
      });

    renderNotebook(`${route}?oauthSuccess=1`);

    await waitFor(() => {
      expect(screen.getByTestId('auth-interstitial')).toBeInTheDocument();
    });
    expect(window.location.search).not.toContain('oauthSuccess');
  });

  it('renders guide template home content when no conversation or user home page', async () => {
    vi.mocked(api.projects.notebookTemplates.getById).mockResolvedValue({
      id: 'guide-1',
      templateName: 'Guide Template',
      authProviders: [],
      homeContent: '# Welcome to the guide',
    } as never);

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('markdown-viewer')).toHaveTextContent('# Welcome to the guide');
    });
    expect(screen.queryByTestId('notebook-content')).not.toBeInTheDocument();
  });

  it('fetches home page markdown content when homePageFileId is set', async () => {
    createReadyContexts({
      notebook: { homePageFileId: 'home-md' },
    });
    vi.mocked(notebookFilesApi.getNotebookFileContent).mockResolvedValue(
      createTextBlob('# User Home Page')
    );

    renderNotebook();

    await waitFor(() => {
      expect(notebookFilesApi.getNotebookFileContent).toHaveBeenCalledWith(
        projectId,
        notebookId,
        'home.md',
        'hash-home'
      );
    });
  });

  it('renders embedded file preview for non-markdown home page file', async () => {
    createReadyContexts({
      notebook: { homePageFileId: 'home-pdf' },
    });

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('embedded-preview')).toHaveTextContent('home.pdf');
    });
  });

  it('shows admin service toolbar for authenticated admin users', async () => {
    mockAuth = { role: 'Admin', status: 'authenticated' };

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('header-center')).toContainElement(
        screen.getByTestId('service-toolbar')
      );
    });
  });

  it('shows no-chat-model dialog when readiness reports missing model', async () => {
    mockWorkspaceControls = {
      chat: { effectiveModelId: null, blockers: ['No model'] },
      chatIsLoading: false,
      toolbar: null,
      toolbarIsLoading: false,
      refresh: vi.fn(),
      inFlight: false,
      setInFlight: vi.fn(),
    };
    window.history.replaceState({}, '', `${route}?c=conv-123`);

    renderNotebook(`${route}?c=conv-123`);

    await waitFor(() => {
      expect(screen.getByTestId('no-chat-model-dialog')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('Dismiss No Model'));
    await waitFor(() => {
      expect(screen.queryByTestId('no-chat-model-dialog')).not.toBeInTheDocument();
    });
  });

  it('handles llama runtime window events for load, crash, and restart', async () => {
    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('notebook-layout')).toBeInTheDocument();
    });

    act(() => {
      window.dispatchEvent(
        new CustomEvent('llama-runtime-requires-load', {
          detail: { runtimeStatus: { state: 'requires_load' }, assistantId: 'assistant-1' },
        })
      );
    });

    await waitFor(() => {
      expect(screen.getByTestId('llama-runtime-modal')).toBeInTheDocument();
    });

    act(() => {
      window.dispatchEvent(
        new CustomEvent('llama-runtime-crashed', {
          detail: { reason: 'Crashed', upstreamDetail: 'OOM' },
        })
      );
    });

    await waitFor(() => {
      expect(screen.getByTestId('llama-crashed-modal')).toBeInTheDocument();
      expect(screen.queryByTestId('llama-runtime-modal')).not.toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('After Restart'));

    await waitFor(() => {
      expect(screen.queryByTestId('llama-crashed-modal')).not.toBeInTheDocument();
      expect(screen.getByTestId('llama-runtime-modal')).toBeInTheDocument();
    });
  });

  it('toasts when llama runtime notebook check fails', async () => {
    vi.mocked(api.projects.notebooks.conversations.checkLlamaRuntime).mockRejectedValueOnce(
      new Error('Runtime unreachable')
    );

    renderNotebook();

    await waitFor(() => {
      expect(mockShowToast).toHaveBeenCalledWith(
        expect.objectContaining({
          type: 'error',
          title: 'Local Runtime Check Failed',
        })
      );
    });
  });

  it('starts llama runtime load from modal and handles ready response', async () => {
    vi.mocked(api.projects.notebooks.conversations.checkLlamaRuntime).mockResolvedValue({
      state: 'requires_load',
    } as never);
    vi.mocked(api.projects.notebooks.conversations.loadLlamaRuntime).mockResolvedValueOnce({
      state: 'ready',
      operationId: 'op-ready',
    } as never);

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('llama-runtime-modal')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('Start Load'));

    await waitFor(() => {
      expect(api.projects.notebooks.conversations.loadLlamaRuntime).toHaveBeenCalled();
    });
  });

  it('refreshes project when returning with refreshProject navigation state', async () => {
    renderNotebookWithNavState(route, { refreshProject: true });

    await waitFor(() => {
      expect(mockRefreshProject).toHaveBeenCalled();
    });
  });

  it('registers tour screens for notebook views', async () => {
    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('notebook-layout')).toBeInTheDocument();
    });

    expect(vi.mocked(useRegisterTour)).toHaveBeenCalledWith(
      'notebook-conversation',
      expect.any(Array)
    );
    expect(vi.mocked(useRegisterTour)).toHaveBeenCalledWith('notebook-cells', expect.any(Array));
    expect(vi.mocked(useRegisterTour)).toHaveBeenCalledWith('notebook-home', expect.any(Array));
    expect(vi.mocked(useRegisterTour)).toHaveBeenCalledWith(
      'notebook-file-preview',
      expect.any(Array)
    );
  });

  it('still renders layout when template fetch fails', async () => {
    vi.mocked(api.projects.notebookTemplates.getById).mockRejectedValueOnce(new Error('template down'));

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('notebook-layout')).toBeInTheDocument();
    });
  });

  it('closes mobile sidebar when previewing a file on mobile', async () => {
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('sidebar')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('mobile-sidebar-toggle'));
    fireEvent.click(screen.getByTestId('trigger-preview'));

    expect(screen.getByTestId('notebook-layout')).toHaveAttribute('data-mobile-sidebar-open', 'false');
  });

  it('invokes setHomePageFile when sidebar sets home page', async () => {
    const setHomePageFile = vi.fn();
    createReadyContexts({ notebookContext: { setHomePageFile } });

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('sidebar')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('set-home-page'));
    expect(setHomePageFile).toHaveBeenCalledWith('file-1');
  });

  it('shows loading when notebook context is null because auth check never completes', async () => {
    createReadyContexts();
    mockNotebookContext.notebook = null;

    renderNotebook();

    expect(screen.getByTestId('loading')).toHaveTextContent('Loading notebook...');
  });

  it('shows project error from project context', async () => {
    createReadyContexts();
    mockProjectContext.error = 'Project load failed';

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('error')).toHaveTextContent('Project load failed');
    });
  });

  it('invokes error back handler on error screen', async () => {
    createReadyContexts();
    mockNotebookContext.error = 'boom';

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('error-back')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('error-back'));
    expect(mockNavigate).toHaveBeenCalledWith(`/projects/${projectId}`);
  });

  it('navigates to project root when project not found back is clicked', async () => {
    createReadyContexts();
    mockProjectContext.project = null;

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('error-back')).toBeInTheDocument();
    });
    fireEvent.click(screen.getByTestId('error-back'));
    expect(mockNavigate).toHaveBeenCalledWith('/');
  });

  it('hides edit button when user cannot edit', async () => {
    createReadyContexts();
    mockProjectContext.canEdit = () => false;

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('notebook-layout')).toHaveAttribute('data-can-edit', 'false');
    });
    expect(screen.queryByTestId('layout-edit')).not.toBeInTheDocument();
  });

  it('navigates to notebook edit when edit is clicked', async () => {
    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('layout-edit')).toBeInTheDocument();
    });
    fireEvent.click(screen.getByTestId('layout-edit'));
    expect(mockNavigate).toHaveBeenCalledWith(`/projects/${projectId}/notebooks/${notebookId}/edit`);
  });

  it('navigates back to project from layout back button', async () => {
    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('layout-back')).toBeInTheDocument();
    });
    fireEvent.click(screen.getByTestId('layout-back'));
    expect(mockNavigate).toHaveBeenCalledWith(`/projects/${projectId}`);
  });

  it('skips llama runtime check when assistants list is empty', async () => {
    createReadyContexts({ notebookContext: { assistants: [] } });

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('notebook-layout')).toBeInTheDocument();
    });
    expect(api.projects.notebooks.conversations.checkLlamaRuntime).not.toHaveBeenCalled();
  });

  it('closes runtime modal when ready status is applied from initial check', async () => {
    vi.mocked(api.projects.notebooks.conversations.checkLlamaRuntime).mockResolvedValueOnce({
      state: 'ready',
    } as never);

    renderNotebook();

    await waitFor(() => {
      expect(screen.queryByTestId('llama-runtime-modal')).not.toBeInTheDocument();
    });
  });

  it('opens runtime modal on llama-runtime-loading window event', async () => {
    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('notebook-layout')).toBeInTheDocument();
    });

    act(() => {
      window.dispatchEvent(
        new CustomEvent('llama-runtime-loading', {
          detail: { operation: { operationId: 'op-load' }, assistantId: 'assistant-1' },
        })
      );
    });

    await waitFor(() => {
      expect(screen.getByTestId('llama-runtime-modal')).toBeInTheDocument();
    });
  });

  it('polls runtime operation until ready and closes modal', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.mocked(api.projects.notebooks.conversations.checkLlamaRuntime).mockResolvedValueOnce({
      state: 'loading',
      activeOperation: { operationId: 'op-poll', state: 'loading' },
    } as never);
    vi.mocked(api.projects.notebooks.conversations.pollLlamaRuntimeOperation).mockResolvedValueOnce({
      state: 'ready',
      operationId: 'op-poll',
    } as never);

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('llama-runtime-modal')).toBeInTheDocument();
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(2000);
    });

    await waitFor(() => {
      expect(api.projects.notebooks.conversations.pollLlamaRuntimeOperation).toHaveBeenCalled();
      expect(screen.queryByTestId('llama-runtime-modal')).not.toBeInTheDocument();
    });
    vi.useRealTimers();
  });

  it('shows toast when runtime poll reports failed operation', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.mocked(api.projects.notebooks.conversations.checkLlamaRuntime).mockResolvedValueOnce({
      state: 'loading',
      activeOperation: { operationId: 'op-fail', state: 'loading' },
    } as never);
    vi.mocked(api.projects.notebooks.conversations.pollLlamaRuntimeOperation).mockResolvedValueOnce({
      state: 'failed',
      errorDetails: 'GPU OOM',
    } as never);

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('llama-runtime-modal')).toBeInTheDocument();
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(2000);
    });

    await waitFor(() => {
      expect(mockShowToast).toHaveBeenCalledWith(
        expect.objectContaining({ title: 'Model Load Failed', message: 'GPU OOM' })
      );
    });
    vi.useRealTimers();
  });

  it('continues polling when operation is still loading', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.mocked(api.projects.notebooks.conversations.checkLlamaRuntime).mockResolvedValueOnce({
      state: 'loading',
      activeOperation: { operationId: 'op-keep', state: 'loading' },
    } as never);
    vi.mocked(api.projects.notebooks.conversations.pollLlamaRuntimeOperation).mockResolvedValue({
      state: 'loading',
      operationId: 'op-keep',
    } as never);

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('llama-runtime-modal')).toBeInTheDocument();
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(4000);
    });

    expect(api.projects.notebooks.conversations.pollLlamaRuntimeOperation).toHaveBeenCalledTimes(2);
    vi.useRealTimers();
  });

  it('shows toast when runtime poll throws', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.mocked(api.projects.notebooks.conversations.checkLlamaRuntime).mockResolvedValueOnce({
      state: 'loading',
      activeOperation: { operationId: 'op-err', state: 'loading' },
    } as never);
    vi.mocked(api.projects.notebooks.conversations.pollLlamaRuntimeOperation).mockRejectedValueOnce(
      new Error('poll down')
    );

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('llama-runtime-modal')).toBeInTheDocument();
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(2000);
    });

    await waitFor(() => {
      expect(mockShowToast).toHaveBeenCalledWith(
        expect.objectContaining({ title: 'Model Load Poll Failed', message: 'poll down' })
      );
    });
    vi.useRealTimers();
  });

  it('shows toast when start load returns failed state', async () => {
    vi.mocked(api.projects.notebooks.conversations.checkLlamaRuntime).mockResolvedValue({
      state: 'requires_load',
    } as never);
    vi.mocked(api.projects.notebooks.conversations.loadLlamaRuntime).mockResolvedValueOnce({
      state: 'failed',
      errorDetails: 'load failed',
    } as never);

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('llama-runtime-modal')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('Start Load'));

    await waitFor(() => {
      expect(mockShowToast).toHaveBeenCalledWith(
        expect.objectContaining({ title: 'Model Load Failed', message: 'load failed' })
      );
    });
  });

  it('starts polling when start load returns loading state', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.mocked(api.projects.notebooks.conversations.checkLlamaRuntime).mockResolvedValue({
      state: 'requires_load',
    } as never);
    vi.mocked(api.projects.notebooks.conversations.loadLlamaRuntime).mockResolvedValueOnce({
      state: 'loading',
      operationId: 'op-start',
    } as never);
    vi.mocked(api.projects.notebooks.conversations.pollLlamaRuntimeOperation).mockResolvedValue({
      state: 'loading',
      operationId: 'op-start',
    } as never);

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('llama-runtime-modal')).toBeInTheDocument();
    });

    await act(async () => {
      fireEvent.click(screen.getByText('Start Load'));
      await Promise.resolve();
    });

    await waitFor(() => {
      expect(api.projects.notebooks.conversations.loadLlamaRuntime).toHaveBeenCalled();
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(2000);
    });

    expect(api.projects.notebooks.conversations.pollLlamaRuntimeOperation).toHaveBeenCalled();
    vi.useRealTimers();
  });

  it('shows toast when start load throws', async () => {
    vi.mocked(api.projects.notebooks.conversations.checkLlamaRuntime).mockResolvedValue({
      state: 'requires_load',
    } as never);
    vi.mocked(api.projects.notebooks.conversations.loadLlamaRuntime).mockRejectedValueOnce(
      new Error('network down')
    );

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('llama-runtime-modal')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('Start Load'));

    await waitFor(() => {
      expect(mockShowToast).toHaveBeenCalledWith(
        expect.objectContaining({ title: 'Model Load Failed', message: 'network down' })
      );
    });
  });

  it('closes runtime modal when close is clicked and not polling', async () => {
    vi.mocked(api.projects.notebooks.conversations.checkLlamaRuntime).mockResolvedValue({
      state: 'requires_load',
    } as never);

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('llama-runtime-modal')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('Close Runtime Modal'));

    await waitFor(() => {
      expect(screen.queryByTestId('llama-runtime-modal')).not.toBeInTheDocument();
    });
  });

  it('enters polling when requires_load recheck detects external startup loading', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      vi.mocked(api.projects.notebooks.conversations.checkLlamaRuntime)
        .mockResolvedValueOnce({ state: 'requires_load' } as never)
        .mockResolvedValue({
          state: 'loading',
          activeOperation: { operationId: '__external_loading__', state: 'loading' },
        } as never);
      vi.mocked(api.projects.notebooks.conversations.pollLlamaRuntimeOperation).mockResolvedValue({
        state: 'ready',
        operationId: '__external_loading__',
      } as never);

      renderNotebook();

      await waitFor(() => {
        expect(screen.getByTestId('llama-runtime-modal')).toBeInTheDocument();
      });

      await waitFor(() => {
        expect(api.projects.notebooks.conversations.checkLlamaRuntime.mock.calls.length).toBeGreaterThanOrEqual(2);
      });

      await act(async () => {
        await vi.advanceTimersByTimeAsync(2000);
      });

      await waitFor(() => {
        expect(api.projects.notebooks.conversations.pollLlamaRuntimeOperation).toHaveBeenCalled();
      });
    } finally {
      vi.useRealTimers();
    }
  });

  it('dismisses crash modal via onClose', async () => {
    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('notebook-layout')).toBeInTheDocument();
    });

    act(() => {
      window.dispatchEvent(
        new CustomEvent('llama-runtime-crashed', {
          detail: { reason: 'Crashed', upstreamDetail: 'segfault' },
        })
      );
    });

    await waitFor(() => {
      expect(screen.getByTestId('llama-crashed-modal')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('Dismiss Crash'));

    await waitFor(() => {
      expect(screen.queryByTestId('llama-crashed-modal')).not.toBeInTheDocument();
    });
  });

  async function resolveConversationAfterTreeRefresh(
    path: string,
    configureGet?: () => void
  ) {
    renderNotebook(path);
    await waitFor(() => {
      expect(screen.getByTestId('notebook-layout')).toBeInTheDocument();
    });
    vi.mocked(api.projects.notebooks.conversations.get).mockReset();
    configureGet?.();
    mockFileTreeLastUpdated = new Date('2024-06-02T12:00:00Z');
    fireEvent.click(screen.getByTestId('toggle-section'));
    fireEvent.click(screen.getByTestId('toggle-section'));
  }

  it('clears active conversation when API returns 404', async () => {
    window.history.replaceState({}, '', `${route}?c=conv-stale`);
    await resolveConversationAfterTreeRefresh(`${route}?c=conv-stale`, () => {
      vi.mocked(api.projects.notebooks.conversations.get).mockRejectedValue({ status: 404 });
    });

    await waitFor(() => {
      expect(screen.queryByTestId('conversation-panel')).not.toBeInTheDocument();
    });
    expect(window.location.search).not.toContain('c=');
  });

  it('dispatches refresh-conversations when active conversation resolves', async () => {
    const refreshSpy = vi.fn();
    window.addEventListener('refresh-conversations', refreshSpy);
    window.history.replaceState({}, '', `${route}?c=conv-123`);

    await resolveConversationAfterTreeRefresh(`${route}?c=conv-123`, () => {
      vi.mocked(api.projects.notebooks.conversations.get).mockResolvedValue({ id: 'conv-123' } as never);
    });

    await waitFor(() => {
      expect(api.projects.notebooks.conversations.get).toHaveBeenCalledWith(
        projectId,
        notebookId,
        'conv-123'
      );
      expect(refreshSpy).toHaveBeenCalled();
    });
    window.removeEventListener('refresh-conversations', refreshSpy);
  });

  it('ignores navigation state conversation when URL already has c param', async () => {
    window.history.replaceState({}, '', `${route}?c=url-conv`);
    renderNotebookWithNavState(`${route}?c=url-conv`, { conversationId: 'nav-conv-99' });

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toHaveTextContent('url-conv');
    });
    expect(mockNavigate).toHaveBeenCalledWith('.', { replace: true, state: {} });
  });

  it('handles oauth callback auth check failure gracefully', async () => {
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    vi.mocked(checkNotebookAuthRequirements).mockRejectedValueOnce(new Error('oauth check failed'));

    renderNotebook(`${route}?oauthSuccess=1`);

    await waitFor(() => {
      expect(consoleSpy).toHaveBeenCalled();
    });
    consoleSpy.mockRestore();
  });

  it('re-checks auth after user completes interstitial', async () => {
    vi.mocked(checkNotebookAuthRequirements)
      .mockResolvedValueOnce({
        needsAuth: true,
        requiredProviders: [],
        missingProviders: [],
      })
      .mockResolvedValueOnce({
        needsAuth: false,
        requiredProviders: [],
        missingProviders: [],
      });

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('auth-interstitial')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('auth-complete'));

    await waitFor(() => {
      expect(checkNotebookAuthRequirements).toHaveBeenCalledTimes(2);
    });
  });

  it('completes auth flow and shows notebook when requirements satisfied', async () => {
    vi.mocked(checkNotebookAuthRequirements)
      .mockResolvedValueOnce({
        needsAuth: true,
        requiredProviders: [],
        missingProviders: [],
      })
      .mockResolvedValueOnce({
        needsAuth: false,
        requiredProviders: [],
        missingProviders: [],
      });

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('auth-interstitial')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('auth-complete'));

    await waitFor(() => {
      expect(screen.queryByTestId('auth-interstitial')).not.toBeInTheDocument();
      expect(screen.getByTestId('notebook-layout')).toBeInTheDocument();
    });
  });

  it('still loads when notebook is missing guideId', async () => {
    createReadyContexts({ notebook: { guideId: undefined } });
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('notebook-layout')).toBeInTheDocument();
    });
    expect(consoleSpy).toHaveBeenCalled();
    consoleSpy.mockRestore();
  });

  it('renders user home page markdown content in viewer', async () => {
    createReadyContexts({ notebook: { homePageFileId: 'home-md' } });
    vi.mocked(notebookFilesApi.getNotebookFileContent).mockResolvedValue(
      createTextBlob('# Rendered Home')
    );

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('markdown-viewer')).toHaveTextContent('# Rendered Home');
    });
  });

  it('handles home page content fetch failure', async () => {
    createReadyContexts({ notebook: { homePageFileId: 'home-md' } });
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    vi.mocked(notebookFilesApi.getNotebookFileContent).mockRejectedValueOnce(new Error('fetch failed'));

    renderNotebook();

    await waitFor(() => {
      expect(consoleSpy).toHaveBeenCalled();
    });
    consoleSpy.mockRestore();
  });

  it('fetches home page markdown with .markdown extension', async () => {
    mockFolderTree = {
      ...defaultFolderTree(),
      files: [
        {
          id: 'home-md-ext',
          fileName: 'readme.markdown',
          relativePath: 'readme.markdown',
          fileHash: 'hash-md-ext',
          fileSize: 20,
          lastModifiedUtc: '2024-01-01T00:00:00Z',
          isIndexed: false,
        },
      ],
    };
    createReadyContexts({ notebook: { homePageFileId: 'home-md-ext' } });
    vi.mocked(notebookFilesApi.getNotebookFileContent).mockResolvedValue(
      createTextBlob('# Markdown Ext')
    );

    renderNotebook();

    await waitFor(() => {
      expect(notebookFilesApi.getNotebookFileContent).toHaveBeenCalledWith(
        projectId,
        notebookId,
        'readme.markdown',
        'hash-md-ext'
      );
    });
  });

  it('invokes clearHomePage when sidebar clears home page', async () => {
    const clearHomePage = vi.fn();
    createReadyContexts({ notebookContext: { clearHomePage } });

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('sidebar')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('clear-home-page'));
    expect(clearHomePage).toHaveBeenCalled();
  });

  it('fetches origin file info when publishing files with lineage', async () => {
    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('sidebar')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('publish-with-origin'));

    await waitFor(() => {
      expect(notebookFilesApi.getOriginFileInfo).toHaveBeenCalledWith(
        projectId,
        notebookId,
        'origin-v1'
      );
      expect(screen.getByTestId('publish-dialog')).toHaveTextContent('origin.md');
    });
  });

  it('closes publish dialog via onClose', async () => {
    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('sidebar')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('trigger-publish'));
    await waitFor(() => {
      expect(screen.getByTestId('publish-dialog')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('publish-close'));
    await waitFor(() => {
      expect(screen.queryByTestId('publish-dialog')).not.toBeInTheDocument();
    });
  });

  it('submits publish and navigates to project on complete', async () => {
    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('sidebar')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('trigger-publish'));
    await waitFor(() => {
      expect(screen.getByTestId('publish-dialog')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('publish-submit'));
    await waitFor(() => {
      expect(notebookFilesApi.publishToProject).toHaveBeenCalled();
    });

    fireEvent.click(screen.getByTestId('publish-complete'));
    await waitFor(() => {
      expect(mockRefreshProject).toHaveBeenCalled();
      expect(mockNavigate).toHaveBeenCalledWith(`/projects/${projectId}?fileId=pub-file-1`);
    });
  });

  it('closes mobile sidebar when publishing on mobile', async () => {
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('sidebar')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('mobile-sidebar-toggle'));
    expect(screen.getByTestId('notebook-layout')).toHaveAttribute('data-mobile-sidebar-open', 'true');

    fireEvent.click(screen.getByTestId('trigger-publish'));

    await waitFor(() => {
      expect(screen.getByTestId('notebook-layout')).toHaveAttribute('data-mobile-sidebar-open', 'false');
    });
  });

  it('opens file preview from conversation preview-by-path handler', async () => {
    window.history.replaceState({}, '', `${route}?c=conv-123`);
    renderNotebook(`${route}?c=conv-123`);

    await waitFor(() => {
      expect(screen.getByTestId('conversation-provider')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('preview-by-path'));

    await waitFor(() => {
      expect(screen.getByTestId('file-preview-overlay')).toHaveTextContent('doc.md');
    });
  });

  it('opens overlay preview when navigating from embedded home preview', async () => {
    createReadyContexts({ notebook: { homePageFileId: 'home-pdf' } });

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('embedded-preview')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('preview-navigate'));

    await waitFor(() => {
      expect(screen.getByTestId('file-preview-overlay')).toHaveTextContent('doc.md');
    });
  });

  it('switches to new conversation from conversation panel callback', async () => {
    window.history.replaceState({}, '', `${route}?c=conv-123`);
    renderNotebook(`${route}?c=conv-123`);

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toHaveTextContent('conv-123');
    });

    fireEvent.click(screen.getByTestId('new-conversation'));

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toHaveTextContent('new-conv-1');
    });
    expect(window.location.search).toContain('c=new-conv-1');
  });

  it('navigates to settings from no-chat-model dialog', async () => {
    mockWorkspaceControls = {
      chat: { effectiveModelId: null, blockers: ['No model'] },
      chatIsLoading: false,
      toolbar: null,
      toolbarIsLoading: false,
      refresh: vi.fn(),
      inFlight: false,
      setInFlight: vi.fn(),
    };
    window.history.replaceState({}, '', `${route}?c=conv-123`);
    renderNotebook(`${route}?c=conv-123`);

    await waitFor(() => {
      expect(screen.getByTestId('no-chat-model-dialog')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('go-to-settings'));
    expect(mockNavigate).toHaveBeenCalledWith('/settings');
  });

  it('shows contributor runtime loading for non-admin when chat readiness loading', async () => {
    mockAuth = { role: 'User', status: 'authenticated' };
    mockWorkspaceControls = {
      chat: { effectiveModelId: 'model-1', inProgressState: 'loading' },
      chatIsLoading: false,
      toolbar: null,
      toolbarIsLoading: false,
      refresh: vi.fn(),
      inFlight: false,
      setInFlight: vi.fn(),
    };
    window.history.replaceState({}, '', `${route}?c=conv-123`);
    renderNotebook(`${route}?c=conv-123`);

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toHaveAttribute('data-runtime-loading', 'true');
    });
  });

  it('updates headerIsMobile on window resize for admin toolbar', async () => {
    mockAuth = { role: 'Admin', status: 'authenticated' };
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('service-toolbar')).toBeInTheDocument();
    });

    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 1200 });
    act(() => {
      window.dispatchEvent(new Event('resize'));
    });

    expect(screen.getByTestId('service-toolbar')).toBeInTheDocument();
  });

  it('navigates to next conversation on bulk delete with next id', async () => {
    window.history.replaceState({}, '', `${route}?c=conv-x`);
    renderNotebook(`${route}?c=conv-x`);

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toHaveTextContent('conv-x');
    });

    fireEvent.click(screen.getByTestId('bulk-delete-with-next'));

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toHaveTextContent('conv-y');
    });
  });

  it('updates selected item when deleting inactive conversation', async () => {
    window.history.replaceState({}, '', `${route}?c=conv-active`);
    renderNotebook(`${route}?c=conv-active`);

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toHaveTextContent('conv-active');
    });

    fireEvent.click(screen.getByTestId('delete-inactive-conversation'));

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toHaveTextContent('conv-active');
    });
  });

  it('invokes tour onHighlight and onDeselect callbacks for conversation tour', async () => {
    const selector = document.createElement('div');
    selector.setAttribute('data-tour-id', 'conversation.assistant.selector');
    const button = document.createElement('button');
    selector.appendChild(button);
    const dropdown = document.createElement('div');
    dropdown.setAttribute('data-tour-id', 'conversation.assistant.selector.dropdown');
    document.body.appendChild(selector);
    document.body.appendChild(dropdown);
    const clickSpy = vi.spyOn(button, 'click');

    window.history.replaceState({}, '', `${route}?c=conv-123`);
    renderNotebook(`${route}?c=conv-123`);

    await waitFor(() => {
      expect(capturedTourSteps['notebook-conversation']).toBeDefined();
    });

    const assistantStep = capturedTourSteps['notebook-conversation'].find(
      (s) => s.onHighlight !== undefined
    );
    act(() => {
      assistantStep?.onHighlight?.();
    });

    await waitFor(() => {
      expect(clickSpy).toHaveBeenCalled();
    });

    act(() => {
      assistantStep?.onDeselect?.();
    });

    selector.remove();
    dropdown.remove();
    clickSpy.mockRestore();
  });

  it('invokes cells and home tour onDeselect callbacks', async () => {
    vi.stubGlobal(
      'MouseEvent',
      class PatchedMouseEvent extends Event {
        constructor(type: string, eventInitDict?: EventInit) {
          super(type, eventInitDict);
        }
      }
    );

    const rootFolder = document.createElement('div');
    rootFolder.setAttribute('data-tour-id', 'notebook.folder.root');
    Object.defineProperty(rootFolder, 'getBoundingClientRect', {
      value: () => ({ left: 0, top: 0, width: 100, height: 40, right: 100, bottom: 40 }),
    });
    document.body.appendChild(rootFolder);

    renderNotebook();

    await waitFor(() => {
      expect(capturedTourSteps['notebook-cells']).toBeDefined();
    });

    const cellsFolderStep = capturedTourSteps['notebook-cells'].find((s) => s.onHighlight !== undefined);
    act(() => {
      cellsFolderStep?.onHighlight?.();
    });

    const closeSpy = vi.fn();
    window.addEventListener('close-context-menus', closeSpy);
    capturedTourSteps['notebook-cells']
      .filter((s) => s.onDeselect)
      .forEach((s) => s.onDeselect?.());
    expect(closeSpy).toHaveBeenCalled();
    window.removeEventListener('close-context-menus', closeSpy);

    await waitFor(() => {
      expect(cellsFolderStep?.onHighlight).toBeDefined();
    });

    rootFolder.remove();
    vi.unstubAllGlobals();
  });

  it('registers home tour when template home content is shown', async () => {
    vi.mocked(api.projects.notebookTemplates.getById).mockResolvedValue({
      id: 'guide-1',
      templateName: 'Guide',
      authProviders: [],
      homeContent: '# Home Tour',
    } as never);

    renderNotebook();

    await waitFor(() => {
      expect(capturedTourSteps['notebook-home']).toBeDefined();
    });
  });

  it('registers file-preview tour when preview overlay is open', async () => {
    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('sidebar')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('trigger-preview'));

    await waitFor(() => {
      expect(capturedTourSteps['notebook-file-preview']).toBeDefined();
    });
  });

  it('warns when preview by id is triggered from conversation context', async () => {
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
    window.history.replaceState({}, '', `${route}?c=conv-123`);
    renderNotebook(`${route}?c=conv-123`);

    await waitFor(() => {
      expect(screen.getByTestId('preview-by-id')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('preview-by-id'));
    expect(warnSpy).toHaveBeenCalledWith(
      'Preview by ID disabled - use preview from sidebar context menu instead'
    );
    warnSpy.mockRestore();
  });

  it('does not show service toolbar for unauthenticated admin role', async () => {
    mockAuth = { role: 'Admin', status: 'unauthenticated' };

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('notebook-layout')).toBeInTheDocument();
    });
    expect(screen.queryByTestId('service-toolbar')).not.toBeInTheDocument();
  });

  it('restarts llama runtime from crash modal', async () => {
    vi.mocked(api.projects.notebooks.conversations.restartLlamaRuntime).mockResolvedValueOnce({} as never);

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('notebook-layout')).toBeInTheDocument();
    });

    act(() => {
      window.dispatchEvent(
        new CustomEvent('llama-runtime-crashed', {
          detail: { reason: 'Crashed', upstreamDetail: 'OOM' },
        })
      );
    });

    await waitFor(() => {
      expect(screen.getByTestId('llama-crashed-modal')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('Restart Runtime'));

    await waitFor(() => {
      expect(api.projects.notebooks.conversations.restartLlamaRuntime).toHaveBeenCalledWith(
        projectId,
        notebookId
      );
    });
  });

  it('navigates to project without fileId when publish completes with no ids', async () => {
    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('sidebar')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('trigger-publish'));
    await waitFor(() => {
      expect(screen.getByTestId('publish-dialog')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('publish-complete-empty'));

    await waitFor(() => {
      expect(mockNavigate).toHaveBeenCalledWith(`/projects/${projectId}`);
    });
  });

  it('continues publish when origin file info fetch fails', async () => {
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    vi.mocked(notebookFilesApi.getOriginFileInfo).mockRejectedValueOnce(new Error('origin down'));

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('sidebar')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('publish-with-origin'));

    await waitFor(() => {
      expect(screen.getByTestId('publish-dialog')).toHaveTextContent('origin.md');
    });
    expect(consoleSpy).toHaveBeenCalled();
    consoleSpy.mockRestore();
  });

  it('finds files in nested folder tree for preview by path', async () => {
    mockPreviewByPathTarget = 'docs/nested.md';
    mockFolderTree = {
      name: 'root',
      relativePath: '',
      subFolders: [
        {
          name: 'docs',
          relativePath: 'docs',
          subFolders: [],
          files: [
            {
              id: 'nested-doc',
              fileName: 'nested.md',
              relativePath: 'docs/nested.md',
              fileHash: 'hash-nested',
              fileSize: 10,
              lastModifiedUtc: '2024-01-01T00:00:00Z',
              isIndexed: false,
            },
          ],
        },
      ],
      files: [],
    };

    window.history.replaceState({}, '', `${route}?c=conv-123`);
    renderNotebook(`${route}?c=conv-123`);

    await waitFor(() => {
      expect(screen.getByTestId('preview-by-path')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('preview-by-path'));

    await waitFor(() => {
      expect(screen.getByTestId('file-preview-overlay')).toHaveTextContent('nested.md');
    });
  });

  it('invokes home tour onHighlight callbacks', async () => {
    vi.stubGlobal(
      'MouseEvent',
      class PatchedMouseEvent extends Event {
        constructor(type: string, eventInitDict?: EventInit) {
          super(type, eventInitDict);
        }
      }
    );

    const rootFolder = document.createElement('div');
    rootFolder.setAttribute('data-tour-id', 'notebook.folder.root');
    Object.defineProperty(rootFolder, 'getBoundingClientRect', {
      value: () => ({ left: 0, top: 0, width: 100, height: 40, right: 100, bottom: 40 }),
    });
    document.body.appendChild(rootFolder);

    vi.mocked(api.projects.notebookTemplates.getById).mockResolvedValue({
      id: 'guide-1',
      templateName: 'Guide',
      authProviders: [],
      homeContent: '# Home Tour',
    } as never);

    renderNotebook();

    await waitFor(() => {
      expect(capturedTourSteps['notebook-home']).toBeDefined();
    });

    capturedTourSteps['notebook-home']
      .filter((s) => s.onHighlight)
      .forEach((s) => {
        act(() => {
          s.onHighlight?.();
        });
      });

    rootFolder.remove();
    vi.unstubAllGlobals();
  });

  it('logs when auth re-check fails after completing interstitial', async () => {
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    vi.mocked(checkNotebookAuthRequirements)
      .mockResolvedValueOnce({
        needsAuth: true,
        requiredProviders: [],
        missingProviders: [],
      })
      .mockRejectedValueOnce(new Error('auth re-check failed'));

    renderNotebook();

    await waitFor(() => {
      expect(screen.getByTestId('auth-interstitial')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('auth-complete'));

    await waitFor(() => {
      expect(consoleSpy).toHaveBeenCalled();
    });
    consoleSpy.mockRestore();
  });

  it('shows chat model missing flag on conversation panel', async () => {
    mockWorkspaceControls = {
      chat: { effectiveModelId: null },
      chatIsLoading: false,
      toolbar: null,
      toolbarIsLoading: false,
      refresh: vi.fn(),
      inFlight: false,
      setInFlight: vi.fn(),
    };
    window.history.replaceState({}, '', `${route}?c=conv-123`);
    renderNotebook(`${route}?c=conv-123`);

    await waitFor(() => {
      expect(screen.getByTestId('conversation-panel')).toHaveAttribute(
        'data-chat-model-missing',
        'true'
      );
    });
  });
});
