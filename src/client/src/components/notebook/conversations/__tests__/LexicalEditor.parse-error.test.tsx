import React, { useRef, useEffect, useState } from 'react';
import { render, waitFor } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';

const hoisted = vi.hoisted(() => ({
  updateThrowAfter: 0,
  updateCallCount: 0,
}));

vi.mock('@lexical/react/LexicalComposerContext', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@lexical/react/LexicalComposerContext')>();
  return {
    ...actual,
    useLexicalComposerContext: () => {
      const [editor] = actual.useLexicalComposerContext();
      const originalUpdate = editor.update.bind(editor);
      const wrappedEditor = Object.create(editor) as typeof editor;
      Object.assign(wrappedEditor, editor, {
        update: (...args: Parameters<typeof editor.update>) => {
          hoisted.updateCallCount += 1;
          if (hoisted.updateThrowAfter > 0 && hoisted.updateCallCount >= hoisted.updateThrowAfter) {
            throw new Error('Simulated sync update failure');
          }
          return originalUpdate(...args);
        },
      });
      return [wrappedEditor];
    },
  };
});

import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

describe('LexicalEditor – toggleSourceMode parse error', () => {
  it('stays in source mode and logs when editor.update throws during parse', async () => {
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    hoisted.updateCallCount = 0;
    hoisted.updateThrowAfter = 2;

    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);
      useEffect(() => {
        if (!ref.current) return;
        editorRef = ref.current;
        if (step === 0) {
          ref.current.setValue('**valid** markdown');
          window.setTimeout(() => setStep(1), 250);
        } else if (step === 1) {
          ref.current.toggleSourceMode();
          window.setTimeout(() => setStep(2), 100);
        } else if (step === 2) {
          ref.current.toggleSourceMode();
        }
      }, [step]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);

    await waitFor(() => {
      expect(consoleSpy).toHaveBeenCalledWith(
        'Error parsing markdown:',
        expect.objectContaining({ message: 'Simulated sync update failure' })
      );
    });

    expect(editorRef!.isSourceMode()).toBe(true);
    consoleSpy.mockRestore();
  });
});
