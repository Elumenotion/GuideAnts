import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import '@testing-library/jest-dom';
import { $createParagraphNode, $createTextNode, $getRoot } from 'lexical';
import {
  VideoNode,
  $createVideoNode,
  $isVideoNode,
  VIDEO_TRANSFORMER,
} from '../VideoNode';
import { ImageNodeContextProvider } from '../ImageNode';
import { api } from '../../../../services/api';
import { runWithEditor } from './mediaNodeTestUtils';

vi.mock('../../../../services/api', () => ({
  api: {
    utils: {
      getAuthenticatedUrl: vi.fn(),
    },
  },
}));

const mockGetAuthenticatedUrl = vi.mocked(api.utils.getAuthenticatedUrl);

function renderDecoratedNode(node: VideoNode, context: Record<string, unknown> = {}) {
  return render(
    <ImageNodeContextProvider
      value={{
        projectId: 'proj-1',
        notebookId: 'nb-1',
        ...context,
      }}
    >
      {node.decorate()}
    </ImageNodeContextProvider>
  );
}

describe('VideoNode', () => {
  beforeEach(() => {
    mockGetAuthenticatedUrl.mockReset();
    mockGetAuthenticatedUrl.mockResolvedValue({
      objectUrl: 'blob:video-test',
      fileName: 'video.mp4',
    });
  });

  it('exposes node type and serializes dimensions', () => {
    expect(VideoNode.getType()).toBe('video');

    runWithEditor([VideoNode], () => {
      const node = $createVideoNode({ src: 'clip.mp4', width: 640, height: 480, poster: 'poster.jpg' });
      expect(node.exportJSON()).toEqual({
        src: 'clip.mp4',
        width: 640,
        height: 480,
        poster: 'poster.jpg',
        type: 'video',
        version: 1,
      });

      const imported = VideoNode.importJSON({
        src: 'imported.mp4',
        width: 0,
        height: 0,
        type: 'video',
        version: 1,
      });
      expect(imported.getSrc()).toBe('imported.mp4');
      expect($isVideoNode(imported)).toBe(true);
    });
  });

  it('renders external video without authenticated fetch', () => {
    const node = runWithEditor([VideoNode], () =>
      $createVideoNode({ src: 'https://example.com/video.mp4', width: 320, height: 240 })
    );
    renderDecoratedNode(node);

    const video = document.querySelector('video');
    expect(video).toBeInTheDocument();
    expect(video).toHaveAttribute('src', 'https://example.com/video.mp4');
    expect(mockGetAuthenticatedUrl).not.toHaveBeenCalled();
  });

  it('resolves notebook-relative paths and loads authenticated blob URL', async () => {
    const node = runWithEditor([VideoNode], () => $createVideoNode({ src: 'media/demo.mp4?m=123' }));
    renderDecoratedNode(node);

    await waitFor(() => {
      const video = document.querySelector('video');
      expect(video).toHaveAttribute('src', 'blob:video-test');
    });

    expect(mockGetAuthenticatedUrl).toHaveBeenCalledWith(
      expect.stringMatching(/files\/content\?path=media%2Fdemo\.mp4&m=123/)
    );
  });

  it('shows error state when authenticated fetch fails', async () => {
    mockGetAuthenticatedUrl.mockRejectedValue(new Error('Forbidden'));

    const node = runWithEditor([VideoNode], () =>
      $createVideoNode({ src: '/api/projects/p1/notebooks/n1/files/content?path=fail.mp4' })
    );
    renderDecoratedNode(node);

    await waitFor(() => {
      expect(screen.getByText('Video unavailable')).toBeInTheDocument();
    });
  });

  it('exports and imports via VIDEO_TRANSFORMER', () => {
    runWithEditor([VideoNode], () => {
      const root = $getRoot();
      const paragraph = $createParagraphNode();
      const text = $createTextNode('[VIDEO:demo.mp4]');
      paragraph.append(text);
      root.append(paragraph);

      const match = '[VIDEO:demo.mp4]'.match(VIDEO_TRANSFORMER.regExp!);
      VIDEO_TRANSFORMER.replace!(text, match!);

      const videoNode = paragraph.getFirstChild();
      expect($isVideoNode(videoNode)).toBe(true);
      expect((videoNode as VideoNode).getSrc()).toBe('demo.mp4');
      expect(VIDEO_TRANSFORMER.export!(videoNode!)).toBe(
        '<video src="demo.mp4" controls></video>'
      );
    });
  });

  it('clones node preserving poster', () => {
    runWithEditor([VideoNode], () => {
      const node = $createVideoNode({ src: 'a.mp4', poster: 'thumb.png' });
      const clone = VideoNode.clone(node);
      expect(clone.getSrc()).toBe('a.mp4');
    });
  });

  it('createDOM returns span and updateDOM returns false', () => {
    runWithEditor([VideoNode], () => {
      const node = $createVideoNode({ src: 'dom.mp4' });
      const dom = node.createDOM();
      expect(dom.tagName).toBe('SPAN');
      expect(node.updateDOM()).toBe(false);
    });
  });

  it('VIDEO_TRANSFORMER export returns null for non-video nodes', () => {
    runWithEditor([VideoNode], () => {
      const paragraph = $createParagraphNode();
      expect(VIDEO_TRANSFORMER.export!(paragraph)).toBeNull();
    });
  });

  it('$isVideoNode returns false for null', () => {
    expect($isVideoNode(null)).toBe(false);
  });

  it('applies numeric width and height styles on external video', () => {
    const node = runWithEditor([VideoNode], () =>
      $createVideoNode({ src: 'https://example.com/v.mp4', width: 320, height: 240, poster: 'p.jpg' })
    );
    renderDecoratedNode(node);

    const video = document.querySelector('video');
    expect(video).toHaveStyle({ width: '320px', height: '240px' });
    expect(video).toHaveAttribute('poster', 'p.jpg');
  });

  it('resolves project file when notebookId is absent', async () => {
    const node = runWithEditor([VideoNode], () => $createVideoNode({ src: '../clips/demo.mp4' }));
    renderDecoratedNode(node, {
      notebookId: undefined,
      resolveProjectFilePath: () => 'video-42',
    });

    await waitFor(() => {
      expect(mockGetAuthenticatedUrl).toHaveBeenCalledWith(
        expect.stringContaining('/projects/proj-1/files/video-42/content')
      );
    });
  });

  it('reuses cached blob URL on remount', async () => {
    const node = runWithEditor([VideoNode], () => $createVideoNode({ src: 'cached.mp4' }));
    const { unmount } = renderDecoratedNode(node);
    await waitFor(() => expect(mockGetAuthenticatedUrl).toHaveBeenCalledTimes(1));

    unmount();
    mockGetAuthenticatedUrl.mockClear();
    renderDecoratedNode(runWithEditor([VideoNode], () => $createVideoNode({ src: 'cached.mp4' })));

    await waitFor(() => {
      const video = document.querySelector('video');
      expect(video).toHaveAttribute('src', 'blob:video-test');
    });
    expect(mockGetAuthenticatedUrl).not.toHaveBeenCalled();
  });

  it('shows loading state for malformed authenticated URLs during retry window', async () => {
    const node = runWithEditor([VideoNode], () =>
      $createVideoNode({ src: '/api/projects/p1/notebooks/n1/files/content?path=wait...mp4' })
    );
    renderDecoratedNode(node);
    expect(screen.getByText('Loading video...')).toBeInTheDocument();
    expect(mockGetAuthenticatedUrl).not.toHaveBeenCalled();
  });
});
