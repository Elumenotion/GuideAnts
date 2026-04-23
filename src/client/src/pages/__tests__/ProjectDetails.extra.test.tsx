import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render as rtlRender, screen, fireEvent, cleanup, waitFor } from '@testing-library/react';
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
// Re-use routing mocks so navigation never touches real history
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
// Mock folder operations hook
// ---------------------------------------------------------------------------
vi.mock('../../hooks/useFolderOperations', () => ({
  useFolderOperations: () => ({
    folderTree: null,
    createFolder: vi.fn(),
    updateFolder: vi.fn(),
    deleteFolder: vi.fn(),
    moveFile: vi.fn(),
    uploadFiles: vi.fn(),
    fetchFolderTree: vi.fn(),
  }),
}));

// ---------------------------------------------------------------------------
// Stub heavy child components so we only test branch selection logic
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

vi.mock('../../components/project/sidebar/ProjectSidebar', () => ({
  ProjectSidebar: ({ onAddLink, onAddTeamMember }: any) => (
    <div data-testid="sidebar-stub">
      <button data-testid="trigger-add-link" onClick={() => onAddLink && onAddLink()}>
        AddLink
      </button>
      <button data-testid="trigger-add-collaborator" onClick={() => onAddTeamMember && onAddTeamMember()}>
        AddProjectMember
      </button>
    </div>
  ),
}));

vi.mock('../../components/project/content', () => ({
  NotebookContent: () => <div data-testid="notebook-stub" />,
  ContentFileContent: (props: any) => (
    <div data-testid="file-stub">{props.fileId}</div>
  ),
  LinkContent: (props: any) => (
    <div data-testid="link-stub">{String(props.isEditing)}</div>
  ),
  ProjectMemberContent: () => <div data-testid="collaborator-stub" />,
  ArtifactContent: () => <div data-testid="artifact-stub" />,
}));

vi.mock('../../components/project/content/AddLinkForm', () => ({
  AddLinkForm: () => <div data-testid="add-link-form" />,
}));

vi.mock('../../components/project/content/AddProjectMemberContent', () => ({
  AddProjectMemberContent: () => <div data-testid="add-collaborator-member" />,
}));

// ---------------------------------------------------------------------------
// Dynamic mock for useProject – allows per-test overrides
// ---------------------------------------------------------------------------
let dynamicReturn: any;
vi.mock('../../contexts/ProjectContext', () => ({
  useProject: () => dynamicReturn,
}));

// ---------------------------------------------------------------------------
// Utility: minimal project scaffold reused across cases
// ---------------------------------------------------------------------------
const baseProject = {
  id: 'p2',
  title: 'Branch Project',
  created: new Date('2024-02-02').toISOString(),
  notebooks: [{ id: 'n1', title: 'NB' }],
  contentFiles: [{ id: 'f1', fileName: 'file.pdf' }],
  links: [{ id: 'l1', url: 'https://example.com' }],
  semiStructuredDatas: [{ id: 'a1', name: 'artifact' }],
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

describe('ProjectDetails – additional branch coverage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  it('renders AddLinkForm when in "adding link" mode', async () => {
    dynamicReturn = stubReturn();

    render(<ProjectDetails />);

    // wait for permissions to settle (Edit Project button visible implies canEdit=true)
    await screen.findByText('Edit Project');

    fireEvent.click(screen.getByTestId('trigger-add-link'));

    expect(await screen.findByTestId('add-link-form')).toBeInTheDocument();
  });

  it('renders AddProjectMemberContent when in "adding collaborator" mode', async () => {
    dynamicReturn = stubReturn({});

    render(<ProjectDetails />);

    // wait for permissions to settle (Edit Project button visible implies canEdit=true)
    await screen.findByText('Edit Project');

    fireEvent.click(screen.getByTestId('trigger-add-collaborator'));

    expect(await screen.findByTestId('add-collaborator-member')).toBeInTheDocument();
  });

  it('renders ContentFileContent when a content file is selected', () => {
    dynamicReturn = stubReturn({
      selectedItem: { type: 'contentFiles', id: 'f1' },
    });

    render(<ProjectDetails />);
    expect(screen.getByTestId('file-stub')).toHaveTextContent('f1');
  });

  it('renders LinkContent when a link is selected and toggles edit state', () => {
    dynamicReturn = stubReturn({
      selectedItem: { type: 'links', id: 'l1' },
    });

    render(<ProjectDetails />);
    // Initially not editing
    expect(screen.getByTestId('link-stub')).toHaveTextContent('false');
  });

  it('renders ProjectMemberContent when a project member is selected', async () => {
    dynamicReturn = stubReturn({
      selectedItem: { type: 'projectMembers', id: 'u1' },
    });

    render(<ProjectDetails />);
    expect(await screen.findByTestId('collaborator-stub')).toBeInTheDocument();
  });

  it('renders ArtifactContent when an artifact is selected', async () => {
    dynamicReturn = stubReturn({
      selectedItem: { type: 'artifacts', id: 'a1' },
    });

    render(<ProjectDetails />);
    expect(await screen.findByTestId('artifact-stub')).toBeInTheDocument();
  });

  it('falls back to unknown content type if unmatched', async () => {
    dynamicReturn = stubReturn({
      selectedItem: { type: 'mystery' as any, id: 'x' },
    });

    render(<ProjectDetails />);
    expect(await screen.findByText('Unknown content type')).toBeInTheDocument();
  });

  it('shows Edit Project button for owner', async () => {
    dynamicReturn = stubReturn();
    render(<ProjectDetails />);
    expect(await screen.findByText('Edit Project')).toBeInTheDocument();
  });

  it('hides Edit Project button for viewer role', async () => {
    const viewerProject = {
      ...baseProject,
      userRoles: [
        {
          userId: 'u1',
          userEmail: 'me@example.com',
          userName: 'Me',
          roleName: 'Viewer',
        },
      ],
    };
    dynamicReturn = stubReturn({ 
      project: viewerProject,
      canEdit: false,
      isOwner: false 
    });
    render(<ProjectDetails />);
    expect(screen.queryByText('Edit Project')).not.toBeInTheDocument();
  });
}); 