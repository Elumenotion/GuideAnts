import React from 'react';
import { render, fireEvent, waitFor } from '../../../../test/test-utils';
import UserCell from '../UserCell';
import { describe, it, expect, vi } from 'vitest';

// Mock portal for ConfirmationDialog
vi.mock('react-dom', async () => {
  const actual = await vi.importActual<typeof import('react-dom')>('react-dom');
  return { ...actual, createPortal: (node: any) => node };
});

describe('UserCell', () => {
  it('calls onUndo after confirmation', async () => {
    const onUndo = vi.fn();
    const { getByLabelText, getByText } = render(
      <UserCell
        content="Hello"
        isLast={true}
        onUndo={onUndo}
      />
    );

    // Click undo icon
    const undoBtn = getByLabelText(/undo last turn/i);
    fireEvent.click(undoBtn);

    // Confirm dialog appears and click Undo button
    const confirmBtn = getByText(/^undo$/i);
    fireEvent.click(confirmBtn);

    await waitFor(() => {
      expect(onUndo).toHaveBeenCalled();
    });
  });
}); 