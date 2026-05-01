import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import { NotebookFolderTree } from '../NotebookFolderTree';
import { NotebookFolderTreeDto, NotebookFileDto, NotebookSidebarSelectedItem } from '../../../../types/notebook';
import '@testing-library/jest-dom';

const mockFolderTree: NotebookFolderTreeDto = {
  id: 'root',
  name: 'Test Notebook',
  relativePath: '',
  subFolders: [
    {
      id: 'folder-1',
      name: 'Documents',
      relativePath: 'Documents',
      subFolders: [],
      files: [
        {
          id: 'file-2',
          fileName: 'document.pdf',
          relativePath: 'Documents/document.pdf',
          fileSize: 2048,
          lastModifiedUtc: '2023-01-02T00:00:00Z',
          fileHash: 'hash456',
          isIndexed: true,
          index: false,
        },
      ],
    },
    {
      id: 'folder-2',
      name: 'Images',
      relativePath: 'Images',
      subFolders: [],
      files: [],
    },
  ],
  files: [
    {
      id: 'file-1',
      fileName: 'test.txt',
      relativePath: 'test.txt',
      fileSize: 1024,
      lastModifiedUtc: '2023-01-01T00:00:00Z',
      fileHash: 'hash123',
      isIndexed: false,
      index: false,
    },
  ],
};

const defaultProps = {
  tree: mockFolderTree,
  notebookName: 'Test Notebook',
  selectedItem: null as NotebookSidebarSelectedItem | null,
  onItemSelect: vi.fn(),
  onMoveFile: vi.fn(),
  onDeleteFile: vi.fn(),
  onCreateFolder: vi.fn(),
  onRenameFolder: vi.fn(),
  onDeleteFolder: vi.fn(),
  onUploadToFolder: vi.fn(),
  onPublishToProject: vi.fn(),
  onPreviewFile: vi.fn(),
  onRenameFile: vi.fn(),
  disabled: false,
};

