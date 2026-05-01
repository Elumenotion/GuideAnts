import React from 'react';
import { render, screen } from '@testing-library/react';
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

  it('displays all assistants in dropdown', async () => {
    const user = userEvent.setup();
    render(<AssistantDropdown {...defaultProps} />);
    
    await user.click(screen.getByRole('button'));
    
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

  it('calls onSelect when assistant is chosen', async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();
    render(<AssistantDropdown {...defaultProps} onSelect={onSelect} />);
    
    await user.click(screen.getByRole('button'));
    await user.click(screen.getByRole('option', { name: /Web Ants/ }));
    
    expect(onSelect).toHaveBeenCalledWith('Web Ants');
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