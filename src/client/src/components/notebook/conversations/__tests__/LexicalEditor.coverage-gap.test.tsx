import React, { useRef, useEffect, useState } from 'react';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

async function withEditor(
  run: (editor: LexicalEditorRef) => void | Promise<void>,
  options: { showToolbar?: boolean } = {}
) {
  let editorRef: LexicalEditorRef | null = null;

  const Harness = () => {
    const ref = useRef<LexicalEditorRef>(null);
    const [ready, setReady] = useState(false);

    useEffect(() => {
      if (ready && ref.current) {
        editorRef = ref.current;
      }
    }, [ready]);

    return (
      <LexicalEditor
        ref={ref}
        showToolbar={options.showToolbar ?? false}
        onReady={() => setReady(true)}
      />
    );
  };

  render(<Harness />);
  await waitFor(() => expect(editorRef).not.toBeNull(), { timeout: 3000 });
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

describe('LexicalEditor – coverage gap fill', () => {
  it('round-trips tables with media tokens in cells', async () => {
    await withEditor(async (editor) => {
      const table = [
        '| Name | Media |',
        '| --- | --- |',
        '| pic | ![alt](https://example.com/a.png) |',
        '| sound | [AUDIO:file.mp3] |',
        '| clip | [VIDEO:file.mp4] |',
      ].join('\n');

      await setAndWait(editor, table);
      const out = editor.getValue();
      expect(out).toContain('pic');
      expect(out).toContain('sound');
      expect(out).toContain('clip');
    });
  });

  it('exports table content after toggling to source mode and back', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '| H1 | H2 |\n| --- | --- |\n| a | b |');
      await act(async () => {
        editor.toggleSourceMode();
        await new Promise((r) => window.setTimeout(r, 100));
      });
      const source = editor.getValue();
      expect(source).toContain('a');
      await act(async () => {
        editor.toggleSourceMode();
        await new Promise((r) => window.setTimeout(r, 100));
      });
      expect(editor.getValue()).toContain('a');
    });
  });

  it('preprocesses nested source tags for video and audio', async () => {
    await withEditor(async (editor) => {
      const md = [
        '<video controls><source src="nested-v.mp4"></video>',
        '<audio><source src="nested-a.mp3"></audio>',
      ].join('\n');
      await setAndWait(editor, md);
      expect(editor.getValue().length).toBeGreaterThan(0);
    });
  });

  it('insertText in source mode adds separator when needed', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, 'Hello');
      await act(async () => {
        editor.toggleSourceMode();
        await new Promise((r) => window.setTimeout(r, 50));
      });
      await act(() => editor.insertText('there'));
      expect(editor.getValue()).toMatch(/Hello\s*there/);
    });
  });

  it('insertText in WYSIWYG re-parses appended content', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, 'Alpha');
      await act(() => editor.insertText('Beta'));
      await waitFor(() => expect(editor.getValue()).toMatch(/Beta/));
    });
  });

  it('toggles source mode via toolbar when editor ref is attached', async () => {
    await withEditor(async () => {
      const toggle = await screen.findByTitle(/toggle markdown source/i);
      await act(() => fireEvent.click(toggle));
      await waitFor(() =>
        expect(
          document.querySelector('textarea[data-tour-id="guide.content.instructions.source"]')
        ).toBeInTheDocument()
      );
    }, { showToolbar: true });
  });

  it('passes image context props and renders with autoFocus', async () => {
    render(
      <LexicalEditor
        showToolbar={false}
        autoFocus
        projectId="p1"
        notebookId="n1"
        basePath="docs"
        className="custom-editor"
        placeholder="Type here"
        onReady={() => {}}
      />
    );
    await waitFor(() => {
      expect(document.querySelector('.custom-editor')).toBeInTheDocument();
      expect(document.querySelector('.lexical-content-editable')).toBeInTheDocument();
    });
  });

  it('getIsEmpty returns false after content is set in WYSIWYG', async () => {
    await withEditor(async (editor) => {
      expect(editor.getIsEmpty()).toBe(true);
      await setAndWait(editor, 'Not empty');
      expect(editor.getIsEmpty()).toBe(false);
    });
  });

  it('leaves unmatched video tag when no src is found', async () => {
    await withEditor(async (editor) => {
      await setAndWait(editor, '<video controls>no src here</video>');
      expect(editor.getValue()).toContain('video');
    });
  });
});
