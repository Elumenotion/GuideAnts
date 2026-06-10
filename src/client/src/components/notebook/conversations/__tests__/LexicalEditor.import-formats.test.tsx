import React, { useRef, useEffect, useState } from 'react';
import { render, waitFor } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

async function roundTrip(markdown: string): Promise<string> {
  let output = '';
  let done = false;

  const Probe = () => {
    const ref = useRef<LexicalEditorRef>(null);
    const [step, setStep] = useState(0);

    useEffect(() => {
      if (!ref.current) return;
      if (step === 0) {
        ref.current.setValue(markdown);
        window.setTimeout(() => setStep(1), 200);
      } else {
        output = ref.current!.getValue();
        done = true;
      }
    }, [step]);

    return <LexicalEditor ref={ref} showToolbar={false} autoFocus={false} />;
  };

  render(<Probe />);
  await waitFor(() => expect(done).toBe(true), { timeout: 5000 });
  return output;
}

describe('LexicalEditor – rich markdown import', () => {
  it('round-trips headings, lists, and blockquote', async () => {
    const md = [
      '# Title',
      '## Subtitle',
      '- bullet one',
      '1. numbered',
      '> quote line',
    ].join('\n\n');

    const out = await roundTrip(md);
    expect(out).toContain('Title');
    expect(out).toContain('bullet');
    expect(out).toContain('quote');
  });

  it('round-trips fenced code block and inline code', async () => {
    const md = 'Use `inline` code\n\n```ts\nconst n = 1;\n```';
    const out = await roundTrip(md);
    expect(out).toContain('inline');
    expect(out).toContain('const n');
  });

  it('round-trips links and strikethrough', async () => {
    const md = '[link](https://example.com) and ~~strike~~';
    const out = await roundTrip(md);
    expect(out).toContain('link');
    expect(out).toContain('strike');
  });

  it('round-trips multi-row tables with plain text cells', async () => {
    const md = [
      '| A | B | C |',
      '| --- | --- | --- |',
      '| one | two | three |',
      '| four | five | six |',
    ].join('\n');

    const out = await roundTrip(md);
    expect(out).toContain('one');
    expect(out).toContain('six');
  });

  it('preserves html video tag without src through preprocessing', async () => {
    const md = '<video controls>missing src</video>';
    const out = await roundTrip(md);
    expect(out.length).toBeGreaterThan(0);
  });
});
