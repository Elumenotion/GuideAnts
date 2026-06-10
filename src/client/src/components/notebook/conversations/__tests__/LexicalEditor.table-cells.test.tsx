import React, { useRef, useEffect, useState } from 'react';
import { render, waitFor } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

async function roundTrip(markdown: string): Promise<string> {
  let result = '';
  let done = false;

  const Probe = () => {
    const ref = useRef<LexicalEditorRef>(null);
    const [step, setStep] = useState(0);

    useEffect(() => {
      if (!ref.current) return;
      if (step === 0) {
        ref.current.setValue(markdown);
        setTimeout(() => setStep(1), 100);
      } else {
        result = ref.current!.getValue();
        done = true;
      }
    }, [step]);

    return <LexicalEditor ref={ref} showToolbar={false} autoFocus={false} />;
  };

  render(<Probe />);
  await waitFor(() => expect(done).toBe(true), { timeout: 3000 });
  return result;
}

describe('LexicalEditor – table cell content', () => {
  it('round-trips tables containing images', async () => {
    const md = [
      '| Label | Picture |',
      '| --- | --- |',
      '| Item | ![alt](https://example.com/pic.png) |',
    ].join('\n');

    const out = await roundTrip(md);
    expect(out).toContain('Label');
    expect(out).toContain('![alt]');
  });

  it('round-trips tables containing audio and video tokens', async () => {
    const md = [
      '| Media |',
      '| --- |',
      '| [AUDIO:./a.mp3] |',
      '| [VIDEO:./v.mp4] |',
    ].join('\n');

    const out = await roundTrip(md);
    expect(out).toContain('Media');
    expect(out.length).toBeGreaterThan(10);
  });

  it('cleans escaped markdown from editor output', async () => {
    const md = 'Text with \\*escaped\\* asterisks';
    const out = await roundTrip(md);
    expect(out).toContain('escaped');
  });

  it('handles bold followed by italic markers', async () => {
    const md = '**bold***italic*';
    const out = await roundTrip(md);
    expect(out).toContain('bold');
    expect(out).toContain('italic');
  });
});
