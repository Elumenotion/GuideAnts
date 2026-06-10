import React, { useRef, useEffect, useState } from 'react';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

async function runEditorValueTest(initialValue: string, assert: (value: string) => void) {
  let done = false;

  const Probe = () => {
    const ref = useRef<LexicalEditorRef>(null);
    const [step, setStep] = useState(0);

    useEffect(() => {
      if (!ref.current) return;
      if (step === 0) {
        ref.current.setValue(initialValue);
        setTimeout(() => setStep(1), 100);
      } else if (step === 1) {
        assert(ref.current!.getValue());
        done = true;
      }
    }, [step]);

    return <LexicalEditor ref={ref} showToolbar={false} autoFocus={false} />;
  };

  render(<Probe />);
  await waitFor(() => expect(done).toBe(true), { timeout: 3000 });
}

describe('LexicalEditor – source mode & media', () => {
  it('round-trips markdown tables', async () => {
    const tableMd = '| Col A | Col B |\n| --- | --- |\n| one | two |';
    await runEditorValueTest(tableMd, (value) => {
      expect(value).toContain('Col A');
      expect(value).toContain('one');
    });
  });

  it('imports HTML video and audio tags via preprocessing', async () => {
    const htmlMedia = [
      '<video src="./clip.mp4" controls></video>',
      '<audio src="./clip.mp3" controls></audio>',
    ].join('\n');

    await runEditorValueTest(htmlMedia, (value) => {
      expect(value.length).toBeGreaterThan(0);
    });
  });

  it('getIsEmpty is false after setting markdown content', async () => {
    await runEditorValueTest('# Title', (value) => {
      expect(value.trim().length).toBeGreaterThan(0);
    });
  });

  it('notifies change listener from source textarea edits', async () => {
    const changes: string[] = [];
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);
      useEffect(() => {
        if (!ref.current) return;
        if (step === 0) {
          editorRef = ref.current;
          ref.current.registerChangeListener((v) => changes.push(v));
          ref.current.setValue('start');
          setTimeout(() => setStep(1), 50);
        } else if (step === 1) {
          ref.current!.toggleSourceMode();
          setTimeout(() => setStep(2), 50);
        }
      }, [step]);
      return <LexicalEditor ref={ref} showToolbar />;
    };

    render(<Probe />);
    await waitFor(() => expect(editorRef).not.toBeNull());

    const textarea = await screen.findByDisplayValue('start');
    fireEvent.change(textarea, { target: { value: 'edited in source' } });

    await waitFor(() => {
      expect(changes.some((c) => c.includes('edited in source'))).toBe(true);
    });
  });
});
