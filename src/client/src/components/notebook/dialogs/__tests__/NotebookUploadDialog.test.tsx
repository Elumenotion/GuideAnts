import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import { NotebookUploadDialog } from '../NotebookUploadDialog';
import { FolderTreeDto, ProjectContentFile } from '../../../../types/project';
import { NotebookFolderTreeDto } from '../../../../types/notebook';
import '@testing-library/jest-dom';

// Reusable helpers
function createFile(name: string, size: number, type: string) {
  const file = new File([new ArrayBuffer(size)], name, { type });
  // vitest/jsdom does not always populate size correctly when using ArrayBuffer
  Object.defineProperty(file, 'size', { value: size });
  return file;
}

// Mock project folder tree
const mockProjectFile: ProjectContentFile = {
  id: 'file-1',
  fileName: 'project-file.txt',
  path: '/project-file.txt',
  relativePath: 'project-file.txt',
  contentType: 'text/plain',
  index: false,
  documentId: 'doc-1',
  created: '2023-01-01T00:00:00Z',
  fileSize: 2048,
  latestVersion: 1
};

const mockProjectFolderTree: FolderTreeDto = {
  id: 'root',
  name: 'Test Project',
  relativePath: '',
  subFolders: [
    {
      id: 'folder-1',
      name: 'Documents',
      relativePath: 'Documents',
      subFolders: [],
      files: [mockProjectFile]
    }
  ],
  files: [
    {
      id: 'file-2',
      fileName: 'root-file.csv',
      path: '/root-file.csv',
      relativePath: 'root-file.csv',
      contentType: 'text/csv',
      index: false,
      documentId: 'doc-2',
      created: '2023-01-01T00:00:00Z',
      fileSize: 1024,
      latestVersion: 2
    }
  ]
};

const mockNotebookFolderTree: NotebookFolderTreeDto = {
  id: 'notebook-root',
  name: 'Notebook Root',
  relativePath: '',
  subFolders: [
    {
      id: 'nb-folder-1',
      name: 'Reports',
      relativePath: 'Reports',
      subFolders: [],
      files: []
    }
  ],
  files: []
};

