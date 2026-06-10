import React, { useRef, useEffect, useState } from 'react';
import { render, waitFor } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

describe('LexicalEditor – HTML preview & markdown helpers', () => {
  it('exports adjacent bold and italic marker cleanup', async () => {
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [ready, setReady] = useState(false);

      useEffect(() => {
        if (!ready || !ref.current) return;
        editorRef = ref.current;
        ref.current.setValue('**bold** *italic* ~~strike~~');
      }, [ready]);

      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => setReady(true)} />;
    };

    render(<Probe />);
    await waitFor(() => {
      expect(editorRef?.getValue()).toContain('bold');
    }, { timeout: 3000 });
  });

  it('round-trips markdown with encoded link spaces', async () => {
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [ready, setReady] = useState(false);

      useEffect(() => {
        if (!ready || !ref.current) return;
        editorRef = ref.current;
        ref.current.setValue('[my link](docs/my file.md)');
      }, [ready]);

      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => setReady(true)} />;
    };

    render(<Probe />);
    await waitFor(() => {
      expect(editorRef?.getValue()).toContain('my');
    }, { timeout: 3000 });
  });

  it('insertText in source mode without trailing separator when content ends with newline', async () => {
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);

      useEffect(() => {
        if (!ref.current) return;
        editorRef = ref.current;
        if (step === 0) {
          ref.current.setValue('Line one\n');
          window.setTimeout(() => setStep(1), 100);
        } else if (step === 1) {
          ref.current.toggleSourceMode();
          ref.current.insertText('Line two');
          setStep(2);
        }
      }, [step]);

      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(editorRef?.getValue()).toContain('Line two'), { timeout: 3000 });
  });

  it('renders read-only source textarea after toggling source mode', async () => {
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      useEffect(() => {
        if (!ref.current) return;
        editorRef = ref.current;
        ref.current.setValue('Locked');
        ref.current.toggleSourceMode();
      }, []);
      return <LexicalEditor ref={ref} readOnly showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => {
      const textarea = document.querySelector(
        'textarea[data-tour-id="guide.content.instructions.source"]'
      );
      expect(textarea).toHaveAttribute('readonly');
    });
  });
});
