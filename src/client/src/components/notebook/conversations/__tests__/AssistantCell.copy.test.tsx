import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import AssistantCell from '../AssistantCell';
import { ToastProvider } from '../../../common/Toast';

// Mock createPortal for testing
vi.mock('react-dom', async () => {
  const actual = await vi.importActual('react-dom');
  return {
    ...actual,
    createPortal: (children: React.ReactNode) => children,
  };
});

const renderWithToast = (component: React.ReactElement) => {
  return render(
    <ToastProvider>
      {component}
    </ToastProvider>
  );
};

describe('AssistantCell - Copy Functionality', () => {
  const defaultProps = {
    content: 'This is a test assistant response that can be copied to clipboard.',
    isLast: true,
    assistantName: 'Claude'
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it('should render copy button when content is available', () => {
    renderWithToast(<AssistantCell {...defaultProps} />);

    const copyButton = screen.getByLabelText('Copy to clipboard');
    expect(copyButton).toBeInTheDocument();
  });

  it('should not render copy button when content is empty', () => {
    renderWithToast(
      <AssistantCell {...defaultProps} content="" />
    );

    const copyButton = screen.queryByLabelText('Copy to clipboard');
    expect(copyButton).not.toBeInTheDocument();
  });

  it('should not render copy button when content is only whitespace', () => {
    const whitespaceContent = "   \n\t  ";
    
    renderWithToast(
      <AssistantCell {...defaultProps} content={whitespaceContent} />
    );

    const copyButton = screen.queryByLabelText('Copy to clipboard');
    expect(copyButton).not.toBeInTheDocument();
  });

  it('should show success toast when copy button is clicked', async () => {
    const user = userEvent.setup();
    
    // Mock clipboard API to succeed
    const mockWriteText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText: mockWriteText },
      writable: true,
      configurable: true
    });

    renderWithToast(<AssistantCell {...defaultProps} />);

    const copyButton = screen.getByLabelText('Copy to clipboard');
    await user.click(copyButton);

    await waitFor(() => {
      expect(screen.getByText('Content copied to clipboard')).toBeInTheDocument();
    });
  });

  it('should show error toast when clipboard API fails', async () => {
    const user = userEvent.setup();
    
    // Mock clipboard API to fail
    const mockWriteText = vi.fn().mockRejectedValue(new Error('Clipboard access denied'));
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText: mockWriteText },
      writable: true,
      configurable: true
    });

    renderWithToast(<AssistantCell {...defaultProps} />);

    const copyButton = screen.getByLabelText('Copy to clipboard');
    await user.click(copyButton);

    await waitFor(() => {
      expect(screen.getByText('Copy failed')).toBeInTheDocument();
    }, { timeout: 3000 });
  });

  it('should use fallback method when clipboard API is not available', async () => {
    const user = userEvent.setup();
    
    // Mock navigator.clipboard as undefined
    Object.defineProperty(navigator, 'clipboard', {
      value: undefined,
      writable: true,
      configurable: true
    });
    
    // Mock document.execCommand to succeed
    const mockExecCommand = vi.fn().mockReturnValue(true);
    Object.defineProperty(document, 'execCommand', {
      value: mockExecCommand,
      writable: true,
      configurable: true
    });
    
    renderWithToast(<AssistantCell {...defaultProps} />);

    const copyButton = screen.getByLabelText('Copy to clipboard');
    await user.click(copyButton);

    await waitFor(() => {
      expect(screen.getByText('Content copied to clipboard')).toBeInTheDocument();
    });
  });

  it('should show error when fallback method fails', async () => {
    const user = userEvent.setup();
    
    // Mock navigator.clipboard as undefined
    Object.defineProperty(navigator, 'clipboard', {
      value: undefined,
      writable: true,
      configurable: true
    });
    
    // Mock document.execCommand to fail
    const mockExecCommand = vi.fn().mockReturnValue(false);
    Object.defineProperty(document, 'execCommand', {
      value: mockExecCommand,
      writable: true,
      configurable: true
    });
    
    renderWithToast(<AssistantCell {...defaultProps} />);

    const copyButton = screen.getByLabelText('Copy to clipboard');
    await user.click(copyButton);

    await waitFor(() => {
      expect(screen.getByText('Copy failed')).toBeInTheDocument();
    });
  });

  it('should position copy button correctly in button stack', () => {
    renderWithToast(<AssistantCell {...defaultProps} />);

    const buttonContainer = screen.getByLabelText('Copy to clipboard').closest('div');
    expect(buttonContainer).toHaveClass('absolute', 'top-1', 'right-3', 'flex', 'flex-row', 'items-center', 'space-x-2');
    
    // Verify button order: Copy should be first, Full screen second
    const allButtons = buttonContainer?.querySelectorAll('button');
    expect(allButtons?.[0]).toHaveAttribute('aria-label', 'Copy to clipboard');
    expect(allButtons?.[1]).toHaveAttribute('aria-label', 'Full screen');
  });

  it('should stop event propagation when copy button is clicked', async () => {
    const user = userEvent.setup();
    const mockCellClick = vi.fn();
    
    renderWithToast(
      <div onClick={mockCellClick}>
        <AssistantCell {...defaultProps} />
      </div>
    );

    const copyButton = screen.getByLabelText('Copy to clipboard');
    await user.click(copyButton);

    expect(mockCellClick).not.toHaveBeenCalled();
  });

  it('should have proper accessibility attributes', () => {
    renderWithToast(<AssistantCell {...defaultProps} />);

    const copyButton = screen.getByLabelText('Copy to clipboard');
    
    expect(copyButton).toHaveAttribute('aria-label', 'Copy to clipboard');
    expect(copyButton).toHaveAttribute('title', 'Copy to clipboard');
    expect(copyButton).toHaveAttribute('type', 'button');
  });

  describe('Save Button Functionality', () => {
    const saveProps = {
      ...defaultProps,
      projectId: 'test-project',
      notebookId: 'test-notebook',
      notebookTitle: 'Test Notebook',
      onSaveToNotebook: vi.fn(),
      conversationContext: {
        messageId: 'msg-123',
        conversationId: 'conv-456',
        totalMessages: 5
      }
    };

    it('should not render save button when onSaveToNotebook is missing', () => {
      renderWithToast(<AssistantCell {...saveProps} onSaveToNotebook={undefined} />);

      const saveButton = screen.queryByLabelText('Save to notebook');
      expect(saveButton).not.toBeInTheDocument();
    });

    it('should not render save button when onSaveToNotebook is missing', () => {
      renderWithToast(<AssistantCell {...saveProps} onSaveToNotebook={undefined} />);

      const saveButton = screen.queryByLabelText('Save to notebook');
      expect(saveButton).not.toBeInTheDocument();
    });

    it('should not render save button when streaming', () => {
      renderWithToast(<AssistantCell {...saveProps} isStreaming={true} />);

      const saveButton = screen.queryByLabelText('Save to notebook');
      expect(saveButton).not.toBeInTheDocument();
    });

    it('should not render save button when content is empty', () => {
      renderWithToast(<AssistantCell {...saveProps} content="" />);

      const saveButton = screen.queryByLabelText('Save to notebook');
      expect(saveButton).not.toBeInTheDocument();
    });

  });
}); 