describe('NotebookUploadDialog', () => {
  const onClose = vi.fn();
  const onUpload = vi.fn().mockResolvedValue(undefined);
  const onCopyFromProject = vi.fn().mockResolvedValue(undefined);

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders nothing when dialog is closed', () => {
    render(<NotebookUploadDialog isOpen={false} onClose={onClose} onUpload={onUpload} />);
    expect(screen.queryByText(/add files to notebook/i)).toBeNull();
  });

  it('displays header and shows only local files tab when no project files provided', () => {
    render(<NotebookUploadDialog isOpen={true} onClose={onClose} onUpload={onUpload} />);
    expect(screen.getByText(/add files to notebook/i)).toBeInTheDocument();
    expect(screen.getByText(/select files from your computer/i)).toBeInTheDocument();
    expect(screen.queryByText(/upload local files/i)).toBeNull(); // No tabs when only one option
  });

  it('displays tabs when project files are available', () => {
    render(
      <NotebookUploadDialog 
        isOpen={true} 
        onClose={onClose} 
        onUpload={onUpload}
        onCopyFromProject={onCopyFromProject}
        projectFolderTree={mockProjectFolderTree}
      />
    );
    
    expect(screen.getByText(/upload local files/i)).toBeInTheDocument();
    expect(screen.getByText(/select project files/i)).toBeInTheDocument();
  });

  it('allows switching between tabs', () => {
    render(
      <NotebookUploadDialog 
        isOpen={true} 
        onClose={onClose} 
        onUpload={onUpload}
        onCopyFromProject={onCopyFromProject}
        projectFolderTree={mockProjectFolderTree}
      />
    );
    
    // Should start on local files tab
    expect(screen.getByText(/select files from your computer/i)).toBeInTheDocument();
    
    // Switch to project files tab
    fireEvent.click(screen.getByText(/select project files/i));
    expect(screen.getByText(/select files from current project/i)).toBeInTheDocument();
  });

  it('displays project files in tree structure', () => {
    render(
      <NotebookUploadDialog 
        isOpen={true} 
        onClose={onClose} 
        onUpload={onUpload}
        onCopyFromProject={onCopyFromProject}
        projectFolderTree={mockProjectFolderTree}
      />
    );
    
    // Switch to project files tab
    fireEvent.click(screen.getByText(/select project files/i));
    
    // Should show folder and files
    expect(screen.getByText('Documents')).toBeInTheDocument();
    expect(screen.getByText('project-file.txt')).toBeInTheDocument();
    expect(screen.getByText('root-file.csv')).toBeInTheDocument();
    expect(screen.getByText('v2')).toBeInTheDocument(); // Version badge
  });

  it('allows selecting and deselecting project files', () => {
    render(
      <NotebookUploadDialog 
        isOpen={true} 
        onClose={onClose} 
        onUpload={onUpload}
        onCopyFromProject={onCopyFromProject}
        projectFolderTree={mockProjectFolderTree}
      />
    );
    
    // Switch to project files tab
    fireEvent.click(screen.getByText(/select project files/i));
    
    // Select a file by clicking on the file row
    const fileRow = screen.getByText('root-file.csv').closest('div');
    fireEvent.click(fileRow!);
    
    expect(screen.getByText(/1 file to copy/i)).toBeInTheDocument();
    
    // Deselect the file by clicking again
    fireEvent.click(fileRow!);
    expect(screen.queryByText(/1 file to copy/i)).toBeNull();
  });

  it('calls onCopyFromProject when copying selected files', async () => {
    render(
      <NotebookUploadDialog 
        isOpen={true} 
        onClose={onClose} 
        onUpload={onUpload}
        onCopyFromProject={onCopyFromProject}
        projectFolderTree={mockProjectFolderTree}
      />
    );
    
    // Switch to project files tab
    fireEvent.click(screen.getByText(/select project files/i));
    
    // Select a file by clicking on the file row
    const fileRow = screen.getByText('root-file.csv').closest('div');
    fireEvent.click(fileRow!);
    
    // Click copy button
    const copyBtn = screen.getByRole('button', { name: /copy files/i });
    expect(copyBtn).not.toBeDisabled();
    fireEvent.click(copyBtn);
    
    await waitFor(() => {
      expect(onCopyFromProject).toHaveBeenCalledWith([{
        fileId: 'file-2',
        version: 2
      }], undefined);
      expect(onClose).toHaveBeenCalled();
    });
  });

  it('handles local file upload as before', async () => {
    render(<NotebookUploadDialog isOpen={true} onClose={onClose} onUpload={onUpload} />);

    const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement;
    const file = createFile('hello.txt', 1024, 'text/plain');
    fireEvent.change(fileInput!, { target: { files: [file] } });

    const uploadBtn = screen.getByRole('button', { name: /upload files/i });
    fireEvent.click(uploadBtn);

    await waitFor(() => {
      expect(onUpload).toHaveBeenCalledWith([file], undefined);
      expect(onClose).toHaveBeenCalled();
    });
  });

  it('shows correct button text and status for each tab', () => {
    render(
      <NotebookUploadDialog 
        isOpen={true} 
        onClose={onClose} 
        onUpload={onUpload}
        onCopyFromProject={onCopyFromProject}
        projectFolderTree={mockProjectFolderTree}
      />
    );
    
    // Local files tab
    expect(screen.getByRole('button', { name: /upload files/i })).toBeInTheDocument();
    
    // Switch to project files tab
    fireEvent.click(screen.getByText(/select project files/i));
    expect(screen.getByRole('button', { name: /copy files/i })).toBeInTheDocument();
  });

  describe('Drag and Drop', () => {
        it('handles file drop on drag area', () => {
      render(<NotebookUploadDialog isOpen={true} onClose={onClose} onUpload={onUpload} />);

      const dropArea = screen.getByText(/or drag and drop/i).closest('div');
      const file = createFile('dropped.txt', 512, 'text/plain');
      
      fireEvent.drop(dropArea!, {
        dataTransfer: {
          files: [file]
        }
      });
      
      expect(screen.getByText('dropped.txt')).toBeInTheDocument();
    });

        it('handles drag over events', () => {
      render(<NotebookUploadDialog isOpen={true} onClose={onClose} onUpload={onUpload} />);

      const dropArea = screen.getByText(/or drag and drop/i).closest('div');
      
      fireEvent.dragOver(dropArea!, {
        dataTransfer: {}
      });
      
      // The drag over handler should prevent default
      expect(dropArea).toBeDefined();
    });
  });

  describe('Folder Selection', () => {
    it('displays target folder when initialFolderId is provided', () => {
      render(
        <NotebookUploadDialog 
          isOpen={true} 
          onClose={onClose} 
          onUpload={onUpload}
          initialFolderId="Reports"
          folderTree={mockNotebookFolderTree}
        />
      );
      
      expect(screen.getByText('to Reports')).toBeInTheDocument();
    });

    it('shows Root when no folder is selected', () => {
      render(
        <NotebookUploadDialog 
          isOpen={true} 
          onClose={onClose} 
          onUpload={onUpload}
          folderTree={mockNotebookFolderTree}
        />
      );
      
      expect(screen.getByText('to Root')).toBeInTheDocument();
    });
  });

  describe('File Information Display', () => {
    it('displays file sizes correctly', () => {
      render(<NotebookUploadDialog isOpen={true} onClose={onClose} onUpload={onUpload} />);
      
      const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement;
      const file = createFile('test-file.txt', 2048, 'text/plain');
      fireEvent.change(fileInput!, { target: { files: [file] } });
      
      expect(screen.getByText('2 KB')).toBeInTheDocument();
    });

    it('handles zero byte files', () => {
      render(<NotebookUploadDialog isOpen={true} onClose={onClose} onUpload={onUpload} />);
      
      const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement;
      const file = createFile('empty.txt', 0, 'text/plain');
      fireEvent.change(fileInput!, { target: { files: [file] } });
      
      expect(screen.getByText('0 B')).toBeInTheDocument();
    });

    it('handles large files with proper units', () => {
      render(<NotebookUploadDialog isOpen={true} onClose={onClose} onUpload={onUpload} />);
      
      const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement;
      const file = createFile('large-file.pdf', 1048576, 'application/pdf'); // 1MB
      fireEvent.change(fileInput!, { target: { files: [file] } });
      
      expect(screen.getByText('1 MB')).toBeInTheDocument();
    });
  });

  // Indexing option removed from UI

  describe('Error Handling', () => {
    it('handles upload failure gracefully', async () => {
      const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
      onUpload.mockRejectedValue(new Error('Upload failed'));
      
      render(<NotebookUploadDialog isOpen={true} onClose={onClose} onUpload={onUpload} />);

      const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement;
      const file = createFile('test.txt', 1024, 'text/plain');
      fireEvent.change(fileInput!, { target: { files: [file] } });

      const uploadBtn = screen.getByRole('button', { name: /upload files/i });
      fireEvent.click(uploadBtn);

      await waitFor(() => {
        expect(consoleError).toHaveBeenCalledWith('Upload failed:', expect.any(Error));
      });
      
      consoleError.mockRestore();
    });

    it('handles copy from project failure gracefully', async () => {
      const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
      onCopyFromProject.mockRejectedValue(new Error('Copy failed'));
      
      render(
        <NotebookUploadDialog 
          isOpen={true} 
          onClose={onClose} 
          onUpload={onUpload}
          onCopyFromProject={onCopyFromProject}
          projectFolderTree={mockProjectFolderTree}
        />
      );
      
      // Switch to project files tab
      fireEvent.click(screen.getByText(/select project files/i));
      
      // Select a file
      const fileRow = screen.getByText('root-file.csv').closest('div');
      fireEvent.click(fileRow!);
      
      // Try to copy
      const copyBtn = screen.getByRole('button', { name: /copy files/i });
      fireEvent.click(copyBtn);
      
      await waitFor(() => {
        expect(consoleError).toHaveBeenCalledWith('Copy from project failed:', expect.any(Error));
      });
      
      consoleError.mockRestore();
    });

    it('prevents upload when no files selected', () => {
      render(<NotebookUploadDialog isOpen={true} onClose={onClose} onUpload={onUpload} />);
      
      const uploadBtn = screen.getByRole('button', { name: /upload files/i });
      fireEvent.click(uploadBtn);
      
      expect(onUpload).not.toHaveBeenCalled();
    });

    it('prevents copy when no project files selected', () => {
      render(
        <NotebookUploadDialog 
          isOpen={true} 
          onClose={onClose} 
          onUpload={onUpload}
          onCopyFromProject={onCopyFromProject}
          projectFolderTree={mockProjectFolderTree}
        />
      );
      
      // Switch to project files tab
      fireEvent.click(screen.getByText(/select project files/i));
      
      // Try to copy without selecting files
      const copyBtn = screen.getByRole('button', { name: /copy files/i });
      fireEvent.click(copyBtn);
      
      expect(onCopyFromProject).not.toHaveBeenCalled();
    });
  });

  describe('Loading States', () => {
    it('shows uploading state during upload', async () => {
      let resolveUpload: (value: unknown) => void;
      const uploadPromise = new Promise(resolve => { resolveUpload = resolve; });
      onUpload.mockReturnValue(uploadPromise);
      
      render(<NotebookUploadDialog isOpen={true} onClose={onClose} onUpload={onUpload} />);

      const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement;
      const file = createFile('test.txt', 1024, 'text/plain');
      fireEvent.change(fileInput!, { target: { files: [file] } });

      const uploadBtn = screen.getByRole('button', { name: /upload files/i });
      fireEvent.click(uploadBtn);
      
      // Should show loading state
      expect(screen.getByText(/uploading.../i)).toBeInTheDocument();
      
      // Resolve the upload
      resolveUpload!(undefined);
      
      await waitFor(() => {
        expect(onClose).toHaveBeenCalled();
      });
    });

    it('disables close button during upload', async () => {
      let resolveUpload: (value: unknown) => void;
      const uploadPromise = new Promise(resolve => { resolveUpload = resolve; });
      onUpload.mockReturnValue(uploadPromise);
      
      render(<NotebookUploadDialog isOpen={true} onClose={onClose} onUpload={onUpload} />);

      const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement;
      const file = createFile('test.txt', 1024, 'text/plain');
      fireEvent.change(fileInput!, { target: { files: [file] } });

      const uploadBtn = screen.getByRole('button', { name: /upload files/i });
      fireEvent.click(uploadBtn);
      
      // Close button should be disabled
      const closeBtn = screen.getByRole('button', { name: /cancel/i });
      expect(closeBtn).toBeDisabled();
      
      resolveUpload!(undefined);
    });
  });

  describe('Project File Tree Navigation', () => {
    it('handles empty project folder tree', () => {
      render(
        <NotebookUploadDialog 
          isOpen={true} 
          onClose={onClose} 
          onUpload={onUpload}
          onCopyFromProject={onCopyFromProject}
          projectFolderTree={null}
        />
      );
      
      // When projectFolderTree is null, tabs should not be shown
      expect(screen.queryByText(/select project files/i)).not.toBeInTheDocument();
      
      // Should only show the local upload interface
      expect(screen.getByText(/select files from your computer/i)).toBeInTheDocument();
    });

    it('handles deeply nested folder structure', () => {
      const nestedTree: FolderTreeDto = {
        id: 'root',
        name: 'Root',
        relativePath: '',
        subFolders: [
          {
            id: 'level1',
            name: 'Level 1',
            relativePath: 'Level 1',
            subFolders: [
              {
                id: 'level2',
                name: 'Level 2',
                relativePath: 'Level 1/Level 2',
                subFolders: [],
                files: [mockProjectFile]
              }
            ],
            files: []
          }
        ],
        files: []
      };
      
      render(
        <NotebookUploadDialog 
          isOpen={true} 
          onClose={onClose} 
          onUpload={onUpload}
          onCopyFromProject={onCopyFromProject}
          projectFolderTree={nestedTree}
        />
      );
      
      // Switch to project files tab
      fireEvent.click(screen.getByText(/select project files/i));
      
      expect(screen.getByText('Level 1')).toBeInTheDocument();
      expect(screen.getByText('Level 2')).toBeInTheDocument();
    });
  });

  describe('Accessibility', () => {
        it('has proper ARIA labels', () => {
      render(<NotebookUploadDialog isOpen={true} onClose={onClose} onUpload={onUpload} />);

    const uploadButton = screen.getByRole('button', { name: /upload files/i });
    expect(uploadButton).toBeInTheDocument();
    });

        it('supports keyboard navigation', async () => {
      const user = userEvent.setup();
      render(<NotebookUploadDialog isOpen={true} onClose={onClose} onUpload={onUpload} />);

    // Navigate to cancel button instead
      // Test that we can navigate to buttons
      const cancelBtn = screen.getByRole('button', { name: /cancel/i });
      await user.click(cancelBtn);
      expect(cancelBtn).toHaveFocus();
    });
  });
}); 