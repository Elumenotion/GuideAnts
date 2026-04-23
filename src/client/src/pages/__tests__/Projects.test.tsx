import React from 'react';
import '@testing-library/jest-dom';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '../../test/test-utils';
import userEvent from '@testing-library/user-event';
import Projects from '../Projects';
import { api } from '../../services/api';
import { waitFor } from '@testing-library/react';

// Mock react-router navigate
const mockNavigate = vi.fn();
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual('react-router-dom');
  return {
    ...actual,
    useNavigate: () => mockNavigate,
  };
});

// Mock api layer
vi.mock('../../services/api', () => ({
  api: {
    auth: {
      getCurrentUser: vi.fn().mockResolvedValue({ email: 'user@example.com' }),
    },
    projects: {
      getUserProjects: vi.fn(),
      copyProject: vi.fn(),
      deleteProject: vi.fn(),
    },
  },
}));

describe('Projects page', () => {
  const sampleProjects = [
    { id: '1', title: 'Proj 1', description: 'Desc 1', created: 'now', userRoles: [] },
    { id: '2', title: 'Proj 2', description: 'Desc 2', created: 'now', userRoles: [] },
  ];

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows loading spinner while fetching', async () => {
    (api.projects.getUserProjects as any).mockResolvedValue(sampleProjects);

    render(<Projects />);
    expect(screen.getByText('Loading projects...')).toBeInTheDocument();

    // wait for next tick
    await Promise.resolve();
  });

  it('renders project list after successful fetch', async () => {
    (api.projects.getUserProjects as any).mockResolvedValue(sampleProjects);
    render(<Projects />);
    expect(await screen.findByText('Proj 1')).toBeInTheDocument();
    expect(screen.getByText('Proj 2')).toBeInTheDocument();
  });

  it('displays error message on fetch failure', async () => {
    (api.projects.getUserProjects as any).mockRejectedValue(new Error('Boom'));
    render(<Projects />);
    expect(await screen.findByText('Boom')).toBeInTheDocument();
  });

  it('navigates to new project page when button clicked', async () => {
    (api.projects.getUserProjects as any).mockResolvedValue(sampleProjects);
    render(<Projects />);
    await screen.findByText('New Project');
    await userEvent.click(screen.getByText('New Project'));
    expect(mockNavigate).toHaveBeenCalledWith('/new-project');
  });

  it('navigates to project details when card clicked', async () => {
    (api.projects.getUserProjects as any).mockResolvedValue(sampleProjects);
    render(<Projects />);
    await screen.findByText('Proj 2');
    await userEvent.click(screen.getByText('Proj 2'));
    expect(mockNavigate).toHaveBeenCalledWith('/projects/2');
  });

  it('shows owner actions in project menu when current user is owner', async () => {
    const ownerProjects = [
      {
        id: '10',
        title: 'Owned Project',
        description: 'Desc',
        created: 'now',
        userRoles: [
          {
            userId: 'u1',
            userEmail: 'user@example.com',
            userName: 'Owner',
            roleName: 'Owner',
          },
        ],
      },
    ];

    (api.projects.getUserProjects as any).mockResolvedValue(ownerProjects);

    render(<Projects />);

    // Wait for project to appear then open its options menu
    await screen.findByText('Owned Project');
    const menuButton = screen.getByText('⋯');
    await userEvent.click(menuButton);

    expect(screen.getByText('Edit')).toBeInTheDocument();
    expect(screen.getByText('Copy')).toBeInTheDocument();
    expect(screen.getByText('Delete')).toBeInTheDocument();
  });

  it('hides owner actions for projects the user does not own', async () => {
    const otherProjects = [
      {
        id: '20',
        title: 'Not Mine',
        description: 'Desc',
        created: 'now',
        userRoles: [
          {
            userId: 'u2',
            userEmail: 'someone@else.com',
            userName: 'Stranger',
            roleName: 'Owner',
          },
        ],
      },
    ];

    (api.projects.getUserProjects as any).mockResolvedValue(otherProjects);

    render(<Projects />);

    // Wait for project then open menu
    await screen.findByText('Not Mine');
    await userEvent.click(screen.getByText('⋯'));

    expect(screen.queryByText('Edit')).not.toBeInTheDocument();
    expect(screen.queryByText('Copy')).not.toBeInTheDocument();
    expect(screen.queryByText('Delete')).not.toBeInTheDocument();
  });

  it('invokes navigate when Edit action clicked', async () => {
    const ownerProjects = [
      {
        id: '30',
        title: 'Edit Me',
        description: 'Desc',
        created: 'now',
        userRoles: [
          {
            userId: 'u1',
            userEmail: 'user@example.com',
            userName: 'Owner',
            roleName: 'Owner',
          },
        ],
      },
    ];

    (api.projects.getUserProjects as any).mockResolvedValue(ownerProjects);

    render(<Projects />);
    await screen.findByText('Edit Me');
    await userEvent.click(screen.getByText('⋯'));
    await userEvent.click(screen.getByText('Edit'));

    expect(mockNavigate).toHaveBeenCalledWith('/projects/30/edit');
  });

  it('copies a project and refetches list', async () => {
    const ownerProjects = [
      {
        id: '40',
        title: 'Copy Me',
        description: 'Desc',
        created: 'now',
        userRoles: [
          {
            userId: 'u1',
            userEmail: 'user@example.com',
            userName: 'Owner',
            roleName: 'Owner',
          },
        ],
      },
    ];

    (api.projects.getUserProjects as any).mockResolvedValueOnce(ownerProjects).mockResolvedValueOnce(ownerProjects);
    (api.projects.copyProject as any).mockResolvedValue({});

    render(<Projects />);
    await screen.findByText('Copy Me');
    await userEvent.click(screen.getByText('⋯'));
    await userEvent.click(screen.getByText('Copy'));

    expect(api.projects.copyProject).toHaveBeenCalledWith('40');
    // getUserProjects should be called twice: initial load + refresh after copy
    expect((api.projects.getUserProjects as any).mock.calls.length).toBe(2);
  });

  it('deletes a project after confirmation and removes it from the list', async () => {
    const ownerProjects = [
      {
        id: '50',
        title: 'Delete Me',
        description: 'Desc',
        created: 'now',
        userRoles: [
          {
            userId: 'u1',
            userEmail: 'user@example.com',
            userName: 'Owner',
            roleName: 'Owner',
          },
        ],
      },
    ];

    (api.projects.getUserProjects as any).mockResolvedValue(ownerProjects);
    (api.projects.deleteProject as any).mockResolvedValue({});

    render(<Projects />);
    await screen.findByText('Delete Me');
    await userEvent.click(screen.getByText('⋯'));
    await userEvent.click(screen.getByText('Delete'));

    // Click confirm in dialog
    const confirmButton = await screen.findByTestId('confirm');
    await userEvent.click(confirmButton);

    await waitFor(() => {
      expect(api.projects.deleteProject).toHaveBeenCalledWith('50');
    });

    // Success toast appears
    await screen.findByText('Project deleted successfully');

    expect(screen.queryByText('Delete Me')).not.toBeInTheDocument();
  });
}); 