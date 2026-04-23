import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render as rtlRender, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { DndProvider } from 'react-dnd';
import { HTML5Backend } from 'react-dnd-html5-backend';
import { ToastProvider } from '../../components/common/Toast';
import ProjectDetails from '../ProjectDetails';

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

// Mock navigate and params
const mockNavigate = vi.fn();
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual('react-router-dom');
  return {
    ...actual,
    useNavigate: () => mockNavigate,
    useParams: () => ({ projectId: '123' }),
  };
});

// Mock folder operations hook
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

// Mock the project context hook
let dynamicReturnValue: any;
vi.mock('../../contexts/ProjectContext', () => ({
  useProject: () => dynamicReturnValue,
}));

describe('ProjectDetails page', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders loading spinner', () => {
    dynamicReturnValue = {
      project: null,
      error: null,
      isLoading: true,
      selectedItem: null,
      expandedSections: new Set(),
      folderTree: null,
      currentUserEmail: null,
      setSelectedItem: vi.fn(),
      toggleSection: vi.fn(),
      refreshProject: vi.fn(),
      loadProject: vi.fn(),
      setFolderTree: vi.fn(),
      canEdit: false,
      isOwner: false,
      reset: vi.fn(),
    };

    render(<ProjectDetails />);
    expect(screen.getByText('Loading project details...')).toBeInTheDocument();
  });

  it('displays error state', () => {
    dynamicReturnValue = {
      project: null,
      error: 'Boom',
      isLoading: false,
      selectedItem: null,
      expandedSections: new Set(),
      folderTree: null,
      currentUserEmail: null,
      setSelectedItem: vi.fn(),
      toggleSection: vi.fn(),
      refreshProject: vi.fn(),
      loadProject: vi.fn(),
      setFolderTree: vi.fn(),
      canEdit: false,
      isOwner: false,
      reset: vi.fn(),
    };

    render(<ProjectDetails />);
    expect(screen.getByText('Boom', { selector: 'p.text-gray-600' })).toBeInTheDocument();
  });

  it('shows "Project not found" when project null and no error', () => {
    dynamicReturnValue = {
      project: null,
      error: null,
      isLoading: false,
      selectedItem: null,
      expandedSections: new Set(),
      folderTree: null,
      currentUserEmail: null,
      setSelectedItem: vi.fn(),
      toggleSection: vi.fn(),
      refreshProject: vi.fn(),
      loadProject: vi.fn(),
      setFolderTree: vi.fn(),
      canEdit: false,
      isOwner: false,
      reset: vi.fn(),
    };

    render(<ProjectDetails />);
    expect(screen.getByText('Project not found')).toBeInTheDocument();
  });
}); 