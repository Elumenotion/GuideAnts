import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '../../test/test-utils';
import userEvent from '@testing-library/user-event';
import EditProject from '../EditProject';
import { api } from '../../services/api';

// Provide route params via useParams
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual('react-router-dom');
  return {
    ...actual,
    useParams: () => ({ projectId: '123' }),
    useNavigate: () => mockNavigate,
  };
});

const mockNavigate = vi.fn();

// Mock api layer
vi.mock('../../services/api', () => ({
  api: {
    projects: {
      getProject: vi.fn(),
      updateProject: vi.fn(),
    },
  },
}));

describe('EditProject page', () => {
  const sampleProject = { id: '123', title: 'Old', description: 'Desc', created: 'now', userRoles: [] };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows loading spinner while fetching', async () => {
    (api.projects.getProject as any).mockResolvedValue(sampleProject);
    render(<EditProject />);
    expect(screen.getByText('Loading project...')).toBeInTheDocument();
    await screen.findByDisplayValue('Old'); // wait fetch
  });

  it('populates form with fetched data and updates project', async () => {
    (api.projects.getProject as any).mockResolvedValue(sampleProject);
    (api.projects.updateProject as any).mockResolvedValue({});

    render(<EditProject />);

    const titleInput = await screen.findByLabelText(/project title/i);
    await userEvent.clear(titleInput);
    await userEvent.type(titleInput, 'New Title');
    await userEvent.click(screen.getByRole('button', { name: /save changes/i }));

    expect(api.projects.updateProject).toHaveBeenCalledWith('123', { title: 'New Title', description: 'Desc' });
    await Promise.resolve();
    expect(mockNavigate).toHaveBeenCalledWith('/projects/123');
  });

  it('displays error message on fetch failure', async () => {
    (api.projects.getProject as any).mockRejectedValue(new Error('Failed'));
    render(<EditProject />);
    expect(await screen.findByText('Failed')).toBeInTheDocument();
  });
}); 