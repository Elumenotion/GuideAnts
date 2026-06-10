import React from 'react';
import { render, screen, waitFor, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import '@testing-library/jest-dom';
import { $createParagraphNode, $createTextNode, $getRoot } from 'lexical';
import {
  ImageNode,
  $createImageNode,
  $isImageNode,
  IMAGE_TRANSFORMER,
  ImageNodeContextProvider,
} from '../ImageNode';
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

function renderDecoratedNode(node: ImageNode, context: Record<string, unknown> = {}) {
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

describe('ImageNode', () => {
  beforeEach(() => {
    mockGetAuthenticatedUrl.mockReset();
    mockGetAuthenticatedUrl.mockResolvedValue({
      objectUrl: 'blob:image-test',
      fileName: 'photo.png',
    });
  });

  it('exposes node type and serializes alt text', () => {
    expect(ImageNode.getType()).toBe('image');

    runWithEditor([ImageNode], () => {
      const node = $createImageNode({ src: 'pic.png', altText: 'A photo', width: 200, height: 100 });
      expect(node.getAltText()).toBe('A photo');
      expect(node.exportJSON()).toEqual({
        altText: 'A photo',
        height: 100,
        src: 'pic.png',
        type: 'image',
        version: 1,
        width: 200,
      });

      const imported = ImageNode.importJSON({
        altText: 'Imported',
        src: 'x.png',
        type: 'image',
        version: 1,
      });
      expect(imported.getAltText()).toBe('Imported');
      expect($isImageNode(imported)).toBe(true);
    });
  });

  it('renders external image without authenticated fetch', () => {
    const node = runWithEditor([ImageNode], () =>
      $createImageNode({ src: 'https://example.com/photo.jpg', altText: 'External' })
    );
    renderDecoratedNode(node);

    const img = screen.getByRole('img', { name: 'External' });
    expect(img).toHaveAttribute('src', 'https://example.com/photo.jpg');
    expect(mockGetAuthenticatedUrl).not.toHaveBeenCalled();
  });

  it('resolves notebook-relative paths with basePath and parent navigation', async () => {
    const node = runWithEditor([ImageNode], () =>
      $createImageNode({ src: '../shared/logo.png', altText: 'Logo' })
    );
    renderDecoratedNode(node, { basePath: 'docs/guides' });

    await waitFor(() => {
      const img = screen.getByRole('img', { name: 'Logo' });
      expect(img).toHaveAttribute('src', 'blob:image-test');
    });

    expect(mockGetAuthenticatedUrl.mock.calls[0][0]).toContain('docs%2Fshared%2Flogo.png');
  });

  it('resolves project file via resolveProjectFilePath', async () => {
    const node = runWithEditor([ImageNode], () =>
      $createImageNode({ src: './assets/icon.png', altText: 'Icon' })
    );
    renderDecoratedNode(node, {
      notebookId: undefined,
      resolveProjectFilePath: (path: string) => (path === 'assets/icon.png' ? 'file-99' : undefined),
    });

    await waitFor(() => {
      const img = screen.getByRole('img', { name: 'Icon' });
      expect(img).toHaveAttribute('src', 'blob:image-test');
    });

    expect(mockGetAuthenticatedUrl).toHaveBeenCalledWith(
      expect.stringContaining('/projects/proj-1/files/file-99/content')
    );
  });

  it('shows loading state while authenticated image is fetched', () => {
    mockGetAuthenticatedUrl.mockImplementation(() => new Promise(() => {}));

    const node = runWithEditor([ImageNode], () =>
      $createImageNode({ src: 'image.png', altText: 'Loading' })
    );
    renderDecoratedNode(node);

    expect(screen.getByText('Loading image...')).toBeInTheDocument();
  });

  it('shows error when authenticated fetch fails', async () => {
    mockGetAuthenticatedUrl.mockRejectedValue({ message: 'Not found', status: 403 });

    const node = runWithEditor([ImageNode], () =>
      $createImageNode({
        src: '/api/projects/p1/notebooks/n1/files/content?path=missing.png',
        altText: 'Missing',
      })
    );
    renderDecoratedNode(node);

    await waitFor(() => {
      expect(screen.getByText('Image unavailable')).toBeInTheDocument();
    });
  });

  it('exports and imports via IMAGE_TRANSFORMER', () => {
    runWithEditor([ImageNode], () => {
      const root = $getRoot();
      const paragraph = $createParagraphNode();
      const text = $createTextNode('![Alt text](image.png)');
      paragraph.append(text);
      root.append(paragraph);

      const match = '![Alt text](image.png)'.match(IMAGE_TRANSFORMER.regExp!);
      IMAGE_TRANSFORMER.replace!(text, match!);

      const imageNode = paragraph.getFirstChild();
      expect($isImageNode(imageNode)).toBe(true);
      expect((imageNode as ImageNode).getAltText()).toBe('Alt text');
      expect((imageNode as ImageNode).getSrc()).toBe('image.png');
      expect(IMAGE_TRANSFORMER.export!(imageNode!)).toBe('![Alt text](image.png)');
    });
  });

  it('updates alt text and src via setters', () => {
    runWithEditor([ImageNode], () => {
      const node = $createImageNode({ src: 'a.png', altText: 'A' });
      node.setAltText('B');
      node.setSrc('b.png');
      expect(node.getAltText()).toBe('B');
      expect(node.getSrc()).toBe('b.png');
    });
  });

  it('createDOM returns span and updateDOM returns false', () => {
    runWithEditor([ImageNode], () => {
      const node = $createImageNode({ src: 'dom.png', altText: 'DOM' });
      const dom = node.createDOM();
      expect(dom.tagName).toBe('SPAN');
      expect(node.updateDOM()).toBe(false);
    });
  });

  it('IMAGE_TRANSFORMER export returns null for non-image nodes', () => {
    runWithEditor([ImageNode], () => {
      const paragraph = $createParagraphNode();
      expect(IMAGE_TRANSFORMER.export!(paragraph)).toBeNull();
    });
  });

  it('$isImageNode returns false for null', () => {
    expect($isImageNode(null)).toBe(false);
  });

  it('shows coming-up placeholder for malformed authenticated URLs', () => {
    const node = runWithEditor([ImageNode], () =>
      $createImageNode({
        src: '/api/projects/p1/notebooks/n1/files/content?path=stream...png',
        altText: 'Streaming',
      })
    );
    renderDecoratedNode(node);
    expect(screen.getByText('Image coming up...')).toBeInTheDocument();
  });

  it('normalizes escaped ampersand sequences in src', async () => {
    const node = runWithEditor([ImageNode], () =>
      $createImageNode({ src: 'pic.png?m=1\\u0026v=2', altText: 'Amp' })
    );
    renderDecoratedNode(node);

    await waitFor(() => {
      expect(mockGetAuthenticatedUrl).toHaveBeenCalledWith(expect.stringContaining('&v=2'));
    });
  });

  it('retries transient 404 errors before showing failure', async () => {
    vi.useFakeTimers();
    try {
      mockGetAuthenticatedUrl
        .mockRejectedValueOnce({ message: 'Not ready', status: 404 })
        .mockResolvedValueOnce({ objectUrl: 'blob:retry-image', fileName: 'pic.png' });

      const node = runWithEditor([ImageNode], () =>
        $createImageNode({ src: 'retry.png', altText: 'Retry' })
      );
      renderDecoratedNode(node);

      expect(screen.getByText('Loading image...')).toBeInTheDocument();

      await act(async () => {
        await vi.advanceTimersByTimeAsync(600);
      });

      const img = screen.getByRole('img', { name: 'Retry' });
      expect(img).toHaveAttribute('src', 'blob:retry-image');
    } finally {
      vi.useRealTimers();
    }
  });

  it('reuses cached blob URL on remount', async () => {
    const node = runWithEditor([ImageNode], () =>
      $createImageNode({ src: 'cached.png', altText: 'Cached' })
    );
    const { unmount } = renderDecoratedNode(node);
    await waitFor(() => {
      const img = screen.getByRole('img', { name: 'Cached' });
      expect(img).toHaveAttribute('src', 'blob:image-test');
    });
    expect(mockGetAuthenticatedUrl).toHaveBeenCalledTimes(1);

    unmount();
    mockGetAuthenticatedUrl.mockClear();
    renderDecoratedNode(
      runWithEditor([ImageNode], () => $createImageNode({ src: 'cached.png', altText: 'Cached' }))
    );

    await waitFor(() => {
      const img = screen.getByRole('img', { name: 'Cached' });
      expect(img).toHaveAttribute('src', 'blob:image-test');
    });
    expect(mockGetAuthenticatedUrl).not.toHaveBeenCalled();
  });

  it('warns when project file path cannot be resolved', () => {
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const node = runWithEditor([ImageNode], () =>
      $createImageNode({ src: './ghost.png', altText: 'Ghost' })
    );
    renderDecoratedNode(node, {
      notebookId: undefined,
      resolveProjectFilePath: () => undefined,
    });

    const img = screen.getByRole('img', { name: 'Ghost' });
    expect(img).toHaveAttribute('src', './ghost.png');
    expect(warnSpy).toHaveBeenCalled();
    warnSpy.mockRestore();
  });
});
