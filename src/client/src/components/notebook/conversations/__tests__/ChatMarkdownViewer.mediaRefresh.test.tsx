import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '../../../../test/test-utils';
import ChatMarkdownViewer, { invalidateImageCacheForPaths } from '../ChatMarkdownViewer';
import { api } from '../../../../services/api';
import { clearAuthenticatedMediaCache, getCachedMediaUrl } from '../../../../utils/authenticatedMediaCache';

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

const IMG_URL =
  '/api/projects/proj-1/notebooks/nb-1/files/content?path=sine-wave.png';

describe('ChatMarkdownViewer - media refresh after file re-emission (regression)', () => {
  beforeEach(() => {
    mockGetAuthenticatedUrl.mockReset();
    clearAuthenticatedMediaCache();
  });

  it('old cell keeps v1 on re-render; new cell fetches v2 after a turn re-emits the file', async () => {
    let fetchCount = 0;
    mockGetAuthenticatedUrl.mockImplementation(async () => {
      fetchCount += 1;
      return {
        objectUrl: `blob:v${fetchCount}`,
        fileName: 'sine-wave.png',
        contentType: 'image/png',
      };
    });

    // Turn 1: assistant emits an image.
    const { unmount, rerender } = render(
      <ChatMarkdownViewer text="![wave](sine-wave.png)" projectId="proj-1" notebookId="nb-1" />
    );
    await waitFor(() => expect(screen.getByRole('img')).toHaveAttribute('src', 'blob:v1'));
    expect(fetchCount).toBe(1);

    // The LLM re-emits the same file in turn 2. The server reports it via the
    // turnFilesModified prop (CWD-relative path, as stored on the turn). The existing
    // cell must KEEP the version it already rendered (no re-fetch, no swap).
    rerender(
      <ChatMarkdownViewer
        text="![wave](sine-wave.png)"
        projectId="proj-1"
        notebookId="nb-1"
        turnFilesModified={['sine-wave.png']}
      />
    );
    await waitFor(() => expect(screen.getByRole('img')).toHaveAttribute('src', 'blob:v1'));
    expect(fetchCount).toBe(1);

    // A newly mounted cell (the new turn's cell) fetches the fresh bytes.
    unmount();
    render(
      <ChatMarkdownViewer
        text="![wave](sine-wave.png)"
        projectId="proj-1"
        notebookId="nb-1"
        turnFilesModified={['sine-wave.png']}
      />
    );
    await waitFor(() => expect(screen.getByRole('img')).toHaveAttribute('src', 'blob:v2'));
    expect(fetchCount).toBe(2);

    // The new blob is what the cache now serves for this notebook path.
    expect(getCachedMediaUrl(IMG_URL)).toBe('blob:v2');
  });

  it('does not bump the revision when the turn modified a different file', async () => {
    mockGetAuthenticatedUrl.mockResolvedValue({
      objectUrl: 'blob:only',
      fileName: 'sine-wave.png',
      contentType: 'image/png',
    });

    render(
      <ChatMarkdownViewer
        text="![wave](sine-wave.png)"
        projectId="proj-1"
        notebookId="nb-1"
        turnFilesModified={['other-file.png']}
      />
    );
    await waitFor(() => expect(screen.getByRole('img')).toHaveAttribute('src', 'blob:only'));
    expect(mockGetAuthenticatedUrl).toHaveBeenCalledTimes(1);
  });

  it('invalidateImageCacheForPaths (deprecated shim) causes a fresh fetch on the next mount', async () => {
    let fetchCount = 0;
    mockGetAuthenticatedUrl.mockImplementation(async () => {
      fetchCount += 1;
      return {
        objectUrl: `blob:v${fetchCount}`,
        fileName: 'sine-wave.png',
        contentType: 'image/png',
      };
    });

    const { unmount } = render(
      <ChatMarkdownViewer text="![wave](sine-wave.png)" projectId="proj-1" notebookId="nb-1" />
    );
    await waitFor(() => expect(screen.getByRole('img')).toHaveAttribute('src', 'blob:v1'));
    expect(fetchCount).toBe(1);

    // Simulates the SSE `complete` handler bumping the notebook revision.
    invalidateImageCacheForPaths(['sine-wave.png'], 'proj-1', 'nb-1');

    unmount();
    render(<ChatMarkdownViewer text="![wave](sine-wave.png)" projectId="proj-1" notebookId="nb-1" />);
    await waitFor(() => expect(screen.getByRole('img')).toHaveAttribute('src', 'blob:v2'));
    expect(fetchCount).toBe(2);
  });
});
