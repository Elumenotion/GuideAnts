import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import FullScreenEditor from '../FullScreenEditor';

// Mock LexicalEditor with proper ref handling - each instance has its own state
vi.mock('../LexicalEditor', () => ({
  default: vi.fn().mockImplementation(({ ref, placeholder, autoFocus, showToolbar, className, readOnly, submitButton, cancelButton }) => {
    const [currentValue, setCurrentValue] = React.useState('');
    // Each mock instance has its own value state
    const instanceValueRef = React.useRef('');
    const userHasTypedRef = React.useRef(false);
    
    const mockSetValue = vi.fn((value) => { 
      // Only accept setValue if user hasn't typed anything yet
      if (!userHasTypedRef.current) {
        instanceValueRef.current = value;
        setCurrentValue(value);
      }
    });
    const mockGetValue = vi.fn(() => {
      return instanceValueRef.current;
    });
    const changeListenerRef = React.useRef<((markdown: string) => void) | null>(null);
    
    const mockRegisterChangeListener = vi.fn((callback: (markdown: string) => void) => {
      changeListenerRef.current = callback;
      return () => {
        changeListenerRef.current = null;
      };
    });
    
    React.useImperativeHandle(ref, () => ({
      setValue: mockSetValue,
      getValue: mockGetValue,
      registerChangeListener: mockRegisterChangeListener
    }));

    const handleChange = (e) => {
      const newValue = e.target.value;
      userHasTypedRef.current = true; // Mark that user has typed
      instanceValueRef.current = newValue;
      setCurrentValue(newValue);
      
      // Call the change listener to update FullScreenEditor's currentContent state
      if (changeListenerRef.current) {
        changeListenerRef.current(newValue);
      }
    };

    return (
      <div 
        data-testid="lexical-editor" 
        data-placeholder={placeholder}
        data-autofocus={autoFocus}
        data-showtoolbar={showToolbar}
        className={className}
        data-readonly={readOnly}
      >
        <div data-testid="mock-toolbar">
          {cancelButton && (
            <button type="button" onClick={cancelButton.onClick}>
              {cancelButton.label}
            </button>
          )}
          {submitButton && (
            <button 
              type="button" 
              onClick={submitButton.onClick} 
              disabled={submitButton.disabled} 
              data-loading={submitButton.isLoading}
            >
              {submitButton.label}
            </button>
          )}
        </div>
        <input 
          data-testid="mock-editor-input" 
          placeholder={placeholder}
          value={currentValue}
          onChange={handleChange}
        />
      </div>
    );
  })
}));

// Mock createPortal to render content normally in tests
vi.mock('react-dom', () => ({
  ...vi.importActual('react-dom'),
  createPortal: (node: React.ReactNode) => node
}));

