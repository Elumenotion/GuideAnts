import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render as rtlRender, screen, waitFor, cleanup } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { DndProvider } from 'react-dnd';
import { HTML5Backend } from 'react-dnd-html5-backend';
import { ToastProvider } from '../../components/common/Toast';
import ProjectDetails from '../ProjectDetails';
import '@testing-library/jest-dom';

// Custom render function without ProjectProvider since we're mocking the context
function render(ui: React.ReactElement) {
  const Wrapper = ({ children }: { children: React.ReactNode }) => (
    <ToastProvider>
      <DndProvider backend={HTML5Backend}>
        <MemoryRouter initialEntries={['/']} initialIndex={0}>
          {children}
        </MemoryRouter>
      </DndProvider>
    </ToastProvider>
  );

  return rtlRender(ui, { wrapper: Wrapper });
}

// ---------------------------------------------------------------------------
// Routing mocks – consistent with other suites
// ---------------------------------------------------------------------------
const mockNavigate = vi.fn();
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual('react-router-dom');
  return {
    ...actual,
    useNavigate: () => mockNavigate,
    useParams: () => ({ projectId: 'p2' }),
  };
});

// ---------------------------------------------------------------------------
// Folder-operation stubs returned by mocked hook
// ---------------------------------------------------------------------------
var folderOpsStubs: any;
vi.mock('../../hooks/useFolderOperations', () => {
  folderOpsStubs = {
    folderTree: null,
    createFolder: vi.fn().mockResolvedValue({ id: 'fld' }),
    updateFolder: vi.fn().mockResolvedValue({}),
    deleteFolder: vi.fn().mockResolvedValue({}),
    moveFile: vi.fn().mockResolvedValue({}),
    uploadFiles: vi.fn().mockResolvedValue([{ id: 'uf1' }]),
    fetchFolderTree: vi.fn(),
  };
  return {
    useFolderOperations: () => folderOpsStubs,
  };
});

// ---------------------------------------------------------------------------
// API project stubs extended for notebook operations
// ---------------------------------------------------------------------------
var apiProjectStubs: any;
vi.mock('../../services/api', () => {
  apiProjectStubs = {
    createNotebook: vi.fn().mockResolvedValue({ id: 'nb1' }),
    deleteNotebook: vi.fn().mockResolvedValue({}),
    copyNotebook: vi.fn().mockResolvedValue({ id: 'nbCopy' }),
    updateNotebook: vi.fn().mockResolvedValue({}),
    deleteContentFile: vi.fn().mockResolvedValue({}),
    deleteLink: vi.fn().mockResolvedValue({}),
  };
  return {
    api: {
      auth: {
        getCurrentUser: vi.fn().mockResolvedValue({ email: 'me@example.com' }),
      },
      projects: apiProjectStubs,
    },
  };
});

// ---------------------------------------------------------------------------
// Layout component mocks
// ---------------------------------------------------------------------------
vi.mock('../../components/layouts/ProjectLayout', () => ({
  ProjectLayout: ({ sidebar, content, project, canEdit }: any) => (
    <div data-testid="project-layout">
      <div data-testid="project-header">
        <h1>{project?.title}</h1>
        {canEdit && <button>Edit Project</button>}
        <button>Back to Projects</button>
      </div>
      {sidebar}
      {content}
    </div>
  ),
}));

vi.mock('../../components/layouts/SidebarContainer', () => ({
  SidebarContainer: ({ children }: any) => (
    <div data-testid="sidebar-container">{children}</div>
  ),
}));

// ---------------------------------------------------------------------------
// Stub ProjectSidebar to expose all callback props for direct invocation
// ---------------------------------------------------------------------------
let sidebarProps: any;
vi.mock('../../components/project/sidebar/ProjectSidebar', () => ({
  ProjectSidebar: (props: any) => {
    sidebarProps = props;
    return <div data-testid="sidebar-stub" />;
  },
}));

// ---------------------------------------------------------------------------
// Minimal stub for useProject – dynamic per test
// ---------------------------------------------------------------------------
let dynamicReturn: any;
vi.mock('../../contexts/ProjectContext', () => ({
  useProject: () => dynamicReturn,
}));

