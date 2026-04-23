import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '../../../../test/test-utils';
import { PublishToProjectDialog } from '../PublishToProjectDialog';
import { ProjectFolderDto } from '../../../../types/project';
import { NotebookFileDto, OriginFileInfoDto } from '../../../../types/notebook';
import '@testing-library/jest-dom';
import userEvent from '@testing-library/user-event';

// Mock data
const mockProjectFolders: ProjectFolderDto[] = [
  {
    id: 'folder-1',
    name: 'Documents',
    relativePath: 'Documents',
    projectId: 'project-1',
    created: '2023-01-01T00:00:00Z',
    subFolders: [],
    files: []
  },
  {
    id: 'folder-2',
    name: 'Images',
    relativePath: 'Images',
    projectId: 'project-1',
    created: '2023-01-02T00:00:00Z',
    subFolders: [],
    files: []
  }
];

const mockNotebookFile: NotebookFileDto = {
  id: 'file-1',
  fileName: 'test.txt',
  relativePath: 'test.txt',
  fileSize: 1024,
  lastModifiedUtc: '2023-01-01T00:00:00Z',
  fileHash: 'hash123',
  index: false
};

const mockNotebookFileWithLineage: NotebookFileDto = {
  ...mockNotebookFile,
  originContentFileVersionId: 'content-file-1'
};

const mockOriginFileInfo: OriginFileInfoDto = {
  fileName: 'test.txt',
  folderPath: 'Documents/test.txt',
  contentFileId: 'content-file-1',
  versionNumber: 1
};

const defaultProps = {
  isOpen: true,
  notebookFile: mockNotebookFile,
  projectFolders: mockProjectFolders,
  projectTitle: 'Test Project',
  onClose: vi.fn(),
  onPublish: vi.fn(),
};

