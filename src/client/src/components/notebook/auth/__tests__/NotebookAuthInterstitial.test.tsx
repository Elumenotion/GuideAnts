import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { render } from '@/test/test-utils';

import { NotebookAuthInterstitial } from '../NotebookAuthInterstitial';
import { api } from '../../../../services/api';
import { beginOAuthConnection } from '../../../../utils/notebookAuth';
import type { NotebookTemplateDto } from '../../../../types/project';

const mockNavigate = vi.fn();
const mockShowToast = vi.fn();

vi.mock('react-router', async () => {
  const actual = await vi.importActual<typeof import('react-router')>('react-router');
  return {
    ...actual,
    useNavigate: () => mockNavigate,
  };
});

vi.mock('../../../common/Toast', async () => {
  const actual = await vi.importActual<typeof import('../../../common/Toast')>('../../../common/Toast');
  return {
    ...actual,
    useToast: () => ({ showToast: mockShowToast }),
  };
});

vi.mock('../../../../services/api', () => ({
  api: {
    projects: {
      externalAuth: {
        oauth: {
          status: vi.fn(),
        },
      },
    },
  },
}));

vi.mock('../../../../utils/notebookAuth', () => ({
  beginOAuthConnection: vi.fn(),
}));

const baseTemplate: NotebookTemplateDto = {
  id: 'guide-1',
  templateName: 'Test Guide',
  description: 'A test guide',
  avatarUrl: '/api/avatars/guide.png',
  authProviders: [
    {
      id: 'graph.microsoft.com',
      authType: 'OAuth',
      userConfigPolicy: 'required',
      clientId: 'ms-client',
      scopes: ['Mail.Read', 'Calendars.Read'],
    },
    {
      id: 'api.github.com',
      authType: 'OAuth',
      userConfigPolicy: 'optional',
      clientId: 'gh-client',
      scopes: ['repo'],
    },
    {
      id: 'api-key-provider',
      authType: 'ApiKey',
      userConfigPolicy: 'required',
    },
  ],
} as NotebookTemplateDto;

const defaultProps = {
  projectId: 'proj-1',
  notebookId: 'nb-1',
  notebookTitle: 'My Notebook',
  template: baseTemplate,
  onAuthComplete: vi.fn(),
};

describe('NotebookAuthInterstitial', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.projects.externalAuth.oauth.status).mockImplementation(async (_projectId, providerId) => ({
      connected: providerId === 'api.github.com',
    }));
  });

  it('renders providers that need authentication', async () => {
    render(<NotebookAuthInterstitial {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Connect to External Services')).toBeInTheDocument();
    });

    expect(screen.getByText('Microsoft 365')).toBeInTheDocument();
    expect(screen.getByText(/Access your emails, calendar, notes/)).toBeInTheDocument();
    expect(screen.getByText(/Permissions requested: Mail.Read, Calendars.Read/)).toBeInTheDocument();
    expect(screen.queryByText('GitHub')).not.toBeInTheDocument();
  });

  it('calls onAuthComplete when all OAuth providers are connected', async () => {
    vi.mocked(api.projects.externalAuth.oauth.status).mockResolvedValue({ connected: true });

    render(<NotebookAuthInterstitial {...defaultProps} />);

    await waitFor(() => {
      expect(defaultProps.onAuthComplete).toHaveBeenCalled();
    });
  });

  it('renders nothing once all providers are connected', async () => {
    vi.mocked(api.projects.externalAuth.oauth.status).mockResolvedValue({ connected: true });

    const { container } = render(<NotebookAuthInterstitial {...defaultProps} />);

    await waitFor(() => {
      expect(defaultProps.onAuthComplete).toHaveBeenCalled();
    });
    expect(container.firstChild).toBeNull();
  });

  it('starts OAuth flow when Sign In is clicked', async () => {
    const user = userEvent.setup();
    vi.mocked(beginOAuthConnection).mockResolvedValue(undefined);

    render(<NotebookAuthInterstitial {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Microsoft 365')).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: 'Sign In' }));

    expect(beginOAuthConnection).toHaveBeenCalledWith(
      'proj-1',
      expect.objectContaining({ id: 'graph.microsoft.com' }),
      '/projects/proj-1/notebooks/nb-1'
    );
  });

  it('shows error toast when OAuth initiation fails', async () => {
    const user = userEvent.setup();
    vi.mocked(beginOAuthConnection).mockRejectedValue(new Error('OAuth failed'));

    render(<NotebookAuthInterstitial {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Microsoft 365')).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: 'Sign In' }));

    await waitFor(() => {
      expect(mockShowToast).toHaveBeenCalledWith(
        expect.objectContaining({
          type: 'error',
          title: 'Authentication Error',
        })
      );
    });
  });

  it('navigates back to project when back link is clicked', async () => {
    render(<NotebookAuthInterstitial {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('← Back to Project')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('← Back to Project'));
    expect(mockNavigate).toHaveBeenCalledWith('/projects/proj-1');
  });

  it('treats OAuth status check failures as disconnected', async () => {
    vi.mocked(api.projects.externalAuth.oauth.status).mockRejectedValue(new Error('network'));

    render(<NotebookAuthInterstitial {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText('Microsoft 365')).toBeInTheDocument();
      expect(screen.getByText('GitHub')).toBeInTheDocument();
    });
  });

  it('shows guide avatar and notebook title', async () => {
    render(<NotebookAuthInterstitial {...defaultProps} />);

    await waitFor(() => {
      expect(screen.getByText(/My Notebook/)).toBeInTheDocument();
      expect(screen.getByText(/Test Guide/)).toBeInTheDocument();
    });

    const avatar = screen.getByRole('img', { name: 'Test Guide' });
    expect(avatar).toHaveAttribute('src', expect.stringContaining('projectId=proj-1'));
  });
});
