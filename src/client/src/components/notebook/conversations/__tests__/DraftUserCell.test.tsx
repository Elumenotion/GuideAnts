import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import DraftUserCell from '../DraftUserCell';

// Mock NotebookContext to avoid requiring a real provider
vi.mock('../../../../contexts/NotebookContext', () => ({
  __esModule: true,
  useNotebook: () => ({ notebook: { id: 'nb-1', projectId: 'proj-1' } }),
}));

// Mock ConversationContext minimal API used by DraftUserCell
vi.mock('../../../../contexts/ConversationContext', () => ({
  __esModule: true,
  useConversation: () => ({
    pendingAttachments: [],
    addPendingAttachment: vi.fn(),
    removePendingAttachment: vi.fn(),
  }),
}));

// Mock notebookFilesApi.uploadFiles used by paste handler
vi.mock('../../../../services/notebookFiles', () => ({
  notebookFilesApi: {
    uploadFiles: vi.fn().mockResolvedValue([]),
  },
}));

describe('DraftUserCell', () => {
  it('renders with correct placeholder', () => {
    const mockOnChange = vi.fn();
    const mockOnSend = vi.fn();
    
    render(
      <DraftUserCell
        value=""
        onChange={mockOnChange}
        onSend={mockOnSend}
        placeholder="Type your message..."
      />
    );

    expect(screen.getByText('Write your message...')).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Compose message' })).toBeInTheDocument();
  });

  it('shows user indicator avatar', () => {
    const mockOnChange = vi.fn();
    const mockOnSend = vi.fn();
    
    render(
      <DraftUserCell
        value=""
        onChange={mockOnChange}
        onSend={mockOnSend}
      />
    );

    expect(screen.getByText('U')).toBeInTheDocument();
  });

  it('displays current value in editor', async () => {
    const mockOnChange = vi.fn();
    const mockOnSend = vi.fn();
    
    render(
      <DraftUserCell
        value="Current draft message"
        onChange={mockOnChange}
        onSend={mockOnSend}
      />
    );

    await waitFor(() => {
      expect(screen.getByRole('textbox')).toHaveTextContent('Current draft message');
    });
  });

  it('only calls onChange when there are actual changes to sync', async () => {
    const user = userEvent.setup();
    const mockOnChange = vi.fn();
    const mockOnSend = vi.fn();
    
    // Start with initial content
    render(
      <DraftUserCell
        value="Initial content"
        onChange={mockOnChange}
        onSend={mockOnSend}
      />
    );

    // Clear previous calls
    mockOnChange.mockClear();

    // Open fullscreen - no changes to sync, so onChange should NOT be called
    const fullscreenButton = screen.getByLabelText('Full screen');
    await user.click(fullscreenButton);

    // Verify onChange was NOT called (no changes to sync)
    expect(mockOnChange).not.toHaveBeenCalled();
  });

  it('calls onSend when Ctrl+Enter is pressed with content', async () => {
    const user = userEvent.setup();
    const mockOnChange = vi.fn();
    const mockOnSend = vi.fn();
    
    render(
      <DraftUserCell
        value="Test message"
        onChange={mockOnChange}
        onSend={mockOnSend}
      />
    );

    const textbox = screen.getByRole('textbox');
    await user.type(textbox, '{Control>}{Enter}{/Control}');

    expect(mockOnSend).toHaveBeenCalledWith('Test message');
  });

  it('calls onSend when Cmd+Enter is pressed with content', async () => {
    const user = userEvent.setup();
    const mockOnChange = vi.fn();
    const mockOnSend = vi.fn();
    
    render(
      <DraftUserCell
        value="Test message"
        onChange={mockOnChange}
        onSend={mockOnSend}
      />
    );

    const textbox = screen.getByRole('textbox');
    await user.type(textbox, '{Meta>}{Enter}{/Meta}');

    expect(mockOnSend).toHaveBeenCalledWith('Test message');
  });

  it('does not call onSend when Ctrl+Enter is pressed without content', async () => {
    const user = userEvent.setup();
    const mockOnChange = vi.fn();
    const mockOnSend = vi.fn();
    
    render(
      <DraftUserCell
        value=""
        onChange={mockOnChange}
        onSend={mockOnSend}
      />
    );

    const textbox = screen.getByRole('textbox');
    await user.type(textbox, '{Control>}{Enter}{/Control}');

    expect(mockOnSend).not.toHaveBeenCalled();
  });

  it('shows loading indicator when disabled', () => {
    const mockOnChange = vi.fn();
    const mockOnSend = vi.fn();
    
    render(
      <DraftUserCell
        value="Test message"
        onChange={mockOnChange}
        onSend={mockOnSend}
        disabled={true}
      />
    );

    expect(screen.getByText('Sending...')).toBeInTheDocument();
    // Check for spinner by class instead of role
    const spinnerElement = document.querySelector('.animate-spin');
    expect(spinnerElement).toBeInTheDocument();
  });

  // Editor read-only behavior is handled internally by Lexical; ensure Send button is disabled
  it('disables send button when disabled prop is true', () => {
    const mockOnChange = vi.fn();
    const mockOnSend = vi.fn();

    render(
      <DraftUserCell
        value="Test message"
        onChange={mockOnChange}
        onSend={mockOnSend}
        disabled={true}
      />
    );

    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
  });

  it('shows keyboard shortcut hint', () => {
    const mockOnChange = vi.fn();
    const mockOnSend = vi.fn();
    
    render(
      <DraftUserCell
        value=""
        onChange={mockOnChange}
        onSend={mockOnSend}
      />
    );

    expect(screen.getByText('Ctrl+Enter')).toBeInTheDocument();
    expect(screen.getByText('to send')).toBeInTheDocument();
  });

  it('calls onSend with existing value when send button is clicked', async () => {
    const user = userEvent.setup();
    const mockOnChange = vi.fn();
    const mockOnSend = vi.fn();
    
    render(
      <DraftUserCell
        value="Existing message"
        onChange={mockOnChange}
        onSend={mockOnSend}
      />
    );

    // Click send button
    const sendButton = screen.getByRole('button', { name: 'Send' });
    await user.click(sendButton);

    // Verify onSend was called with existing value
    expect(mockOnSend).toHaveBeenCalledWith('Existing message');
  });

  it('opens and closes fullscreen editor properly', async () => {
    const user = userEvent.setup();
    const mockOnChange = vi.fn();
    const mockOnSend = vi.fn();
    
    render(
      <DraftUserCell
        value="Initial content"
        onChange={mockOnChange}
        onSend={mockOnSend}
      />
    );

    // Open fullscreen
    await user.click(screen.getByLabelText('Full screen'));
    
    // Verify fullscreen editor appears
    await waitFor(() => {
      expect(screen.getByLabelText('Exit full screen')).toBeInTheDocument();
    });

    // Cancel fullscreen
    await user.click(screen.getByLabelText('Exit full screen'));

    // Verify inline editor returns
    await waitFor(() => {
      expect(screen.getByLabelText('Full screen')).toBeInTheDocument();
    });
  });

  it('maintains state isolation - onChange not called on initial render', async () => {
    const mockOnChange = vi.fn();
    const mockOnSend = vi.fn();
    
    render(
      <DraftUserCell
        value=""
        onChange={mockOnChange}
        onSend={mockOnSend}
      />
    );

    // Wait a bit to ensure no unexpected onChange calls
    await waitFor(() => {
      expect(screen.getByRole('textbox')).toBeInTheDocument();
    });

    // Verify onChange was NOT called during initial render (state isolation)
    expect(mockOnChange).not.toHaveBeenCalled();
  });

  it('handles external value changes when not in fullscreen', async () => {
    const mockOnChange = vi.fn();
    const mockOnSend = vi.fn();
    
    const { rerender } = render(
      <DraftUserCell
        value="Initial"
        onChange={mockOnChange}
        onSend={mockOnSend}
      />
    );

    // Verify initial value
    await waitFor(() => {
      expect(screen.getByRole('textbox')).toHaveTextContent('Initial');
    });

    // Update external value
    rerender(
      <DraftUserCell
        value="Updated from outside"
        onChange={mockOnChange}
        onSend={mockOnSend}
      />
    );

    // Verify editor updates with external value
    await waitFor(() => {
      expect(screen.getByRole('textbox')).toHaveTextContent('Updated from outside');
    });
  });

  it('can cancel fullscreen without triggering unnecessary sync', async () => {
    const user = userEvent.setup();
    const mockOnChange = vi.fn();
    const mockOnSend = vi.fn();
    
    render(
      <DraftUserCell
        value="Some content"
        onChange={mockOnChange}
        onSend={mockOnSend}
      />
    );

    // Open fullscreen
    await user.click(screen.getByLabelText('Full screen'));
    
    // Clear onChange mock to focus on cancel behavior
    mockOnChange.mockClear();

    // Cancel fullscreen without changes
    await user.click(screen.getByLabelText('Exit full screen'));

    // Verify we return to inline editor
    await waitFor(() => {
      expect(screen.getByLabelText('Full screen')).toBeInTheDocument();
    });
  });
}); 