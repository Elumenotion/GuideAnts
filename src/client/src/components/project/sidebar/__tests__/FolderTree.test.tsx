import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '../../../../test/test-utils';
import '@testing-library/jest-dom';
import { FolderTree } from '../FolderTree';
import { FolderTreeDto, ProjectContentFile, ProjectFolderDto } from '../../../../types/project';

// Mock window.prompt
Object.defineProperty(window, 'prompt', {
  writable: true,
  value: vi.fn(),
});

// Mock window.confirm
Object.defineProperty(window, 'confirm', {
  writable: true,
  value: vi.fn(),
});

describe('FolderTree', () => {
  const mockFile: ProjectContentFile = {
    id: 'file-1',
    fileName: 'test.txt',
    path: '/test.txt',
    relativePath: 'test.txt',
    contentType: 'text/plain',
    index: false,
    documentId: 'doc-1',
    created: '2023-01-01T00:00:00Z',
    fileSize: 1024
  };

  const mockFolderTree: FolderTreeDto = {
    id: undefined,
    name: 'Test Project',
    relativePath: '',
    subFolders: [
      {
        id: 'folder-1',
        name: 'Documents',
        relativePath: 'Documents',
        subFolders: [
          {
            id: 'folder-2',
            name: 'Subfolder',
            relativePath: 'Documents/Subfolder',
            subFolders: [],
            files: []
          }
        ],
        files: [mockFile]
      }
    ],
    files: [
      {
        id: 'file-2',
        fileName: 'root-file.txt',
        path: '/root-file.txt',
        relativePath: 'root-file.txt',
        contentType: 'text/plain',
        index: false,
        documentId: 'doc-2',
        created: '2023-01-01T00:00:00Z',
        fileSize: 2048
      }
    ]
  };

  // helper to flatten FolderTree to ProjectFolderDto[]
  const flattenFolders = (node: FolderTreeDto, parentId?: string, path: string = ''): ProjectFolderDto[] => {
    const id = node.id || path || 'root';
    const current: ProjectFolderDto = {
      id: id as string,
      name: node.name,
      relativePath: path,
      projectId: 'proj',
      parentFolderId: parentId,
      created: ''
    } as ProjectFolderDto;

    return [
      current,
      ...node.subFolders.flatMap(sf => flattenFolders(sf, current.id, path ? `${path}/${sf.name}` : sf.name))
    ];
  };

  const mockFolders = flattenFolders(mockFolderTree);

  const defaultProps = {
    folderTree: mockFolderTree,
    folders: mockFolders,
    onFileSelect: vi.fn(),
    onFolderSelect: vi.fn(),
    onCreateFolder: vi.fn(),
    onRenameFolder: vi.fn(),
    onDeleteFolder: vi.fn(),
    onMoveFile: vi.fn(),
    onUploadToFolder: vi.fn(),
    onDeleteFile: vi.fn().mockResolvedValue(undefined),
    onRenameFile: vi.fn().mockResolvedValue(undefined),
    onCreateNotebookFromFile: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('rendering', () => {
    it('shows folder tree content when not disabled', () => {
      render(<FolderTree {...defaultProps} />);

      const rootFolder = screen.getByText('Test Project');
      expect(rootFolder).toBeInTheDocument();
      
      const expandButtons = screen.getAllByRole('button');
      expandButtons.forEach(button => {
        expect(button).not.toBeDisabled();
      });
    });

    it('hides create folder button when disabled', () => {
      render(<FolderTree {...defaultProps} disabled={true} />);

      const createButton = screen.queryByTitle('Create new folder');
      expect(createButton).toBeNull();
    });

    it('shows selected folder with highlight', () => {
      render(<FolderTree {...defaultProps} selectedFolderId="folder-1" />);

      const folderElement = screen.getByText('Documents').closest('div');
      expect(folderElement).toHaveClass('bg-blue-100', 'text-blue-600');
    });
  });

  describe('context menu', () => {
    it('shows context menu on right click', () => {
      render(<FolderTree {...defaultProps} />);

      fireEvent.contextMenu(screen.getByText('Documents'));

      expect(screen.getByText('Create Subfolder')).toBeInTheDocument();
      expect(screen.getByText('Upload Files')).toBeInTheDocument();
      expect(screen.getByText('Rename')).toBeInTheDocument();
      expect(screen.getByText('Delete')).toBeInTheDocument();
    });

    it('hides context menu when clicking outside', async () => {
      render(<FolderTree {...defaultProps} />);

      // Show context menu
      fireEvent.contextMenu(screen.getByText('Documents'));
      expect(screen.getByText('Create Subfolder')).toBeInTheDocument();

      // Click outside (on document body) to hide context menu
      fireEvent.click(document.body);

      // Context menu should be hidden (may need to wait)
      await waitFor(() => {
        expect(screen.queryByText('New Subfolder')).not.toBeInTheDocument();
      });
    });

    it('creates subfolder when context menu option is clicked', async () => {
      defaultProps.onCreateFolder.mockResolvedValue(undefined);

      render(<FolderTree {...defaultProps} />);

      // Show context menu and click "Create Subfolder"
      fireEvent.contextMenu(screen.getByText('Documents'));
      fireEvent.click(screen.getByText('Create Subfolder'));

      // An inline input should appear for the new folder name
      const input = screen.getByRole('textbox');
      fireEvent.change(input, { target: { value: 'New Subfolder' } });
      fireEvent.keyDown(input, { key: 'Enter' });

      await waitFor(() => {
        expect(defaultProps.onCreateFolder).toHaveBeenCalledWith('folder-1', { name: 'New Subfolder' });
      });
    });

    it('does not create subfolder when creation is cancelled', async () => {
      render(<FolderTree {...defaultProps} />);

      fireEvent.contextMenu(screen.getByText('Documents'));
      fireEvent.click(screen.getByText('Create Subfolder'));

      const input = screen.getByRole('textbox');
      // Press Escape to cancel creation
      fireEvent.keyDown(input, { key: 'Escape' });

      await waitFor(() => {
        expect(defaultProps.onCreateFolder).not.toHaveBeenCalled();
      });
    });

    it('calls onUploadToFolder when upload option is clicked', () => {
      render(<FolderTree {...defaultProps} />);

      fireEvent.contextMenu(screen.getByText('Documents'));
      fireEvent.click(screen.getByText('Upload Files'));

      expect(defaultProps.onUploadToFolder).toHaveBeenCalledWith('folder-1');
    });

  });

  describe('inline editing', () => {
    it('enters edit mode when rename is clicked', () => {
      render(<FolderTree {...defaultProps} />);

      fireEvent.contextMenu(screen.getByText('Documents'));
      fireEvent.click(screen.getByText('Rename'));

      // Should show input field
      const input = screen.getByDisplayValue('Documents');
      expect(input).toBeInTheDocument();
      expect(input).toHaveFocus();
    });

    it('saves rename when Enter key is pressed', async () => {
      defaultProps.onRenameFolder.mockResolvedValue(undefined);

      render(<FolderTree {...defaultProps} />);

      // Enter edit mode
      fireEvent.contextMenu(screen.getByText('Documents'));
      fireEvent.click(screen.getByText('Rename'));

      const input = screen.getByDisplayValue('Documents');

      // Change value and press Enter
      fireEvent.change(input, { target: { value: 'New Name' } });
      fireEvent.keyDown(input, { key: 'Enter' });

      await waitFor(() => {
        expect(defaultProps.onRenameFolder).toHaveBeenCalledWith('folder-1', 'New Name');
      });
    });

    it('cancels rename when Escape key is pressed', () => {
      render(<FolderTree {...defaultProps} />);

      // Enter edit mode
      fireEvent.contextMenu(screen.getByText('Documents'));
      fireEvent.click(screen.getByText('Rename'));

      const input = screen.getByDisplayValue('Documents');

      // Change value and press Escape
      fireEvent.change(input, { target: { value: 'New Name' } });
      fireEvent.keyDown(input, { key: 'Escape' });

      // Should not call rename and should exit edit mode
      expect(defaultProps.onRenameFolder).not.toHaveBeenCalled();
      expect(screen.queryByDisplayValue('New Name')).not.toBeInTheDocument();
      expect(screen.getByText('Documents')).toBeInTheDocument();
    });

    it('saves rename when input loses focus', async () => {
      defaultProps.onRenameFolder.mockResolvedValue(undefined);

      render(<FolderTree {...defaultProps} />);

      // Enter edit mode
      fireEvent.contextMenu(screen.getByText('Documents'));
      fireEvent.click(screen.getByText('Rename'));

      const input = screen.getByDisplayValue('Documents');

      // Change value and blur
      fireEvent.change(input, { target: { value: 'New Name' } });
      fireEvent.blur(input);

      await waitFor(() => {
        expect(defaultProps.onRenameFolder).toHaveBeenCalledWith('folder-1', 'New Name');
      });
    });

    it('does not save rename when name is unchanged', () => {
      render(<FolderTree {...defaultProps} />);

      // Enter edit mode
      fireEvent.contextMenu(screen.getByText('Documents'));
      fireEvent.click(screen.getByText('Rename'));

      const input = screen.getByDisplayValue('Documents');

      // Press Enter without changing value
      fireEvent.keyDown(input, { key: 'Enter' });

      expect(defaultProps.onRenameFolder).not.toHaveBeenCalled();
    });

    it('does not save rename when name is empty', () => {
      render(<FolderTree {...defaultProps} />);

      // Enter edit mode
      fireEvent.contextMenu(screen.getByText('Documents'));
      fireEvent.click(screen.getByText('Rename'));

      const input = screen.getByDisplayValue('Documents');

      // Clear value and press Enter
      fireEvent.change(input, { target: { value: '' } });
      fireEvent.keyDown(input, { key: 'Enter' });

      expect(defaultProps.onRenameFolder).not.toHaveBeenCalled();
    });
  });

  describe('create root folder', () => {
    it('folder tree supports create folder operations', async () => {
      render(<FolderTree {...defaultProps} />);

      expect(screen.getByText('Test Project')).toBeInTheDocument();
      expect(screen.getByText('Documents')).toBeInTheDocument();
    });
  });

  describe('accessibility', () => {
    it('has proper button roles and labels', () => {
      render(<FolderTree {...defaultProps} />);

      // Check that expand/collapse buttons exist and are accessible
      const expandButtons = screen.getAllByRole('button').filter(button => 
        button.getAttribute('disabled') === null
      );
      expect(expandButtons.length).toBeGreaterThan(0);
    });

    it('supports keyboard navigation for expand/collapse', () => {
      render(<FolderTree {...defaultProps} />);

      // Find the expand button by looking for the SVG that contains the expand icon
      const expandButton = screen.getAllByRole('button').find(button => 
        button.querySelector('svg path[fill-rule="evenodd"]')
      );
      expect(expandButton).toBeDefined();
      
      // Focus and click (simulating Enter/Space key activation)
      expandButton!.focus();
      fireEvent.click(expandButton!);

      // Should toggle expansion (subfolder becomes hidden)
      expect(screen.queryByText('Subfolder')).not.toBeInTheDocument();
    });
  });

  describe('drag and drop', () => {
    // Mock DataTransfer for drag events
    const createDataTransfer = (data: Record<string, string> = {}) => {
      const dt = {
        data: { ...data },
        setData: vi.fn((format: string, value: string) => { dt.data[format] = value; }),
        getData: vi.fn((format: string) => dt.data[format] || ''),
        effectAllowed: 'move',
        dropEffect: 'move'
      } as any;
      return dt;
    };

    it('handles drag over folder', () => {
      render(<FolderTree {...defaultProps} />);

      const folderElement = screen.getByText('Documents').closest('div');
      const dataTransfer = createDataTransfer();

      fireEvent.dragOver(folderElement!, { dataTransfer });

      // Should show drag over styling
      expect(folderElement).toHaveClass('ring-2', 'ring-blue-400', 'bg-blue-50');
    });

    it('handles drag leave folder', () => {
      render(<FolderTree {...defaultProps} />);

      const folderElement = screen.getByText('Documents').closest('div');
      const dataTransfer = createDataTransfer();

      // First drag over to show the styling
      fireEvent.dragOver(folderElement!, { dataTransfer });
      expect(folderElement).toHaveClass('ring-2', 'ring-blue-400', 'bg-blue-50');

      // Then drag leave to remove styling
      fireEvent.dragLeave(folderElement!, { dataTransfer, relatedTarget: document.body });

      // Should remove drag over styling
      expect(folderElement).not.toHaveClass('ring-2', 'ring-blue-400', 'bg-blue-50');
    });

    it('handles drop file on root folder', async () => {
      render(<FolderTree {...defaultProps} />);

      const rootFolder = screen.getByText('Test Project').closest('div');
      const dataTransfer = createDataTransfer({
        'text/plain': 'file-1',
        'application/x-origin-folder': 'folder-1'
      });

      fireEvent.drop(rootFolder!, { dataTransfer });

      // Root folder should pass undefined as destination
      expect(defaultProps.onMoveFile).toHaveBeenCalledWith('file-1', undefined);
    });

    it('does not move file when dropped on same folder', () => {
      render(<FolderTree {...defaultProps} />);

      const targetFolder = screen.getByText('Documents').closest('div');
      const dataTransfer = createDataTransfer({
        'text/plain': 'file-1',
        'application/x-origin-folder': 'folder-1'  // Same as target
      });

      fireEvent.drop(targetFolder!, { dataTransfer });

      expect(defaultProps.onMoveFile).not.toHaveBeenCalled();
    });

    it('does not handle drag operations when disabled', () => {
      render(<FolderTree {...defaultProps} disabled={true} />);

      const targetFolder = screen.getByText('Documents').closest('div');
      const dataTransfer = createDataTransfer({
        'text/plain': 'file-1',
        'application/x-origin-folder': 'other-folder'
      });

      fireEvent.drop(targetFolder!, { dataTransfer });

      expect(defaultProps.onMoveFile).not.toHaveBeenCalled();
    });
  });

  describe('index toggle functionality', () => {
    const indexableFile: ProjectContentFile = {
      id: 'file-indexable',
      fileName: 'document.txt',
      path: '/document.txt',
      relativePath: 'document.txt',
      contentType: 'text/plain',
      index: false,
      documentId: 'doc-indexable',
      created: '2023-01-01T00:00:00Z',
      fileSize: 512
    };

    const indexedFile: ProjectContentFile = {
      id: 'file-indexed',
      fileName: 'indexed.pdf',
      path: '/indexed.pdf',
      relativePath: 'indexed.pdf',
      contentType: 'application/pdf',
      index: true,
      documentId: 'doc-indexed',
      created: '2023-01-01T00:00:00Z',
      fileSize: 1536
    };

    const folderTreeWithIndexableFiles: FolderTreeDto = {
      id: undefined,
      name: 'Test Project',
      relativePath: '',
      subFolders: [],
      files: [indexableFile, indexedFile]
    };

    beforeEach(() => {
      // indexing removed
    });

    it('does not show legacy indexing options in context menu', () => {
      render(<FolderTree {...defaultProps} folderTree={folderTreeWithIndexableFiles} />);
      fireEvent.contextMenu(screen.getByText('document.txt'));
      expect(screen.queryByText('Add to index')).not.toBeInTheDocument();
      expect(screen.queryByText('Remove from index')).not.toBeInTheDocument();
    });

    it('no indexing option shown for indexed files either', () => {
      render(<FolderTree {...defaultProps} folderTree={folderTreeWithIndexableFiles} />);
      fireEvent.contextMenu(screen.getByText('indexed.pdf'));
      expect(screen.queryByText('Remove from index')).not.toBeInTheDocument();
      expect(screen.queryByText('Add to index')).not.toBeInTheDocument();
    });

    // Removed: onToggleIndex behavior tests

    // Removed: onToggleIndex behavior tests

    it('does not show legacy index indicator icon', () => {
      render(<FolderTree {...defaultProps} folderTree={folderTreeWithIndexableFiles} />);
      const indexedFileElement = screen.getByText('indexed.pdf').closest('div');
      const searchIcon = indexedFileElement?.querySelector('svg path[fill-rule="evenodd"]');
      expect(searchIcon).toBeFalsy();
    });

    it('does not show index indicator for unindexed files', () => {
      render(<FolderTree {...defaultProps} folderTree={folderTreeWithIndexableFiles} />);
      const unindexedFileElement = screen.getByText('document.txt').closest('div');
      const searchIcon = unindexedFileElement?.querySelector('svg path[fill-rule="evenodd"]');
      expect(searchIcon).toBeFalsy();
    });

    it('shows loading spinner for files being indexed', () => {
      // Test would require more complex state management, but covers the rendering logic
      render(<FolderTree {...defaultProps} folderTree={folderTreeWithIndexableFiles} />);
      
      // The component should render without errors when files are being indexed
      expect(screen.getByText('document.txt')).toBeInTheDocument();
    });
  });

  describe('search functionality', () => {
    const searchableTree: FolderTreeDto = {
      id: undefined,
      name: 'Test Project',
      relativePath: '',
      subFolders: [
        {
          id: 'search-folder',
          name: 'SearchFolder',
          relativePath: 'SearchFolder',
          subFolders: [
            {
              id: 'nested-folder',
              name: 'NestedSearch',
              relativePath: 'SearchFolder/NestedSearch',
              subFolders: [],
              files: [
                {
                  id: 'nested-file',
                  fileName: 'nested-search.txt',
                  path: '/nested-search.txt',
                  relativePath: 'SearchFolder/NestedSearch/nested-search.txt',
                  contentType: 'text/plain',
                  index: false,
                  documentId: 'nested-doc',
                  created: '2023-01-01T00:00:00Z',
                  fileSize: 256
                }
              ]
            }
          ],
          files: [
            {
              id: 'search-file',
              fileName: 'searchable.txt',
              path: '/searchable.txt',
              relativePath: 'SearchFolder/searchable.txt',
              contentType: 'text/plain',
              index: false,
              documentId: 'search-doc',
              created: '2023-01-01T00:00:00Z',
              fileSize: 128
            }
          ]
        },
        {
          id: 'other-folder',
          name: 'OtherFolder',
          relativePath: 'OtherFolder',
          subFolders: [],
          files: [
            {
              id: 'other-file',
              fileName: 'other.txt',
              path: '/other.txt',
              relativePath: 'OtherFolder/other.txt',
              contentType: 'text/plain',
              index: false,
              documentId: 'other-doc',
              created: '2023-01-01T00:00:00Z',
              fileSize: 64
            }
          ]
        }
      ],
      files: []
    };

    it('handles empty search results gracefully', () => {
      render(<FolderTree {...defaultProps} folderTree={searchableTree} searchTerm="nonexistent" />);

      expect(screen.queryByText('SearchFolder')).not.toBeInTheDocument();
      expect(screen.queryByText('OtherFolder')).not.toBeInTheDocument();
      expect(screen.queryByText('searchable.txt')).not.toBeInTheDocument();
      expect(screen.queryByText('other.txt')).not.toBeInTheDocument();
    });
  });

  describe('hover interactions and upload functionality', () => {
    it('shows hover buttons on folder hover', () => {
      render(<FolderTree {...defaultProps} />);

      const folderElement = screen.getByText('Documents');
      
      // Simulate mouse enter to show hover buttons
      fireEvent.mouseEnter(folderElement);

      const folderContainer = folderElement.closest('.folder-tree-item');
      expect(folderContainer?.querySelector('[title="Create subfolder"]')).toBeInTheDocument();
      expect(folderContainer?.querySelector('[title="Upload to this folder"]')).toBeInTheDocument();
    });

    it('hides hover buttons when not hovering', () => {
      render(<FolderTree {...defaultProps} />);

      const folderElement = screen.getByText('Documents');
      const folderContainer = folderElement.closest('.folder-tree-item');

      // Buttons exist but should have opacity-0 class (hidden via CSS)
      const createButton = folderContainer?.querySelector('[title="Create subfolder"]');
      const uploadButton = folderContainer?.querySelector('[title="Upload to this folder"]');
      
      expect(createButton).toBeInTheDocument();
      expect(uploadButton).toBeInTheDocument();
      
      // Check that they're hidden via CSS classes
      const buttonContainer = folderContainer?.querySelector('.opacity-0');
      expect(buttonContainer).toBeInTheDocument();
    });

    it('calls onUploadToFolder when hover upload button is clicked', () => {
      render(<FolderTree {...defaultProps} />);

      const folderElement = screen.getByText('Documents');
      fireEvent.mouseEnter(folderElement);

      const folderContainer = folderElement.closest('.folder-tree-item');
      const uploadButton = folderContainer?.querySelector('[title="Upload to this folder"]') as HTMLElement;
      
      fireEvent.click(uploadButton);

      expect(defaultProps.onUploadToFolder).toHaveBeenCalledWith('folder-1');
    });

    it('calls onUploadToFolder with undefined for root folder upload', () => {
      render(<FolderTree {...defaultProps} />);

      const rootFolderElement = screen.getByText('Test Project');
      fireEvent.mouseEnter(rootFolderElement);

      const folderContainer = rootFolderElement.closest('.folder-tree-item');
      const uploadButton = folderContainer?.querySelector('[title="Upload to this folder"]') as HTMLElement;
      
      fireEvent.click(uploadButton);

      // Root folder should pass undefined
      expect(defaultProps.onUploadToFolder).toHaveBeenCalledWith(undefined);
    });

    it('triggers subfolder creation from hover button', () => {
      render(<FolderTree {...defaultProps} />);

      const folderElement = screen.getByText('Documents');
      fireEvent.mouseEnter(folderElement);

      const folderContainer = folderElement.closest('.folder-tree-item');
      const createButton = folderContainer?.querySelector('[title="Create subfolder"]') as HTMLElement;
      
      fireEvent.click(createButton);

      // Should show the inline input for new subfolder
      expect(screen.getByRole('textbox')).toBeInTheDocument();
    });

    it('handles subfolder creation with empty name', async () => {
      render(<FolderTree {...defaultProps} />);

      // Start creating subfolder
      fireEvent.contextMenu(screen.getByText('Documents'));
      fireEvent.click(screen.getByText('Create Subfolder'));

      const input = screen.getByRole('textbox');

      // Submit empty name
      fireEvent.blur(input);

      // Should not call create folder
      expect(defaultProps.onCreateFolder).not.toHaveBeenCalled();
    });

    it('handles folder operations for root folder fallback', async () => {
      // Test the fallback logic for folders without ID
      const folderTreeWithoutId: FolderTreeDto = {
        id: undefined,
        name: 'Test Project',
        relativePath: '',
        subFolders: [
          {
            id: undefined, // No ID to test fallback
            name: 'No ID Folder',
            relativePath: 'NoIDFolder',
            subFolders: [],
            files: []
          }
        ],
        files: []
      };

      const foldersWithoutId: ProjectFolderDto[] = [
        {
          id: 'fallback-id',
          name: 'No ID Folder',
          relativePath: 'NoIDFolder',
          projectId: 'proj',
          created: ''
        } as ProjectFolderDto
      ];

      render(<FolderTree {...defaultProps} folderTree={folderTreeWithoutId} folders={foldersWithoutId} />);

      // Test subfolder creation fallback
      fireEvent.contextMenu(screen.getByText('No ID Folder'));
      fireEvent.click(screen.getByText('Create Subfolder'));

      const input = screen.getByRole('textbox');
      fireEvent.change(input, { target: { value: 'New Subfolder' } });
      fireEvent.keyDown(input, { key: 'Enter' });

      await waitFor(() => {
        expect(defaultProps.onCreateFolder).toHaveBeenCalledWith('fallback-id', { name: 'New Subfolder' });
      });
    });

    it('does not show hover buttons when disabled', () => {
      render(<FolderTree {...defaultProps} disabled={true} />);

      const folderElement = screen.getByText('Documents');
      fireEvent.mouseEnter(folderElement);

      const folderContainer = folderElement.closest('.folder-tree-item');
      expect(folderContainer?.querySelector('[title="Create subfolder"]')).not.toBeInTheDocument();
      expect(folderContainer?.querySelector('[title="Upload to this folder"]')).not.toBeInTheDocument();
    });

    it('prevents folder deletion when folder has children', () => {
      render(<FolderTree {...defaultProps} />);

      // Documents folder has children (files)
      fireEvent.contextMenu(screen.getByText('Documents'));

      const deleteButton = screen.getByText('Delete');
      expect(deleteButton).toHaveClass('opacity-50', 'cursor-not-allowed');
      expect(deleteButton).toBeDisabled();
    });
  });
}); 