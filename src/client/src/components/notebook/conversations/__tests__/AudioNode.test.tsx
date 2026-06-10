import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import '@testing-library/jest-dom';
import { $createParagraphNode, $createTextNode, $getRoot } from 'lexical';
import {
  AudioNode,
  $createAudioNode,
  $isAudioNode,
  AUDIO_TRANSFORMER,
  AudioPayload,
} from '../AudioNode';
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

function renderDecoratedNode(node: AudioNode, context: Record<string, unknown> = {}) {
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

describe('AudioNode', () => {
  beforeEach(() => {
    mockGetAuthenticatedUrl.mockReset();
    mockGetAuthenticatedUrl.mockResolvedValue({
      objectUrl: 'blob:audio-test',
      fileName: 'audio.mp3',
    });
  });

  it('exposes node type and serializes to JSON', () => {
    expect(AudioNode.getType()).toBe('audio');

    runWithEditor([AudioNode], () => {
      const node = $createAudioNode({ src: 'clip.mp3' });
      expect(node.getSrc()).toBe('clip.mp3');
      expect(node.exportJSON()).toEqual({
        src: 'clip.mp3',
        type: 'audio',
        version: 1,
      });

      const imported = AudioNode.importJSON({ src: 'imported.mp3', type: 'audio', version: 1 });
      expect(imported.getSrc()).toBe('imported.mp3');
      expect($isAudioNode(imported)).toBe(true);
      expect($isAudioNode(null)).toBe(false);
    });
  });

  it('clones and updates src', () => {
    runWithEditor([AudioNode], () => {
      const node = $createAudioNode({ src: 'a.mp3' });
      const clone = AudioNode.clone(node);
      expect(clone.getSrc()).toBe('a.mp3');

      node.setSrc('b.mp3');
      expect(node.getSrc()).toBe('b.mp3');
    });
  });

  it('renders external audio without authenticated fetch', () => {
    const node = runWithEditor([AudioNode], () => $createAudioNode({ src: 'https://example.com/audio.mp3' }));
    renderDecoratedNode(node);

    const audio = document.querySelector('audio');
    expect(audio).toBeInTheDocument();
    expect(audio).toHaveAttribute('src', 'https://example.com/audio.mp3');
    expect(mockGetAuthenticatedUrl).not.toHaveBeenCalled();
  });

  it('resolves notebook-relative paths and loads authenticated blob URL', async () => {
    const node = runWithEditor([AudioNode], () => $createAudioNode({ src: 'sounds/clip.mp3' }));
    renderDecoratedNode(node, { basePath: 'Output' });

    await waitFor(() => {
      const audio = document.querySelector('audio');
      expect(audio).toHaveAttribute('src', 'blob:audio-test');
    });

    expect(mockGetAuthenticatedUrl).toHaveBeenCalledWith(
      expect.stringContaining('/projects/proj-1/notebooks/nb-1/files/content?path=')
    );
    expect(mockGetAuthenticatedUrl.mock.calls[0][0]).toContain('Output%2Fsounds%2Fclip.mp3');
  });

  it('shows loading then error state when authenticated fetch fails', async () => {
    mockGetAuthenticatedUrl.mockRejectedValue(new Error('Network error'));

    const node = runWithEditor([AudioNode], () =>
      $createAudioNode({ src: '/api/projects/p1/notebooks/n1/files/content?path=fail.mp3' })
    );
    renderDecoratedNode(node);

    expect(screen.getByText('Loading audio...')).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getByText('Audio unavailable')).toBeInTheDocument();
    });
  });

  it('exports and imports via AUDIO_TRANSFORMER', () => {
    runWithEditor([AudioNode], () => {
      const root = $getRoot();
      const paragraph = $createParagraphNode();
      const text = $createTextNode('[AUDIO:track.mp3]');
      paragraph.append(text);
      root.append(paragraph);

      const match = '[AUDIO:track.mp3]'.match(AUDIO_TRANSFORMER.regExp!);
      expect(match).not.toBeNull();
      AUDIO_TRANSFORMER.replace!(text, match!);

      const audioNode = paragraph.getFirstChild();
      expect($isAudioNode(audioNode)).toBe(true);
      expect(AUDIO_TRANSFORMER.export!(audioNode!)).toBe(
        '<audio src="track.mp3" controls></audio>'
      );
    });
  });

  it('creates node via $createAudioNode factory', () => {
    runWithEditor([AudioNode], () => {
      const payload: AudioPayload = { src: 'factory.mp3' };
      const node = $createAudioNode(payload);
      expect($isAudioNode(node)).toBe(true);
      expect(node.getSrc()).toBe('factory.mp3');
    });
  });

  it('createDOM returns span and updateDOM returns false', () => {
    runWithEditor([AudioNode], () => {
      const node = $createAudioNode({ src: 'dom.mp3' });
      const dom = node.createDOM();
      expect(dom.tagName).toBe('SPAN');
      expect(node.updateDOM()).toBe(false);
    });
  });

  it('AUDIO_TRANSFORMER export returns null for non-audio nodes', () => {
    runWithEditor([AudioNode], () => {
      const paragraph = $createParagraphNode();
      expect(AUDIO_TRANSFORMER.export!(paragraph)).toBeNull();
    });
  });

  it('normalizes escaped ampersand sequences in src', async () => {
    const node = runWithEditor([AudioNode], () =>
      $createAudioNode({ src: 'track.mp3?m=1%5Cu0026v=2' })
    );
    renderDecoratedNode(node);

    await waitFor(() => {
      expect(mockGetAuthenticatedUrl).toHaveBeenCalledWith(expect.stringContaining('&v=2'));
    });
  });

  it('resolves project file via resolveProjectFilePath when notebookId is absent', async () => {
    const node = runWithEditor([AudioNode], () => $createAudioNode({ src: './assets/sound.mp3' }));
    renderDecoratedNode(node, {
      notebookId: undefined,
      resolveProjectFilePath: (path: string) => (path === 'assets/sound.mp3' ? 'audio-file-1' : undefined),
    });

    await waitFor(() => {
      expect(mockGetAuthenticatedUrl).toHaveBeenCalledWith(
        expect.stringContaining('/projects/proj-1/files/audio-file-1/content')
      );
    });
  });

  it('warns and uses raw src when project file cannot be resolved', () => {
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const node = runWithEditor([AudioNode], () => $createAudioNode({ src: './missing.mp3' }));
    renderDecoratedNode(node, {
      notebookId: undefined,
      resolveProjectFilePath: () => undefined,
    });

    const audio = document.querySelector('audio');
    expect(audio).toHaveAttribute('src', './missing.mp3');
    expect(warnSpy).toHaveBeenCalledWith(
      '[AudioNode] Could not resolve project file for relative path:',
      './missing.mp3'
    );
    warnSpy.mockRestore();
  });

  it('reuses cached blob URL on remount', async () => {
    const node = runWithEditor([AudioNode], () => $createAudioNode({ src: 'cached.mp3' }));
    const { unmount } = renderDecoratedNode(node);
    await waitFor(() => expect(mockGetAuthenticatedUrl).toHaveBeenCalledTimes(1));

    unmount();
    mockGetAuthenticatedUrl.mockClear();
    renderDecoratedNode(runWithEditor([AudioNode], () => $createAudioNode({ src: 'cached.mp3' })));

    await waitFor(() => {
      const audio = document.querySelector('audio');
      expect(audio).toHaveAttribute('src', 'blob:audio-test');
    });
    expect(mockGetAuthenticatedUrl).not.toHaveBeenCalled();
  });

  it('shows loading state for malformed authenticated URLs during retry window', async () => {
    const node = runWithEditor([AudioNode], () =>
      $createAudioNode({ src: '/api/projects/p1/notebooks/n1/files/content?path=wait...mp3' })
    );
    renderDecoratedNode(node);
    expect(screen.getByText('Loading audio...')).toBeInTheDocument();
    expect(mockGetAuthenticatedUrl).not.toHaveBeenCalled();
  });
});
