import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '../../../../test/test-utils';
import ChatMarkdownViewer, { invalidateImageCacheForPaths } from '../ChatMarkdownViewer';
import { api } from '../../../../services/api';

vi.mock('../../../common/MermaidRenderer', () => ({
  default: ({ chart }: { chart: string }) => <div data-testid="mermaid">{chart}</div>,
}));

vi.mock('../ImageFullscreenViewer', () => ({
  default: ({ onClose }: { onClose: () => void }) => (
    <div data-testid="image-fullscreen">
      <button onClick={onClose}>Close image</button>
    </div>
  ),
}));

vi.mock('../../../../services/api', () => ({
  api: {
    utils: {
      getAuthenticatedUrl: vi.fn(),
    },
  },
}));

const mockGetAuthenticatedUrl = vi.mocked(api.utils.getAuthenticatedUrl);

describe('ChatMarkdownViewer – extended rendering', () => {
  beforeEach(() => {
    mockGetAuthenticatedUrl.mockReset();
    mockGetAuthenticatedUrl.mockResolvedValue({
      objectUrl: 'blob:mock-image',
      fileName: 'image.png',
    });
  });

  it('renders blockquote and fenced code block', () => {
    const md = '> Quoted text\n\n```js\nconst x = 1;\n```';
    const { container } = render(<ChatMarkdownViewer text={md} />);
    expect(container.querySelector('blockquote')).toBeInTheDocument();
    expect(container.querySelector('pre code')).toHaveTextContent('const x = 1;');
  });

  it('renders VIDEO and AUDIO markdown tokens', () => {
    const md = '[VIDEO:clip.mp4]\n\n[AUDIO:clip.mp3]';
    const { container } = render(<ChatMarkdownViewer text={md} />);
    expect(container.querySelector('video')).toHaveAttribute('src', 'clip.mp4');
    expect(container.querySelector('audio')).toHaveAttribute('src', 'clip.mp3');
  });

  it('renders fullscreen overlay and exits on close', () => {
    const onExit = vi.fn();
    render(
      <ChatMarkdownViewer
        text="# Fullscreen doc"
        isFullScreen
        onExitFullScreen={onExit}
      />
    );
    fireEvent.click(screen.getByLabelText('Exit full screen'));
    expect(onExit).toHaveBeenCalled();
  });

  it('opens image fullscreen viewer on image click', () => {
    const md = '![diagram](https://example.com/diagram.png)';
    render(<ChatMarkdownViewer text={md} enableImageFullscreen />);
    fireEvent.click(screen.getByRole('img', { name: 'diagram' }));
    expect(screen.getByTestId('image-fullscreen')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Close image'));
    expect(screen.queryByTestId('image-fullscreen')).not.toBeInTheDocument();
  });

  it('resolves relative notebook image paths with authentication', async () => {
    const md = '![local](./Output/chart.png)';
    render(
      <ChatMarkdownViewer
        text={md}
        projectId="proj-1"
        notebookId="nb-1"
      />
    );
    await vi.waitFor(() => expect(mockGetAuthenticatedUrl).toHaveBeenCalled());
  });

  it('shows streaming placeholder for incomplete authenticated image URLs', () => {
    const md = '![loading](/api/projects/p/n/files/content?path=img.png)';
    render(
      <ChatMarkdownViewer
        text={md}
        projectId="proj-1"
        notebookId="nb-1"
        isStreaming
      />
    );
    expect(screen.getByText('Image coming up...')).toBeInTheDocument();
  });

  it('invalidateImageCacheForPaths is safe with empty input', () => {
    expect(() => invalidateImageCacheForPaths([], 'proj', 'nb')).not.toThrow();
    expect(() => invalidateImageCacheForPaths(['a.png'], undefined, undefined)).not.toThrow();
  });

  it('uses electron openExternal for external links when available', () => {
    const openExternal = vi.fn();
    (window as any).electron = { openExternal };

    render(<ChatMarkdownViewer text="[site](https://example.com)" />);
    fireEvent.click(screen.getByRole('link', { name: 'site' }));
    expect(openExternal).toHaveBeenCalledWith('https://example.com');
  });
});
