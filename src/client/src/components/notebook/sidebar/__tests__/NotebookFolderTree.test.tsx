import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import { NotebookFolderTree } from '../NotebookFolderTree';
import { NotebookFolderTreeDto, NotebookFileDto, NotebookSidebarSelectedItem } from '../../../../types/notebook';
import '@testing-library/jest-dom';

// JSDOM doesn't implement DataTransfer, so we need to mock it.
class DataTransferMock {
  private data: Record<string, string> = {};
  setData(format: string, data: string) {
    this.data[format] = data;
  }
  getData(format: string) {
    return this.data[format];
  }
}
vi.stubGlobal('DataTransfer', DataTransferMock);

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

    it('shows create subfolder and upload buttons on hover', () => {
      render(<NotebookFolderTree {...defaultProps} />);
      const folderElement = screen.getByText('Documents');
      fireEvent.mouseEnter(folderElement);
      
      const folderContainer = folderElement.closest('.folder-tree-item');
      expect(within(folderContainer as HTMLElement).getByTitle('Create subfolder')).toBeInTheDocument();
      expect(within(folderContainer as HTMLElement).getByTitle('Upload to this folder')).toBeInTheDocument();
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

    it('calls onItemSelect when a folder is clicked', async () => {
      const user = userEvent.setup();
      render(<NotebookFolderTree {...defaultProps} />);
      const folder = screen.getByText('Documents');
      await user.click(folder);
      expect(defaultProps.onItemSelect).toHaveBeenCalledWith('notebookFiles', 'Documents');
    });

    it('creates a new folder via context menu', async () => {
        const user = userEvent.setup();
        vi.mocked(window.prompt).mockReturnValue('New Subfolder');
        const { baseElement } = render(<NotebookFolderTree {...defaultProps} />);
        
        const folder = screen.getByText('Documents');
        fireEvent.contextMenu(folder);
      
        const createFolderButton = await findByTextIn(baseElement, 'Create Subfolder');
        await user.click(createFolderButton);
      
        expect(await screen.findByPlaceholderText('Folder name...')).toBeInTheDocument();
    });

    it('creates subfolder via create button', async () => {
      const user = userEvent.setup();
      render(<NotebookFolderTree {...defaultProps} />);
      
      const folder = screen.getByText('Documents');
      fireEvent.mouseEnter(folder);
      
      const folderContainer = folder.closest('.folder-tree-item');
      const createButton = within(folderContainer as HTMLElement).getByTitle('Create subfolder');
      await user.click(createButton);
      
      expect(screen.getByPlaceholderText('Folder name...')).toBeInTheDocument();
    });

    it('creates subfolder when name is entered', async () => {
      const user = userEvent.setup();
      render(<NotebookFolderTree {...defaultProps} />);
      
      const folder = screen.getByText('Documents');
      fireEvent.mouseEnter(folder);
      
      const folderContainer = folder.closest('.folder-tree-item');
      const createButton = within(folderContainer as HTMLElement).getByTitle('Create subfolder');
      await user.click(createButton);
      
      const input = screen.getByPlaceholderText('Folder name...');
      await user.type(input, 'New Folder');
      fireEvent.blur(input);
      
      expect(defaultProps.onCreateFolder).toHaveBeenCalledWith('Documents', { name: 'New Folder' });
    });

    it('creates subfolder when Enter key is pressed', async () => {
      const user = userEvent.setup();
      render(<NotebookFolderTree {...defaultProps} />);
      
      const folder = screen.getByText('Documents');
      fireEvent.mouseEnter(folder);
      
      const folderContainer = folder.closest('.folder-tree-item');
      const createButton = within(folderContainer as HTMLElement).getByTitle('Create subfolder');
      await user.click(createButton);
      
      const input = screen.getByPlaceholderText('Folder name...');
      await user.type(input, 'New Folder');
      fireEvent.keyDown(input, { key: 'Enter' });
      
      expect(defaultProps.onCreateFolder).toHaveBeenCalledWith('Documents', { name: 'New Folder' });
    });

    it('cancels subfolder creation when Escape key is pressed', async () => {
      const user = userEvent.setup();
      render(<NotebookFolderTree {...defaultProps} />);
      
      const folder = screen.getByText('Documents');
      fireEvent.mouseEnter(folder);
      
      const folderContainer = folder.closest('.folder-tree-item');
      const createButton = within(folderContainer as HTMLElement).getByTitle('Create subfolder');
      await user.click(createButton);
      
      const input = screen.getByPlaceholderText('Folder name...');
      await user.type(input, 'New Folder');
      fireEvent.keyDown(input, { key: 'Escape' });
      
      expect(screen.queryByPlaceholderText('Folder name...')).not.toBeInTheDocument();
      expect(defaultProps.onCreateFolder).not.toHaveBeenCalled();
    });

    it('deletes a folder via context menu', async () => {
      const user = userEvent.setup();
      const { baseElement } = render(<NotebookFolderTree {...defaultProps} />);
      
      const folder = screen.getByText('Images');
      fireEvent.contextMenu(folder);

      const deleteButton = await findByTextIn(baseElement, 'Delete');
      await user.click(deleteButton);

      // Confirm dialog interaction
      const confirmButton = await screen.findByTestId('confirm');
      await user.click(confirmButton);

      expect(defaultProps.onDeleteFolder).toHaveBeenCalledWith('Images');
    });

    it('deletes a file via context menu', async () => {
      const user = userEvent.setup();
      const { baseElement } = render(<NotebookFolderTree {...defaultProps} />);

      const file = screen.getByText('test.txt');
      fireEvent.contextMenu(file);

      const deleteButton = await findByTextIn(baseElement, 'Delete');
      await user.click(deleteButton);

      const confirmButton = await screen.findByTestId('confirm');
      await user.click(confirmButton);

      expect(defaultProps.onDeleteFile).toHaveBeenCalledWith('file-1');
    });

    it('uploads files via upload button', async () => {
      const user = userEvent.setup();
      render(<NotebookFolderTree {...defaultProps} />);
      
      const folder = screen.getByText('Documents');
      fireEvent.mouseEnter(folder);
      
      const folderContainer = folder.closest('.folder-tree-item');
      const uploadButton = within(folderContainer as HTMLElement).getByTitle('Upload to this folder');
      await user.click(uploadButton);
      
      expect(defaultProps.onUploadToFolder).toHaveBeenCalledWith('Documents');
    });

    it('uploads files via context menu', async () => {
      const user = userEvent.setup();
      const { baseElement } = render(<NotebookFolderTree {...defaultProps} />);
      
      const folder = screen.getByText('Documents');
      fireEvent.contextMenu(folder);

      const uploadButton = await findByTextIn(baseElement, 'Upload Files');
      await user.click(uploadButton);

      expect(defaultProps.onUploadToFolder).toHaveBeenCalledWith('Documents');
    });
  });

  describe('Context Menu', () => {
    it('shows context menu on folder right-click', async () => {
      const { baseElement } = render(<NotebookFolderTree {...defaultProps} />);
      const folder = screen.getByText('Documents');
      fireEvent.contextMenu(folder);
      
              expect(await findByTextIn(baseElement, 'Create Subfolder')).toBeInTheDocument();
      expect(await findByTextIn(baseElement, 'Upload Files')).toBeInTheDocument();
    });

    it('shows context menu on file right-click', async () => {
      const { baseElement } = render(<NotebookFolderTree {...defaultProps} />);
      const file = screen.getByText('test.txt');
      fireEvent.contextMenu(file);
      
      expect(await findByTextIn(baseElement, 'Delete')).toBeInTheDocument();
              expect(await findByTextIn(baseElement, 'Publish to Project')).toBeInTheDocument();
    });
  });

  describe('Selection State', () => {
//    it('highlights selected file with specific class', () => {
//      const selectedItem: NotebookSidebarSelectedItem = { type: 'notebookFiles', id: 'file-1' };
//      render(<NotebookFolderTree {...defaultProps} selectedItem={selectedItem} />);
//      
//      const fileDiv = screen.getByText('test.txt').closest('.group');
//      expect(fileDiv).toHaveClass('bg-blue-100');
//    });

    it('highlights selected folder with specific class', () => {
      const selectedItem: NotebookSidebarSelectedItem = { type: 'notebookFiles', id: 'folder-1' };
      render(<NotebookFolderTree {...defaultProps} selectedItem={selectedItem} />);
      
      const folderDiv = screen.getByText('Documents').closest('.group');
      expect(folderDiv).toHaveClass('bg-blue-100');
    });
  });

  describe('Drag and Drop', () => {
    it('calls onMoveFile when a file is dropped on a folder', async () => {
      render(<NotebookFolderTree {...defaultProps} />);
      
      const file = screen.getByText('test.txt');
      const targetFolder = screen.getByText('Images');
      const dropTarget = targetFolder.closest('.group');
      if (!dropTarget) throw new Error('Could not find drop target');

      const dataTransfer = new DataTransfer();
      fireEvent.dragStart(file, { dataTransfer });
      fireEvent.drop(dropTarget, { dataTransfer });

      await waitFor(() => {
        expect(defaultProps.onMoveFile).toHaveBeenCalledWith('file-1', 'Images');
      });
    });
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


    it('publishes file to project', async () => {
      const user = userEvent.setup();
      const { baseElement } = render(<NotebookFolderTree {...defaultProps} />);
      
      const file = screen.getByText('test.txt');
      fireEvent.contextMenu(file);

      const publishButton = await findByTextIn(baseElement, 'Publish to Project');
      await user.click(publishButton);

      expect(defaultProps.onPublishToProject).toHaveBeenCalledWith([expect.objectContaining({
        id: 'file-1',
        fileName: 'test.txt'
      })]);
    });

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

      // The folder node should render but no placeholder '(empty)' text should appear
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

  describe('Error Handling', () => {
    it('handles cancelled prompt for folder rename', async () => {
      const user = userEvent.setup();
      vi.mocked(window.prompt).mockReturnValue(null); // User cancels
      const { baseElement } = render(<NotebookFolderTree {...defaultProps} />);
      
      const folder = screen.getByText('Documents');
      fireEvent.contextMenu(folder);

              const createFolderButton = await findByTextIn(baseElement, 'Create Subfolder');
      await user.click(createFolderButton);

      expect(defaultProps.onCreateFolder).not.toHaveBeenCalled();
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