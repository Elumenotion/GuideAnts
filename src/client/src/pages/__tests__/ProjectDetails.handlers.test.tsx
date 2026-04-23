import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  render as rtlRender,
  screen,
  cleanup,
  fireEvent,
  waitFor,
} from '@testing-library/react';
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
// Routing mocks
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
// API mocks – we fill in project methods later per test
// ---------------------------------------------------------------------------
var apiProjectStubs: any;

vi.mock('../../services/api', () => {
  // create stub collection and expose
  const projectFns = {
    addProjectMember: vi.fn(),
    updateProjectMemberRole: vi.fn(),
    removeProjectMember: vi.fn(),
    uploadFiles: vi.fn(),
    addLink: vi.fn(),
    updateLink: vi.fn(),
    deleteLink: vi.fn(),
  };
  apiProjectStubs = projectFns;
  return {
    api: {
      auth: {
        getCurrentUser: vi.fn().mockResolvedValue({ email: 'me@example.com' }),
      },
      projects: projectFns,
    },
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
// Stubs for child components – expose handler props for tests and expose buttons
// ---------------------------------------------------------------------------
let triggerUploadFiles: ((fl: FileList) => void) | undefined;

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
  ProjectSidebar: ({ onUploadFiles, onAddTeamMember, onAddLink }: any) => {
    triggerUploadFiles = onUploadFiles;
    return (
      <div data-testid="sidebar-stub">
        <button
          data-testid="add-collaborator-button"
          onClick={() => onAddTeamMember && onAddTeamMember()}
        />
        <button
          data-testid="add-link-button"
          onClick={() => onAddLink && onAddLink()}
        />
      </div>
    );
  },
}));

let capturedAddMember: any;

vi.mock('../../components/project/content/AddProjectMemberContent', () => ({
  AddProjectMemberContent: (props: any) => {
    capturedAddMember = props.onAdd;
    return <div data-testid="add-collaborator-stub" />;
  },
}));

let capturedUpdateRole: any;
let capturedRemoveMember: any;
let capturedIsOwner: boolean | undefined;
let capturedFileDelete: (() => void) | undefined;
let capturedAddLink: ((url: string) => void) | undefined;
let capturedCancelAddLink: (() => void) | undefined;
let capturedUpdateLink: any;
let capturedDeleteLink: any;

vi.mock('../../components/project/content', () => ({
  NotebookContent: () => <div />,
  ContentFileContent: (props: any) => (
    <div data-testid="file-stub">{props.fileId}</div>
  ),
  LinkContent: () => <div />,
  ProjectMemberContent: (props: any) => {
    capturedUpdateRole = props.onUpdate;
    capturedRemoveMember = props.onDelete;
    return <div data-testid="collaborator-stub" />;
  },
  ArtifactContent: () => <div data-testid="artifact-stub" />,
}));

vi.mock('../../components/project/content/AddLinkForm', () => ({
  AddLinkForm: (props: any) => {
    capturedAddLink = props.onAdd;
    capturedCancelAddLink = props.onCancel;
    return <div data-testid="add-link-form-stub" />;
  },
}));

// ---------------------------------------------------------------------------
// Dynamic context mock – same pattern as other specs
// ---------------------------------------------------------------------------
let dynamicReturn: any;
vi.mock('../../contexts/ProjectContext', () => ({
  useProject: () => dynamicReturn,
}));

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
const baseProject = {
  id: 'p2',
  title: 'Branch Project',
  created: new Date('2024-02-02').toISOString(),
  notebooks: [],
  contentFiles: [{ id: 'f1', fileName: 'one.pdf' }],
  links: [],
  semiStructuredDatas: [],
  userRoles: [
    {
      userId: 'u1',
      userEmail: 'me@example.com',
      userName: 'Me',
      roleName: 'Owner',
    },
  ],
};

