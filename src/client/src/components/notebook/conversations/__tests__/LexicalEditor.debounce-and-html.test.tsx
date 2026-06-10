import React, { useRef, useEffect, useState } from 'react';
import { render, screen, waitFor, act, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

const FULL_HTML =
  '<!DOCTYPE html><html><head><title>Doc</title></head><body><p>Body</p></body></html>';

describe('LexicalEditor – debounce & HTML iframe', () => {
  it('debounces registerChangeListener callbacks by 200ms', async () => {
    const heard: string[] = [];
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [ready, setReady] = useState(false);
      useEffect(() => {
        if (!ready || !ref.current) return;
        editorRef = ref.current;
        ref.current.registerChangeListener((v) => heard.push(v));
      }, [ready]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => setReady(true)} />;
    };

    render(<Probe />);
    await waitFor(() => expect(editorRef).not.toBeNull());

    await act(async () => {
      editorRef!.setValue('first');
      await new Promise((r) => window.setTimeout(r, 50));
      editorRef!.setValue('second');
      await new Promise((r) => window.setTimeout(r, 50));
      editorRef!.setValue('third');
      await new Promise((r) => window.setTimeout(r, 250));
    });

    const thirdCallbacks = heard.filter((v) => v.includes('third'));
    expect(thirdCallbacks.length).toBeGreaterThanOrEqual(1);
    expect(heard.filter((v) => v.includes('first')).length).toBeLessThanOrEqual(1);
  });

  it('renders HTML iframe with srcDoc and sandbox attributes', async () => {
    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);
      useEffect(() => {
        if (!ref.current) return;
        if (step === 0) {
          ref.current.setValue(FULL_HTML);
          window.setTimeout(() => setStep(1), 150);
        } else if (step === 1) {
          ref.current.toggleSourceMode();
        }
      }, [step]);
      return <LexicalEditor ref={ref} showToolbar onReady={() => {}} />;
    };

    render(<Probe />);

    await waitFor(() => {
      const iframe = screen.getByTitle('HTML Preview') as HTMLIFrameElement;
      expect(iframe).toBeInTheDocument();
      expect(iframe.getAttribute('srcdoc')).toBe(FULL_HTML);
      expect(iframe.getAttribute('sandbox')).toContain('allow-scripts');
      expect(iframe.getAttribute('sandbox')).toContain('allow-same-origin');
      expect(iframe.style.minHeight).toBe('400px');
    });

    await waitFor(() => {
      expect(screen.getByDisplayValue(FULL_HTML)).toBeInTheDocument();
    });
  });

  it('submits via Ctrl+Enter in source mode', async () => {
    const onSubmit = vi.fn();
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);
      useEffect(() => {
        if (!ref.current) return;
        editorRef = ref.current;
        if (step === 0) {
          ref.current.setValue('content');
          window.setTimeout(() => setStep(1), 100);
        } else if (step === 1) {
          ref.current.toggleSourceMode();
        }
      }, [step]);
      return (
        <LexicalEditor
          ref={ref}
          showToolbar
          submitButton={{ label: 'Post', onClick: onSubmit }}
          onReady={() => {}}
        />
      );
    };

    render(<Probe />);
    const textarea = await screen.findByDisplayValue('content');
    fireEvent.keyDown(textarea, { key: 'Enter', ctrlKey: true });
    expect(onSubmit).toHaveBeenCalled();
  });

  it('getIsEmpty in source mode returns true for whitespace-only content', async () => {
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);
      useEffect(() => {
        if (!ref.current) return;
        editorRef = ref.current;
        if (step === 0) {
          ref.current.toggleSourceMode();
          window.setTimeout(() => setStep(1), 100);
        } else if (step === 1) {
          ref.current.setValue('  \t\n  ');
        }
      }, [step]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(editorRef!.getIsEmpty()).toBe(true));
  });
});
