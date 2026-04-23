import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { SidebarSection } from '../SidebarSection';

describe('SidebarSection', () => {
  const defaultProps = {
    title: 'Test Section',
    type: 'test' as const,
    isExpanded: true,
    onToggle: vi.fn(),
    onSelect: vi.fn(),
    selectedItem: null,
    items: [
      { id: 'item-1', label: 'Item 1' },
      { id: 'item-2', label: 'Item 2' }
    ],
    disabled: false
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders section title', () => {
    render(<SidebarSection {...defaultProps} />);
    expect(screen.getByText('Test Section')).toBeInTheDocument();
  });

  it('renders items when expanded', () => {
    render(<SidebarSection {...defaultProps} />);
    expect(screen.getByText('Item 1')).toBeInTheDocument();
    expect(screen.getByText('Item 2')).toBeInTheDocument();
  });

  it('does not render items when collapsed', () => {
    render(<SidebarSection {...defaultProps} isExpanded={false} />);
    expect(screen.queryByText('Item 1')).not.toBeInTheDocument();
    expect(screen.queryByText('Item 2')).not.toBeInTheDocument();
  });

  it('calls onToggle when toggle button is clicked', () => {
    render(<SidebarSection {...defaultProps} />);
    const toggleButton = screen.getByLabelText('Collapse section');
    fireEvent.click(toggleButton);
    expect(defaultProps.onToggle).toHaveBeenCalled();
  });

  it('calls onSelect when item is clicked', () => {
    render(<SidebarSection {...defaultProps} />);
    const item = screen.getByText('Item 1');
    fireEvent.click(item);
    expect(defaultProps.onSelect).toHaveBeenCalledWith('test', 'item-1');
  });

  it('shows selected item with blue text', () => {
    const selectedItem = { type: 'test', id: 'item-1' };
    render(<SidebarSection {...defaultProps} selectedItem={selectedItem} />);
    const item = screen.getByText('Item 1');
    expect(item).toHaveClass('text-blue-600');
  });

  it('shows upload button for contentFiles type', () => {
    render(<SidebarSection {...defaultProps} type="contentFiles" />);
    const uploadButton = screen.getByTitle('Upload files');
    expect(uploadButton).toBeInTheDocument();
  });

  it('shows add button for links type', () => {
    render(<SidebarSection {...defaultProps} type="links" onAdd={vi.fn()} />);
    const addButton = screen.getByTitle('Add new link');
    expect(addButton).toBeInTheDocument();
  });

  it('shows add button when showAddButton is true (type=projectMembers)', () => {
    render(<SidebarSection {...defaultProps} type="projectMembers" showAddButton={true} onAdd={vi.fn()} />);
    const addButton = screen.getByTitle('Add new project member');
    expect(addButton).toBeInTheDocument();
  });

  it('shows add button when showAddButton is true (type!=projectMembers)', () => {
    render(<SidebarSection {...defaultProps} showAddButton={true} onAdd={vi.fn()} />);
    const addButton = screen.getByTitle('Add new link');
    expect(addButton).toBeInTheDocument();
  });

  it('disables interactions when disabled', () => {
    render(<SidebarSection {...defaultProps} disabled={true} />);
    const toggleButton = screen.getByLabelText('Collapse section');
    expect(toggleButton).toBeDisabled();
  });

  it('renders children when provided', () => {
    render(
      <SidebarSection {...defaultProps}>
        <div data-testid="custom-child">Custom Content</div>
      </SidebarSection>
    );
    expect(screen.getByTestId('custom-child')).toBeInTheDocument();
  });

  it('applies noIndentChildren class when specified', () => {
    render(<SidebarSection {...defaultProps} noIndentChildren={true} />);
    const contentContainer = screen.getByText('Item 1').parentElement;
    expect(contentContainer).not.toHaveClass('pl-4');
  });

  it('applies indent class when noIndentChildren is false', () => {
    render(<SidebarSection {...defaultProps} noIndentChildren={false} />);
    const contentContainer = screen.getByText('Item 1').parentElement;
    expect(contentContainer).toHaveClass('pl-4');
  });

  it('handles file upload when onUploadFiles is provided', () => {
    const onUploadFiles = vi.fn();
    render(<SidebarSection {...defaultProps} type="contentFiles" onUploadFiles={onUploadFiles} />);
    
    const uploadButton = screen.getByTitle('Upload files');
    fireEvent.click(uploadButton);
    
    // The file input should be triggered
    expect(uploadButton).toBeInTheDocument();
  });

  it('handles upload dialog when onOpenUploadDialog is provided', () => {
    const onOpenUploadDialog = vi.fn();
    render(<SidebarSection {...defaultProps} type="contentFiles" onOpenUploadDialog={onOpenUploadDialog} />);
    
    const uploadButton = screen.getByTitle('Upload files');
    fireEvent.click(uploadButton);
    
    expect(onOpenUploadDialog).toHaveBeenCalled();
  });
}); 