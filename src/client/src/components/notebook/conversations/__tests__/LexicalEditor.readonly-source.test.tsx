import React, { useRef, useEffect, useState } from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

describe('LexicalEditor – readonly & source editing', () => {
  it('keeps WYSIWYG content editable when not readOnly', async () => {
    render(<LexicalEditor showToolbar={false} autoFocus={false} onReady={() => {}} />);
    await waitFor(() => {
      expect(document.querySelector('[contenteditable="true"]')).toBeInTheDocument();
    });
  });

  it('imports unmatched html video tag unchanged in markdown output', async () => {
    let output = '';
    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [done, setDone] = useState(false);
      useEffect(() => {
        if (!ref.current || done) return;
        ref.current.setValue('<video controls>no src</video>');
        window.setTimeout(() => {
          output = ref.current!.getValue();
          setDone(true);
        }, 150);
      }, [done]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(output.length).toBeGreaterThan(0), { timeout: 3000 });
  });
});