describe('PublishToProjectDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('Rendering', () => {
    it('renders nothing when dialog is closed', () => {
      render(<PublishToProjectDialog {...defaultProps} isOpen={false} />);
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });

    it('renders dialog with title when open', () => {
      render(<PublishToProjectDialog {...defaultProps} />);
      expect(screen.getByText('Publish to Project')).toBeInTheDocument();
    });

    it('displays the notebook file name', () => {
      render(<PublishToProjectDialog {...defaultProps} />);
      expect(screen.getByText('test.txt')).toBeInTheDocument();
    });

    it('renders publish and cancel buttons', () => {
      render(<PublishToProjectDialog {...defaultProps} />);
      expect(screen.getByRole('button', { name: /publish/i })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /cancel/i })).toBeInTheDocument();
    });
  });

  describe('Folder Selection for New Files', () => {
    it('shows folder selection dropdown for files without lineage', () => {
      render(<PublishToProjectDialog {...defaultProps} />);
      expect(screen.getByLabelText(/destination folder/i)).toBeInTheDocument();
    });

    it('displays project root as default option', () => {
      render(<PublishToProjectDialog {...defaultProps} />);
      expect(screen.getByRole('option', { name: 'Test Project' })).toBeInTheDocument();
    });

    it('displays all available folders in dropdown', () => {
      render(<PublishToProjectDialog {...defaultProps} />);
      expect(screen.getByRole('option', { name: /Documents/i })).toBeInTheDocument();
      expect(screen.getByRole('option', { name: /Images/i })).toBeInTheDocument();
    });

    it('allows folder selection', async () => {
      const user = userEvent.setup();
      render(<PublishToProjectDialog {...defaultProps} />);
      
      const select = screen.getByLabelText(/destination folder/i);
      await user.selectOptions(select, 'folder-1');
      
      expect(select).toHaveValue('folder-1');
    });
  });

  describe('Files with Lineage', () => {
    it('does not show folder selection for files with lineage', () => {
      render(
        <PublishToProjectDialog
          {...defaultProps}
          notebookFile={mockNotebookFileWithLineage}
          originFileInfo={mockOriginFileInfo}
        />
      );
      
      expect(screen.queryByLabelText(/destination folder/i)).not.toBeInTheDocument();
    });

    it('shows lineage information for files with lineage', () => {
      render(
        <PublishToProjectDialog
          {...defaultProps}
          notebookFile={mockNotebookFileWithLineage}
          originFileInfo={mockOriginFileInfo}
        />
      );
      
      expect(screen.getByText(/creating new version/i)).toBeInTheDocument();
      expect(screen.getByText(/Documents\/test\.txt/)).toBeInTheDocument();
    });

    it('shows information about publishing to original location', () => {
      render(
        <PublishToProjectDialog
          {...defaultProps}
          notebookFile={mockNotebookFileWithLineage}
          originFileInfo={mockOriginFileInfo}
        />
      );
      
      expect(screen.getByText(/published as a new version to its original location/i)).toBeInTheDocument();
    });
  });

  describe('Dialog Actions', () => {
    it('calls onClose when cancel button is clicked', async () => {
      const user = userEvent.setup();
      render(<PublishToProjectDialog {...defaultProps} />);
      
      const cancelButton = screen.getByRole('button', { name: /cancel/i });
      await user.click(cancelButton);
      
      expect(defaultProps.onClose).toHaveBeenCalledTimes(1);
    });

    it('calls onClose when X button is clicked', async () => {
      const user = userEvent.setup();
      render(<PublishToProjectDialog {...defaultProps} />);
      
      const closeButton = screen.getByRole('button', { name: '' }); // X button has no text
      await user.click(closeButton);
      
      expect(defaultProps.onClose).toHaveBeenCalledTimes(1);
    });

    it('calls onPublish with correct data when publish button is clicked (no folder selected)', async () => {
      const user = userEvent.setup();
      render(<PublishToProjectDialog {...defaultProps} />);
      
      const publishButton = screen.getByRole('button', { name: /publish/i });
      await user.click(publishButton);
      
      expect(defaultProps.onPublish).toHaveBeenCalledWith({
        notebookFileId: 'file-1',
        destinationFolderId: undefined
      });
    });

    it('calls onPublish with selected folder when publish button is clicked', async () => {
      const user = userEvent.setup();
      render(<PublishToProjectDialog {...defaultProps} />);
      
      // Select a folder
      const select = screen.getByLabelText(/destination folder/i);
      await user.selectOptions(select, 'folder-1');
      
      // Click publish
      const publishButton = screen.getByRole('button', { name: /publish/i });
      await user.click(publishButton);
      
      expect(defaultProps.onPublish).toHaveBeenCalledWith({
        notebookFileId: 'file-1',
        destinationFolderId: 'folder-1'
      });
    });

    it('calls onPublish without destinationFolderId for files with lineage', async () => {
      const user = userEvent.setup();
      render(
        <PublishToProjectDialog
          {...defaultProps}
          notebookFile={mockNotebookFileWithLineage}
          originFileInfo={mockOriginFileInfo}
        />
      );
      
      const publishButton = screen.getByRole('button', { name: /publish/i });
      await user.click(publishButton);
      
      expect(defaultProps.onPublish).toHaveBeenCalledWith({
        notebookFileId: 'file-1',
        destinationFolderId: undefined
      });
    });
  });

  describe('Loading States', () => {
    it('shows publishing state when publish is in progress', async () => {
      const user = userEvent.setup();
      let resolvePublish: () => void;
      const publishPromise = new Promise<void>((resolve) => {
        resolvePublish = resolve;
      });
      
      const mockOnPublish = vi.fn().mockReturnValue(publishPromise);
      render(<PublishToProjectDialog {...defaultProps} onPublish={mockOnPublish} />);
      
      const publishButton = screen.getByRole('button', { name: /publish/i });
      await user.click(publishButton);
      
      expect(screen.getByText(/publishing\.\.\./i)).toBeInTheDocument();
      expect(publishButton).toBeDisabled();
      
      // Resolve the promise to avoid test hanging
      resolvePublish!();
      await publishPromise;
    });

    it('disables cancel button while publishing', async () => {
      const user = userEvent.setup();
      let resolvePublish: () => void;
      const publishPromise = new Promise<void>((resolve) => {
        resolvePublish = resolve;
      });
      
      const mockOnPublish = vi.fn().mockReturnValue(publishPromise);
      render(<PublishToProjectDialog {...defaultProps} onPublish={mockOnPublish} />);
      
      const publishButton = screen.getByRole('button', { name: /publish/i });
      await user.click(publishButton);
      
      const cancelButton = screen.getByRole('button', { name: /cancel/i });
      expect(cancelButton).toBeDisabled();
      
      // Resolve the promise to avoid test hanging
      resolvePublish!();
      await publishPromise;
    });
  });

  describe('Error Handling', () => {
    it('handles publish error gracefully', async () => {
      const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const user = userEvent.setup();
      const mockOnPublish = vi.fn().mockRejectedValue(new Error('Publish failed'));
      
      render(<PublishToProjectDialog {...defaultProps} onPublish={mockOnPublish} />);
      
      const publishButton = screen.getByRole('button', { name: /publish/i });
      await user.click(publishButton);
      
      await waitFor(() => {
        expect(consoleSpy).toHaveBeenCalledWith('Failed to publish file', expect.any(Error));
      });
      
      // Dialog should remain open after error
      expect(screen.getByText('Publish to Project')).toBeInTheDocument();
      
      consoleSpy.mockRestore();
    });

    it('handles null notebookFile gracefully', () => {
      render(<PublishToProjectDialog {...defaultProps} notebookFile={null} />);
      expect(screen.getByText('Publish to Project')).toBeInTheDocument();
    });
  });

  describe('Edge Cases', () => {
    it('handles empty project folders list', () => {
      render(<PublishToProjectDialog {...defaultProps} projectFolders={[]} />);
      expect(screen.getByRole('option', { name: 'Test Project' })).toBeInTheDocument();
    });

    it('handles undefined project title', () => {
      render(<PublishToProjectDialog {...defaultProps} projectTitle="" />);
      expect(screen.getByRole('option', { name: 'Project Root' })).toBeInTheDocument();
    });

    it('handles special characters in file names', () => {
      const specialFile: NotebookFileDto = {
        ...mockNotebookFile,
        fileName: 'file with spaces & symbols!.txt'
      };
      
      render(<PublishToProjectDialog {...defaultProps} notebookFile={specialFile} />);
      expect(screen.getByText('file with spaces & symbols!.txt')).toBeInTheDocument();
    });
  });
}); 