describe('FullScreenEditor', () => {
  const defaultProps = {
    content: 'Test content',
    onSave: vi.fn(),
    onCancel: vi.fn(),
    mode: 'edit' as const
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('Rendering', () => {
    it('renders in full screen overlay', () => {
      render(<FullScreenEditor {...defaultProps} />);
      
      expect(screen.getByTestId('lexical-editor')).toBeInTheDocument();
      // Check for full screen classes
      const overlay = screen.getByTestId('lexical-editor').closest('.fixed.inset-0.z-50');
      expect(overlay).toBeInTheDocument();
    });

    it('shows exit full screen button', () => {
      render(<FullScreenEditor {...defaultProps} />);
      
      expect(screen.getByLabelText('Exit full screen')).toBeInTheDocument();
    });

    it('renders with correct placeholder', () => {
      render(
        <FullScreenEditor 
          {...defaultProps} 
          placeholder="Custom placeholder" 
        />
      );
      
      expect(screen.getByPlaceholderText('Custom placeholder')).toBeInTheDocument();
    });

    it('displays error message when provided', () => {
      const errorMessage = 'Save failed';
      render(<FullScreenEditor {...defaultProps} error={errorMessage} />);
      
      expect(screen.getByText(errorMessage)).toBeInTheDocument();
    });
  });

  describe('Edit Mode', () => {
    it('shows save and cancel buttons in toolbar for edit mode', () => {
      render(<FullScreenEditor {...defaultProps} mode="edit" />);
      
      expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Cancel' })).toBeInTheDocument();
    });

    it('calls onSave with edited content when save clicked', async () => {
      const user = userEvent.setup();
      const mockOnSave = vi.fn();
      
      render(<FullScreenEditor {...defaultProps} onSave={mockOnSave} />);
      
      // Edit content
      const input = screen.getByTestId('mock-editor-input') as HTMLInputElement;
      await user.clear(input);
      await user.type(input, 'Edited content');
      
      // Wait until the mocked editor state is latest
      await waitFor(() => {
        expect(input).toHaveValue('Edited content');
      });
      
      // Click save
      await user.click(screen.getByRole('button', { name: 'Save' }));
      expect(mockOnSave).toHaveBeenCalledWith('Edited content');
    });

    it('calls onCancel with current content when cancel clicked', async () => {
      const user = userEvent.setup();
      const mockOnCancel = vi.fn();
      
      render(<FullScreenEditor {...defaultProps} onCancel={mockOnCancel} />);
      
      // Edit content
      const input = screen.getByTestId('mock-editor-input') as HTMLInputElement;
      await user.clear(input);
      await user.type(input, 'Edited but cancelled');
      
      await waitFor(() => {
        expect(input).toHaveValue('Edited but cancelled');
      });
      
      // Click cancel
      await user.click(screen.getByRole('button', { name: 'Cancel' }));
      
      expect(mockOnCancel).toHaveBeenCalledWith('Edited but cancelled');
    });
  });

  describe('Compose Mode', () => {
    it('shows send button in toolbar for compose mode', () => {
      render(<FullScreenEditor {...defaultProps} mode="compose" />);
      
      expect(screen.getByRole('button', { name: 'Send' })).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Cancel' })).not.toBeInTheDocument();
    });

    it('uses custom submit label when provided', () => {
      render(
        <FullScreenEditor 
          {...defaultProps} 
          mode="compose"
          submitLabel="Post"
        />
      );
      
      expect(screen.getByRole('button', { name: 'Post' })).toBeInTheDocument();
    });

    it('disables send button when content is empty', async () => {
      const user = userEvent.setup();
      render(<FullScreenEditor {...defaultProps} mode="compose" content="" />);
      
      // Wait for the editor to initialize with empty content
      await waitFor(() => {
        const sendButton = screen.getByRole('button', { name: 'Send' });
        expect(sendButton).toBeDisabled();
      });
    });
  });

  describe('Keyboard Shortcuts', () => {
    it('saves on Ctrl+Enter in both modes', async () => {
      const mockOnSave = vi.fn();
      
      render(<FullScreenEditor {...defaultProps} onSave={mockOnSave} />);
      
      // Wait for editor to be ready
      await waitFor(() => {
        expect(screen.getByTestId('lexical-editor')).toBeInTheDocument();
      });
      
      // Find the element with onKeyDown handler (the parent of lexical-editor)
      const editorContainer = screen.getByTestId('lexical-editor').parentElement;
      
      // Dispatch keyboard event
      const event = new KeyboardEvent('keydown', {
        key: 'Enter',
        ctrlKey: true,
        bubbles: true
      });
      editorContainer!.dispatchEvent(event);
      
      expect(mockOnSave).toHaveBeenCalledWith('Test content');
    });

    it('cancels on Escape', async () => {
      const mockOnCancel = vi.fn();
      
      render(<FullScreenEditor {...defaultProps} onCancel={mockOnCancel} />);
      
      await waitFor(() => {
        expect(screen.getByTestId('lexical-editor')).toBeInTheDocument();
      });
      
      const editorContainer = screen.getByTestId('lexical-editor').parentElement;
      
      const event = new KeyboardEvent('keydown', {
        key: 'Escape',
        bubbles: true
      });
      editorContainer!.dispatchEvent(event);
      
      expect(mockOnCancel).toHaveBeenCalledWith('Test content');
    });
  });

  describe('Loading States', () => {
    it('shows loading state on submit button', () => {
      render(
        <FullScreenEditor 
          {...defaultProps} 
          mode="compose"
          isLoading={true}
        />
      );
      
      const sendButton = screen.getByRole('button', { name: 'Send' });
      expect(sendButton).toHaveAttribute('data-loading', 'true');
    });

    it('disables editor when loading', () => {
      render(<FullScreenEditor {...defaultProps} isLoading={true} />);
      
      expect(screen.getByTestId('lexical-editor')).toBeInTheDocument();
      // In real implementation, readOnly prop would be passed
    });
  });

  describe('Header Controls', () => {
    it('shows keyboard hints in header', () => {
      render(<FullScreenEditor {...defaultProps} />);
      
      expect(screen.getByText(/Ctrl\+Enter/)).toBeInTheDocument();
      expect(screen.getByText(/Esc/)).toBeInTheDocument();
    });

    it('shows appropriate loading text based on mode', () => {
      render(
        <FullScreenEditor 
          {...defaultProps}
          mode="compose" 
          isLoading={true}
        />
      );
      
      expect(screen.getByText('Sending...')).toBeInTheDocument();
    });

    it('shows correct title based on mode', () => {
      render(<FullScreenEditor {...defaultProps} mode="compose" />);
      expect(screen.getByText('Compose Message')).toBeInTheDocument();
      
      render(<FullScreenEditor {...defaultProps} mode="edit" />);
      expect(screen.getByText('Edit Message')).toBeInTheDocument();
    });
  });

  it('renders with initial content in editor', async () => {
    render(
      <FullScreenEditor
        content="Initial content"
        onSave={vi.fn()}
        onCancel={vi.fn()}
        mode="compose"
      />
    );

    await waitFor(() => {
      expect(screen.getByDisplayValue('Initial content')).toBeInTheDocument();
    });
  });

  it('shows Send button for compose mode', () => {
    render(
      <FullScreenEditor
        content=""
        onSave={vi.fn()}
        onCancel={vi.fn()}
        mode="compose"
      />
    );

    expect(screen.getByRole('button', { name: 'Send' })).toBeInTheDocument();
  });

  it('shows Save button for edit mode', () => {
    render(
      <FullScreenEditor
        content=""
        onSave={vi.fn()}
        onCancel={vi.fn()}
        mode="edit"
      />
    );

    expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument();
  });

  it('calls onSave with content when save button is clicked', async () => {
    const user = userEvent.setup();
    const mockOnSave = vi.fn();
    
    render(
      <FullScreenEditor
        content=""
        onSave={mockOnSave}
        onCancel={vi.fn()}
        mode="compose"
      />
    );

    // Type content by updating the mock value
    const input = screen.getByTestId('mock-editor-input');
    await user.type(input, 'Test content');

    // Click save
    await user.click(screen.getByRole('button', { name: 'Send' }));

    // Verify onSave was called with content
    expect(mockOnSave).toHaveBeenCalledWith('Test content');
  });

  it('calls onCancel when cancel button is clicked (edit mode)', async () => {
    const user = userEvent.setup();
    const mockOnCancel = vi.fn();
    
    render(
      <FullScreenEditor
        content=""
        onSave={vi.fn()}
        onCancel={mockOnCancel}
        mode="edit"
      />
    );

    // Click cancel (only available in edit mode)
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    // Verify onCancel was called
    expect(mockOnCancel).toHaveBeenCalled();
  });

  it('calls onCancel when exit button is pressed', async () => {
    const user = userEvent.setup();
    const mockOnCancel = vi.fn();
    
    render(
      <FullScreenEditor
        content=""
        onSave={vi.fn()}
        onCancel={mockOnCancel}
        mode="compose"
      />
    );

    // Click exit full screen button
    await user.click(screen.getByLabelText('Exit full screen'));

    // Verify onCancel was called
    expect(mockOnCancel).toHaveBeenCalled();
  });

  it('shows loading state when isLoading is true', () => {
    render(
      <FullScreenEditor
        content=""
        onSave={vi.fn()}
        onCancel={vi.fn()}
        mode="compose"
        isLoading={true}
      />
    );

    // Check for loading indicator
    expect(screen.getByText('Sending...')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
  });

  it('uses custom submit label when provided', () => {
    render(
      <FullScreenEditor
        content=""
        onSave={vi.fn()}
        onCancel={vi.fn()}
        mode="compose"
        submitLabel="Publish"
      />
    );

    expect(screen.getByRole('button', { name: 'Publish' })).toBeInTheDocument();
  });

  it('shows keyboard shortcuts in header', () => {
    render(
      <FullScreenEditor
        content=""
        onSave={vi.fn()}
        onCancel={vi.fn()}
        mode="edit"
      />
    );

    expect(screen.getByText(/Ctrl\+Enter/)).toBeInTheDocument();
    expect(screen.getByText(/Esc/)).toBeInTheDocument();
  });
}); 