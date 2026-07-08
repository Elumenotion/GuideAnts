import { beforeEach, describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
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
      },
    },
  },
}));

vi.mock('../../../utils/claudeSkillPackDownload', () => ({
  patchClaudeSkillPackEnv: vi.fn(async (blob: Blob) => blob),
  sanitizeClaudeSkillDownloadFileName: vi.fn((name: string) => name.replace(/\s+/g, '-')),
  triggerBlobDownload: vi.fn(),
}));

import { api } from '../../../services/api';
import { triggerBlobDownload } from '../../../utils/claudeSkillPackDownload';

function createPublishedGuide(overrides?: Partial<PublishedGuideDto>): PublishedGuideDto {
  return {
    id: 'pub-1',
    guideId: 'guide-1',
    guideName: 'Guide',
    notebookId: 'notebook-1',
    projectId: 'project-1',
    created: '2026-06-22T00:00:00Z',
    active: true,
    mcpEnabled: true,
    mcpPersisted: true,
    hasApiKey: true,
    ...overrides,
  };
}

describe('PublishGuideDialog MCP downloads and keyboard shortcuts', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('downloads the Claude skill pack when MCP is persisted', async () => {
    const user = userEvent.setup();
    vi.mocked(api.guides.guides.downloadClaudeSkill).mockResolvedValue(
      new Blob(['zip'], { type: 'application/zip' }),
    );

    render(
      <PublishGuideDialog
        guideName="Guide"
        guideId="guide-1"
        publishedGuide={createPublishedGuide({ friendlyName: 'public-guide' })}
        onUpdate={vi.fn()}
        onCancel={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: /MCP and Skills/i }));
    await user.click(screen.getByRole('button', { name: /Download Agent Skill/i }));

    await waitFor(() => {
      expect(api.guides.guides.downloadClaudeSkill).toHaveBeenCalledWith('guide-1', 'pub-1');
      expect(triggerBlobDownload).toHaveBeenCalledWith(
        expect.any(Blob),
        'public-guide-claude-skill.zip',
      );
    });
  });

  it('cancels the dialog when Escape is pressed', async () => {
    const onCancel = vi.fn();

    render(
      <PublishGuideDialog
        guideName="Guide"
        guideId="guide-1"
        publishedGuide={createPublishedGuide()}
        onUpdate={vi.fn()}
        onCancel={onCancel}
      />,
    );

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onCancel).toHaveBeenCalled();
  });

  it('submits save changes on Enter outside textareas and buttons', async () => {
    const onUpdate = vi.fn();

    render(
      <PublishGuideDialog
        guideName="Guide"
        guideId="guide-1"
        publishedGuide={createPublishedGuide({ displayMode: 'full' })}
        onUpdate={onUpdate}
        onCancel={vi.fn()}
      />,
    );

    fireEvent.keyDown(window, { key: 'Enter' });

    await waitFor(() => {
      expect(onUpdate).toHaveBeenCalled();
    });
  });

  it('disables MCP when the API key is removed in edit mode', async () => {
    const user = userEvent.setup();
    const onPublishedGuideUpdated = vi.fn();
    vi.mocked(api.guides.guides.updatePublished).mockResolvedValue(
      createPublishedGuide({ mcpEnabled: false, mcpPersisted: false, hasApiKey: false }) as never,
    );

    render(
      <PublishGuideDialog
        guideName="Guide"
        guideId="guide-1"
        publishedGuide={createPublishedGuide()}
        onUpdate={vi.fn()}
        onPublishedGuideUpdated={onPublishedGuideUpdated}
        onCancel={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Auth' }));
    vi.mocked(api.guides.guides.removeApiKey).mockResolvedValue(undefined as never);
    await user.click(screen.getByRole('button', { name: 'Remove Key' }));
    await user.click(screen.getByRole('button', { name: 'Confirm Remove' }));

    await waitFor(() => {
      expect(api.guides.guides.removeApiKey).toHaveBeenCalledWith('guide-1', 'pub-1');
      expect(api.guides.guides.updatePublished).toHaveBeenCalledWith(
        'guide-1',
        'pub-1',
        expect.objectContaining({ mcpEnabled: false }),
      );
      expect(onPublishedGuideUpdated).toHaveBeenCalled();
    });
  });
});
