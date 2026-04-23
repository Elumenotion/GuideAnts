import React, { useRef, useEffect, useState } from 'react';
import { render, waitFor } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

const FIXTURES: Record<string, string> = {
  'simple paragraphs': 'First line.\n\nSecond line.',
  'bold & italic': 'This is **bold** and *italic* text.',
  'images': 'An image: ![Alt](https://example.com/img.png)',
  'tables': '| H1       | H2       |\n|----------|----------|\n| C1       | C2       |',
  'code block': '```js\nconsole.log("hi");\n```',
  'multi newline': 'Line1.\n\n\nLine2 after blank.',
  'nested lists': '- Item 1\n- Sub 1\n- Sub 2\n- Item 2\n\n1. Sub A\n2. Sub B',
  'complex table': '| H1       | H2       | H3       |\n|----------|----------|----------|\n| R1C1     | R1C2     | R1C3     |\n| R2C1     | R2C2     | R2C3     |',
};

describe('LexicalEditor markdown round-trip – golden files', () => {
  Object.entries(FIXTURES).forEach(([name, markdown]) => {
    it(`preserves ${name}`, async () => {
      let roundTripped = '';
      let done = false;

      const TestHarness = () => {
        const ref = useRef<LexicalEditorRef>(null);
        const [phase, setPhase] = useState(0);

        useEffect(() => {
          if (!ref.current) return;
          if (phase === 0) {
            ref.current.setValue(markdown);
            setTimeout(() => setPhase(1), 20);
          } else if (phase === 1) {
            roundTripped = ref.current.getValue();
            done = true;
          }
        }, [phase]);

        return <LexicalEditor ref={ref} showToolbar={false} autoFocus={false} />;
      };

      render(<TestHarness />);

      await waitFor(() => expect(done).toBe(true), { timeout: 1000 });
      expect(roundTripped.trim()).toBe(markdown.trim());
    });
  });
}); 