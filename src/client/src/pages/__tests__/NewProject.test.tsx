import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '../../test/test-utils';
import userEvent from '@testing-library/user-event';
import NewProject from '../NewProject';
import { api } from '../../services/api';

// Mock navigate from react-router
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
    projects: {
      create: vi.fn(),
    },
  },
}));

describe('NewProject page', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows validation error when title is empty', async () => {
    render(<NewProject />);
    await userEvent.click(screen.getByRole('button', { name: /create project/i }));
    expect(await screen.findByText('Project title is required')).toBeInTheDocument();
    expect(api.projects.create).not.toHaveBeenCalled();
  });

  it('creates project and navigates on success', async () => {
    (api.projects.create as any).mockResolvedValue({});
    render(<NewProject />);

    await userEvent.type(screen.getByLabelText(/project title/i), 'My Project');
    await userEvent.type(screen.getByLabelText(/description/i), 'Desc');
    await userEvent.click(screen.getByRole('button', { name: /create project/i }));

    expect(api.projects.create).toHaveBeenCalledWith({ title: 'My Project', description: 'Desc' });
    // wait for async navigate after promise resolves
    await Promise.resolve();
    expect(mockNavigate).toHaveBeenCalledWith('/');
  });

  it('shows error message when API call fails', async () => {
    (api.projects.create as any).mockRejectedValue(new Error('Failed'));
    render(<NewProject />);

    await userEvent.type(screen.getByLabelText(/project title/i), 'My Project');
    await userEvent.click(screen.getByRole('button', { name: /create project/i }));

    expect(await screen.findByText('Failed')).toBeInTheDocument();
  });
}); 