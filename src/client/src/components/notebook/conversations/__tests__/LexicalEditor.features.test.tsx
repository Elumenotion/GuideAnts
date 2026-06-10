import React, { useRef, useEffect, useState } from 'react';
import { render, screen, fireEvent, waitFor } from '../../../../test/test-utils';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

function EditorHarness({
  initialMarkdown = '',
  showToolbar = true,
  onReady,
  submitButton,
  autoFocus = false,
}: {
  initialMarkdown?: string;
  showToolbar?: boolean;
  onReady?: () => void;
  submitButton?: { label: string; onClick: () => void };
  autoFocus?: boolean;
}) {
  const editorRef = useRef<LexicalEditorRef>(null);
  const [ready, setReady] = useState(false);

  useEffect(() => {
    if (!ready || !editorRef.current) return;
    if (initialMarkdown) {
      editorRef.current.setValue(initialMarkdown);
    }
  }, [ready, initialMarkdown]);

  return (
    <LexicalEditor
      ref={editorRef}
      placeholder="Test editor"
      showToolbar={showToolbar}
      autoFocus={autoFocus}
      onReady={() => {
        setReady(true);
        onReady?.();
      }}
      submitButton={submitButton}
    />
  );
}

describe('LexicalEditor – imperative API & modes', () => {
  it('calls onReady when editor mounts', async () => {
    const onReady = vi.fn();
    render(<EditorHarness onReady={onReady} />);
    await waitFor(() => expect(onReady).toHaveBeenCalled());
  });

  it('reports empty state for blank editor', async () => {
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      useEffect(() => {
        const id = setInterval(() => {
          if (ref.current) {
            editorRef = ref.current;
            clearInterval(id);
          }
        }, 10);
        return () => clearInterval(id);
      }, []);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(editorRef).not.toBeNull());
    expect(editorRef!.getIsEmpty()).toBe(true);
  });

  it('insertText appends content in WYSIWYG mode', async () => {
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      useEffect(() => {
        const tick = () => {
          if (ref.current) {
            editorRef = ref.current;
            ref.current.setValue('Hello');
            ref.current.insertText(' world');
          } else {
            requestAnimationFrame(tick);
          }
        };
        requestAnimationFrame(tick);
      }, []);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(editorRef?.getValue()).toContain('world'));
  });

  it('toggleSourceMode switches between WYSIWYG and source textarea', async () => {
    let editorRef: LexicalEditorRef | null = null;

    render(
      <EditorHarness
        initialMarkdown="**bold** text"
        onReady={() => {}}
      />
    );

    await waitFor(() => {
      const toggle = screen.getByTitle(/toggle markdown source/i);
      expect(toggle).toBeInTheDocument();
    });

    const toggle = screen.getByTitle(/toggle markdown source/i);
    fireEvent.click(toggle);

    await waitFor(() => {
      expect(screen.getByDisplayValue(/bold/)).toBeInTheDocument();
    });

    fireEvent.click(toggle);
    await waitFor(() => {
      expect(screen.queryByDisplayValue(/bold/)).not.toBeInTheDocument();
    });
  });

  it('renders HTML documents in iframe preview mode', async () => {
    const html = '<!DOCTYPE html><html><body><p>HTML body</p></body></html>';
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [done, setDone] = useState(false);
      useEffect(() => {
        if (ref.current && !done) {
          ref.current.setValue(html);
          setDone(true);
        }
      });
      editorRef = ref.current;
      return <LexicalEditor ref={ref} showToolbar={true} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => {
      expect(screen.getByTitle('HTML Preview')).toBeInTheDocument();
    });
    expect(editorRef?.isSourceMode()).toBe(false);
  });

  it('registerChangeListener receives markdown updates', async () => {
    const onChange = vi.fn();
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      useEffect(() => {
        if (!ref.current) return;
        editorRef = ref.current;
        const unregister = ref.current.registerChangeListener(onChange);
        ref.current.setValue('Initial');
        return unregister;
      }, []);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(onChange).toHaveBeenCalled(), { timeout: 2000 });
  });

  it('submits via Ctrl+Enter in source mode when submit button provided', async () => {
    const onSubmit = vi.fn();
    render(
      <EditorHarness
        submitButton={{ label: 'Save', onClick: onSubmit }}
        initialMarkdown="Source content"
      />
    );

    await waitFor(() => screen.getByTitle(/toggle markdown source/i));
    fireEvent.click(screen.getByTitle(/toggle markdown source/i));

    const textarea = await screen.findByDisplayValue(/Source content/);
    fireEvent.keyDown(textarea, { key: 'Enter', ctrlKey: true });
    expect(onSubmit).toHaveBeenCalled();
  });

  it('hides toolbar when showToolbar is false', async () => {
    render(<EditorHarness showToolbar={false} />);
    await waitFor(() => {
      expect(screen.queryByTitle(/bold/i)).not.toBeInTheDocument();
    });
  });
});
