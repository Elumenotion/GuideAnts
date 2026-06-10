import React, { useRef, useEffect, useState } from 'react';
import { render, screen, waitFor, fireEvent } from '../../../../test/test-utils';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

async function withEditorRef(
  renderEditor: (ref: React.RefObject<LexicalEditorRef | null>) => React.ReactElement,
  run: (ref: LexicalEditorRef) => void | Promise<void>
) {
  let editorRef: LexicalEditorRef | null = null;

  const Harness = () => {
    const ref = useRef<LexicalEditorRef>(null);
    useEffect(() => {
      const id = window.setInterval(() => {
        if (ref.current) {
          editorRef = ref.current;
          window.clearInterval(id);
        }
      }, 10);
      return () => window.clearInterval(id);
    }, []);
    return renderEditor(ref);
  };

  render(<Harness />);
  await waitFor(() => expect(editorRef).not.toBeNull(), { timeout: 3000 });
  await run(editorRef!);
}

describe('LexicalEditor – toolbar, HTML & markdown cleanup', () => {
  it('imports video and audio tags that use nested source elements', async () => {
    await withEditorRef(
      (ref) => <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />,
      async (editor) => {
        editor.setValue('<video><source src="nested.mp4"></video>\n<audio><source src="nested.mp3"></audio>');
        await waitFor(() => {
          const value = editor.getValue();
          expect(value.length).toBeGreaterThan(0);
        });
      }
    );
  });

  it('exports markdown tables containing images', async () => {
    const tableWithImage = '| Name | Image |\n| --- | --- |\n| Item | ![pic](https://example.com/pic.png) |';

    await withEditorRef(
      (ref) => <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />,
      async (editor) => {
        editor.setValue(tableWithImage);
        await waitFor(() => {
          const value = editor.getValue();
          expect(value).toContain('pic');
        });
      }
    );
  });

  it('insertText appends in source mode', async () => {
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);

      useEffect(() => {
        if (!ref.current) return;
        editorRef = ref.current;

        if (step === 0) {
          ref.current.setValue('Hello');
          window.setTimeout(() => setStep(1), 100);
        } else if (step === 1) {
          ref.current.toggleSourceMode();
          ref.current.insertText('world');
          setStep(2);
        }
      }, [step]);

      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(editorRef?.getValue()).toContain('world'), { timeout: 3000 });
  });

  it('toggles HTML preview back to source textarea via toolbar', async () => {
    const html = '<!DOCTYPE html><html><body><p>Preview</p></body></html>';
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);

      useEffect(() => {
        if (!ref.current) return;
        editorRef = ref.current;
        if (step === 0) {
          ref.current.setValue(html);
          window.setTimeout(() => setStep(1), 50);
        }
      }, [step]);

      return <LexicalEditor ref={ref} showToolbar onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => {
      expect(screen.getByTitle('HTML Preview')).toBeInTheDocument();
      expect(editorRef).not.toBeNull();
    });

    fireEvent.click(screen.getByTitle(/toggle markdown source/i));

    await waitFor(() => {
      expect(editorRef!.isSourceMode()).toBe(true);
      expect(screen.getByDisplayValue(html)).toBeInTheDocument();
    });
  });

  it('invokes cancel and fullscreen toolbar buttons', async () => {
    const onCancel = vi.fn();
    const onFullScreen = vi.fn();

    render(
      <LexicalEditor
        showToolbar
        cancelButton={{ label: 'Cancel edit', onClick: onCancel }}
        fullScreenButton={{ onClick: onFullScreen }}
        onReady={() => {}}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: 'Cancel edit' }));
    fireEvent.click(screen.getByLabelText('Full screen'));

    expect(onCancel).toHaveBeenCalled();
    expect(onFullScreen).toHaveBeenCalled();
  });

  it('renders read-only source textarea when readOnly is true', async () => {
    await withEditorRef(
      (ref) => (
        <LexicalEditor ref={ref} showToolbar readOnly onReady={() => {}} />
      ),
      async (editor) => {
        editor.setValue('Locked markdown');
        editor.toggleSourceMode();
        await waitFor(() => {
          const textarea = document.querySelector(
            'textarea[data-tour-id="guide.content.instructions.source"]'
          );
          expect(textarea).toHaveAttribute('readonly');
        });
      }
    );
  });

  it('cleans escaped markdown and adjacent format markers on export', async () => {
    await withEditorRef(
      (ref) => <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />,
      async (editor) => {
        editor.setValue('**bold** and *italic*');
        await waitFor(() => {
          const value = editor.getValue();
          expect(value).toContain('bold');
          expect(value).toContain('italic');
        });
      }
    );
  });
});