describe('NotebookFolderTree', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.prompt = vi.fn();
    window.confirm = vi.fn(() => true);
  });

  describe('Rendering', () => {
    it('renders the folder tree structure', () => {
      render(<NotebookFolderTree {...defaultProps} />);
      expect(screen.getByText('Documents')).toBeInTheDocument();
      expect(screen.getByText('Images')).toBeInTheDocument();
      expect(screen.getByText('test.txt')).toBeInTheDocument();
      expect(screen.getByText('document.pdf')).toBeInTheDocument();
    });

    it('shows file sizes', () => {
        render(<NotebookFolderTree {...defaultProps} />);
        expect(screen.getByText('1 KB')).toBeInTheDocument();
        expect(screen.getByText('2 KB')).toBeInTheDocument();
      });
  
          it('shows empty state message when tree is null', () => {
      render(<NotebookFolderTree {...defaultProps} tree={null} />);
      expect(screen.getByText('No files available')).toBeInTheDocument();
    });

    it('shows indexing indicator for indexed files', () => {
      render(<NotebookFolderTree {...defaultProps} />);
      const indexedFileElement = screen.getByText('document.pdf').closest('div');
      expect(indexedFileElement?.querySelector('svg')).toBeInTheDocument();
    });

    it('hides action buttons when disabled', () => {
      render(<NotebookFolderTree {...defaultProps} disabled={true} />);
      const folderElement = screen.getByText('Documents');
      fireEvent.mouseEnter(folderElement);
      
      expect(screen.queryByTitle('Create subfolder')).not.toBeInTheDocument();
      expect(screen.queryByTitle('Upload to this folder')).not.toBeInTheDocument();
    });
  });

  describe('Folder and File Interactions', () => {
    it('expands and collapses folders', async () => {
      const user = userEvent.setup();
      render(<NotebookFolderTree {...defaultProps} />);
      
      const documentsFolder = screen.getByText('Documents').closest('.group');
      const collapseButton = documentsFolder?.querySelector('button');

      expect(screen.getByText('document.pdf')).toBeInTheDocument();

      if (collapseButton) {
        await user.click(collapseButton);
      }
      
      await waitFor(() => {
        expect(screen.queryByText('document.pdf')).not.toBeInTheDocument();
      });
    });

//    it('calls onItemSelect when a file is clicked', async () => {
//      const user = userEvent.setup();
//      render(<NotebookFolderTree {...defaultProps} />);
//      const file = screen.getByText('test.txt');
//      await user.click(file);
//      expect(defaultProps.onItemSelect).toHaveBeenCalledWith('notebookFiles', 'file-1');
//    });
  });

  describe('Disabled State', () => {
    it('disables interactions when disabled prop is true', async () => {
      const user = userEvent.setup();
      const { baseElement } = render(<NotebookFolderTree {...defaultProps} disabled={true} />);
      
      const file = screen.getByText('test.txt');
      await user.click(file);
      expect(defaultProps.onItemSelect).not.toHaveBeenCalled();

      fireEvent.contextMenu(file);
      await waitFor(() => {
        expect(queryByTextIn(baseElement, 'Delete')).not.toBeInTheDocument();
      });
    });
  });

  describe('File Operations', () => {
    it('previews file', async () => {
      const user = userEvent.setup();
      const { baseElement } = render(<NotebookFolderTree {...defaultProps} />);
      
      const file = screen.getByText('test.txt');
      fireEvent.contextMenu(file);

      const previewButton = await findByTextIn(baseElement, 'Preview');
      await user.click(previewButton);

      expect(defaultProps.onPreviewFile).toHaveBeenCalledWith(expect.objectContaining({
        id: 'file-1',
        fileName: 'test.txt'
      }));
    });
  });

  describe('Empty States', () => {
    it('does not show "No files available" when root folder exists but has no children', () => {
      const emptyTree: NotebookFolderTreeDto = { ...mockFolderTree, files: [], subFolders: [] };
      render(<NotebookFolderTree {...defaultProps} tree={emptyTree} />);
      expect(screen.getByText('Test Notebook')).toBeInTheDocument();
      expect(screen.queryByText('No files available')).not.toBeInTheDocument();
    });

    it('renders empty subfolder without placeholder text', async () => {
      const folderWithEmptySubfolder: NotebookFolderTreeDto = {
        ...mockFolderTree,
        subFolders: [
          {
            id: 'folder-3',
            name: 'Empty Folder',
            relativePath: 'Empty Folder',
            subFolders: [],
            files: []
          }
        ],
        files: []
      };
      render(<NotebookFolderTree {...defaultProps} tree={folderWithEmptySubfolder} />);

      expect(screen.getByText('Empty Folder')).toBeInTheDocument();
      expect(screen.queryByText('(empty)')).not.toBeInTheDocument();
    });

    it('shows correct empty state message when notebookName is provided', () => {
      render(<NotebookFolderTree {...defaultProps} tree={null} notebookName="My Notebook" />);
      expect(screen.getByText('No files available')).toBeInTheDocument();
    });

    it('shows default empty state message when no notebookName', () => {
      render(<NotebookFolderTree {...defaultProps} tree={null} notebookName="" />);
      expect(screen.getByText('No files available')).toBeInTheDocument();
    });
  });
});

// Helper to find elements within a specific container, useful for portals
const findByTextIn = (container: HTMLElement, text: string) => waitFor(() => getByTextIn(container, text));
const getByTextIn = (container: HTMLElement, text: string) => {
    const element = Array.from(container.querySelectorAll('*')).find(e => e.textContent === text);
    if (!element) throw new Error(`Text not found: ${text}`);
    return element as HTMLElement;
};
const queryByTextIn = (container: HTMLElement, text: string) => {
    return Array.from(container.querySelectorAll('*')).find(e => e.textContent === text) || null;
} 