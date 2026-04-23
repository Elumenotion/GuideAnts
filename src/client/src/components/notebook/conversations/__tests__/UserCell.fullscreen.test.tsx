import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { fireEvent } from '@testing-library/react';

import UserCell from '../UserCell';

// Re-use custom RTL render wrapper
import { render, screen } from '../../../../test/test-utils';

/**
 * Shared mocks --------------------------------------------------------------
 */

// Mock markdown viewer to avoid heavy markdown processing
vi.mock('../ChatMarkdownViewer', () => ({
  default: ({ text }: { text: string }) => <div data-testid="markdown-viewer">{text}</div>
}));

// Stub portal creation so components render in-tree
vi.mock('react-dom', async () => {
  const actual = await vi.importActual<typeof import('react-dom')>('react-dom');
  return {
    ...actual,
    createPortal: (node: React.ReactNode) => node,
  };
});

// Mock FullScreenEditor – expose save / cancel controls to drive callbacks
vi.mock('../FullScreenEditor', () => ({
  default: ({ content, onSave, onCancel }: { content: string; onSave: (c: string) => void; onCancel: () => void }) => (
    <div data-testid="mock-fse">
      <span>{content}</span>
      <button onClick={() => onSave(`${content} (edited)`)} data-testid="fse-save">save</button>
      <button onClick={onCancel} data-testid="fse-cancel">cancel</button>
    </div>
  ),
}));

/**
 * Tests ---------------------------------------------------------------------
 */

describe('UserCell – full-screen & edit interactions', () => {
  it('toggles to full-screen view and back', async () => {
    const user = userEvent.setup();
    render(<UserCell content="Viewing message" isLast={true} />);

    // Open full-screen overlay
    await user.click(screen.getByLabelText('Full screen'));
    expect(screen.getByLabelText('Exit full screen')).toBeInTheDocument();

    // Exit overlay
    await user.click(screen.getByLabelText('Exit full screen'));
    expect(screen.queryByLabelText('Exit full screen')).not.toBeInTheDocument();
  });

  it('shows full-screen viewer (no in-place edit path) and allows exit', async () => {
    const user = userEvent.setup();
    const onEdit = vi.fn();

    render(<UserCell content="Original" isLast={true} onEdit={onEdit} />);

    await user.click(screen.getByLabelText('Full screen'));
    expect(screen.getByLabelText('Exit full screen')).toBeInTheDocument();
    // Ensure no edit UI is present in viewer-only mode
    expect(screen.queryByLabelText('Edit message')).not.toBeInTheDocument();
    expect(screen.queryByTestId('mock-fse')).not.toBeInTheDocument();

    await user.click(screen.getByLabelText('Exit full screen'));
    expect(screen.queryByLabelText('Exit full screen')).not.toBeInTheDocument();
    expect(onEdit).not.toHaveBeenCalled();
  });

  it('exits full-screen without calling onEdit', async () => {
    const user = userEvent.setup();
    const onEdit = vi.fn();

    render(<UserCell content="Original" isLast={true} onEdit={onEdit} />);

    await user.click(screen.getByLabelText('Full screen'));
    await user.click(screen.getByLabelText('Exit full screen'));

    expect(onEdit).not.toHaveBeenCalled();
  });
});

describe('UserCell – avatar initials', () => {
  it('shows initials derived from userName', () => {
    render(<UserCell content="Hi" isLast={false} userName="Ada Lovelace" />);
    expect(screen.getByText('AL')).toBeInTheDocument();
  });

  it('falls back to email initials when name missing', () => {
    render(<UserCell content="Hi" isLast={false} userEmail="alice@example.com" />);
    expect(screen.getByText('A')).toBeInTheDocument();
  });
});

describe('UserCell – undo dialog extra path', () => {
  it('closes confirmation dialog with Escape key without calling onUndo', async () => {
    const user = userEvent.setup();
    const onUndo = vi.fn();

    render(<UserCell content="Hello" isLast={true} onUndo={onUndo} />);

    // Open dialog
    await user.click(screen.getByLabelText('Undo last turn'));
    const heading = screen.getByText('Undo Last Turn');
    expect(heading).toBeInTheDocument();

    // Fire Escape on the dialog container to trigger handler
    const dialogContainer = heading.closest('div');
    fireEvent.keyDown(dialogContainer as HTMLElement, { key: 'Escape', code: 'Escape' });

    expect(screen.queryByText('Undo Last Turn')).not.toBeInTheDocument();
    expect(onUndo).not.toHaveBeenCalled();
  });
}); 