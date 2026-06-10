import React, { useRef, useEffect, useState } from 'react';
import { render, waitFor, act } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

async function withToolbarEditor(run: (editor: LexicalEditorRef) => void | Promise<void>) {
  let editorRef: LexicalEditorRef | null = null;

  const Harness = () => {
    const ref = useRef<LexicalEditorRef>(null);
    const [ready, setReady] = useState(false);
    useEffect(() => {
      if (ready && ref.current) editorRef = ref.current;
    }, [ready]);
    return <LexicalEditor ref={ref} showToolbar onReady={() => setReady(true)} />;
  };

  render(<Harness />);
  await waitFor(() => expect(editorRef).not.toBeNull(), { timeout: 5000 });
  await act(async () => {
    await run(editorRef!);
  });
}

describe('LexicalEditor – TABLE_TRANSFORMER import with trailing line', () => {
  it('imports table markdown when followed by non-table line', async () => {
    await withToolbarEditor(async (editor) => {
      const md = [
        '| Kind | Asset |',
        '| --- | --- |',
        '| pic | ![alt](https://cdn.test/p.png) |',
        '| sound | [AUDIO:track.mp3] |',
        '| clip | [VIDEO:clip.mp4] |',
        '',
        'After table paragraph',
      ].join('\n');

      await act(async () => {
        editor.setValue(md);
        await new Promise((r) => window.setTimeout(r, 500));
      });

      expect(document.querySelector('table')).toBeInTheDocument();
      const exported = editor.getValue();
      expect(exported).toContain('![alt](https://cdn.test/p.png)');
      expect(exported).toMatch(/<audio[^>]+src="track\.mp3"/i);
      expect(exported).toMatch(/<video[^>]+src="clip\.mp4"/i);
      expect(exported).toContain('After table');
    });
  });

  it('imports table with fewer data cells than headers when followed by text', async () => {
    await withToolbarEditor(async (editor) => {
      const md = '| Col1 | Col2 | Col3 |\n| --- | --- | --- |\n| only |\n\ntail';
      await act(async () => {
        editor.setValue(md);
        await new Promise((r) => window.setTimeout(r, 500));
      });
      expect(editor.getValue()).toContain('only');
      expect(editor.getValue()).toContain('tail');
    });
  });

});
