import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { SaveAssistantContentDialog } from '../SaveAssistantContentDialog';
import { ToastProvider } from '../../../common/Toast';
import { NotebookFolderTreeDto } from '../../../../types/notebook';

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

const mockFolderTree: NotebookFolderTreeDto = {
  id: 'root',
  name: 'Root',
  relativePath: '',
  subFolders: [
    {
      id: 'folder1',
      name: 'Documents',
      relativePath: 'documents',
      subFolders: [
        {
          id: 'subfolder1',
          name: 'Reports',
          relativePath: 'documents/reports',
          subFolders: [],
          files: []
        }
      ],
      files: []
    },
    {
      id: 'folder2',
      name: 'Images',
      relativePath: 'images',
      subFolders: [],
      files: []
    }
  ],
  files: []
};

describe('SaveAssistantContentDialog', () => {
  const defaultProps = {
    isOpen: true,
    onClose: vi.fn(),
    onSave: vi.fn(),
    content: 'This is a test assistant response with some content to save.',
    assistantName: 'Claude',
    notebookTitle: 'Test Notebook'
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it('should not render when isOpen is false', () => {
    renderWithToast(
      <SaveAssistantContentDialog {...defaultProps} isOpen={false} />
    );

    expect(screen.queryByText('Save Assistant Response')).not.toBeInTheDocument();
  });

  it('should render dialog when isOpen is true', () => {
    renderWithToast(<SaveAssistantContentDialog {...defaultProps} />);

    expect(screen.getByText('Save Assistant Response')).toBeInTheDocument();
    expect(screen.getByText('to Test Notebook')).toBeInTheDocument();
    expect(screen.getByText('Content Preview')).toBeInTheDocument();
    expect(screen.getByText('File Name')).toBeInTheDocument();
  });

  it('should show content preview with word and character count', () => {
    renderWithToast(<SaveAssistantContentDialog {...defaultProps} />);

    expect(screen.getByText(/This is a test assistant response/)).toBeInTheDocument();
    expect(screen.getByText(/11 words/)).toBeInTheDocument();
    expect(screen.getByText(/60 characters/)).toBeInTheDocument();
  });

  it('should generate intelligent default filename', () => {
    renderWithToast(<SaveAssistantContentDialog {...defaultProps} />);

    const fileNameInput = screen.getByLabelText('File Name') as HTMLInputElement;
    expect(fileNameInput.value).toMatch(/^claude-this-is-a-test-assistant-response.*\.md$/);
  });

  it('should allow editing the filename', async () => {
    const user = userEvent.setup();
    renderWithToast(<SaveAssistantContentDialog {...defaultProps} />);

    const fileNameInput = screen.getByLabelText('File Name');
    await user.clear(fileNameInput);
    await user.type(fileNameInput, 'custom-filename.md');

    expect(fileNameInput).toHaveValue('custom-filename.md');
  });

  it('should validate filename and show error for invalid names', async () => {
    const user = userEvent.setup();
    renderWithToast(<SaveAssistantContentDialog {...defaultProps} />);

    const fileNameInput = screen.getByLabelText('File Name');
    
    // Test empty filename
    await user.clear(fileNameInput);
    expect(screen.getByText('File name is required')).toBeInTheDocument();

    // Test filename without .md extension
    await user.type(fileNameInput, 'invalid-filename.txt');
    expect(screen.getByText(/File name must end with \.md/)).toBeInTheDocument();

    // Test filename with invalid characters
    await user.clear(fileNameInput);
    await user.type(fileNameInput, 'invalid<>filename.md');
    expect(screen.getByText(/File name must end with \.md and contain only valid characters/)).toBeInTheDocument();
  });

  it('should disable save button when filename is invalid', async () => {
    const user = userEvent.setup();
    renderWithToast(<SaveAssistantContentDialog {...defaultProps} />);

    const fileNameInput = screen.getByLabelText('File Name');
    const saveButton = screen.getByText('Save File');

    await user.clear(fileNameInput);
    expect(saveButton).toBeDisabled();
  });

  it('should render folder tree when provided', () => {
    renderWithToast(
      <SaveAssistantContentDialog 
        {...defaultProps} 
        folderTree={mockFolderTree}
      />
    );

    expect(screen.getByText('Select Folder (Optional)')).toBeInTheDocument();
    // Verify folder options exist (avoid brittle count assertions for "Root")
    expect(screen.getByText('Documents')).toBeInTheDocument();
    expect(screen.getByText('Images')).toBeInTheDocument();
    expect(screen.getByText('Reports')).toBeInTheDocument();
    // Verify selection display exists
    expect(screen.getByText('Selected:')).toBeInTheDocument();
  });

  it('should allow selecting folders from the tree', async () => {
    const user = userEvent.setup();
    renderWithToast(
      <SaveAssistantContentDialog 
        {...defaultProps} 
        folderTree={mockFolderTree}
      />
    );

    // Radios represent selection state; first radio corresponds to Root
    const radios = screen.getAllByRole('radio');
    expect(radios[0]).toBeChecked();

    // Click on Documents folder row and expect its radio to be checked
    const documentsFolder = screen.getByText('Documents');
    await user.click(documentsFolder);

    await waitFor(() => {
      expect(radios[1]).toBeChecked();
      expect(radios[0]).not.toBeChecked();
    });
  });

  it('should call onSave with correct parameters when save button is clicked', async () => {
    const user = userEvent.setup();
    const mockOnSave = vi.fn().mockResolvedValue(undefined);
    
    renderWithToast(
      <SaveAssistantContentDialog 
        {...defaultProps} 
        onSave={mockOnSave}
        folderTree={mockFolderTree}
      />
    );

    // Select a folder
    const documentsFolder = screen.getByText('Documents');
    await user.click(documentsFolder);

    // Edit filename
    const fileNameInput = screen.getByLabelText('File Name');
    await user.clear(fileNameInput);
    await user.type(fileNameInput, 'test-response.md');

    // Click save
    const saveButton = screen.getByText('Save File');
    await user.click(saveButton);

    await waitFor(() => {
      expect(mockOnSave).toHaveBeenCalledWith('test-response.md', 'documents');
    });
  });

  it('should call onSave with undefined folder when root is selected', async () => {
    const user = userEvent.setup();
    const mockOnSave = vi.fn().mockResolvedValue(undefined);
    
    renderWithToast(
      <SaveAssistantContentDialog 
        {...defaultProps} 
        onSave={mockOnSave}
        folderTree={mockFolderTree}
      />
    );

    // Root should be selected by default
    const fileNameInput = screen.getByLabelText('File Name');
    await user.clear(fileNameInput);
    await user.type(fileNameInput, 'test-response.md');

    const saveButton = screen.getByText('Save File');
    await user.click(saveButton);

    await waitFor(() => {
      expect(mockOnSave).toHaveBeenCalledWith('test-response.md', undefined);
    });
  });

  it('should show loading state during save operation', async () => {
    const user = userEvent.setup();
    let resolveSave: () => void;
    const mockOnSave = vi.fn(() => new Promise<void>(resolve => {
      resolveSave = resolve;
    }));
    
    renderWithToast(
      <SaveAssistantContentDialog 
        {...defaultProps} 
        onSave={mockOnSave}
      />
    );

    const saveButton = screen.getByText('Save File');
    await user.click(saveButton);

    expect(screen.getByText('Saving...')).toBeInTheDocument();
    expect(saveButton).toBeDisabled();

    // Resolve the save operation
    resolveSave!();
    
    await waitFor(() => {
      expect(screen.queryByText('Saving...')).not.toBeInTheDocument();
    });
  });

  it('should handle save errors and show error message', async () => {
    const user = userEvent.setup();
    const mockOnSave = vi.fn<[string, string?], Promise<void>>().mockRejectedValue(new Error('Network error'));
    
    renderWithToast(
      <SaveAssistantContentDialog 
        {...defaultProps} 
        onSave={mockOnSave}
      />
    );

    const saveButton = screen.getByText('Save File');
    await user.click(saveButton);

    await waitFor(() => {
      expect(screen.getByText('Save Failed')).toBeInTheDocument();
    });
  });

  it('should handle authentication errors specifically', async () => {
    const user = userEvent.setup();
    const mockOnSave = vi.fn().mockRejectedValue(new Error('Authentication failed')) as (fileName: string, folderPath?: string) => Promise<void>;
    
    renderWithToast(
      <SaveAssistantContentDialog 
        {...defaultProps} 
        onSave={mockOnSave}
      />
    );

    const saveButton = screen.getByText('Save File');
    await user.click(saveButton);

    await waitFor(() => {
      expect(screen.getByText('Save Failed')).toBeInTheDocument();
    });
  });

  it('should call onClose when cancel button is clicked', async () => {
    const user = userEvent.setup();
    const mockOnClose = vi.fn();
    
    renderWithToast(
      <SaveAssistantContentDialog 
        {...defaultProps} 
        onClose={mockOnClose}
      />
    );

    const cancelButton = screen.getByText('Cancel');
    await user.click(cancelButton);

    expect(mockOnClose).toHaveBeenCalled();
  });

  it('should call onClose when X button is clicked', async () => {
    const user = userEvent.setup();
    const mockOnClose = vi.fn();
    
    renderWithToast(
      <SaveAssistantContentDialog 
        {...defaultProps} 
        onClose={mockOnClose}
      />
    );

    const closeButton = screen.getByLabelText('Close');
    await user.click(closeButton);

    expect(mockOnClose).toHaveBeenCalled();
  });

  it('should prevent closing when save is in progress', async () => {
    const user = userEvent.setup();
    const mockOnClose = vi.fn();
    const mockOnSave = vi.fn(() => new Promise<void>(() => {})); // Never resolves
    
    renderWithToast(
      <SaveAssistantContentDialog 
        {...defaultProps} 
        onClose={mockOnClose}
        onSave={mockOnSave}
      />
    );

    // Start save operation
    const saveButton = screen.getByText('Save File');
    await user.click(saveButton);

    // Try to close
    const cancelButton = screen.getByText('Cancel');
    await user.click(cancelButton);

    expect(mockOnClose).not.toHaveBeenCalled();
  });

  it('should be disabled when disabled prop is true', () => {
    renderWithToast(
      <SaveAssistantContentDialog 
        {...defaultProps} 
        disabled={true}
      />
    );

    const saveButton = screen.getByText('Save File');
    expect(saveButton).toBeDisabled();
  });

  it('should show assistant name in footer when provided', () => {
    renderWithToast(<SaveAssistantContentDialog {...defaultProps} />);

    expect(screen.getByText('From Claude')).toBeInTheDocument();
  });

  it('should truncate content preview for long content', () => {
    const longContent = 'A'.repeat(300);
    
    renderWithToast(
      <SaveAssistantContentDialog 
        {...defaultProps} 
        content={longContent}
      />
    );

    const preview = screen.getByText(/A{200}\.\.\./, { exact: false });
    expect(preview).toBeInTheDocument();
  });

  it('should calculate word count correctly', () => {
    const content = 'This is exactly five words';
    
    renderWithToast(
      <SaveAssistantContentDialog 
        {...defaultProps} 
        content={content}
      />
    );

    expect(screen.getByText(/5.*words/)).toBeInTheDocument();
  });
}); 