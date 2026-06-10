import React, { useRef, useEffect, useState } from 'react';
import { render, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import '@testing-library/jest-dom';

const hoisted = vi.hoisted(() => ({
  capturedOnError: null as ((error: Error) => void) | null,
}));

vi.mock('@lexical/react/LexicalComposer', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@lexical/react/LexicalComposer')>();
  return {
    ...actual,
    LexicalComposer: (props: React.ComponentProps<typeof actual.LexicalComposer>) => {
      hoisted.capturedOnError = props.initialConfig?.onError ?? null;
      return actual.LexicalComposer(props);
    },
  };
});

import LexicalEditor from '../LexicalEditor';

describe('LexicalEditor – editorConfig.onError', () => {
  beforeEach(() => {
    hoisted.capturedOnError = null;
  });

  it('logs lexical errors via editorConfig.onError', async () => {
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

    render(<LexicalEditor showToolbar={false} onReady={() => {}} />);

    await waitFor(() => expect(hoisted.capturedOnError).not.toBeNull());

    const testError = new Error('Lexical update failed');
    hoisted.capturedOnError!(testError);

    expect(consoleSpy).toHaveBeenCalledWith('Lexical Error:', testError);
    consoleSpy.mockRestore();
  });
});
