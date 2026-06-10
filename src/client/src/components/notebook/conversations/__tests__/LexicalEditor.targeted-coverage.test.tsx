import React, { useRef, useEffect, useState } from 'react';
import { render, screen, waitFor, act, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import LexicalEditor, { LexicalEditorRef } from '../LexicalEditor';

const FULL_HTML =
  '<!DOCTYPE html><html><head><title>T</title></head><body><p>Full doc</p></body></html>';

const SOURCE_TEXTAREA = 'textarea[data-tour-id="guide.content.instructions.source"]';

describe('LexicalEditor – targeted coverage', () => {
  it('detects HTML documents that start with <html> without doctype', async () => {
    const html = '<html><body><p>No doctype</p></body></html>';

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [done, setDone] = useState(false);
      useEffect(() => {
        if (ref.current && !done) {
          ref.current.setValue(html);
          setDone(true);
        }
      });
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(screen.getByTitle('HTML Preview')).toBeInTheDocument());
  });

  it('setValue with full HTML document skips lexical parsing and shows iframe preview', async () => {
    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [done, setDone] = useState(false);
      useEffect(() => {
        if (ref.current && !done) {
          ref.current.setValue(FULL_HTML);
          setDone(true);
        }
      });
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => {
      expect(screen.getByTitle('HTML Preview')).toBeInTheDocument();
      expect(document.querySelector('.lexical-content-editable')).not.toBeInTheDocument();
    });
  });

  it('getValue in source mode returns raw textarea content', async () => {
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);
      useEffect(() => {
        if (!ref.current) return;
        editorRef = ref.current;
        if (step === 0) {
          ref.current.setValue('**markdown** body');
          window.setTimeout(() => setStep(1), 200);
        } else if (step === 1) {
          ref.current.toggleSourceMode();
          window.setTimeout(() => setStep(2), 150);
        }
      }, [step]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(document.querySelector(SOURCE_TEXTAREA)).toBeInTheDocument());
    expect(editorRef!.getValue()).toContain('markdown');
  });

  it('getIsEmpty treats whitespace-only source textarea as empty', async () => {
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
          ref.current.setValue('   \n  ');
          window.setTimeout(() => setStep(2), 50);
        }
      }, [step]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(editorRef).not.toBeNull());
    await waitFor(() => expect(editorRef!.getIsEmpty()).toBe(true));
  });

  it('toggleSourceMode round-trips full HTML between iframe preview and source textarea', async () => {
    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);
      useEffect(() => {
        if (!ref.current) return;
        if (step === 0) {
          ref.current.setValue(FULL_HTML);
          window.setTimeout(() => setStep(1), 100);
        } else if (step === 1) {
          ref.current.toggleSourceMode();
          window.setTimeout(() => setStep(2), 150);
        } else if (step === 2) {
          ref.current.toggleSourceMode();
          window.setTimeout(() => setStep(3), 150);
        }
      }, [step]);
      return <LexicalEditor ref={ref} showToolbar onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(screen.getByTitle('HTML Preview')).toBeInTheDocument());
    await waitFor(() => {
      const textarea = screen.getByDisplayValue(FULL_HTML);
      expect(textarea).toHaveValue(FULL_HTML);
    });
    await waitFor(() => expect(screen.getByTitle('HTML Preview')).toBeInTheDocument());
    expect(screen.queryByDisplayValue(FULL_HTML)).not.toBeInTheDocument();
  });

  it('toggleSourceMode from HTML source back to preview without parsing markdown', async () => {
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);
      useEffect(() => {
        if (!ref.current) return;
        editorRef = ref.current;
        if (step === 0) {
          ref.current.setValue(FULL_HTML);
          window.setTimeout(() => setStep(1), 100);
        } else if (step === 1) {
          ref.current.toggleSourceMode();
          window.setTimeout(() => setStep(2), 150);
        } else if (step === 2) {
          ref.current.toggleSourceMode();
        }
      }, [step]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(document.querySelector(SOURCE_TEXTAREA)).toBeInTheDocument());
    await waitFor(() => {
      expect(screen.getByTitle('HTML Preview')).toBeInTheDocument();
      expect(document.querySelector('.lexical-content-editable')).not.toBeInTheDocument();
    });
    expect(editorRef!.getValue()).toBe(FULL_HTML);
  });

  it('insertText in WYSIWYG mode adds separator when appending to existing text', async () => {
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);
      useEffect(() => {
        if (!ref.current) return;
        editorRef = ref.current;
        if (step === 0) {
          ref.current.setValue('Hello');
          window.setTimeout(() => setStep(1), 200);
        } else if (step === 1) {
          ref.current.insertText('World');
        }
      }, [step]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(editorRef?.getValue()).toMatch(/Hello\s+World/));
  });

  it('exports TABLE_TRANSFORMER cells with image, audio, and video HTML', async () => {
    let editorRef: LexicalEditorRef | null = null;
    const table = [
      '| Kind | Asset |',
      '| --- | --- |',
      '| img | ![alt text](https://cdn.test/photo.png) |',
      '| aud | [AUDIO:track.mp3] |',
      '| vid | [VIDEO:clip.mp4] |',
    ].join('\n');

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [ready, setReady] = useState(false);
      useEffect(() => {
        if (ready && ref.current) {
          editorRef = ref.current;
          ref.current.setValue(table);
        }
      }, [ready]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => setReady(true)} />;
    };

    render(<Probe />);
    await waitFor(() => expect(editorRef).not.toBeNull());
    await waitFor(() => {
      const exported = editorRef!.getValue();
      expect(exported).toContain('![alt text](https://cdn.test/photo.png)');
      expect(exported).toMatch(/<audio[^>]+src="track\.mp3"[^>]*controls/i);
      expect(exported).toMatch(/<video[^>]+src="clip\.mp4"[^>]*controls/i);
    });
  });

  it('insertText in source mode appends without extra separator when content ends with newline', async () => {
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
        }
      }, [step]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(editorRef?.getValue()).toBe('Line one\nLine two'));
  });

  it('submits via Meta+Enter in source mode', async () => {
    const onSubmit = vi.fn();

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);
      useEffect(() => {
        if (!ref.current) return;
        if (step === 0) {
          ref.current.setValue('Submit me');
          window.setTimeout(() => setStep(1), 100);
        } else if (step === 1) {
          ref.current.toggleSourceMode();
        }
      }, [step]);
      return (
        <LexicalEditor
          ref={ref}
          showToolbar
          submitButton={{ label: 'Save', onClick: onSubmit }}
          onReady={() => {}}
        />
      );
    };

    render(<Probe />);
    const textarea = await screen.findByDisplayValue('Submit me');
    fireEvent.keyDown(textarea, { key: 'Enter', metaKey: true });
    expect(onSubmit).toHaveBeenCalled();
  });

  it('insertText in source mode adds space separator between words', async () => {
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [step, setStep] = useState(0);
      useEffect(() => {
        if (!ref.current) return;
        editorRef = ref.current;
        if (step === 0) {
          ref.current.setValue('Hello');
          window.setTimeout(() => setStep(1), 150);
        } else if (step === 1) {
          ref.current.toggleSourceMode();
          window.setTimeout(() => setStep(2), 150);
        } else if (step === 2) {
          ref.current.insertText('there');
        }
      }, [step]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => {
      const textarea = document.querySelector(SOURCE_TEXTAREA) as HTMLTextAreaElement;
      expect(textarea?.value).toMatch(/Hello\s+there/);
    });
  });

  it('setValue while in source mode updates textarea without touching lexical tree', async () => {
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
          ref.current.setValue('Only in source');
        }
      }, [step]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => {}} />;
    };

    render(<Probe />);
    await waitFor(() => expect(screen.getByDisplayValue('Only in source')).toBeInTheDocument());
    expect(document.querySelector('.lexical-content-editable')).not.toBeInTheDocument();
  });

  it('leaves audio and video tags unchanged when no src is present', async () => {
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [ready, setReady] = useState(false);
      useEffect(() => {
        if (ready && ref.current) {
          editorRef = ref.current;
          ref.current.setValue('<video controls></video>\n<audio controls></audio>');
        }
      }, [ready]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => setReady(true)} />;
    };

    render(<Probe />);
    await waitFor(() => {
      const value = editorRef!.getValue();
      expect(value).toContain('<video');
      expect(value).toContain('<audio');
    });
  });

  it('exports table rows with empty cells', async () => {
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [ready, setReady] = useState(false);
      useEffect(() => {
        if (ready && ref.current) {
          editorRef = ref.current;
          ref.current.setValue('| A | B |\n| --- | --- |\n|  | filled |');
        }
      }, [ready]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => setReady(true)} />;
    };

    render(<Probe />);
    await waitFor(() => expect(editorRef!.getValue()).toContain('filled'));
  });

  it('unregisterChangeListener stops callbacks', async () => {
    const heard: string[] = [];
    let editorRef: LexicalEditorRef | null = null;

    const Probe = () => {
      const ref = useRef<LexicalEditorRef>(null);
      const [ready, setReady] = useState(false);
      useEffect(() => {
        if (!ready || !ref.current) return;
        editorRef = ref.current;
        const unregister = ref.current.registerChangeListener((v) => heard.push(v));
        ref.current.setValue('initial');
        window.setTimeout(() => unregister(), 50);
      }, [ready]);
      return <LexicalEditor ref={ref} showToolbar={false} onReady={() => setReady(true)} />;
    };

    render(<Probe />);
    await waitFor(() => expect(editorRef).not.toBeNull());
    await act(async () => {
      await new Promise((r) => window.setTimeout(r, 400));
      editorRef!.setValue('after unregister');
      await new Promise((r) => window.setTimeout(r, 400));
    });

    expect(heard.some((v) => v.includes('initial'))).toBe(true);
    expect(heard.some((v) => v.includes('after unregister'))).toBe(false);
  });
});
