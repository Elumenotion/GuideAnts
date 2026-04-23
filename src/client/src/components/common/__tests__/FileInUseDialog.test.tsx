import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import '@testing-library/jest-dom';

import { FileInUseDialog } from '../FileInUseDialog';

// Mock useNavigate
const mockNavigate = vi.fn();
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual('react-router-dom');
  return {
    ...actual,
    useNavigate: () => mockNavigate,
  };
});

describe('FileInUseDialog', () => {
  const defaultProps = {
    isOpen: false,
    onClose: vi.fn(),
    fileName: 'test-file.txt',
    notebooksUsingFile: [
      {
        notebookId: 'notebook-1',
        notebookFileId: 'file-1',
        notebookTitle: 'Test Notebook 1',
        fileName: 'test-file.txt',
        relativePath: 'shared-content/test-file.txt'
      },
      {
        notebookId: 'notebook-2',
        notebookFileId: 'file-2',
        notebookTitle: 'Another Test Notebook',
        fileName: 'test-file.txt',
        relativePath: 'data/test-file.txt'
      }
    ],
    projectId: 'test-project-id'
  };

  beforeEach(() => {
    vi.clearAllMocks();
    cleanup();
  });

  const renderWithRouter = (props = defaultProps) => {
    return render(
      <MemoryRouter>
        <FileInUseDialog {...props} />
      </MemoryRouter>
    );
  };

  describe('Rendering', () => {
    it('renders nothing when closed', () => {
      renderWithRouter();
      expect(screen.queryByText(/cannot delete file/i)).not.toBeInTheDocument();
    });

    it('renders dialog when open', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      expect(screen.getByText(/cannot delete file/i)).toBeInTheDocument();
      expect(screen.getAllByText((content, element) => 
        element?.textContent?.includes('test-file.txt') ?? false
      )[0]).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /close/i })).toBeInTheDocument();
    });

    it('displays the correct file name', () => {
      renderWithRouter({ 
        ...defaultProps, 
        isOpen: true, 
        fileName: 'my-document.pdf' 
      });
      
      expect(screen.getAllByText((content, element) => 
        element?.textContent?.includes('my-document.pdf') ?? false
      )[0]).toBeInTheDocument();
    });

    it('shows warning message about file being in use', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      expect(screen.getByText(/cannot be deleted because it is currently being used/i)).toBeInTheDocument();
    });

    it('shows data traceability information', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      expect(screen.getByText(/this feature preserves data traceability/i)).toBeInTheDocument();
    });

    it('lists all notebooks using the file', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      expect(screen.getByText('Test Notebook 1')).toBeInTheDocument();
      expect(screen.getByText('Another Test Notebook')).toBeInTheDocument();
      expect(screen.getByText('shared-content/test-file.txt')).toBeInTheDocument();
      expect(screen.getByText('data/test-file.txt')).toBeInTheDocument();
    });

    it('shows deletion instructions', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      expect(screen.getByText(/to delete this file/i)).toBeInTheDocument();
      expect(screen.getByText(/remove the file from all notebooks/i)).toBeInTheDocument();
      expect(screen.getByText(/return to this page and try deleting again/i)).toBeInTheDocument();
    });
  });

  describe('User Interactions', () => {
    it('calls onClose when close button clicked', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      const closeButton = screen.getByRole('button', { name: /close/i });
      fireEvent.click(closeButton);
      
      expect(defaultProps.onClose).toHaveBeenCalled();
    });

    it('calls onClose when clicking outside (backdrop)', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      const backdrop = document.querySelector('.fixed.inset-0');
      fireEvent.click(backdrop!);
      
      expect(defaultProps.onClose).toHaveBeenCalled();
    });

    it('does not close when clicking on modal content', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      const modal = document.querySelector('.bg-white.rounded-lg');
      fireEvent.click(modal!);
      
      expect(defaultProps.onClose).not.toHaveBeenCalled();
    });

    it('navigates to notebook when clicking on notebook item', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      const notebookItem = screen.getByText('Test Notebook 1');
      fireEvent.click(notebookItem);
      
      expect(defaultProps.onClose).toHaveBeenCalled();
      expect(mockNavigate).toHaveBeenCalledWith('/projects/test-project-id/notebooks/notebook-1');
    });

    it('navigates to correct notebook for each item', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      const notebook2Item = screen.getByText('Another Test Notebook');
      fireEvent.click(notebook2Item);
      
      expect(mockNavigate).toHaveBeenCalledWith('/projects/test-project-id/notebooks/notebook-2');
    });
  });

  describe('Dialog Structure and Styling', () => {
    it('has proper dialog structure with overlay', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      const overlay = document.querySelector('.fixed.inset-0.bg-black.bg-opacity-50');
      expect(overlay).toBeInTheDocument();
    });

    it('has proper modal structure', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      const modal = document.querySelector('.bg-white.rounded-lg.shadow-xl');
      expect(modal).toBeInTheDocument();
    });

    it('applies hover effects to notebook items', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      const notebookItems = screen.getAllByText(/Test Notebook/);
      expect(notebookItems[0].closest('.hover\\:bg-gray-50')).toBeInTheDocument();
    });

    it('shows proper styling for file paths', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      const codePath = document.querySelector('code.bg-gray-100');
      expect(codePath).toBeInTheDocument();
    });

    it('displays info icon for traceability message', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      const infoIcons = document.querySelectorAll('svg');
      expect(infoIcons.length).toBeGreaterThan(0);
    });
  });

  describe('Edge Cases', () => {
    it('handles empty notebooks list', () => {
      renderWithRouter({ 
        ...defaultProps, 
        isOpen: true, 
        notebooksUsingFile: [] 
      });
      
      expect(screen.getByText(/cannot delete file/i)).toBeInTheDocument();
      expect(screen.getByText(/notebooks using this file/i)).toBeInTheDocument();
    });

    it('handles single notebook', () => {
      renderWithRouter({ 
        ...defaultProps, 
        isOpen: true, 
        notebooksUsingFile: [defaultProps.notebooksUsingFile[0]] 
      });
      
      expect(screen.getByText('Test Notebook 1')).toBeInTheDocument();
      expect(screen.queryByText('Another Test Notebook')).not.toBeInTheDocument();
    });

    it('handles empty filename gracefully', () => {
      renderWithRouter({ 
        ...defaultProps, 
        isOpen: true, 
        fileName: '' 
      });
      
      expect(screen.getByText(/cannot delete file/i)).toBeInTheDocument();
    });

    it('handles very long filename', () => {
      const longFileName = 'a'.repeat(100) + '.txt';
      renderWithRouter({ 
        ...defaultProps, 
        isOpen: true, 
        fileName: longFileName 
      });
      
      expect(screen.getAllByText((content, element) => 
        element?.textContent?.includes(longFileName) ?? false
      )[0]).toBeInTheDocument();
    });

    it('handles special characters in filename', () => {
      const specialFileName = 'file-with-special-chars_@#$%.txt';
      renderWithRouter({ 
        ...defaultProps, 
        isOpen: true, 
        fileName: specialFileName 
      });
      
      expect(screen.getAllByText((content, element) => 
        element?.textContent?.includes(specialFileName) ?? false
      )[0]).toBeInTheDocument();
    });

    it('handles notebooks with very long titles', () => {
      const longTitle = 'A'.repeat(100);
      renderWithRouter({ 
        ...defaultProps, 
        isOpen: true,
        notebooksUsingFile: [{
          ...defaultProps.notebooksUsingFile[0],
          notebookTitle: longTitle
        }]
      });
      
      expect(screen.getByText(longTitle)).toBeInTheDocument();
    });
  });

  describe('State Management', () => {
    it('maintains state when props change', () => {
      const { rerender } = render(
        <MemoryRouter>
          <FileInUseDialog {...defaultProps} isOpen={true} fileName="file1.txt" />
        </MemoryRouter>
      );
      
      expect(screen.getAllByText((content, element) => 
        element?.textContent?.includes('file1.txt') ?? false
      )[0]).toBeInTheDocument();
      
      rerender(
        <MemoryRouter>
          <FileInUseDialog {...defaultProps} isOpen={true} fileName="file2.txt" />
        </MemoryRouter>
      );
      
      expect(screen.getAllByText((content, element) => 
        element?.textContent?.includes('file2.txt') ?? false
      )[0]).toBeInTheDocument();
      expect(screen.queryByText((content, element) => 
        element?.textContent?.includes('file1.txt') ?? false
      )).not.toBeInTheDocument();
    });

    it('properly handles open/close state changes', () => {
      const { rerender } = render(
        <MemoryRouter>
          <FileInUseDialog {...defaultProps} isOpen={false} />
        </MemoryRouter>
      );
      
      expect(screen.queryByText(/cannot delete file/i)).not.toBeInTheDocument();
      
      rerender(
        <MemoryRouter>
          <FileInUseDialog {...defaultProps} isOpen={true} />
        </MemoryRouter>
      );
      
      expect(screen.getByText(/cannot delete file/i)).toBeInTheDocument();
      
      rerender(
        <MemoryRouter>
          <FileInUseDialog {...defaultProps} isOpen={false} />
        </MemoryRouter>
      );
      
      expect(screen.queryByText(/cannot delete file/i)).not.toBeInTheDocument();
    });
  });

  describe('Accessibility', () => {
    it('has accessible close button', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      const closeButton = screen.getByRole('button', { name: /close/i });
      expect(closeButton).toBeInTheDocument();
      expect(closeButton).not.toBeDisabled();
    });

    it('maintains focus management', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      const closeButton = screen.getByRole('button', { name: /close/i });
      closeButton.focus();
      expect(document.activeElement).toBe(closeButton);
    });

    it('has proper heading structure', () => {
      renderWithRouter({ ...defaultProps, isOpen: true });
      
      const heading = screen.getByRole('heading', { level: 3 });
      expect(heading).toHaveTextContent(/cannot delete file/i);
    });
  });
}); 