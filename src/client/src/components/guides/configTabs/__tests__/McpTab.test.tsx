import { describe, expect, it, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { McpTab } from '../McpTab';

vi.mock('../PublishedGuideApiKeySection', () => ({
  PublishedGuideApiKeySection: () => <div data-testid="api-key-section">api-key-section</div>,
}));

const baseProps = {
  mcpEnabled: false,
  setMcpEnabled: vi.fn(),
  mcpDescription: 'Helps with architecture reviews.',
  setMcpDescription: vi.fn(),
  hasApiKey: true,
  sessionApiKey: 'gak_test',
  guideId: 'guide-1',
  publishedGuideId: 'pub-1',
  authWebhookUrl: '',
  mcpPersisted: true,
  onApiKeyChange: vi.fn(),
  onSessionApiKeyChange: vi.fn(),
};

describe('McpTab', () => {
  const writeText = vi.fn().mockResolvedValue(undefined);

  beforeEach(() => {
    vi.clearAllMocks();
    writeText.mockClear();
    vi.stubGlobal('navigator', {
      ...navigator,
      clipboard: { writeText },
    });
  });

  it('edits description and enables MCP endpoint when configured', async () => {
    const user = userEvent.setup();
    const setMcpDescription = vi.fn();
    const setMcpEnabled = vi.fn();

    render(
      <McpTab
        {...baseProps}
        mcpEnabled={false}
        setMcpDescription={setMcpDescription}
        setMcpEnabled={setMcpEnabled}
      />,
    );

    await user.type(screen.getByLabelText(/Guide Description for MCP Clients/i), '!');
    expect(setMcpDescription).toHaveBeenCalled();

    await user.click(screen.getAllByRole('checkbox').at(-1)!);
    expect(setMcpEnabled).toHaveBeenCalledWith(true);
  });

  it('copies endpoint URL when MCP is enabled', async () => {
    const user = userEvent.setup();

    render(<McpTab {...baseProps} mcpEnabled />);

    await user.click(screen.getByRole('button', { name: 'Copy' }));
    expect(await screen.findByRole('button', { name: 'Copied!' })).toBeInTheDocument();
  });

  it('runs quick setup and surfaces enable failures', async () => {
    const user = userEvent.setup();
    const onEnableMcpAccess = vi.fn().mockRejectedValue(new Error('Setup failed'));

    render(
      <McpTab
        {...baseProps}
        mcpPersisted={false}
        onEnableMcpAccess={onEnableMcpAccess}
      />,
    );

    await user.click(screen.getByRole('button', { name: /Enable MCP Access/i }));
    expect(await screen.findByText('Setup failed')).toBeInTheDocument();
  });

  it('downloads agent skill and warns when session key is hidden', async () => {
    const user = userEvent.setup();
    const onDownloadClaudeSkill = vi.fn().mockResolvedValue(undefined);

    render(
      <McpTab
        {...baseProps}
        mcpEnabled
        sessionApiKey={null}
        onDownloadClaudeSkill={onDownloadClaudeSkill}
      />,
    );

    await user.click(screen.getByRole('button', { name: /Download Agent Skill/i }));
    await waitFor(() => expect(onDownloadClaudeSkill).toHaveBeenCalled());
    expect(screen.getByText(/placeholder for `.env`/i)).toBeInTheDocument();
  });

  it('shows publish-first guidance when guide is unpublished', () => {
    render(<McpTab {...baseProps} publishedGuideId={undefined} />);

    expect(screen.getByText(/Publish the guide first/i)).toBeInTheDocument();
    expect(screen.queryByRole('checkbox', { name: /Enable MCP Endpoint/i })).not.toBeInTheDocument();
  });
});
