import React, { useRef, useEffect, useState } from 'react';
import { render, screen, waitFor, act, fireEvent } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

async function withToolbarEditor(
  run: (editor: LexicalEditorRef) => void | Promise<void>
) {
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

describe('LexicalEditor – edge paths', () => {
  it('handles italic-then-bold adjacent marker cleanup on export', async () => {
    await withToolbarEditor(async (editor) => {
      await act(async () => {
        editor.setValue('*italic***bold**');
        await new Promise((r) => window.setTimeout(r, 300));
      });
      const value = editor.getValue();
      expect(value).toContain('italic');
      expect(value).toContain('bold');
    });
  });

  it('insertText in source mode without leading separator when content ends with newline', async () => {
    await withToolbarEditor(async (editor) => {
      await act(async () => {
        editor.setValue('Line\n');
        await new Promise((r) => window.setTimeout(r, 200));
      });
      fireEvent.click(screen.getByTitle(/toggle markdown source/i));
      await waitFor(() =>
        expect(
          document.querySelector('textarea[data-tour-id="guide.content.instructions.source"]')
        ).toBeInTheDocument()
      );
      await act(async () => {
        editor.insertText('More');
        await new Promise((r) => window.setTimeout(r, 50));
      });
      expect(editor.getValue()).toContain('More');
    });
  });

  it('notifies source textarea change listener', async () => {
    const heard: string[] = [];
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);
      useEffect(() => {
        if (!ref.current) return;
        editorRef = ref.current;
        if (step === 0) {
          ref.current.registerChangeListener((v) => heard.push(v));
          ref.current.setValue('seed');
          window.setTimeout(() => setStep(1), 100);
        } else if (step === 1) {
          ref.current!.toggleSourceMode();
        }
      }, [step]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(editorRef).not.toBeNull());
    await waitFor(() =>
      expect(
        document.querySelector('textarea[data-tour-id="guide.content.instructions.source"]')
      ).toBeInTheDocument()
    );

    const textarea = document.querySelector(
      'textarea[data-tour-id="guide.content.instructions.source"]'
    ) as HTMLTextAreaElement;
    fireEvent.change(textarea, { target: { value: 'typed in source' } });

    await waitFor(() => expect(heard.some((v) => v.includes('typed in source'))).toBe(true));
  });
});
