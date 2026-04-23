import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import UserCell from '../UserCell';

// Mock ChatMarkdownViewer component
vi.mock('../ChatMarkdownViewer', () => ({
  default: ({ text }: { text: string }) => <div data-testid="markdown-viewer">{text}</div>
}));

// Mock createPortal to render directly in tests
vi.mock('react-dom', async () => {
  const actual = await vi.importActual('react-dom');
  return {
    ...actual,
    createPortal: (children: React.ReactNode) => children,
  };
});

describe('UserCell', () => {
  it('renders user message content', () => {
    render(
      <UserCell
        content="Hello, this is a test message"
        isLast={false}
      />
    );

    expect(screen.getByText('Hello, this is a test message')).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'User message' })).toBeInTheDocument();
  });

  it('shows user indicator avatar', () => {
    render(
      <UserCell
        content="Test message"
        isLast={false}
      />
    );

    expect(screen.getByText('U')).toBeInTheDocument();
  });

  it('shows undo button when isLast and onUndo provided', () => {
    const mockOnUndo = vi.fn();
    
    render(
      <UserCell
        content="Test message"
        isLast={true}
        onUndo={mockOnUndo}
      />
    );

    const undoButton = screen.getByLabelText('Undo last turn');
    expect(undoButton).toBeInTheDocument();
  });

  it('does not show undo button when not last message', () => {
    const mockOnUndo = vi.fn();
    
    render(
      <UserCell
        content="Test message"
        isLast={false}
        onUndo={mockOnUndo}
      />
    );

    expect(screen.queryByLabelText('Undo last turn')).not.toBeInTheDocument();
  });

  it('does not show undo button when no onUndo handler', () => {
    render(
      <UserCell
        content="Test message"
        isLast={true}
      />
    );

    expect(screen.queryByLabelText('Undo last turn')).not.toBeInTheDocument();
  });

  it('shows confirmation dialog when undo button is clicked', async () => {
    const user = userEvent.setup();
    const mockOnUndo = vi.fn();
    
    render(
      <UserCell
        content="Test message"
        isLast={true}
        onUndo={mockOnUndo}
      />
    );

    const undoButton = screen.getByLabelText('Undo last turn');
    await user.click(undoButton);

    // Should show confirmation dialog
    expect(screen.getByText('Undo Last Turn')).toBeInTheDocument();
    expect(screen.getByText('This will remove your last message and the assistant\'s response. This action cannot be undone.')).toBeInTheDocument();
    expect(screen.getByText('Undo')).toBeInTheDocument();
    expect(screen.getByText('Cancel')).toBeInTheDocument();

    // onUndo should not be called yet
    expect(mockOnUndo).not.toHaveBeenCalled();
  });

  it('calls onUndo when confirmation is accepted', async () => {
    const user = userEvent.setup();
    const mockOnUndo = vi.fn();
    
    render(
      <UserCell
        content="Test message"
        isLast={true}
        onUndo={mockOnUndo}
      />
    );

    // Click undo button to show confirmation
    const undoButton = screen.getByLabelText('Undo last turn');
    await user.click(undoButton);

    // Click confirm in the dialog
    const confirmButton = screen.getByText('Undo');
    await user.click(confirmButton);

    expect(mockOnUndo).toHaveBeenCalledTimes(1);
  });

  it('does not call onUndo when confirmation is cancelled', async () => {
    const user = userEvent.setup();
    const mockOnUndo = vi.fn();
    
    render(
      <UserCell
        content="Test message"
        isLast={true}
        onUndo={mockOnUndo}
      />
    );

    // Click undo button to show confirmation
    const undoButton = screen.getByLabelText('Undo last turn');
    await user.click(undoButton);

    // Click cancel in the dialog
    const cancelButton = screen.getByText('Cancel');
    await user.click(cancelButton);

    expect(mockOnUndo).not.toHaveBeenCalled();
  });

  it('renders markdown content using MarkdownViewer', () => {
    render(
      <UserCell
        content="**Bold text** and `code`"
        isLast={false}
      />
    );

    const markdownViewer = screen.getByTestId('markdown-viewer');
    expect(markdownViewer).toBeInTheDocument();
    expect(markdownViewer).toHaveTextContent('**Bold text** and `code`');
  });
}); 