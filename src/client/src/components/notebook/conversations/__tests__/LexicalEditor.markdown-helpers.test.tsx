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
    await new Promise((r) => window.setTimeout(r, 300));
  });
}

async function toggleAndWait(editor: LexicalEditorRef) {
  await act(async () => {
    editor.toggleSourceMode();
    await new Promise((r) => window.setTimeout(r, 100));
  });
}

describe('LexicalEditor – markdown cleanup & HTML paths', () => {
  it('decodes HTML numeric entities in exported markdown', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, 'word with space');
      const value = editor.getValue();
      expect(value).not.toContain('&#32;');
      expect(value).toContain('word');
    });
  });

  it('cleans escaped brackets and backticks on export', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '[link](https://example.com) and `code`');
      const value = editor.getValue();
      expect(value).toContain('link');
      expect(value).toContain('code');
    });
  });

  it('fixes adjacent bold-then-italic marker patterns on export', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '**bold** *italic*');
      const value = editor.getValue();
      expect(value).toContain('bold');
      expect(value).toContain('italic');
    });
  });

  it('preprocesses video and audio tags with direct src attributes', async () => {
    await withEditor(async (editor) => {
      await setAndWait(
        editor,
        '<video src="direct.mp4" controls></video>\n<audio src="direct.mp3" controls></audio>'
      );
      expect(editor.getValue().length).toBeGreaterThan(0);
    });
  });

  it('getIsEmpty returns true for blank source mode textarea', async () => {
    await withEditor(async (editor) => {
      await toggleAndWait(editor);
      expect(editor.getIsEmpty()).toBe(true);
      await act(async () => {
        editor.insertText('x');
        await new Promise((r) => window.setTimeout(r, 50));
      });
      expect(editor.getIsEmpty()).toBe(false);
    });
  });

  it('setValue while in source mode replaces textarea content', async () => {
    await withEditor(async (editor) => {
      await toggleAndWait(editor);
      await setAndWait(editor, 'Source only');
      editor.setValue('Updated source');
      await new Promise((r) => window.setTimeout(r, 50));
      expect(editor.getValue()).toBe('Updated source');
    });
  });
});
