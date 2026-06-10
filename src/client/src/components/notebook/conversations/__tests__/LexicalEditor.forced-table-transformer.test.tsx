import React, { useRef, useEffect, useState } from 'react';
import { render, waitFor, act } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';

vi.mock('@lexical/markdown', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@lexical/markdown')>();
  return {
    ...actual,
    TRANSFORMERS: [],
  };
});

import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

async function withEditor(run: (editor: LexicalEditorRef) => void | Promise<void>) {
  let editorRef: LexicalEditorRef | null = null;

  const Harness = () => {
    const ref = useRef<LexicalEditorRef>(null);
    const [ready, setReady] = useState(false);
    useEffect(() => {
      if (ready && ref.current) editorRef = ref.current;
    }, [ready]);
    return <LexicalEditor ref={ref} showToolbar={false} onReady={() => setReady(true)} />;
  };

  render(<Harness />);
  await waitFor(() => expect(editorRef).not.toBeNull(), { timeout: 5000 });
  await act(async () => {
    await run(editorRef!);
  });
}

async function setAndWait(editor: LexicalEditorRef, markdown: string) {
  await act(async () => {
    editor.setValue(markdown);
    await new Promise((r) => window.setTimeout(r, 400));
  });
}

describe('LexicalEditor – TABLE_TRANSFORMER with built-in transformers disabled', () => {
  it('imports and exports table with image cell via custom transformer', async () => {
    await withEditor(async (editor) => {
      await setAndWait(
        editor,
        '| Label | Asset |\n| --- | --- |\n| pic | ![alt](https://cdn.test/p.png) |\n\ntail'
      );
      const out = editor.getValue();
      expect(out).toContain('![alt](https://cdn.test/p.png)');
      expect(out).toContain('Label');
    });
  });

  it('imports and exports table with audio token cell', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '| H | A |\n| --- | --- |\n| row | [AUDIO:track.mp3] |\n\ntail');
      const out = editor.getValue();
      expect(out).toMatch(/<audio[^>]+src="track\.mp3"/i);
    });
  });

  it('imports and exports table with video token cell', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '| H | V |\n| --- | --- |\n| row | [VIDEO:clip.mp4] |\n\ntail');
      const out = editor.getValue();
      expect(out).toMatch(/<video[^>]+src="clip\.mp4"/i);
    });
  });

  it('imports table with plain text and empty trailing cells', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '| A | B | C |\n| --- | --- | --- |\n| filled |  |\n\ntail');
      const out = editor.getValue();
      expect(out).toContain('filled');
      expect(out).toContain('A');
    });
  });

  it('imports multi-row table and pads cells on export', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '| X | Y |\n| --- | --- |\n| one | two |\n| three | four |\n\ntail');
      const out = editor.getValue();
      expect(out).toContain('one');
      expect(out).toContain('four');
      expect(out).toMatch(/\|/);
    });
  });

  it('round-trips custom-transformer table through source mode', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '| H1 | H2 |\n| --- | --- |\n| a | b |\n\ntail');
      await act(async () => {
        editor.toggleSourceMode();
        await new Promise((r) => window.setTimeout(r, 150));
      });
      expect(editor.getValue()).toContain('a');
      await act(async () => {
        editor.toggleSourceMode();
        await new Promise((r) => window.setTimeout(r, 200));
      });
      expect(editor.getValue()).toContain('b');
    });
  });
});