function makeReturn(partial?: Partial<typeof dynamicReturn>) {
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

function fakeFileList(): FileList {
  const file = new File(['x'], 'foo.txt');
  return [file] as unknown as FileList;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('ProjectDetails – handler/function coverage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    triggerUploadFiles = undefined;
    capturedAddMember = undefined;
    capturedUpdateRole = undefined;
    capturedRemoveMember = undefined;
    capturedIsOwner = undefined;
    capturedAddLink = undefined;
    capturedCancelAddLink = undefined;
    capturedUpdateLink = undefined;
    capturedDeleteLink = undefined;
    capturedFileDelete = undefined;
  });

  afterEach(() => {
    cleanup();
  });

  it('hasEditPermission shows Edit button for owner', async () => {
    dynamicReturn = makeReturn();
    render(<ProjectDetails />);
    await screen.findByText('Edit Project');
    expect(await screen.findByText('Edit Project')).toBeInTheDocument();
  });

  it('hasEditPermission hides Edit button for viewer role', async () => {
    const viewerProject = {
      ...baseProject,
      userRoles: [
        { ...baseProject.userRoles[0], roleName: 'Viewer' },
      ],
    };
    dynamicReturn = makeReturn({ 
      project: viewerProject,
      canEdit: false,
      isOwner: false 
    });
    render(<ProjectDetails />);
    expect(screen.queryByText('Edit Project')).not.toBeInTheDocument();
  });

  it('handleAddCollaborator triggers API and refresh', async () => {
    apiProjectStubs.addProjectMember.mockResolvedValueOnce({});
    dynamicReturn = makeReturn();

    render(<ProjectDetails />);
    await screen.findByText('Edit Project');

    fireEvent.click(screen.getByTestId('add-collaborator-button'));

    await waitFor(() => expect(screen.getByTestId('add-collaborator-stub')).toBeInTheDocument());
    await waitFor(() => expect(capturedAddMember).toBeTypeOf('function'));

    await capturedAddMember('u2', 'Contributor');

    expect(apiProjectStubs.addProjectMember).toHaveBeenCalledWith('p2', 'u2', 'Contributor');
    expect(dynamicReturn.refreshProject).toHaveBeenCalled();
  });

  it('handleUpdateCollaboratorRole works', async () => {
    apiProjectStubs.updateProjectMemberRole.mockResolvedValueOnce({});

    dynamicReturn = makeReturn({
      selectedItem: { type: 'projectMembers', id: 'u1' },
    });

    render(<ProjectDetails />);
    await screen.findByTestId('collaborator-stub');
    await waitFor(() => expect(capturedUpdateRole).toBeTypeOf('function'));

    await capturedUpdateRole('u1', 'Contributor');

    expect(apiProjectStubs.updateProjectMemberRole).toHaveBeenCalledWith('p2', 'u1', 'Contributor');
    expect(dynamicReturn.refreshProject).toHaveBeenCalled();
  });

  it('handleRemoveCollaborator works', async () => {
    apiProjectStubs.removeProjectMember.mockResolvedValueOnce({});

    dynamicReturn = makeReturn({
      selectedItem: { type: 'projectMembers', id: 'u1' },
    });

    render(<ProjectDetails />);
    await screen.findByTestId('collaborator-stub');
    await waitFor(() => expect(capturedRemoveMember).toBeTypeOf('function'));

    await capturedRemoveMember('u1');

    expect(apiProjectStubs.removeProjectMember).toHaveBeenCalledWith('p2', 'u1');
    expect(dynamicReturn.refreshProject).toHaveBeenCalled();
  });

  it('handleFileUpload success path', async () => {
    const files = fakeFileList();
    apiProjectStubs.uploadFiles.mockResolvedValueOnce([{ id: 'newfile' }]);
    dynamicReturn = makeReturn();

    render(<ProjectDetails />);
    await waitFor(() => expect(triggerUploadFiles).toBeTypeOf('function'));

    await triggerUploadFiles!(files);

    expect(apiProjectStubs.uploadFiles).toHaveBeenCalledWith('p2', Array.from(files));
    expect(dynamicReturn.refreshProject).toHaveBeenCalled();
    expect(dynamicReturn.setSelectedItem).toHaveBeenCalledWith({ type: 'contentFiles', id: 'newfile' });
  });



  it('handles adding a link', async () => {
    apiProjectStubs.addLink.mockResolvedValueOnce({});
    dynamicReturn = makeReturn();
    render(<ProjectDetails />);
    await screen.findByText('Edit Project'); // Wait for auth check

    fireEvent.click(screen.getByTestId('add-link-button'));
    await screen.findByTestId('add-link-form-stub');
    await waitFor(() => expect(capturedAddLink).toBeTypeOf('function'));
    
    await capturedAddLink!('https://example.com');
    
    expect(apiProjectStubs.addLink).toHaveBeenCalledWith('p2', 'https://example.com');
    expect(dynamicReturn.refreshProject).toHaveBeenCalled();
    await waitFor(() => {
        expect(screen.queryByTestId('add-link-form-stub')).not.toBeInTheDocument();
    });
  });

  it('handles cancelling adding a link', async () => {
    dynamicReturn = makeReturn();
    render(<ProjectDetails />);
    await screen.findByText('Edit Project'); // Wait for auth check

    fireEvent.click(screen.getByTestId('add-link-button'));
    await screen.findByTestId('add-link-form-stub');
    await waitFor(() => expect(capturedCancelAddLink).toBeTypeOf('function'));

    await capturedCancelAddLink!();

    await waitFor(() => {
        expect(screen.queryByTestId('add-link-form-stub')).not.toBeInTheDocument();
    });
    expect(apiProjectStubs.addLink).not.toHaveBeenCalled();
  });

  it('handles updating a link', async () => {
    // ... existing code ...
  });
}); 