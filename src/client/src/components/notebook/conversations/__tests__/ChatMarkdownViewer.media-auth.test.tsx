import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import ChatMarkdownViewer from '../ChatMarkdownViewer';
import { api } from '../../../../services/api';

vi.mock('../../../common/MermaidRenderer', () => ({
  default: () => <div data-testid="mermaid" />,
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

describe('ChatMarkdownViewer – media & authenticated content', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockGetAuthenticatedUrl.mockResolvedValue({
      objectUrl: 'blob:authenticated-image',
      fileName: 'chart.png',
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    (window as any).electron = undefined;
  });

  it('converts video with nested source element', () => {
    const md = '<video controls><source src="nested.mp4" type="video/mp4"></video>';
    const { container } = render(<ChatMarkdownViewer text={md} />);
    expect(container.querySelector('video')).toHaveAttribute('src', 'nested.mp4');
  });

  it('converts audio with nested source element', () => {
    const md = '<audio controls><source src="nested.mp3" type="audio/mpeg"></audio>';
    const { container } = render(<ChatMarkdownViewer text={md} />);
    expect(container.querySelector('audio')).toHaveAttribute('src', 'nested.mp3');
  });

  it('merges adjacent ordered lists so numbering continues', () => {
    const md = '1. First\n\n1. Second';
    const { container } = render(<ChatMarkdownViewer text={md} />);
    const orderedLists = container.querySelectorAll('ol');
    expect(orderedLists.length).toBe(1);
    expect(orderedLists[0].querySelectorAll('li').length).toBe(2);
  });

  it('nests bullets under preceding ordered list item', () => {
    const md = '1. Parent\n\n- child bullet';
    const { container } = render(<ChatMarkdownViewer text={md} />);
    const topLevelItems = container.querySelectorAll('ol > li');
    expect(topLevelItems.length).toBeGreaterThanOrEqual(1);
    expect(container.querySelector('ul')).toBeInTheDocument();
  });

  it('loads authenticated notebook images and renders blob URL', async () => {
    const md = '![chart](./Output/chart.png)';
    render(
      <ChatMarkdownViewer text={md} projectId="proj-1" notebookId="nb-1" />
    );

    await waitFor(() => expect(mockGetAuthenticatedUrl).toHaveBeenCalled());
    await waitFor(() => {
      const img = screen.getByRole('img', { name: 'chart' }) as HTMLImageElement;
      expect(img.src).toContain('blob:authenticated-image');
    });
  });

  it('resolves relative paths with basePath and parent segments', async () => {
    const md = '![nested](../images/nested.png)';
    render(
      <ChatMarkdownViewer
        text={md}
        projectId="proj-1"
        notebookId="nb-1"
        basePath="docs/guides"
      />
    );

    await waitFor(() => {
      expect(mockGetAuthenticatedUrl).toHaveBeenCalledWith(
        expect.stringContaining('path=docs%2Fimages%2Fnested.png')
      );
    });
  });

  it('shows loading then error when authenticated image fetch fails', async () => {
    mockGetAuthenticatedUrl.mockRejectedValue(new Error('Forbidden'));

    const md = '![secure](/api/projects/p/n/files/content?path=secret.png)';
    render(
      <ChatMarkdownViewer text={md} projectId="proj-1" notebookId="nb-1" />
    );

    await waitFor(() => {
      expect(screen.getByText('Image unavailable')).toBeInTheDocument();
    });
  });

  it('retries transient 404 errors before showing failure', async () => {
    mockGetAuthenticatedUrl
      .mockRejectedValueOnce(Object.assign(new Error('Not found'), { status: 404 }))
      .mockResolvedValueOnce({
        objectUrl: 'blob:retry-success',
        fileName: 'retry.png',
      });

    const md = '![retry](/api/projects/p/n/files/content?path=retry.png)';
    render(
      <ChatMarkdownViewer text={md} projectId="proj-1" notebookId="nb-1" />
    );

    await waitFor(
      () => expect(mockGetAuthenticatedUrl.mock.calls.length).toBeGreaterThanOrEqual(2),
      { timeout: 3000 }
    );
  });

  it('shows streaming placeholders for authenticated video and audio', () => {
    const md = '[VIDEO:/api/projects/p/n/files/content?path=clip.mp4]\n\n[AUDIO:/api/projects/p/n/files/content?path=clip.mp3]';
    render(
      <ChatMarkdownViewer
        text={md}
        projectId="proj-1"
        notebookId="nb-1"
        isStreaming
      />
    );

    expect(screen.getByText('Video coming up...')).toBeInTheDocument();
    expect(screen.getByText('Audio coming up...')).toBeInTheDocument();
  });

  it('downloads authenticated API links through ExternalLink handler', async () => {
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    render(
      <ChatMarkdownViewer text="[file](/api/projects/p1/notebooks/n1/files/content?path=doc.pdf)" />
    );

    fireEvent.click(screen.getByRole('link', { name: 'file' }));
    await waitFor(() => expect(mockGetAuthenticatedUrl).toHaveBeenCalled());
    expect(clickSpy).toHaveBeenCalled();
    clickSpy.mockRestore();
  });

  it('falls back to window.open when electron is unavailable', () => {
    const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null);

    render(<ChatMarkdownViewer text="[ext](https://docs.example.com/page)" />);
    fireEvent.click(screen.getByRole('link', { name: 'ext' }));

    expect(openSpy).toHaveBeenCalledWith(
      'https://docs.example.com/page',
      '_blank',
      'noopener,noreferrer'
    );
    openSpy.mockRestore();
  });

  it('renders overlay controls in fullscreen mode', () => {
    render(
      <ChatMarkdownViewer
        text="# Doc"
        isFullScreen
        overlayControls={<button type="button">Custom control</button>}
      />
    );

    expect(screen.getByRole('button', { name: 'Custom control' })).toBeInTheDocument();
  });

  it('does not open fullscreen viewer when enableImageFullscreen is false', () => {
    render(
      <ChatMarkdownViewer
        text="![pic](https://example.com/pic.png)"
        enableImageFullscreen={false}
      />
    );

    const img = screen.getByRole('img', { name: 'pic' });
    fireEvent.click(img);
    expect(screen.queryByTestId('image-fullscreen')).not.toBeInTheDocument();
  });
});
