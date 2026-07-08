import { beforeEach, describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '@testing-library/react';
import { useState, type ComponentProps } from 'react';
import '@testing-library/jest-dom';
import { PublishedGuideApiKeySection } from '../PublishedGuideApiKeySection';

vi.mock('../../../../services/api', () => ({
  api: {
    guides: {
      guides: {
        generateApiKey: vi.fn(),
        removeApiKey: vi.fn(),
      },
    },
  },
}));

import { api } from '../../../../services/api';

function renderApiKeySection(overrides: Partial<ComponentProps<typeof PublishedGuideApiKeySection>> = {}) {
  const {
    hasApiKey: initialHasApiKey = false,
    sessionApiKey: initialSessionApiKey = null,
    onApiKeyChange: _ignoredKeyChange,
    onSessionApiKeyChange: _ignoredSessionChange,
    ...rest
  } = overrides;

  function Harness() {
    const [hasApiKey, setHasApiKey] = useState(initialHasApiKey);
    const [sessionApiKey, setSessionApiKey] = useState<string | null>(initialSessionApiKey);

    return (
      <PublishedGuideApiKeySection
        context="auth"
        guideId="guide-1"
        publishedGuideId="pub-1"
        authWebhookUrl=""
        hasApiKey={hasApiKey}
        sessionApiKey={sessionApiKey}
        onApiKeyChange={setHasApiKey}
        onSessionApiKeyChange={setSessionApiKey}
        {...rest}
      />
    );
  }

  return render(<Harness />);
}

const baseProps = {
  context: 'auth' as const,
  hasApiKey: false,
  sessionApiKey: null,
  guideId: 'guide-1',
  publishedGuideId: 'pub-1',
  authWebhookUrl: '',
  onApiKeyChange: vi.fn(),
  onSessionApiKeyChange: vi.fn(),
};

describe('PublishedGuideApiKeySection', () => {
  const writeText = vi.fn().mockResolvedValue(undefined);

  beforeEach(() => {
    vi.clearAllMocks();
    writeText.mockClear();
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
  });

  it('renders auth context copy and configured badge when key exists without session plaintext', () => {
    render(
      <PublishedGuideApiKeySection
        {...baseProps}
        context="auth"
        hasApiKey
      />,
    );

    expect(screen.getByText(/Wire API clients/i)).toBeInTheDocument();
    expect(screen.getByText('Configured')).toBeInTheDocument();
    expect(screen.getAllByText(/MCP and Skills/i).length).toBeGreaterThan(0);
  });

  it('renders mcp context copy', () => {
    render(<PublishedGuideApiKeySection {...baseProps} context="mcp" />);

    expect(screen.getByText(/wiring MCP clients/i)).toBeInTheDocument();
    expect(screen.getAllByText(/^Auth$/i).length).toBeGreaterThan(0);
  });

  it('blocks API key actions when webhook URL is configured', () => {
    render(
      <PublishedGuideApiKeySection
        {...baseProps}
        authWebhookUrl="https://example.com/auth"
      />,
    );

    expect(screen.getByText(/Remove the webhook URL/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Generate API Key' })).not.toBeInTheDocument();
  });

  it('generates an API key in edit mode', async () => {
    const user = userEvent.setup();
    vi.mocked(api.guides.guides.generateApiKey).mockResolvedValue({ apiKey: 'gak_new_key' } as never);

    renderApiKeySection();

    await user.click(screen.getByRole('button', { name: 'Generate API Key' }));

    await waitFor(() => {
      expect(api.guides.guides.generateApiKey).toHaveBeenCalledWith('guide-1', 'pub-1');
      expect(screen.getByText('gak_new_key')).toBeInTheDocument();
    });
  });

  it('copies the session API key to clipboard', async () => {
    const user = userEvent.setup();
    const copyToClipboard = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue();

    renderApiKeySection({ hasApiKey: true, sessionApiKey: 'gak_copy_me' });

    await user.click(screen.getByRole('button', { name: 'Copy' }));
    expect(copyToClipboard).toHaveBeenCalledWith('gak_copy_me');
    expect(await screen.findByRole('button', { name: 'Copied!' })).toBeInTheDocument();

    copyToClipboard.mockRestore();
  });

  it('regenerates an API key after confirmation', async () => {
    const user = userEvent.setup();
    vi.mocked(api.guides.guides.generateApiKey).mockResolvedValue({ apiKey: 'gak_regenerated' } as never);

    renderApiKeySection({ hasApiKey: true });

    await user.click(screen.getByRole('button', { name: 'Regenerate Key' }));
    await user.click(screen.getByRole('button', { name: 'Confirm Regenerate' }));

    await waitFor(() => {
      expect(api.guides.guides.generateApiKey).toHaveBeenCalled();
      expect(screen.getByText('gak_regenerated')).toBeInTheDocument();
    });
  });

  it('removes an API key after confirmation', async () => {
    const user = userEvent.setup();
    const onApiKeyChange = vi.fn();
    const onSessionApiKeyChange = vi.fn();
    vi.mocked(api.guides.guides.removeApiKey).mockResolvedValue(undefined as never);

    render(
      <PublishedGuideApiKeySection
        {...baseProps}
        hasApiKey
        onApiKeyChange={onApiKeyChange}
        onSessionApiKeyChange={onSessionApiKeyChange}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Remove Key' }));
    await user.click(screen.getByRole('button', { name: 'Confirm Remove' }));

    await waitFor(() => {
      expect(api.guides.guides.removeApiKey).toHaveBeenCalledWith('guide-1', 'pub-1');
      expect(onSessionApiKeyChange).toHaveBeenCalledWith(null);
      expect(onApiKeyChange).toHaveBeenCalledWith(false);
    });
  });

  it('surfaces generate and remove errors', async () => {
    const user = userEvent.setup();
    vi.mocked(api.guides.guides.generateApiKey).mockRejectedValue(new Error('Generate failed'));
    vi.mocked(api.guides.guides.removeApiKey).mockRejectedValue('remove failed');

    const { rerender } = render(<PublishedGuideApiKeySection {...baseProps} />);
    await user.click(screen.getByRole('button', { name: 'Generate API Key' }));
    expect(await screen.findByText('Generate failed')).toBeInTheDocument();

    rerender(<PublishedGuideApiKeySection {...baseProps} hasApiKey />);
    await user.click(screen.getByRole('button', { name: 'Remove Key' }));
    await user.click(screen.getByRole('button', { name: 'Confirm Remove' }));
    expect(await screen.findByText('Failed to remove API key')).toBeInTheDocument();
  });

  it('shows publish-first guidance when not in edit mode', () => {
    render(
      <PublishedGuideApiKeySection
        {...baseProps}
        publishedGuideId={undefined}
      />,
    );

    expect(screen.getByText(/available after publishing/i)).toBeInTheDocument();
  });
});
