import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import AssistantCell from '../AssistantCell';
import { ToastProvider } from '../../../common/Toast';

// Mock ChatMarkdownViewer component
vi.mock('../ChatMarkdownViewer', () => ({
  default: ({ text }: { text: string }) => <div data-testid="markdown-viewer">{text}</div>
}));

const renderWithToast = (component: React.ReactElement) => {
  return render(
    <ToastProvider>
      {component}
    </ToastProvider>
  );
};

describe('AssistantCell', () => {
  it('renders assistant message content', () => {
    renderWithToast(
      <AssistantCell
        content="Hello, I am an assistant"
        isLast={false}
      />
    );

    expect(screen.getByText('Hello, I am an assistant')).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Assistant message' })).toBeInTheDocument();
  });

  it('shows default assistant indicator when no avatar', () => {
    renderWithToast(
      <AssistantCell
        content="Test message"
        isLast={false}
      />
    );

    expect(screen.getByText('A')).toBeInTheDocument();
  });

  it('shows assistant avatar when provided', () => {
    renderWithToast(
      <AssistantCell
        content="Test message"
        isLast={false}
        avatarUrl="/test-avatar.png"
        assistantName="Test Assistant"
      />
    );

    const avatar = screen.getByAltText('Test Assistant');
    expect(avatar).toBeInTheDocument();
    expect(avatar.getAttribute('src')).toContain('/test-avatar.png');
  });

  it('shows streaming indicator when streaming and no content', () => {
    renderWithToast(
      <AssistantCell
        content=""
        isLast={true}
        isStreaming={true}
      />
    );

    expect(screen.getByText('Thinking...')).toBeInTheDocument();
    // Check for three bouncing dots by class
    const dots = document.querySelectorAll('.animate-bounce');
    expect(dots).toHaveLength(3);
  });

  it('renders content even when streaming if content exists', () => {
    renderWithToast(
      <AssistantCell
        content="Partial response..."
        isLast={true}
        isStreaming={true}
      />
    );

    expect(screen.getByText('Partial response...')).toBeInTheDocument();
    expect(screen.queryByText('Thinking...')).not.toBeInTheDocument();
  });

  it('does not show clickable cursor (edit functionality removed from cell click)', () => {
    const mockOnEditClick = vi.fn();
    
    renderWithToast(
      <AssistantCell
        content="Test message"
        isLast={true}
        onEditClick={mockOnEditClick}
      />
    );

    const cell = screen.getByRole('group', { name: 'Assistant message' });
    expect(cell).not.toHaveClass('cursor-pointer');
  });

  it('does not call onEditClick when cell is clicked (edit functionality removed)', async () => {
    const user = userEvent.setup();
    const mockOnEditClick = vi.fn();
    
    renderWithToast(
      <AssistantCell
        content="Test message"
        isLast={true}
        onEditClick={mockOnEditClick}
        isStreaming={false}
      />
    );

    const cell = screen.getByRole('group', { name: 'Assistant message' });
    await user.click(cell);

    expect(mockOnEditClick).not.toHaveBeenCalled();
  });

  it('does not call onEditClick when streaming', async () => {
    const user = userEvent.setup();
    const mockOnEditClick = vi.fn();
    
    renderWithToast(
      <AssistantCell
        content="Test message"
        isLast={true}
        onEditClick={mockOnEditClick}
        isStreaming={true}
      />
    );

    const cell = screen.getByRole('group', { name: 'Assistant message' });
    await user.click(cell);

    expect(mockOnEditClick).not.toHaveBeenCalled();
  });

  it('renders markdown content using MarkdownViewer', () => {
    renderWithToast(
      <AssistantCell
        content="**Bold text** with `code`"
        isLast={false}
      />
    );

    const markdownViewer = screen.getByTestId('markdown-viewer');
    expect(markdownViewer).toBeInTheDocument();
    expect(markdownViewer).toHaveTextContent('**Bold text** with `code`');
  });

  it('does not show edit icon button (edit functionality moved to full screen)', () => {
    const mockOnEditClick = vi.fn();
    renderWithToast(
      <AssistantCell content="Hello" isLast={true} onEditClick={mockOnEditClick} />
    );
    expect(screen.queryByLabelText('Edit message')).not.toBeInTheDocument();
  });

  it('shows full screen button', () => {
    renderWithToast(
      <AssistantCell content="Test message" isLast={true} />
    );
    expect(screen.getByLabelText('Full screen')).toBeInTheDocument();
  });

  it('updates content as tokens stream in', () => {
    const { rerender } = renderWithToast(
      <AssistantCell
        content=""
        isLast={true}
        isStreaming={true}
      />
    );

    // Initially shows thinking dots
    expect(screen.getByText('Thinking...')).toBeInTheDocument();

    // Re-render with first token
    rerender(
      <ToastProvider>
        <AssistantCell
          content="Hello"
          isLast={true}
          isStreaming={true}
        />
      </ToastProvider>
    );

    expect(screen.getByText('Hello')).toBeInTheDocument();
  });
}); 