import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, beforeEach, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import AssistantDropdown from '../AssistantDropdown';
import { AssistantOption } from '../../AssistantSelector';

const mockAssistants: AssistantOption[] = [
  { name: 'Code Ants', model: 'gpt-4', avatarUrl: '/avatars/code-ants.png' },
  { name: 'Web Ants', model: 'gpt-3.5', avatarUrl: '/avatars/web-ants.png' },
  { name: 'OneDrive Ants', model: 'gpt-4', avatarUrl: '/avatars/onedrive-ants.png' },
  { name: 'Note Ants', model: 'gpt-3.5', avatarUrl: '/avatars/note-ants.png' },
];

const defaultProps = {
  assistants: mockAssistants,
  selectedName: 'Code Ants',
  onSelect: vi.fn(),
};

describe('AssistantDropdown', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders selected assistant correctly', () => {
    render(<AssistantDropdown {...defaultProps} />);
    
    expect(screen.getByText('Code Ants')).toBeInTheDocument();
    expect(screen.getByAltText('Code Ants avatar')).toBeInTheDocument();
  });

  it('opens dropdown when clicked', async () => {
    const user = userEvent.setup();
    render(<AssistantDropdown {...defaultProps} />);
    
    const button = screen.getByRole('button');
    await user.click(button);
    
    expect(screen.getByRole('listbox')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Search assistants by name')).toBeInTheDocument();
  });

  it('displays all assistants in dropdown', async () => {
    const user = userEvent.setup();
    render(<AssistantDropdown {...defaultProps} />);
    
    await user.click(screen.getByRole('button'));
    
    // Check that all assistants appear as options in the dropdown
    mockAssistants.forEach(assistant => {
      expect(screen.getByRole('option', { name: new RegExp(assistant.name) })).toBeInTheDocument();
    });
  });

  it('shows selection indicator for current assistant', async () => {
    const user = userEvent.setup();
    render(<AssistantDropdown {...defaultProps} />);
    
    await user.click(screen.getByRole('button'));
    
    const selectedOption = screen.getByRole('option', { name: /Code Ants/ });
    expect(selectedOption).toHaveAttribute('aria-selected', 'true');
  });

  it('filters assistants based on search query', async () => {
    const user = userEvent.setup();
    render(<AssistantDropdown {...defaultProps} />);
    
    await user.click(screen.getByRole('button'));
    
    const searchInput = screen.getByPlaceholderText('Search assistants by name');
    await user.type(searchInput, 'Web');
    
    // Should show Web Ants option
    expect(screen.getByRole('option', { name: /Web Ants/ })).toBeInTheDocument();
    // Should not show Code Ants as an option in the dropdown (but still shows in button)
    expect(screen.queryByRole('option', { name: /Code Ants/ })).not.toBeInTheDocument();
  });

  it('shows no results message when search has no matches', async () => {
    const user = userEvent.setup();
    render(<AssistantDropdown {...defaultProps} />);
    
    await user.click(screen.getByRole('button'));
    
    const searchInput = screen.getByPlaceholderText('Search assistants by name');
    await user.type(searchInput, 'xyz');
    
    expect(screen.getByText('No assistants found matching "xyz"')).toBeInTheDocument();
  });

  it('calls onSelect when assistant is chosen', async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();
    render(<AssistantDropdown {...defaultProps} onSelect={onSelect} />);
    
    await user.click(screen.getByRole('button'));
    await user.click(screen.getByRole('option', { name: /Web Ants/ }));
    
    expect(onSelect).toHaveBeenCalledWith('Web Ants');
  });

  it('clears search when selecting an assistant', async () => {
    const user = userEvent.setup();
    render(<AssistantDropdown {...defaultProps} />);
    
    await user.click(screen.getByRole('button'));
    
    const searchInput = screen.getByPlaceholderText('Search assistants by name');
    await user.type(searchInput, 'Web');
    
    // Verify search is working
    expect(searchInput).toHaveValue('Web');
    
    // Select an assistant (this will close the dropdown)
    await user.click(screen.getByRole('option', { name: /Web Ants/ }));
    
    // Verify dropdown is closed
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    
    // Reopen dropdown and verify search is cleared
    await user.click(screen.getByRole('button'));
    
    // Wait for the search input to appear and verify it's cleared
    await waitFor(() => {
      const newSearchInput = screen.getByPlaceholderText('Search assistants by name');
      expect(newSearchInput).toHaveValue('');
    });
  });

  it('closes dropdown on escape key', async () => {
    const user = userEvent.setup();
    render(<AssistantDropdown {...defaultProps} />);
    
    await user.click(screen.getByRole('button'));
    expect(screen.getByRole('listbox')).toBeInTheDocument();
    
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
  });

  it('is disabled when disabled prop is true', () => {
    render(<AssistantDropdown {...defaultProps} disabled />);
    
    const button = screen.getByRole('button');
    expect(button).toBeDisabled();
  });

  it('has proper accessibility attributes', async () => {
    const user = userEvent.setup();
    render(<AssistantDropdown {...defaultProps} />);
    
    const button = screen.getByRole('button');
    expect(button).toHaveAttribute('aria-haspopup', 'listbox');
    expect(button).toHaveAttribute('aria-expanded', 'false');
    
    await user.click(button);
    expect(button).toHaveAttribute('aria-expanded', 'true');
  });
}); 