import React, { useRef, useEffect, useState } from 'react';
import { render, waitFor } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';

const hoisted = vi.hoisted(() => ({
  convertToReturn: '',
}));

vi.mock('@lexical/markdown', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@lexical/markdown')>();
  return {
    ...actual,
    $convertToMarkdownString: () => hoisted.convertToReturn,
  };
});

import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

async function withEditor(run: (editor: LexicalEditorRef) => void) {
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
  await waitFor(() => expect(editorRef).not.toBeNull());
  run(editorRef!);
}

describe('LexicalEditor – markdown export helpers via mocked convert', () => {
  it('decodes HTML numeric entities in exported markdown', async () => {
    hoisted.convertToReturn = 'hello&#32;world&#65;';
    await withEditor((editor) => {
      editor.setValue('ignored');
      expect(editor.getValue()).toBe('hello worldA');
    });
  });

  it('fixes bold-then-italic adjacent marker pattern', async () => {
    hoisted.convertToReturn = '**bold*italic***';
    await withEditor((editor) => {
      editor.setValue('ignored');
      expect(editor.getValue()).toBe('**bold***italic*');
    });
  });

  it('fixes italic-then-bold adjacent marker pattern', async () => {
    hoisted.convertToReturn = '*italic**bold***';
    await withEditor((editor) => {
      editor.setValue('ignored');
      expect(editor.getValue()).toBe('*italic***bold**');
    });
  });

  it('applies cleanup in getIsEmpty WYSIWYG path', async () => {
    hoisted.convertToReturn = '   ';
    await withEditor((editor) => {
      expect(editor.getIsEmpty()).toBe(true);
    });
  });

  it('applies cleanup in registerChangeListener debounced callback', async () => {
    const heard: string[] = [];
    await withEditor((editor) => {
      editor.registerChangeListener((v) => heard.push(v));
      hoisted.convertToReturn = 'debounced&#32;value';
      editor.setValue('trigger');
    });
    await waitFor(
      () => expect(heard.some((v) => v === 'debounced value')).toBe(true),
      { timeout: 1000 }
    );
  });
});
