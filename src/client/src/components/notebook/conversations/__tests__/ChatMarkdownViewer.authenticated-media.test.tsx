import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import ChatMarkdownViewer, { invalidateImageCacheForPaths } from '../ChatMarkdownViewer';
import { api } from '../../../../services/api';

vi.mock('../../../common/MermaidRenderer', () => ({
  default: () => null,
}));

vi.mock('../ImageFullscreenViewer', () => ({
  default: () => null,
}));

vi.mock('../../../../services/api', () => ({
  api: {
    utils: {
      getAuthenticatedUrl: vi.fn(),
    },
  },
}));

const mockGetAuthenticatedUrl = vi.mocked(api.utils.getAuthenticatedUrl);

describe('ChatMarkdownViewer – authenticated media states', () => {
  beforeEach(() => {
    mockGetAuthenticatedUrl.mockReset();
    mockGetAuthenticatedUrl.mockResolvedValue({
      objectUrl: 'blob:media',
      fileName: 'media.bin',
    });
  });

  it('renders authenticated video after load completes', async () => {
    const md = '[VIDEO:/api/projects/p/n/files/content?path=clip.mp4]';
    const { container } = render(
      <ChatMarkdownViewer text={md} projectId="p" notebookId="n" />
    );

    await waitFor(() => expect(mockGetAuthenticatedUrl).toHaveBeenCalled());
    await waitFor(() => {
      expect(container.querySelector('video')).toHaveAttribute('src', 'blob:media');
    });
  });

  it('renders authenticated audio after load completes', async () => {
    const md = '[AUDIO:/api/projects/p/n/files/content?path=clip.mp3]';
    const { container } = render(
      <ChatMarkdownViewer text={md} projectId="p" notebookId="n" />
    );

    await waitFor(() => expect(mockGetAuthenticatedUrl).toHaveBeenCalled());
    await waitFor(() => {
      expect(container.querySelector('audio')).toHaveAttribute('src', 'blob:media');
    });
  });

  it('shows video error state when authenticated fetch fails', async () => {
    mockGetAuthenticatedUrl.mockRejectedValue(new Error('Server error'));

    render(
      <ChatMarkdownViewer
        text="[VIDEO:/api/projects/p/n/files/content?path=bad.mp4]"
        projectId="p"
        notebookId="n"
      />
    );

    await waitFor(() => {
      expect(screen.getByText('Video unavailable')).toBeInTheDocument();
    });
  });

  it('invalidates cache entries that include query parameters', () => {
    render(
      <ChatMarkdownViewer
        text="![img](./chart.png?m=123)"
        projectId="proj"
        notebookId="nb"
        turnFilesModified={['chart.png?m=456']}
      />
    );

    expect(() =>
      invalidateImageCacheForPaths(['chart.png?m=456'], 'proj', 'nb')
    ).not.toThrow();
  });

  it('renders center-aligned images from pipe token', async () => {
    const md = '![centered](https://example.com/pic.png|align=center)';
    const { container } = render(<ChatMarkdownViewer text={md} />);
    const img = container.querySelector('img');
    expect(img?.className).toContain('mx-auto');
  });
});
