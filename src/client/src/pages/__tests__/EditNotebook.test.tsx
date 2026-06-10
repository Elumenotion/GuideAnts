import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '../../test/test-utils';
import userEvent from '@testing-library/user-event';
import EditNotebook from '../EditNotebook';
import { api } from '../../services/api';

// Mock react-router-dom
const mockNavigate = vi.fn();
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual('react-router-dom');
  return {
    ...actual,
    useParams: () => ({ projectId: 'test-project-id', notebookId: 'test-notebook-id' }),
    useNavigate: () => mockNavigate,
  };
});

// Mock the project context hook
let mockProjectContext: any;
vi.mock('../../contexts/ProjectContext', async (importOriginal) => {
  const actual: any = await importOriginal();
  return {
    ...actual,
    ProjectProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
    useProject: () => mockProjectContext,
  } as any;
});

// Mock API
vi.mock('../../services/api', () => ({
  api: {
    projects: {
      updateNotebook: vi.fn(),
    },
  },
}));

vi.mock('../../components/ErrorScreen', () => ({
  default: ({
    title,
    onRetry,
    onBack,
  }: {
    title?: string;
    onRetry?: () => void;
    onBack?: () => void;
  }) => (
    <div>
      <h1>{title}</h1>
      <button type="button" onClick={onRetry}>
        Try Again
      </button>
      <button type="button" onClick={onBack}>
        Go Back
      </button>
    </div>
  ),
}));

describe('EditNotebook page', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    
    // Default mock project context
    mockProjectContext = {
      project: {
        id: 'test-project-id',
        title: 'Test Project',
        notebooks: [
          {
            id: 'test-notebook-id',
            title: 'Test Notebook',
            createdAt: '2023-01-01T00:00:00Z',
            updatedAt: '2023-01-01T00:00:00Z',
          },
        ],
      },
      isLoading: false,
      error: null,
      refreshProject: vi.fn(),
    };
  });

  it('renders edit form with notebook title', async () => {
    render(<EditNotebook />);
    
    // Should show the form elements
    expect(screen.getByText('Edit Notebook')).toBeInTheDocument();
    expect(screen.getByText('Update your notebook details')).toBeInTheDocument();
    expect(screen.getByLabelText('Notebook Title')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save Changes' })).toBeInTheDocument();
  });

  it('shows validation error when submitting empty title', async () => {
    render(<EditNotebook />);
    
    const titleInput = screen.getByLabelText('Notebook Title');
    const submitButton = screen.getByRole('button', { name: 'Save Changes' });
    
    await userEvent.clear(titleInput);
    await userEvent.click(submitButton);
    
    expect(await screen.findByText('Notebook title is required')).toBeInTheDocument();
  });

  it('successfully updates notebook when valid data submitted', async () => {
    (api.projects.updateNotebook as any).mockResolvedValue({});
    
    render(<EditNotebook />);
    
    const titleInput = screen.getByLabelText('Notebook Title');
    const submitButton = screen.getByRole('button', { name: 'Save Changes' });
    
    await userEvent.clear(titleInput);
    await userEvent.type(titleInput, 'Updated Notebook Title');
    await userEvent.click(submitButton);
    
    expect(api.projects.updateNotebook).toHaveBeenCalledWith(
      'test-project-id',
      'test-notebook-id',
      { title: 'Updated Notebook Title' }
    );
  });

  it('handles API errors gracefully', async () => {
    (api.projects.updateNotebook as any).mockRejectedValue(new Error('Update failed'));
    
    render(<EditNotebook />);
    
    const titleInput = screen.getByLabelText('Notebook Title');
    const submitButton = screen.getByRole('button', { name: 'Save Changes' });
    
    await userEvent.clear(titleInput);
    await userEvent.type(titleInput, 'Updated Title');
    await userEvent.click(submitButton);
    
    expect(await screen.findByText('Update failed')).toBeInTheDocument();
  });

  it('navigates back when cancel button is clicked', async () => {
    render(<EditNotebook />);
    
    const cancelButton = screen.getByRole('button', { name: 'Cancel' });
    await userEvent.click(cancelButton);
    
    expect(mockNavigate).toHaveBeenCalledWith('/projects/test-project-id/notebooks/test-notebook-id');
  });

  it('shows loading spinner while project context is loading', () => {
    mockProjectContext.isLoading = true;
    render(<EditNotebook />);
    expect(screen.getByText('Loading notebook...')).toBeInTheDocument();
  });

  it('shows critical error screen when project context fails to fetch', () => {
    mockProjectContext.error = 'Failed to fetch project';
    render(<EditNotebook />);
    expect(screen.getByText('Failed to Load Notebook')).toBeInTheDocument();
  });

  it('shows notebook not found when id is missing from project', async () => {
    mockProjectContext.project.notebooks = [];
    render(<EditNotebook />);
    expect(await screen.findByText('Notebook not found')).toBeInTheDocument();
  });

  it('refreshes project and includes description on successful update', async () => {
    (api.projects.updateNotebook as any).mockResolvedValue({});
    const refreshProject = vi.fn().mockResolvedValue(undefined);
    mockProjectContext.refreshProject = refreshProject;

    render(<EditNotebook />);

    await userEvent.type(screen.getByLabelText(/description/i), 'Updated description');
    await userEvent.click(screen.getByRole('button', { name: 'Save Changes' }));

    expect(api.projects.updateNotebook).toHaveBeenCalledWith(
      'test-project-id',
      'test-notebook-id',
      expect.objectContaining({ description: 'Updated description' })
    );
    expect(refreshProject).toHaveBeenCalled();
    expect(mockNavigate).toHaveBeenCalledWith('/projects/test-project-id/notebooks/test-notebook-id');
  });

  it('retries and navigates back from the critical load error screen', async () => {
    const reloadSpy = vi.fn();
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { ...window.location, reload: reloadSpy },
    });
    mockProjectContext.error = 'Failed to fetch project';
    render(<EditNotebook />);

    await screen.findByText('Failed to Load Notebook');
    await userEvent.click(screen.getByRole('button', { name: /try again/i }));
    expect(reloadSpy).toHaveBeenCalled();

    await userEvent.click(screen.getByRole('button', { name: /go back/i }));
    expect(mockNavigate).toHaveBeenCalledWith('/projects/test-project-id/notebooks/test-notebook-id');
  });

  it('shows saving state while notebook update is in flight', async () => {
    (api.projects.updateNotebook as any).mockImplementation(() => new Promise(() => {}));
    render(<EditNotebook />);

    await userEvent.click(screen.getByRole('button', { name: 'Save Changes' }));
    expect(await screen.findByText('Saving...')).toBeInTheDocument();
  });
}); 
