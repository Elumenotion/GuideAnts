import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import FullScreenEditor from '../FullScreenEditor';

vi.mock('../LexicalEditor', () => ({
  default: vi.fn().mockImplementation(({ ref, submitButton, onReady }) => {
    React.useEffect(() => {
      onReady?.();
    }, [onReady]);

    React.useImperativeHandle(ref, () => ({
      setValue: vi.fn(),
      getValue: vi.fn(() => {
        throw new Error('read failed');
      }),
      getIsEmpty: vi.fn(() => false),
      registerChangeListener: vi.fn(() => () => {}),
    }));

    return (
      <div data-testid="lexical-editor">
        {submitButton && (
          <button type="button" onClick={submitButton.onClick}>
            {submitButton.label}
          </button>
        )}
      </div>
    );
  }),
}));

vi.mock('react-dom', () => ({
  ...vi.importActual('react-dom'),
  createPortal: (node: React.ReactNode) => node,
}));

describe('FullScreenEditor – read failure edge cases', () => {
  it('shows value error when editor read fails during save', async () => {
    const user = userEvent.setup();
    const mockOnSave = vi.fn();

    render(
      <FullScreenEditor
        content="Broken read"
        onSave={mockOnSave}
        onCancel={vi.fn()}
        mode="edit"
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(screen.getByText('Failed to read editor content. Please try again.')).toBeInTheDocument();
    expect(mockOnSave).not.toHaveBeenCalled();
  });

  it('falls back to current content when exit read fails', async () => {
    const user = userEvent.setup();
    const mockOnCancel = vi.fn();

    render(
      <FullScreenEditor
        content="Fallback content"
        onSave={vi.fn()}
        onCancel={mockOnCancel}
        mode="compose"
      />,
    );

    await user.click(screen.getByLabelText('Exit full screen'));
    expect(mockOnCancel).toHaveBeenCalledWith('Fallback content');
  });
});
