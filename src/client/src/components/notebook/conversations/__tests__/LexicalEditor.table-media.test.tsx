import React, { useRef, useEffect, useState } from 'react';
import { render, waitFor } from '../../../../test/test-utils';
import { describe, it, expect } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

async function roundTrip(markdown: string, assert: (value: string) => void) {
  let done = false;

  const Probe = () => {
    const ref = useRef<LexicalEditorRef>(null);
    const [step, setStep] = useState(0);

    useEffect(() => {
      if (!ref.current) return;
      if (step === 0) {
        ref.current.setValue(markdown);
        window.setTimeout(() => setStep(1), 150);
      } else if (step === 1) {
        assert(ref.current!.getValue());
        done = true;
      }
    }, [step]);

    return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
  };

  render(<Probe />);
  await waitFor(() => expect(done).toBe(true), { timeout: 5000 });
}

describe('LexicalEditor – table media round-trip', () => {
  it('round-trips table cells with audio and video tokens', async () => {
    const md = [
      '| Media | Clip |',
      '| --- | --- |',
      '| sound | [AUDIO:tune.mp3] |',
      '| motion | [VIDEO:clip.mp4] |',
    ].join('\n');

    await roundTrip(md, (value) => {
      expect(value).toContain('sound');
      expect(value).toContain('motion');
      expect(value.length).toBeGreaterThan(md.length / 2);
    });
  });

  it('round-trips table cells containing inline images', async () => {
    const md = [
      '| Name | Image |',
      '| --- | --- |',
      '| Item | ![pic](https://example.com/pic.png) |',
    ].join('\n');

    await roundTrip(md, (value) => {
      expect(value.toLowerCase()).toContain('pic');
    });
  });

  it('exports non-table markdown with blockquote and code fence', async () => {
    const md = '> quoted\n\n```js\nconst x = 1;\n```';

    await roundTrip(md, (value) => {
      expect(value).toContain('quoted');
      expect(value).toContain('const x = 1');
    });
  });

  it('insertText in WYSIWYG mode appends to existing content', async () => {
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);

      useEffect(() => {
        if (!ref.current) return;
        editorRef = ref.current;
        if (step === 0) {
          ref.current.setValue('Start');
          window.setTimeout(() => setStep(1), 100);
        } else if (step === 1) {
          ref.current.insertText('end');
          setStep(2);
        }
      }, [step]);

      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(editorRef?.getValue()).toMatch(/end/i), { timeout: 3000 });
  });
});