// ---------------------------------------------------------------------------
// Utility helpers
// ---------------------------------------------------------------------------
const baseProject = {
  id: 'p2',
  title: 'Coverage Project',
  created: new Date().toISOString(),
  notebooks: [{ id: 'n1', title: 'Specs' }],
  contentFiles: [{ id: 'f1', fileName: 'file.pdf' }],
  links: [],
  semiStructuredDatas: [],
  folders: [],
  userRoles: [
    {
      userId: 'u1',
      userEmail: 'me@example.com',
      userName: 'Me',
      roleName: 'Owner',
    },
  ],
};

function stubReturn(partial?: Partial<typeof dynamicReturn>): any {
  return {
    project: baseProject,
    error: null,
    isLoading: false,
    selectedItem: null,
    expandedSections: new Set(),
    folderTree: null,
    currentUserEmail: 'me@example.com',
    setSelectedItem: vi.fn(),
    toggleSection: vi.fn(),
    refreshProject: vi.fn(),
    loadProject: vi.fn(),
    setFolderTree: vi.fn(),
    canEdit: true,
    isOwner: true,
    reset: vi.fn(),
    ...partial,
  };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('ProjectDetails – notebook & folder handlers', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    sidebarProps = undefined;
  });

  afterEach(() => {
    cleanup();
  });

  it('handleCreateNotebook triggers API, refresh, and selection', async () => {
    dynamicReturn = stubReturn();

    render(<ProjectDetails />);

    // Wait for sidebar to initialise and props to capture
    await screen.findByTestId('sidebar-stub');
    expect(sidebarProps).toBeDefined();

    await sidebarProps.onCreateNotebook('My Notebook');

    expect(apiProjectStubs.createNotebook).toHaveBeenCalledWith('p2', {
      title: 'My Notebook',
      notebookTemplateId: undefined,
    });
    expect(dynamicReturn.refreshProject).toHaveBeenCalled();
    // ID comes from stubbed response above
    expect(dynamicReturn.setSelectedItem).toHaveBeenCalledWith({
      type: 'notebooks',
      id: 'nb1',
    });
  });

  it('handleCopyNotebook uses correct title and selects new notebook', async () => {
    dynamicReturn = stubReturn();

    render(<ProjectDetails />);
    await screen.findByTestId('sidebar-stub');

    await sidebarProps.onCopyNotebook('n1');

    expect(apiProjectStubs.copyNotebook).toHaveBeenCalledWith('p2', {
      title: 'Copy of Specs',
      sourceNotebookId: 'n1',
    });
    expect(dynamicReturn.refreshProject).toHaveBeenCalled();
    expect(dynamicReturn.setSelectedItem).toHaveBeenCalledWith({
      type: 'notebooks',
      id: 'nbCopy',
    });
  });

  it('handleRenameNotebook calls updateNotebook and refreshes', async () => {
    dynamicReturn = stubReturn();
    render(<ProjectDetails />);
    await screen.findByTestId('sidebar-stub');

    await sidebarProps.onRenameNotebook('n1', 'New Title');

    expect(apiProjectStubs.updateNotebook).toHaveBeenCalledWith('p2', 'n1', {
      title: 'New Title',
    });
    expect(dynamicReturn.refreshProject).toHaveBeenCalled();
  });

  it('folder operations call underlying hook methods', async () => {
    dynamicReturn = stubReturn();
    render(<ProjectDetails />);
    await screen.findByTestId('sidebar-stub');

    // Create folder
    await sidebarProps.onCreateFolder(undefined, { name: 'Assets' });
    expect(folderOpsStubs.createFolder).toHaveBeenCalledWith({
      name: 'Assets',
      parentFolderId: undefined,
    });

    // Rename folder
    await sidebarProps.onRenameFolder('fld1', 'Renamed');
    expect(folderOpsStubs.updateFolder).toHaveBeenCalledWith('fld1', {
      name: 'Renamed',
    });

    // Delete folder
    await sidebarProps.onDeleteFolder('fld1');
    expect(folderOpsStubs.deleteFolder).toHaveBeenCalledWith('fld1');

    // Move file
    await sidebarProps.onMoveFile('file1', 'fld1');
    expect(folderOpsStubs.moveFile).toHaveBeenCalledWith('file1', {
      destinationFolderId: 'fld1',
    });

    // Upload to folder (covers uploadFilesToFolder)
    const file = new File(['x'], 'hello.txt');
    await sidebarProps.onUploadFilesToFolder([file], 'fld1');
    expect(folderOpsStubs.uploadFiles).toHaveBeenCalledWith([file], 'fld1');

    // refreshProject should be called after each operation (at least once)
    expect(dynamicReturn.refreshProject).toHaveBeenCalled();
  });
}); 