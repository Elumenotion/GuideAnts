import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';

import { CreateConversationDialog } from '../CreateConversationDialog';

const defaultProps = {
  isOpen: true,
  onClose: vi.fn(),
  onCreate: vi.fn().mockResolvedValue(undefined),
};

describe('CreateConversationDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders nothing when closed', () => {
    const { container } = render(
      <CreateConversationDialog {...defaultProps} isOpen={false} />
    );
    expect(container.firstChild).toBeNull();
  });

  it('renders dialog when open', () => {
    render(<CreateConversationDialog {...defaultProps} />);
    expect(screen.getByText('Create Conversation')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Enter conversation title')).toBeInTheDocument();
  });

  it('handles input changes', async () => {
    const user = userEvent.setup();
    render(<CreateConversationDialog {...defaultProps} />);
    
    const input = screen.getByPlaceholderText('Enter conversation title');
    // The component pre-populates with "New Conversation" and selects it on mount.
    // Typing will replace the selection, so we just type and expect the new value.
    await user.clear(input);
    await user.type(input, 'My New Chat');
    expect(input).toHaveValue('My New Chat');
  });

  it('creates conversation with valid title', async () => {
    const user = userEvent.setup();
    const mockOnCreate = vi.fn().mockResolvedValue(undefined);
    
    render(
      <CreateConversationDialog {...defaultProps} onCreate={mockOnCreate} />
    );
    
    const input = screen.getByPlaceholderText('Enter conversation title');
    const createButton = screen.getByText('Create');
    
    await user.clear(input);
    await user.type(input, 'New Conversation');
    await user.click(createButton);
    
    expect(mockOnCreate).toHaveBeenCalledWith('New Conversation');
  });

  it('shows validation error for empty title', async () => {
    const user = userEvent.setup();
    render(<CreateConversationDialog {...defaultProps} />);
    
    const createButton = screen.getByText('Create');
    // Clear default value to make it empty
    const input = screen.getByPlaceholderText('Enter conversation title');
    await user.clear(input);
    await user.click(createButton);
    expect(screen.getByText('Title is required')).toBeInTheDocument();
  });

  it('trims whitespace from title', async () => {
    const user = userEvent.setup();
    const mockOnCreate = vi.fn().mockResolvedValue(undefined);
    
    render(<CreateConversationDialog {...defaultProps} onCreate={mockOnCreate} />);
    
    const input = screen.getByPlaceholderText('Enter conversation title');
    const createButton = screen.getByText('Create');
    
    await user.clear(input);
    await user.type(input, '  My Chat  ');
    await user.click(createButton);
    
    expect(mockOnCreate).toHaveBeenCalledWith('My Chat');
  });

  it('closes dialog on cancel', async () => {
    const user = userEvent.setup();
    const mockOnClose = vi.fn();
    
    render(<CreateConversationDialog {...defaultProps} onClose={mockOnClose} />);
    
    const cancelButton = screen.getByText('Cancel');
    await user.click(cancelButton);
    
    expect(mockOnClose).toHaveBeenCalled();
  });

  it('closes dialog on successful creation', async () => {
    const user = userEvent.setup();
    const mockOnClose = vi.fn();
    const mockOnCreate = vi.fn().mockResolvedValue(undefined);
    
    render(
      <CreateConversationDialog 
        {...defaultProps} 
        onClose={mockOnClose}
        onCreate={mockOnCreate}
      />
    );
    
    const input = screen.getByPlaceholderText('Enter conversation title');
    const createButton = screen.getByText('Create');
    
    await user.type(input, 'New Chat');
    await user.click(createButton);
    
    await waitFor(() => {
      expect(mockOnClose).toHaveBeenCalled();
    });
  });

  it('handles creation errors gracefully', async () => {
    const user = userEvent.setup();
    const mockOnCreate = vi.fn().mockRejectedValue(new Error('Creation failed'));
    
    render(
      <CreateConversationDialog {...defaultProps} onCreate={mockOnCreate} />
    );
    
    const input = screen.getByPlaceholderText('Enter conversation title');
    const createButton = screen.getByText('Create');
    
    await user.type(input, 'New Chat');
    await user.click(createButton);
    
    await waitFor(() => {
      expect(screen.getByText('Creation failed')).toBeInTheDocument();
    });
  });

  it('disables create button while creating', async () => {
    const user = userEvent.setup();
    let resolveCreate: (value: void) => void;
    const mockOnCreate = vi.fn(() => new Promise<void>(resolve => { resolveCreate = resolve; }));
    
    render(
      <CreateConversationDialog {...defaultProps} onCreate={mockOnCreate} />
    );
    
    const input = screen.getByPlaceholderText('Enter conversation title');
    const createButton = screen.getByText('Create');
    
    await user.type(input, 'New Chat');
    await user.click(createButton);
    
    expect(createButton).toBeDisabled();
    expect(screen.getByText('Creating...')).toBeInTheDocument();
    
    // Resolve the promise
    resolveCreate!();
    
    await waitFor(() => {
      expect(createButton).not.toBeDisabled();
    });
  });
}); 