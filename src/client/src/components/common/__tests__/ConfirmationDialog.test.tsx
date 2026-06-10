import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { ConfirmationDialog } from '../ConfirmationDialog';

// Mock createPortal to render directly
vi.mock('react-dom', async () => {
  const actual = await vi.importActual('react-dom');
  return {
    ...actual,
    createPortal: (children: React.ReactNode) => children,
  };
});

describe('ConfirmationDialog', () => {
  const defaultProps = {
    isOpen: true,
    onClose: vi.fn(),
    onConfirm: vi.fn(),
    title: 'Test Confirmation',
    message: 'Are you sure you want to proceed?',
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders when open', () => {
    render(<ConfirmationDialog {...defaultProps} />);

    expect(screen.getByText('Test Confirmation')).toBeInTheDocument();
    expect(screen.getByText('Are you sure you want to proceed?')).toBeInTheDocument();
    expect(screen.getByText('Confirm')).toBeInTheDocument();
    expect(screen.getByText('Cancel')).toBeInTheDocument();
  });

  it('does not render when closed', () => {
    render(<ConfirmationDialog {...defaultProps} isOpen={false} />);

    expect(screen.queryByText('Test Confirmation')).not.toBeInTheDocument();
  });

  it('calls onConfirm when confirm button is clicked', async () => {
    const user = userEvent.setup();
    render(<ConfirmationDialog {...defaultProps} />);

    const confirmButton = screen.getByText('Confirm');
    await user.click(confirmButton);

    expect(defaultProps.onConfirm).toHaveBeenCalledTimes(1);
  });

  it('calls onClose when cancel button is clicked', async () => {
    const user = userEvent.setup();
    render(<ConfirmationDialog {...defaultProps} />);

    const cancelButton = screen.getByText('Cancel');
    await user.click(cancelButton);

    expect(defaultProps.onClose).toHaveBeenCalledTimes(1);
  });

  it('calls onClose when Escape key is pressed', async () => {
    const user = userEvent.setup();
    render(<ConfirmationDialog {...defaultProps} />);

    // Focus the dialog first
    const dialog = screen.getByText('Test Confirmation').closest('div[tabindex="-1"]') as HTMLElement;
    if (dialog) {
      dialog.focus();
    }

    await user.keyboard('{Escape}');

    expect(defaultProps.onClose).toHaveBeenCalledTimes(1);
  });

  it('uses custom button text when provided', () => {
    render(
      <ConfirmationDialog
        {...defaultProps}
        confirmText="Delete"
        cancelText="Keep"
      />
    );

    expect(screen.getByText('Delete')).toBeInTheDocument();
    expect(screen.getByText('Keep')).toBeInTheDocument();
  });

  it('shows loading state when isLoading is true', () => {
    render(<ConfirmationDialog {...defaultProps} isLoading={true} />);

    expect(screen.getByText('Confirming...')).toBeInTheDocument();
    
    // Check that buttons are disabled
    const confirmButton = screen.getByText('Confirming...');
    const cancelButton = screen.getByText('Cancel');
    
    expect(confirmButton).toBeDisabled();
    expect(cancelButton).toBeDisabled();
  });

  it('does not call onConfirm when loading', async () => {
    const user = userEvent.setup();
    render(<ConfirmationDialog {...defaultProps} isLoading={true} />);

    const confirmButton = screen.getByText('Confirming...');
    await user.click(confirmButton);

    expect(defaultProps.onConfirm).not.toHaveBeenCalled();
  });

  it('applies custom confirm button styling', () => {
    render(
      <ConfirmationDialog
        {...defaultProps}
        confirmButtonClass="bg-blue-600 hover:bg-blue-700 text-white"
      />
    );

    const confirmButton = screen.getByText('Confirm');
    expect(confirmButton).toHaveClass('bg-blue-600', 'hover:bg-blue-700', 'text-white');
  });

  it('shows warning icon by default', () => {
    render(<ConfirmationDialog {...defaultProps} />);
    
    // The icon should be rendered (FiAlertTriangle)
    const icon = document.querySelector('svg');
    expect(icon).toBeInTheDocument();
  });

  it('calls onConfirm when Enter is pressed outside focused buttons', async () => {
    const user = userEvent.setup();
    render(<ConfirmationDialog {...defaultProps} />);

    await user.keyboard('{Enter}');
    expect(defaultProps.onConfirm).toHaveBeenCalledTimes(1);
  });

  it('does not confirm on Enter when confirmDisabled is true', async () => {
    const user = userEvent.setup();
    render(<ConfirmationDialog {...defaultProps} confirmDisabled />);

    await user.keyboard('{Enter}');
    expect(defaultProps.onConfirm).not.toHaveBeenCalled();
  });

  it('does not call onClose on Escape when loading', async () => {
    const user = userEvent.setup();
    render(<ConfirmationDialog {...defaultProps} isLoading />);

    await user.keyboard('{Escape}');
    expect(defaultProps.onClose).not.toHaveBeenCalled();
  });

  it('does not confirm on Enter when a button is focused', () => {
    render(<ConfirmationDialog {...defaultProps} />);

    const cancelButton = screen.getByText('Cancel');
    cancelButton.focus();
    fireEvent.keyDown(window, { key: 'Enter' });

    expect(defaultProps.onConfirm).not.toHaveBeenCalled();
  });

  it('renders custom body content and hides cancel button when requested', () => {
    render(
      <ConfirmationDialog
        {...defaultProps}
        message=""
        body={<div>Extra body</div>}
        showCancelButton={false}
      />
    );

    expect(screen.getByText('Extra body')).toBeInTheDocument();
    expect(screen.queryByText('Cancel')).not.toBeInTheDocument();
  });
}); 