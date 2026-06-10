import React, { useRef, useEffect, useState } from 'react';
import { render, waitFor, act } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import '@testing-library/jest-dom';
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
    await new Promise((r) => window.setTimeout(r, 350));
  });
}

async function toggleAndWait(editor: LexicalEditorRef) {
  await act(async () => {
    editor.toggleSourceMode();
    await new Promise((r) => window.setTimeout(r, 100));
  });
}

describe('LexicalEditor – TABLE_TRANSFORMER export & import', () => {
  it('exports table with plain text cells after markdown import', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '| Header | Media |\n| --- | --- |\n| Row | plain |');
      const value = editor.getValue();
      expect(value).toContain('Header');
      expect(value).toContain('plain');
      expect(value).toMatch(/\|/);
    });
  });

  it('exports table with image cell after import', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '| H | I |\n| --- | --- |\n| r | ![alt](https://example.com/x.png) |');
      const value = editor.getValue();
      expect(value).toContain('alt');
      expect(value).toContain('example.com');
    });
  });

  it('exports table with audio token after import', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '| H | A |\n| --- | --- |\n| r | [AUDIO:track.mp3] |');
      const value = editor.getValue();
      expect(value.toLowerCase()).toMatch(/audio|track\.mp3/);
    });
  });

  it('exports table with video token after import', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '| H | V |\n| --- | --- |\n| r | [VIDEO:clip.mp4] |');
      const value = editor.getValue();
      expect(value.toLowerCase()).toMatch(/video|clip\.mp4/);
    });
  });

  it('imports multi-row table with mixed media tokens', async () => {
    await withEditor(async (editor) => {
      const md = [
        '| Label | Asset |',
        '| --- | --- |',
        '| pic | ![img](https://cdn.test/p.png) |',
        '| sound | [AUDIO:song.mp3] |',
        '| motion | [VIDEO:scene.mp4] |',
      ].join('\n');
      await setAndWait(editor, md);
      const out = editor.getValue();
      expect(out).toContain('pic');
      expect(out).toContain('sound');
      expect(out).toContain('motion');
    });
  });

  it('imports table with empty trailing cell', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '| A | B |\n| --- | --- |\n| filled |  |');
      expect(editor.getValue()).toContain('filled');
    });
  });

  it('imports table with fewer data cells than header columns', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '| Col1 | Col2 | Col3 |\n| --- | --- | --- |\n| only |');
      const out = editor.getValue();
      expect(out).toContain('only');
      expect(out).toContain('Col1');
    });
  });

  it('round-trips table through source mode preserving rows', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '| X | Y |\n| --- | --- |\n| 1 | 2 |');
      await toggleAndWait(editor);
      expect(editor.getValue()).toContain('1');
      await toggleAndWait(editor);
      await new Promise((r) => window.setTimeout(r, 200));
      expect(editor.getValue()).toContain('2');
    });
  });

  it('exports table after toggling to source and back to WYSIWYG', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '| A | B |\n| --- | --- |\n| x | y |');
      await toggleAndWait(editor);
      await toggleAndWait(editor);
      await new Promise((r) => window.setTimeout(r, 200));
      const out = editor.getValue();
      expect(out).toContain('x');
      expect(out).toContain('y');
    });
  });
});
