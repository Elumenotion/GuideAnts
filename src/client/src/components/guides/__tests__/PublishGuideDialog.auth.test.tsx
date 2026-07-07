import { beforeEach, describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { PublishGuideDialog } from '../PublishGuideDialog';
import type { PublishedGuideDto } from '../../../types/guides';

vi.mock('../../../services/api', () => ({
  api: {
    guides: {
      guides: {
        validateFriendlyName: vi.fn().mockResolvedValue({ available: true }),
        generateApiKey: vi.fn(),
        removeApiKey: vi.fn(),
        updatePublished: vi.fn(),
        downloadClaudeSkill: vi.fn(),
        enableMcpAccess: vi.fn(),
      },
    },
  },
}));

import { api } from '../../../services/api';

function createPublishedGuide(overrides?: Partial<PublishedGuideDto>): PublishedGuideDto {
  return {
    id: 'pub-1',
    guideId: 'guide-1',
    guideName: 'Guide',
    notebookId: 'notebook-1',
    projectId: 'project-1',
    created: '2026-06-22T00:00:00Z',
    active: true,
    ...overrides,
  };
}

describe('PublishGuideDialog auth and MCP tabs', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders app identity guidance on the auth tab', async () => {
    const user = userEvent.setup();

    render(
      <PublishGuideDialog
        guideName="Guide"
        guideId="guide-1"
        publishedGuide={createPublishedGuide({ authMode: 'AppIdentity' })}
        onUpdate={vi.fn()}
        onCancel={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Auth' }));
    expect(screen.getByText(/GuideAnts app identity/i)).toBeInTheDocument();
  });

  it('edits webhook auth fields when API key auth is unavailable', async () => {
    const user = userEvent.setup();

    render(
      <PublishGuideDialog
        guideName="Guide"
        guideId="guide-1"
        publishedGuide={createPublishedGuide({ hasApiKey: false })}
        onUpdate={vi.fn()}
        onCancel={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Auth' }));
    const webhookInput = screen.getByLabelText(/Webhook URL/i);
    await user.type(webhookInput, 'https://example.com/auth');
    expect(webhookInput).toHaveValue('https://example.com/auth');
  });

  it('generates an API key from the auth tab', async () => {
    const user = userEvent.setup();
    vi.mocked(api.guides.guides.generateApiKey).mockResolvedValue({ apiKey: 'gak_from_auth' } as never);

    render(
      <PublishGuideDialog
        guideName="Guide"
        guideId="guide-1"
        publishedGuide={createPublishedGuide({ hasApiKey: false })}
        onUpdate={vi.fn()}
        onCancel={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Auth' }));
    await user.click(screen.getByRole('button', { name: 'Generate API Key' }));

    expect(await screen.findByText('gak_from_auth')).toBeInTheDocument();
  });

  it('shows MCP endpoint details when MCP is enabled', async () => {
    const user = userEvent.setup();

    render(
      <PublishGuideDialog
        guideName="Guide"
        guideId="guide-1"
        publishedGuide={createPublishedGuide({ mcpEnabled: true, hasApiKey: true })}
        onUpdate={vi.fn()}
        onCancel={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: /MCP and Skills/i }));
    expect(screen.getByText(/MCP endpoint/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Copy' })).toBeInTheDocument();
  });

  it('enables MCP access and updates the published guide', async () => {
    const user = userEvent.setup();
    const onPublishedGuideUpdated = vi.fn();
    vi.mocked(api.guides.guides.generateApiKey).mockResolvedValue({ apiKey: 'gak_mcp' } as never);
    vi.mocked(api.guides.guides.updatePublished).mockResolvedValue(
      createPublishedGuide({ mcpEnabled: true, hasApiKey: true }) as never,
    );

    render(
      <PublishGuideDialog
        guideName="Guide"
        guideId="guide-1"
        publishedGuide={createPublishedGuide({ mcpEnabled: false, hasApiKey: false, mcpPersisted: false })}
        onUpdate={vi.fn()}
        onPublishedGuideUpdated={onPublishedGuideUpdated}
        onCancel={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: /MCP and Skills/i }));
    await user.click(screen.getByRole('button', { name: /Generate API Key & Enable MCP/i }));

    await waitFor(() => {
      expect(api.guides.guides.generateApiKey).toHaveBeenCalled();
      expect(api.guides.guides.updatePublished).toHaveBeenCalled();
      expect(onPublishedGuideUpdated).toHaveBeenCalled();
    });
  });

  it('reactivates an inactive published guide', async () => {
    const user = userEvent.setup();
    const onReactivate = vi.fn();

    render(
      <PublishGuideDialog
        guideName="Guide"
        guideId="guide-1"
        publishedGuide={createPublishedGuide({ active: false })}
        onUpdate={vi.fn()}
        onReactivate={onReactivate}
        onCancel={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Reactivate Guide' }));
    expect(onReactivate).toHaveBeenCalled();
  });

  it('disables save when friendly name conflicts with API key auth', async () => {
    const user = userEvent.setup();
    const onUpdate = vi.fn();

    render(
      <PublishGuideDialog
        guideName="Guide"
        guideId="guide-1"
        publishedGuide={createPublishedGuide({ hasApiKey: true, friendlyName: '' })}
        onUpdate={onUpdate}
        onCancel={vi.fn()}
      />,
    );

    await user.type(screen.getByLabelText(/Public URL/i), 'public-guide');

    const saveButton = screen.getByRole('button', { name: 'Save Changes' });
    expect(saveButton).toBeDisabled();
    await user.click(saveButton);
    expect(onUpdate).not.toHaveBeenCalled();
  });
});
