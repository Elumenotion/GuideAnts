import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { PublishGuideDialog } from '../PublishGuideDialog';
import type { PublishedGuideDto } from '../../../types/guides';

vi.mock('../../../services/api', () => ({
  api: {
    guides: {
      guides: {
        validateFriendlyName: vi.fn(),
        generateApiKey: vi.fn(),
        removeApiKey: vi.fn(),
        updatePublished: vi.fn(),
        downloadClaudeSkill: vi.fn(),
      },
    },
  },
}));

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

describe('PublishGuideDialog APIs tab', () => {
  it('shows absolute base URL and per-service SDK examples when enabled without API key', () => {
    render(
      <PublishGuideDialog
        guideName="Guide"
        guideId="guide-1"
        publishedGuide={createPublishedGuide({
          authValidationWebhookUrl: 'https://example.com/auth',
          hasApiKey: false,
          wireApiConfig: {
            enabled: true,
            aliasMap: { guide: 'guide' },
          },
        })}
        onUpdate={vi.fn()}
        onCancel={vi.fn()}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: 'APIs' }));

    expect(screen.getByText(/SDK clients work best with API key authentication/i)).toBeInTheDocument();
    expect(screen.getByText('OpenAI JavaScript SDK')).toBeInTheDocument();
    expect(screen.getByText('OpenAI Python SDK')).toBeInTheDocument();

    const openAiBaseUrlCodeBlocks = screen.getAllByText((content, element) => {
      if (element?.tagName.toLowerCase() !== 'code') {
        return false;
      }

      return /^http:\/\/localhost(?::\d+)?\/api\/published\/openai\/pub-1\/v1$/.test(content);
    });
    expect(openAiBaseUrlCodeBlocks.length).toBeGreaterThan(0);

    const anthropicBaseUrlCodeBlocks = screen.getAllByText((content, element) => {
      if (element?.tagName.toLowerCase() !== 'code') {
        return false;
      }

      return /^http:\/\/localhost(?::\d+)?\/api\/published\/anthropic\/pub-1\/v1$/.test(content);
    });
    expect(anthropicBaseUrlCodeBlocks.length).toBeGreaterThan(0);

    for (const tabLabel of ['Models', 'Chat', 'Responses', 'Messages', 'Embeddings', 'Image', 'Transcription', 'Speech']) {
      expect(screen.getByRole('button', { name: tabLabel })).toBeInTheDocument();
    }

    fireEvent.click(screen.getByRole('button', { name: 'Embeddings' }));
    expect(screen.getAllByText(/client\.embeddings\.create/).length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole('button', { name: 'Responses' }));
    expect(screen.getAllByText(/client\.responses\.create/).length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole('button', { name: 'Models' }));
    expect(screen.getAllByText(/client\.models\.list/).length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole('button', { name: 'Messages' }));
    expect(screen.getByText('Anthropic JavaScript SDK')).toBeInTheDocument();
    expect(screen.getByText('Anthropic Python SDK')).toBeInTheDocument();
    expect(screen.getAllByText(/client\.messages\.create/).length).toBeGreaterThan(0);
  });

  it('round-trips wireApiConfig in update payload', () => {
    const onUpdate = vi.fn();
    render(
      <PublishGuideDialog
        guideName="Guide"
        guideId="guide-1"
        publishedGuide={createPublishedGuide({
          wireApiConfig: {
            enabled: false,
          },
        })}
        onUpdate={onUpdate}
        onCancel={vi.fn()}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: 'APIs' }));
    fireEvent.click(screen.getByLabelText('Enable Published API Endpoints'));
    fireEvent.change(screen.getByLabelText('Guide model alias'), {
      target: { value: 'guide-prod' },
    });
    fireEvent.change(screen.getByLabelText('Embeddings Max Request Bytes'), {
      target: { value: '16384' },
    });
    fireEvent.click(screen.getByLabelText('Responses enabled'));

    fireEvent.click(screen.getByRole('button', { name: 'Save Changes' }));

    expect(onUpdate).toHaveBeenCalledTimes(1);
    expect(onUpdate).toHaveBeenCalledWith(
      expect.objectContaining({
        wireApiConfig: expect.objectContaining({
          enabled: true,
          endpointFlags: expect.objectContaining({
            responses: false,
            chatCompletions: true,
            messages: true,
          }),
          aliasMap: expect.objectContaining({
            guide: 'guide-prod',
          }),
          maxRequestSizes: expect.objectContaining({
            embeddingsBytes: 16384,
          }),
        }),
      })
    );
  });
});

