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

/**
 * Tests ---------------------------------------------------------------------
 */

describe('UserCell – full-screen viewer', () => {
  it('toggles to full-screen view and back', async () => {
    const user = userEvent.setup();
    render(<UserCell content="Viewing message" isLast={true} />);

    await user.click(screen.getByLabelText('Full screen'));
    expect(screen.getByLabelText('Exit full screen')).toBeInTheDocument();

    await user.click(screen.getByLabelText('Exit full screen'));
    expect(screen.queryByLabelText('Exit full screen')).not.toBeInTheDocument();
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

    await user.click(screen.getByLabelText('Undo last turn'));
    const heading = screen.getByText('Undo Last Turn');
    expect(heading).toBeInTheDocument();

    const dialogContainer = heading.closest('div');
    fireEvent.keyDown(dialogContainer as HTMLElement, { key: 'Escape', code: 'Escape' });

    expect(screen.queryByText('Undo Last Turn')).not.toBeInTheDocument();
    expect(onUndo).not.toHaveBeenCalled();
  });
});
