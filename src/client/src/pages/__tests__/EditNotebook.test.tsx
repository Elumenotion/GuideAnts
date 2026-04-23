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
}); 