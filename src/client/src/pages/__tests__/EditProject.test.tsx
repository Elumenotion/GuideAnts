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

  it('shows critical error screen when fetch reports failed to fetch', async () => {
    (api.projects.getProject as any).mockRejectedValue(new Error('Failed to fetch project data'));
    render(<EditProject />);
    expect(await screen.findByText('Failed to Load Project')).toBeInTheDocument();
  });

  it('validates empty title on submit', async () => {
    (api.projects.getProject as any).mockResolvedValue(sampleProject);
    render(<EditProject />);
    const titleInput = await screen.findByLabelText(/project title/i);
    await userEvent.clear(titleInput);
    await userEvent.click(screen.getByRole('button', { name: /save changes/i }));
    expect(await screen.findByText('Project title is required')).toBeInTheDocument();
  });

  it('shows update error and navigates on cancel', async () => {
    (api.projects.getProject as any).mockResolvedValue(sampleProject);
    (api.projects.updateProject as any).mockRejectedValue(new Error('Save failed'));
    render(<EditProject />);

    await screen.findByLabelText(/project title/i);
    await userEvent.click(screen.getByRole('button', { name: /save changes/i }));
    expect(await screen.findByText('Save failed')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /cancel/i }));
    expect(mockNavigate).toHaveBeenCalledWith('/projects/123');
  });

  it('retries and navigates home from the critical load error screen', async () => {
    const reloadSpy = vi.fn();
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { ...window.location, reload: reloadSpy },
    });
    (api.projects.getProject as any).mockRejectedValue(new Error('Failed to fetch project data'));
    render(<EditProject />);

    await screen.findByText('Failed to Load Project');
    await userEvent.click(screen.getByRole('button', { name: /try again/i }));
    expect(reloadSpy).toHaveBeenCalled();

    await userEvent.click(screen.getByRole('button', { name: /go back/i }));
    expect(mockNavigate).toHaveBeenCalledWith('/');
  });

  it('updates description and shows saving state while submit is in flight', async () => {
    (api.projects.getProject as any).mockResolvedValue(sampleProject);
    (api.projects.updateProject as any).mockImplementation(() => new Promise(() => {}));
    render(<EditProject />);

    const description = await screen.findByLabelText(/description/i);
    await userEvent.clear(description);
    await userEvent.type(description, 'Updated description');
    await userEvent.click(screen.getByRole('button', { name: /save changes/i }));

    expect(await screen.findByText('Saving...')).toBeInTheDocument();
    expect((description as HTMLTextAreaElement).value).toBe('Updated description');
  });
}); 