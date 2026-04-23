import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render as rtlRender, screen, fireEvent } from '@testing-library/react';
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

// ----------------------------------------------------------------------------
// Mock routing helpers – keep consistent with existing test file
// ----------------------------------------------------------------------------
const mockNavigate = vi.fn();
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual('react-router-dom');
  return {
    ...actual,
    useNavigate: () => mockNavigate,
    useParams: () => ({ projectId: 'p1' }),
  };
});

// ----------------------------------------------------------------------------
// Mock folder operations hook
// ----------------------------------------------------------------------------
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

// ----------------------------------------------------------------------------
// Layout component mocks
// ----------------------------------------------------------------------------
vi.mock('../../components/layouts/ProjectLayout', () => ({
  ProjectLayout: ({ sidebar, content, project, canEdit, onBack }: any) => (
    <div data-testid="project-layout">
      <div data-testid="project-header">
        <h1>{project?.title}</h1>
        {canEdit && <button>Edit Project</button>}
        <button onClick={onBack}>Back to Projects</button>
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

// ----------------------------------------------------------------------------
// Stub heavy child components & sidebar
// ----------------------------------------------------------------------------
vi.mock('../../components/project/sidebar/ProjectSidebar', () => ({
  ProjectSidebar: () => <div data-testid="sidebar">Sidebar</div>,
}));

vi.mock('../../components/project/content', () => ({
  NotebookContent: ({ notebook, canEdit }: any) => (
    <div data-testid="notebook">{notebook.title}-{String(canEdit)}</div>
  ),
  ContentFileContent: () => <div>FILE</div>,
  LinkContent: () => <div>LINK</div>,
  ProjectMemberContent: () => <div>TEAM</div>,
  ArtifactContent: () => <div>ARTIFACT</div>,
}));

// ----------------------------------------------------------------------------
// Dynamic context mock – same approach as earlier tests
// ----------------------------------------------------------------------------
let dynamicReturnValue: any;
vi.mock('../../contexts/ProjectContext', () => ({
  useProject: () => dynamicReturnValue,
}));

// Sample project object
const sampleProject = {
  id: 'p1',
  title: 'My Project',
  created: new Date('2024-01-01').toISOString(),
  notebooks: [{ id: 'n1', title: 'Notebook 1' }],
  contentFiles: [],
  links: [],
  semiStructuredDatas: [],
  userRoles: [
    { userId: 'u1', userEmail: 'me@example.com', userName: 'Me', roleName: 'Owner' },
  ],
};

function commonReturn(overrides: Partial<typeof dynamicReturnValue> = {}) {
  return {
    project: sampleProject,
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
    ...overrides,
  };
}

// ----------------------------------------------------------------------------
// Tests
// ----------------------------------------------------------------------------

describe('ProjectDetails – loaded state', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows placeholder when no item selected', () => {
    dynamicReturnValue = commonReturn();
    render(<ProjectDetails />);
    expect(screen.getByText(/select an item from the sidebar/i)).toBeInTheDocument();
    expect(screen.getByTestId('sidebar')).toBeInTheDocument();
  });

  it('shows project details with sidebar (notebooks navigate to separate page)', () => {
    dynamicReturnValue = commonReturn({
      selectedItem: { type: 'contentFiles', id: 'f1' },
    });
    render(<ProjectDetails />);
    expect(screen.getByTestId('sidebar')).toBeInTheDocument();
    expect(screen.getByText('My Project')).toBeInTheDocument();
  });

  it('navigates back when "Back to Projects" is clicked', () => {
    dynamicReturnValue = commonReturn();
    render(<ProjectDetails />);
    fireEvent.click(screen.getByText('Back to Projects'));
    expect(mockNavigate).toHaveBeenCalledWith('/');
  });
}); 