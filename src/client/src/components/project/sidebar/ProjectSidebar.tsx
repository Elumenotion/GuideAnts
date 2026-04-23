import { ProjectDetailsDto, SelectedItem, SectionType, FolderTreeDto, CreateFolderDto, ProjectNotebook, NotebookTemplateSummaryDto } from '../../../types/project';
import { useState, useEffect, useCallback, useMemo, useRef } from 'react';
import { useNavigate, useLocation } from 'react-router-dom';
import { FolderTree } from './FolderTree';
import { UploadFilesDialog } from '../dialogs/UploadFilesDialog';
import { createPortal } from 'react-dom';
import { CreateNotebookDialog } from '../dialogs/CreateNotebookDialog';
import { SidebarSection } from '../../common/SidebarSection';
import { ConfirmationDialog } from '../../common/ConfirmationDialog';
import { useToast } from '../../common/Toast';
import { api } from '../../../services/api';
import { useMultiSelect } from '../../../hooks/useMultiSelect';
import { useListKeyboardNavigation } from '../../../hooks/useListKeyboardNavigation';
import { useSidebarKeyboardShortcuts } from '../../../hooks/useSidebarKeyboardShortcuts';
import { useSidebarSelectionCoordinator } from '../../../hooks/useSidebarSelectionCoordinator';
import { useLongPress } from '../../../hooks/useLongPress';

// Props for the extracted NotebookListItem component
interface NotebookListItemProps {
  notebook: ProjectNotebook;
  isEditing: boolean;
  isSelected: boolean;
  isFocused: boolean;
  isOpen: boolean;
  isMobile: boolean;
  disabled: boolean;
  editNotebookTitle: string;
  notebookFocusIntentRef: React.MutableRefObject<boolean>;
  isSectionActive: boolean;
  onTitleChange: (value: string) => void;
  onSaveRename: () => void;
  onCancelRename: () => void;
  onItemSelect: (type: 'notebooks', id: string) => void;
  onClickSelect: (id: string, e: React.MouseEvent) => void;
  onSetFocusedId: (id: string) => void;
  onKeyDown: (e: React.KeyboardEvent) => void;
  onContextMenu: (e: React.MouseEvent, id: string) => void;
  onSetSelection: (ids: string[]) => void;
}

/**
 * Individual notebook list item component.
 * Extracted to properly use the useLongPress hook (hooks can't be called in loops).
 */
function NotebookListItem({
  notebook: n,
  isEditing,
  isSelected,
  isFocused,
  isOpen,
  isMobile,
  disabled,
  editNotebookTitle,
  notebookFocusIntentRef,
  isSectionActive,
  onTitleChange,
  onSaveRename,
  onCancelRename,
  onItemSelect,
  onClickSelect,
  onSetFocusedId,
  onKeyDown,
  onContextMenu,
  onSetSelection,
}: NotebookListItemProps) {
  // Long press handler for mobile context menu
  const longPressHandlers = useLongPress({
    onLongPress: (e) => {
      if (!disabled && isMobile) {
        // Select the item if not already selected
        if (!isSelected) {
          onSetSelection([n.id]);
        }
        // Create a synthetic mouse event for the context menu handler
        onContextMenu({ 
          clientX: e.clientX, 
          clientY: e.clientY,
          preventDefault: () => {},
          stopPropagation: () => {},
        } as React.MouseEvent, n.id);
      }
    },
    disabled: disabled || !isMobile,
    threshold: 500,
  });

  const rowClasses = `w-full text-left py-1 px-2 text-sm ${
    isSelected ? 'bg-blue-100' : 'hover:bg-gray-100'
  } ${isFocused ? 'ring-2 ring-blue-500 ring-inset' : ''} ${
    isOpen ? 'text-blue-600' : ''
  } ${disabled ? 'opacity-50 cursor-not-allowed' : ''}`;

  if (isEditing) {
    return (
      <div className={rowClasses} onContextMenu={(e) => e.stopPropagation()}>
        <input
          className="w-full border rounded px-1 py-0.5 text-sm"
          value={editNotebookTitle}
          onChange={(e) => onTitleChange(e.target.value)}
          autoFocus
          onBlur={onSaveRename}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              onSaveRename();
            } else if (e.key === 'Escape') {
              e.preventDefault();
              onCancelRename();
            }
          }}
        />
      </div>
    );
  }

  return (
    <div
      role="button"
      tabIndex={disabled ? -1 : (isFocused ? 0 : -1)}
      ref={el => {
        // Only apply focus if explicitly requested via keyboard navigation
        // This prevents focus-stealing when polling refreshes the list
        if (el && isFocused && notebookFocusIntentRef.current && isSectionActive) {
          el.focus();
          notebookFocusIntentRef.current = false;
        }
      }}
      onClick={(e) => {
        if (disabled) return;
        // On mobile: single tap opens immediately
        // On desktop: single click selects, double-click opens
        if (isMobile) {
          onItemSelect('notebooks', n.id);
        } else {
          onClickSelect(n.id, e);
          onSetFocusedId(n.id);
        }
      }}
      onDoubleClick={() => {
        if (disabled || isMobile) return;
        onItemSelect('notebooks', n.id);
      }}
      onKeyDown={(e) => {
        if (disabled) return;
        onKeyDown(e);
      }}
      onContextMenu={(e) => {
        if (!isSelected) {
          onSetSelection([n.id]);
        }
        onContextMenu(e, n.id);
      }}
      {...longPressHandlers}
      className={rowClasses}
      aria-disabled={disabled}
      aria-selected={isSelected}
      data-tour-id="sidebar.notebook.item"
      title={isMobile ? "Tap to open, hold for options" : "Double-click or ENTER to open"}
    >
      <div>
        <div>{n.title}</div>
        {n.description && (
          <div className="text-xs text-gray-500 mt-0.5 truncate" title={n.description}>
            {n.description}
          </div>
        )}
      </div>
    </div>
  );
}

interface ProjectSidebarProps {
    project: ProjectDetailsDto;
    expandedSections: Set<SectionType>;
    selectedItem: SelectedItem | null;
    onSectionToggle: (section: SectionType) => void;
    onItemSelect: (type: SectionType, id: string) => void;
    onAddLink?: () => void;
    onUploadFiles?: (files: FileList) => void;
    disabled?: boolean;
    isCurrentUserOwner?: boolean;
    isCollapsed?: boolean;
    // Folder management props
    folderTree?: FolderTreeDto | null;
    // Sidebar polling overrides
    notebooks?: ProjectNotebook[]; // Override project.notebooks with polled data
    onCreateFolder?: (parentFolderId: string | undefined, folderData: CreateFolderDto) => Promise<void>;
    onRenameFolder?: (folderId: string, newName: string) => Promise<void>;
    onDeleteFolder?: (folderId: string) => Promise<void>;
    onMoveFile?: (fileId: string, destinationFolderId: string | undefined) => Promise<void>;
    onUploadFilesToFolder?: (files: File[], folderId?: string) => Promise<void>;
    onDeleteFile?: (fileId: string) => Promise<void>;
    onRenameFile?: (fileId: string, newName: string) => Promise<void>;
    onDeleteLink?: (linkId: string) => Promise<void>;
    onCopyNotebook?: (notebookId: string) => Promise<void>;
    onDeleteNotebook?: (notebookId: string) => Promise<void>;
    onDeleteNotebooks?: (notebookIds: string[]) => Promise<void>;
    onRenameNotebook?: (notebookId: string, newTitle: string) => Promise<void>;
    onCreateNotebook?: (title: string, guideId?: string, description?: string) => Promise<void>;
    // NEW: Handler for creating notebook from file
    onCreateNotebookFromFile?: (fileId: string, fileName: string, title: string, guideId?: string, description?: string) => Promise<void>;
    onCreateNotebookFromFiles?: (files: {id: string; fileName: string}[], title: string, guideId?: string, description?: string) => Promise<void>;
    // indexing removed
    // Home page props
    homePageContentFileId?: string;
    onSetHomePage?: (fileId: string | null) => Promise<void>;
    // File version change notification
    onFileContentChanged?: (fileId: string, newVersion: number) => void;
}

// NOTE: Historical helper removed. We now rely on the real tree everywhere.

export function ProjectSidebar({
    project,
    expandedSections,
    selectedItem,
    onSectionToggle,
    onItemSelect,
    onAddLink,
    onUploadFiles,
    disabled = false,
    isCurrentUserOwner = false,
    isCollapsed = false,
    folderTree,
    notebooks, // Override for polled notebooks
    onCreateFolder,
    onRenameFolder,
    onDeleteFolder,
    onMoveFile,
    onUploadFilesToFolder,
    onDeleteFile,
    onRenameFile,
    onDeleteLink,
    onCopyNotebook,
    onDeleteNotebook,
    onDeleteNotebooks,
    onRenameNotebook,
    onCreateNotebook,
    onCreateNotebookFromFile,
    onCreateNotebookFromFiles,
    homePageContentFileId,
    onSetHomePage,
    onFileContentChanged
}: ProjectSidebarProps) {
    const { showToast } = useToast();
    const navigate = useNavigate();
    const location = useLocation();
    const [showUploadDialog, setShowUploadDialog] = useState(false);
    const [uploadTargetFolderId, setUploadTargetFolderId] = useState<string | undefined>();
    
    // Memoized lookup function for resolving relative paths to file IDs
    const resolveProjectFilePath = useCallback((relativePath: string): string | undefined => {
        // Try to find file by filename or relativePath field
        const file = project.contentFiles.find(f => 
            f.fileName === relativePath ||
            f.relativePath === relativePath ||
            f.fileName === relativePath.split('/').pop() // Try just the filename
        );
        
        return file?.id;
    }, [project.contentFiles]); // Only recreate when contentFiles reference changes

    // Context menu state
    const [showLinkContextMenu, setShowLinkContextMenu] = useState(false);
    const [showNotebookContextMenu, setShowNotebookContextMenu] = useState(false);
    const [linkContextMenuPos, setLinkContextMenuPos] = useState({ x: 0, y: 0 });
    const [notebookContextMenuPos, setNotebookContextMenuPos] = useState({ x: 0, y: 0 });
    const [selectedContextLinkId, setSelectedContextLinkId] = useState<string | null>(null);
    const [selectedContextNotebookId, setSelectedContextNotebookId] = useState<string | null>(null);

    // Confirmation dialog state
    const [showDeleteLinkConfirm, setShowDeleteLinkConfirm] = useState(false);
    const [showDeleteNotebookConfirm, setShowDeleteNotebookConfirm] = useState(false);
    const [linkToDelete, setLinkToDelete] = useState<string | null>(null);
    const [notebookToDelete, setNotebookToDelete] = useState<string | null>(null);

    // Notebook dialog state
    const [showCreateNotebookDialog, setShowCreateNotebookDialog] = useState(false);

    // NEW: Enhanced notebook creation state for creating from file
    const [createNotebookFromFiles, setCreateNotebookFromFiles] = useState<{
        id: string;
        fileName: string;
    }[] | null>(null);

    // Notebook inline rename state
    const [editingNotebookId, setEditingNotebookId] = useState<string | null>(null);
    const [editNotebookTitle, setEditNotebookTitle] = useState('');

    // Search state
    const [searchTerm, setSearchTerm] = useState('');
    const [notebookSort, setNotebookSort] = useState<'recent' | 'alpha'>('recent');
    const lastSortKeyRef = useRef<string | null>(null);
    
    // Mobile detection for touch-friendly interactions
    const [isMobile, setIsMobile] = useState(false);
    useEffect(() => {
        const checkMobile = () => setIsMobile(window.innerWidth < 768);
        checkMobile();
        window.addEventListener('resize', checkMobile);
        return () => window.removeEventListener('resize', checkMobile);
    }, []);
    
    // Guide auth: templates with providers requiring/allowing configuration
    const [authTemplates, setAuthTemplates] = useState<NotebookTemplateSummaryDto[]>([]);
    const [isLoadingAuthTemplates, setIsLoadingAuthTemplates] = useState(false);

    // Use polled notebooks if provided, otherwise fall back to project.notebooks
    const currentNotebooks = notebooks || project.notebooks;

    // Clear search when sidebar collapses
    useEffect(() => {
        if (isCollapsed) {
            setSearchTerm('');
        }
    }, [isCollapsed]);

    // Load notebook templates that have auth providers requiring/allowing configuration (owner-only concern)
    useEffect(() => {
        let cancelled = false;
        const load = async () => {
            if (!isCurrentUserOwner) { setAuthTemplates([]); return; }
            setIsLoadingAuthTemplates(true);
            try {
                const all = await api.projects.notebookTemplates.getAll(project.id);
                if (cancelled) return;
                // Filter templates that have configurable auth (optional or required user config)
                const needingAuth = (all || []).filter(t => t.hasConfigurableAuth === true);
                // Sort by template name for stable UI
                needingAuth.sort((a, b) => a.templateName.localeCompare(b.templateName));
                setAuthTemplates(needingAuth);
            } catch (e) {
                if (process.env.NODE_ENV === 'development') {
                    console.warn('Failed to load notebook templates for Guide Authorization', e);
                }
                setAuthTemplates([]);
            } finally {
                if (!cancelled) setIsLoadingAuthTemplates(false);
            }
        };
        load();
        return () => { cancelled = true; };
    }, [isCurrentUserOwner]);

    // Close link context menu when clicking elsewhere
    useEffect(() => {
        const handleClickOutside = () => {
            setShowLinkContextMenu(false);
            setShowNotebookContextMenu(false);
        };
        document.addEventListener('click', handleClickOutside);

        const globalCloseHandler = () => {
            setShowLinkContextMenu(false);
            setShowNotebookContextMenu(false);
        };
        window.addEventListener('close-context-menus', globalCloseHandler);

        return () => {
            document.removeEventListener('click', handleClickOutside);
            window.removeEventListener('close-context-menus', globalCloseHandler);
        };
    }, []);

    const handleFileSelect = (fileId: string) => {
        onItemSelect('contentFiles', fileId);
    };

    const handleFolderSelect = (folderId: string) => {
        // For now, we can just expand/focus on the folder
        // In the future, this could show folder details or allow operations
        console.log('Folder selected:', folderId);
    };

    const handleUploadFilesLegacy = (files: FileList) => {
        // Convert FileList to File[] and upload to root
        if (onUploadFilesToFolder) {
            const fileArray = Array.from(files);
            onUploadFilesToFolder(fileArray);
        } else if (onUploadFiles) {
            onUploadFiles(files);
        }
    };

    const handleUploadToFolder = (folderId?: string) => {
        setUploadTargetFolderId(folderId);
        setShowUploadDialog(true);
    };

    const handleUploadDialogSubmit = async (files: File[], folderId?: string) => {
        if (onUploadFilesToFolder) {
            await onUploadFilesToFolder(files, folderId);
        }
        setShowUploadDialog(false);
    };

    const handleCreateFolderWrapper = async (parentFolderId: string | undefined, folderData: CreateFolderDto): Promise<void> => {
        if (onCreateFolder) {
            await onCreateFolder(parentFolderId, folderData);
        } else {
            throw new Error('Create folder not implemented');
        }
    };

    // NEW: Handle creating notebook from files
    const handleCreateNotebookFromFiles = (files: { id: string; fileName: string }[]) => {
        setCreateNotebookFromFiles(files);
        setShowCreateNotebookDialog(true);
    };

    // Context menu helpers for Links
    const handleLinkContextMenu = (e: React.MouseEvent, linkId: string) => {
        if (disabled) return;
        // Close any other open context menus across the app
        window.dispatchEvent(new Event('close-context-menus'));

        e.preventDefault();
        e.stopPropagation();
        setSelectedContextLinkId(linkId);
        setLinkContextMenuPos({ x: e.clientX, y: e.clientY });
        setShowLinkContextMenu(true);
    };

    const handleDeleteLinkInternal = async () => {
        if (!selectedContextLinkId || !onDeleteLink) {
            setShowLinkContextMenu(false);
            return;
        }
        setLinkToDelete(selectedContextLinkId);
        setShowDeleteLinkConfirm(true);
        setShowLinkContextMenu(false);
    };

    const handleDeleteLinkConfirm = async () => {
        if (linkToDelete && onDeleteLink) {
            try {
                await onDeleteLink(linkToDelete);
            } catch (error) {
                console.error('Failed to delete link:', error);
                showToast({
                    type: 'error',
                    title: 'Failed to delete link',
                    message: 'Please try again.'
                });
            }
        }
        setShowDeleteLinkConfirm(false);
        setLinkToDelete(null);
    };

    const handleDeleteLinkCancel = () => {
        setShowDeleteLinkConfirm(false);
        setLinkToDelete(null);
    };

    // Notebook context handlers
    const handleNotebookContextMenu = (e: React.MouseEvent, notebookId: string) => {
        if (disabled) return;
        window.dispatchEvent(new Event('close-context-menus'));

        e.preventDefault();
        e.stopPropagation();
        setSelectedContextNotebookId(notebookId);
        setNotebookContextMenuPos({ x: e.clientX, y: e.clientY });
        setShowNotebookContextMenu(true);
    };

    const handleCopyNotebookInternal = async () => {
        if (!selectedContextNotebookId || !onCopyNotebook) {
            setShowNotebookContextMenu(false);
            return;
        }
        try {
            await onCopyNotebook(selectedContextNotebookId);
        } catch (error) {
            console.error('Failed to copy notebook:', error);
            showToast({
                type: 'error',
                title: 'Failed to copy notebook',
                message: 'Please try again.'
            });
        } finally {
            setShowNotebookContextMenu(false);
        }
    };

    const handleDeleteNotebookInternal = async () => {
        if (!selectedContextNotebookId || !onDeleteNotebook) {
            setShowNotebookContextMenu(false);
            return;
        }
        setNotebookToDelete(selectedContextNotebookId);
        setShowDeleteNotebookConfirm(true);
        setShowNotebookContextMenu(false);
    };

    const handleDeleteNotebookConfirm = async () => {
        // Priority: explicitly selected via context menu (notebookToDelete), then multi-selection
        if (notebookToDelete && onDeleteNotebook) {
            try {
                await onDeleteNotebook(notebookToDelete);
            } catch (error) {
                console.error('Failed to delete notebook:', error);
                showToast({
                    type: 'error',
                    title: 'Failed to delete notebook',
                    message: 'Please try again.'
                });
            }
        } else if (notebookSelect.selectedCount > 0) {
            const idsToDelete = Array.from(notebookSelect.selectedIds);
            try {
                if (idsToDelete.length === 1 && onDeleteNotebook) {
                    await onDeleteNotebook(idsToDelete[0]);
                } else if (onDeleteNotebooks) {
                    await onDeleteNotebooks(idsToDelete);
                } else if (onDeleteNotebook) {
                    // Fallback: serial delete if batch not supported
                    for (const id of idsToDelete) {
                        await onDeleteNotebook(id);
                    }
                }
                notebookSelect.clearSelection();
            } catch (error) {
                console.error('Failed to delete notebooks:', error);
                showToast({
                    type: 'error',
                    title: 'Failed to delete notebooks',
                    message: 'Please try again.'
                });
            }
        }
        setShowDeleteNotebookConfirm(false);
        setNotebookToDelete(null);
    };

    const handleDeleteNotebookCancel = () => {
        setShowDeleteNotebookConfirm(false);
        setNotebookToDelete(null);
    };

    const startRenameNotebook = (notebookId: string) => {
        const nb = currentNotebooks.find(n => n.id === notebookId);
        if (!nb) return;
        setEditingNotebookId(notebookId);
        setEditNotebookTitle(nb.title);
        setShowNotebookContextMenu(false);
    };

    const saveRenameNotebook = async () => {
        if (!editingNotebookId || !onRenameNotebook) {
            cancelRenameNotebook();
            return;
        }
        const trimmed = editNotebookTitle.trim();
        const nb = currentNotebooks.find(n => n.id === editingNotebookId);
        if (!trimmed || trimmed === nb?.title) {
            cancelRenameNotebook();
            return;
        }
        try {
            await onRenameNotebook(editingNotebookId, trimmed);
        } catch (error) {
            console.error('Failed to rename notebook:', error);
            showToast({
                type: 'error',
                title: 'Failed to rename notebook',
                message: 'Please try again.'
            });
        } finally {
            cancelRenameNotebook();
        }
    };

    const cancelRenameNotebook = () => {
        setEditingNotebookId(null);
        setEditNotebookTitle('');
    };

    const handleRenameNotebookInternal = () => {
        if (selectedContextNotebookId) {
            startRenameNotebook(selectedContextNotebookId);
        } else {
            setShowNotebookContextMenu(false);
        }
    };

    // Filter functions based on search term
    const filterBySearch = (searchTerm: string) => {
        if (!searchTerm.trim()) return { showAll: true };
        
        const term = searchTerm.toLowerCase().trim();
        
        const filteredNotebooks = currentNotebooks.filter(n => 
            n.title.toLowerCase().includes(term) || 
            (n.description && n.description.toLowerCase().includes(term))
        );
        
        const filteredFiles = project.contentFiles.filter(f =>
            f.fileName.toLowerCase().includes(term)
        );
        
        const filteredLinks = project.links.filter(l => {
            const hostname = new URL(l.url).hostname.toLowerCase();
            return hostname.includes(term) || l.url.toLowerCase().includes(term);
        });
        
        return {
            showAll: false,
            notebooks: filteredNotebooks,
            files: filteredFiles,
            links: filteredLinks,
            hasResults: filteredNotebooks.length > 0 || filteredFiles.length > 0 || filteredLinks.length > 0
        };
    };

    const searchResults = filterBySearch(searchTerm);

    // --- Multi-Select & Keyboard Support ---
    const notebookSortKey = useMemo(() => {
        return `project.sidebar.notebookSort:${project.id}`;
    }, [project.id]);

    useEffect(() => {
        const stored = localStorage.getItem(notebookSortKey);
        if (stored === 'recent' || stored === 'alpha') {
            setNotebookSort(stored);
        } else {
            setNotebookSort('recent');
        }
        lastSortKeyRef.current = notebookSortKey;
    }, [notebookSortKey]);

    useEffect(() => {
        if (lastSortKeyRef.current !== notebookSortKey) return;
        localStorage.setItem(notebookSortKey, notebookSort);
    }, [notebookSortKey, notebookSort]);

    const parseActivityTime = useCallback((value?: string) => {
        if (!value) return 0;
        if (value.endsWith('Z') || value.includes('+') || value.includes('-', 10)) {
            return new Date(value).getTime();
        }
        return new Date(`${value}Z`).getTime();
    }, []);

    const sortedNotebooks = useMemo(() => {
        const list = searchResults.showAll 
            ? currentNotebooks 
            : (searchResults.notebooks || []);
        const sorted = [...list];
        const titleCompare = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: 'base' });

        sorted.sort((a, b) => {
            if (notebookSort === 'alpha') {
                const byTitle = titleCompare(a.title, b.title);
                if (byTitle !== 0) return byTitle;
                const byActivity = parseActivityTime(b.lastActivity) - parseActivityTime(a.lastActivity);
                if (byActivity !== 0) return byActivity;
                return a.id.localeCompare(b.id);
            }

            const byActivity = parseActivityTime(b.lastActivity) - parseActivityTime(a.lastActivity);
            if (byActivity !== 0) return byActivity;
            const byTitle = titleCompare(a.title, b.title);
            if (byTitle !== 0) return byTitle;
            return a.id.localeCompare(b.id);
        });

        return sorted;
    }, [searchResults, currentNotebooks, notebookSort, parseActivityTime]);

    const selectionCoordinator = useSidebarSelectionCoordinator({
        sections: ['notebooks', 'contentFiles']
    });

    const notebookSelect = useMultiSelect({
        items: sortedNotebooks,
        getId: n => n.id,
        onSelectionChange: (ids) => {
            if (ids.size > 0) {
                selectionCoordinator.activateSection('notebooks');
            }
        }
    });

    // Clear notebook selection if another section becomes active
    useEffect(() => {
        if (selectionCoordinator.activeSection !== 'notebooks') {
            notebookSelect.clearSelection();
            notebookNav.setFocusedId(null);
        }
    }, [selectionCoordinator.activeSection]);

    const notebookNav = useListKeyboardNavigation({
        items: sortedNotebooks,
        getId: n => n.id,
        onNavigate: (id) => onItemSelect('notebooks', id),
        onSelectionChange: (id, shift) => {
             notebookSelect.handleClick(id, { 
                 shiftKey: shift, 
                 ctrlKey: false, 
                 metaKey: false, 
                 preventDefault: () => {}, 
                 stopPropagation: () => {} 
             } as any);
             selectionCoordinator.activateSection('notebooks');
        }
    });
    const notebookFocusIntentRef = notebookNav.focusIntentRef;

    const handleDeleteNotebooksBatch = () => {
        if ((!onDeleteNotebooks && !onDeleteNotebook) || notebookSelect.selectedCount === 0) return;
        setShowDeleteNotebookConfirm(true);
    };

    useSidebarKeyboardShortcuts({
        isActive: selectionCoordinator.isSectionActive('notebooks'),
        onDelete: handleDeleteNotebooksBatch,
        onSelectAll: () => notebookSelect.selectAll(),
        onClearSelection: () => notebookSelect.clearSelection(),
        onRename: () => {
             if (notebookSelect.selectedCount === 1) {
                 const id = Array.from(notebookSelect.selectedIds)[0];
                 startRenameNotebook(id);
             }
        }
    });

    return (
        <>
            <div className="h-full border-r bg-white flex flex-col min-h-0">
                {/* Search Box - Hidden when collapsed */}
                {!isCollapsed && (
                    <div className="p-3 border-b">
                        <div className="flex items-center">
                            <div className="relative flex-1">
                                <input
                                    type="text"
                                    placeholder="Search notebooks, files, links, collaborators..."
                                    value={searchTerm}
                                    onChange={(e) => setSearchTerm(e.target.value)}
                                    className="w-full pl-8 pr-4 py-2 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 disabled:opacity-50 disabled:cursor-not-allowed"
                                    disabled={disabled}
                                />
                                <svg 
                                    className="absolute left-2.5 top-2.5 w-4 h-4 text-gray-400" 
                                    fill="none" 
                                    stroke="currentColor" 
                                    viewBox="0 0 24 24"
                                >
                                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                                </svg>
                                {searchTerm && (
                                    <button
                                        onClick={() => setSearchTerm('')}
                                        className="absolute right-2 top-2 p-0.5 text-gray-400 hover:text-gray-600"
                                        disabled={disabled}
                                    >
                                        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                                        </svg>
                                    </button>
                                )}
                            </div>
                            {/* Invisible spacer to match section header button space */}
                            <div className="flex items-center">
                                <div className="ml-2 opacity-0">
                                    <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                                        <polyline points="6 9 12 15 18 9"></polyline>
                                    </svg>
                                </div>
                            </div>
                        </div>
                    </div>
                )}
                <div className="flex-1 overflow-y-auto p-2">
                    {/* No Results Message */}
                    {!searchResults.showAll && !searchResults.hasResults && (
                        <div className="p-4 text-center text-gray-500">
                            <p className="text-sm">No results found for "{searchTerm}"</p>
                            <p className="text-xs mt-1">Try searching for notebooks, files, links, or collaborators</p>
                        </div>
                    )}

                    <SidebarSection
                        title="Notebooks"
                        type="notebooks"
                        isExpanded={expandedSections.has('notebooks')}
                        onToggle={() => onSectionToggle('notebooks')}
                        onSelect={onItemSelect}
                        selectedItem={selectedItem}
                        items={[]}
                        headerActions={
                            <div className="flex items-center gap-1 mr-2">
                                <button
                                    type="button"
                                    onClick={() => setNotebookSort('recent')}
                                    className={`px-2 py-0.5 text-xs rounded border ${
                                        notebookSort === 'recent'
                                            ? 'bg-blue-600 text-white border-blue-600'
                                            : 'bg-white text-gray-600 border-gray-300 hover:bg-gray-50'
                                    }`}
                                    aria-pressed={notebookSort === 'recent'}
                                    title="Sort by last activity"
                                >
                                    Recent
                                </button>
                                <button
                                    type="button"
                                    onClick={() => setNotebookSort('alpha')}
                                    className={`px-2 py-0.5 text-xs rounded border whitespace-nowrap ${
                                        notebookSort === 'alpha'
                                            ? 'bg-blue-600 text-white border-blue-600'
                                            : 'bg-white text-gray-600 border-gray-300 hover:bg-gray-50'
                                    }`}
                                    aria-pressed={notebookSort === 'alpha'}
                                    title="Sort alphabetically"
                                >
                                    A-Z
                                </button>
                            </div>
                        }
                        onAdd={() => setShowCreateNotebookDialog(true)}
                        showAddButton={!!onCreateNotebook}
                        disabled={disabled}
                        data-tour-id="sidebar.section.notebooks"
                    >
                        {sortedNotebooks.map(n => (
                            <NotebookListItem
                                key={n.id}
                                notebook={n}
                                isEditing={editingNotebookId === n.id}
                                isSelected={notebookSelect.isSelected(n.id)}
                                isFocused={notebookNav.focusedId === n.id}
                                isOpen={selectedItem?.type === 'notebooks' && selectedItem.id === n.id}
                                isMobile={isMobile}
                                disabled={disabled}
                                editNotebookTitle={editNotebookTitle}
                                notebookFocusIntentRef={notebookFocusIntentRef}
                                isSectionActive={selectionCoordinator.isSectionActive('notebooks')}
                                onTitleChange={setEditNotebookTitle}
                                onSaveRename={saveRenameNotebook}
                                onCancelRename={cancelRenameNotebook}
                                onItemSelect={onItemSelect}
                                onClickSelect={notebookSelect.handleClick}
                                onSetFocusedId={notebookNav.setFocusedId}
                                onKeyDown={notebookNav.handleKeyDown}
                                onContextMenu={handleNotebookContextMenu}
                                onSetSelection={notebookSelect.setSelection}
                            />
                        ))}
                    </SidebarSection>

                    <SidebarSection
                        title="Files"
                        type="contentFiles"
                        isExpanded={expandedSections.has('contentFiles')}
                        onToggle={() => onSectionToggle('contentFiles')}
                        onSelect={onItemSelect}
                        selectedItem={selectedItem}
                        items={[]} // We're using custom children instead
                        onUploadFiles={onUploadFilesToFolder ? handleUploadFilesLegacy : undefined}
                        onOpenUploadDialog={onUploadFilesToFolder ? () => handleUploadToFolder(undefined) : undefined}
                        noIndentChildren
                        disabled={disabled}
                        data-tour-id="sidebar.section.files"
                    >
                        {folderTree ? (
                            <FolderTree
                                projectId={project.id}
                                folderTree={folderTree}
                                folders={project.folders}
                                selectedFileId={selectedItem?.type === 'contentFiles' ? selectedItem.id : undefined}
                                onFileSelect={handleFileSelect}
                                onFolderSelect={handleFolderSelect}
                                onCreateFolder={handleCreateFolderWrapper}
                                onRenameFolder={onRenameFolder}
                                onDeleteFolder={onDeleteFolder}
                                onMoveFile={onMoveFile}
                                resolveProjectFilePath={resolveProjectFilePath}
                                onUploadToFolder={handleUploadToFolder}
                                onDeleteFile={onDeleteFile}
                                onRenameFile={onRenameFile}
                                onCreateNotebookFromFile={onCreateNotebookFromFile ? (id, name) => handleCreateNotebookFromFiles([{id, fileName: name}]) : undefined}
                                onCreateNotebookFromFiles={onCreateNotebookFromFile ? handleCreateNotebookFromFiles : undefined}
                                disabled={disabled}
                                searchTerm={searchTerm}
                                homePageContentFileId={homePageContentFileId}
                                onSetHomePage={onSetHomePage}
                                onFileContentChanged={onFileContentChanged}
                                activeSection={selectionCoordinator.activeSection || undefined}
                                onSectionActivate={selectionCoordinator.activateSection}
                                onDeleteFiles={async (ids) => {
                                    if (onDeleteFile) {
                                        // TODO: Use batch API if available
                                        await Promise.all(ids.map(id => onDeleteFile(id)));
                                    }
                                }}
                            />
                        ) : (
                            // Fallback to simple file list if no folder tree
                            <>
                                {(searchResults.showAll 
                                    ? [...project.contentFiles]
                                    : [...(searchResults.files || [])]
                                ).sort((a, b) => a.fileName.localeCompare(b.fileName)).map(file => (
                                    <button
                                        key={file.id}
                                        onClick={() => onItemSelect('contentFiles', file.id)}
                                        className={`w-full text-left py-1 px-2 text-sm ${
                                            selectedItem?.type === 'contentFiles' && selectedItem.id === file.id
                                                ? 'text-blue-600'
                                                : ''
                                        } disabled:opacity-50 disabled:cursor-not-allowed`}
                                        disabled={disabled}
                                    >
                                        {file.fileName}
                                    </button>
                                ))}
                            </>
                        )}
                    </SidebarSection>

                    {/* Links Section - Hidden */}
                    {false && (
                        <SidebarSection
                            title="Links"
                            type="links"
                            isExpanded={expandedSections.has('links')}
                            onToggle={() => onSectionToggle('links')}
                            onSelect={onItemSelect}
                            selectedItem={selectedItem}
                            items={[] /* custom children */}
                            onAdd={onAddLink ? onAddLink : undefined}
                            disabled={disabled}
                        >
                            
                            {(searchResults.showAll 
                                ? [...project.links]
                                : [...(searchResults.links || [])]
                            ).sort((a, b) => {
                                const labelA = new URL(a.url).hostname;
                                const labelB = new URL(b.url).hostname;
                                return labelA.localeCompare(labelB);
                            }).map((l) => {
                                const label = new URL(l.url).hostname;
                                return (
                                    <button
                                        key={l.id}
                                        onClick={() => onItemSelect('links', l.id)}
                                        onContextMenu={(e) => handleLinkContextMenu(e, l.id)}
                                        className={`w-full text-left py-1 px-2 text-sm ${
                                            selectedItem?.type === 'links' && selectedItem.id === l.id ? 'text-blue-600' : ''
                                        } disabled:opacity-50 disabled:cursor-not-allowed`}
                                        disabled={disabled}
                                    >
                                        {label}
                                    </button>
                                );
                            })}
                        </SidebarSection>
                    )}
                  



                    {/* Guide Authorization (Owners only, only when there are items) */}
                    {isCurrentUserOwner && authTemplates.length > 0 && (
                        <SidebarSection
                            title="Guide Authorization"
                            type="guideAuthorization"
                            isExpanded={expandedSections.has('guideAuthorization')}
                            onToggle={() => onSectionToggle('guideAuthorization')}
                            onSelect={onItemSelect}
                            selectedItem={selectedItem}
                            items={authTemplates.map(t => ({ id: t.id, label: t.templateName }))}
                            disabled={disabled || isLoadingAuthTemplates}
                        />
                    )}

                    {/* Guides: single link for owners only */}
                    {isCurrentUserOwner && (
                        <div className="mb-4">
                            <button
                                type="button"
                                onClick={() => navigate(`/projects/${project.id}/guides`)}
                                className={`flex items-center p-2 w-full text-left font-medium rounded ${
                                    location.pathname.includes('/guides') ? 'text-blue-600 bg-blue-50' : 'text-gray-900'
                                } hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed`}
                                disabled={disabled}
                                data-tour-id="sidebar.section.guides"
                            >
                                Guides
                            </button>
                        </div>
                    )}
                </div>
            </div>

            {/* Upload Dialog */}
            {onUploadFilesToFolder && (
                <UploadFilesDialog
                    isOpen={showUploadDialog}
                    onClose={() => {
                        setShowUploadDialog(false);
                        setUploadTargetFolderId(undefined);
                    }}
                    onUpload={handleUploadDialogSubmit}
                    initialFolderId={uploadTargetFolderId}
                    folderTree={folderTree}
                    disabled={disabled}
                />
            )}

            {/* Link Context Menu */}
            {showLinkContextMenu && !disabled && createPortal(
                <div
                    className="fixed bg-white shadow-lg rounded-lg py-1 z-[9999]"
                    style={{ top: linkContextMenuPos.y, left: linkContextMenuPos.x }}
                    onClick={(e) => e.stopPropagation()}
                >
                    <button
                        className="block w-full text-left px-4 py-2 text-sm text-red-600 hover:bg-gray-100"
                        onClick={handleDeleteLinkInternal}
                    >
                        Delete
                    </button>
                </div>,
                document.body
            )}

            {/* Create Notebook Dialog */}
            {onCreateNotebook && (
                <CreateNotebookDialog
                    projectId={project.id}
                    isOpen={showCreateNotebookDialog}
                    onClose={() => {
                        setShowCreateNotebookDialog(false);
                        setCreateNotebookFromFiles(null);
                    }}
                    onCreate={async (title, templateId, description) => {
                        if (createNotebookFromFiles && onCreateNotebookFromFiles) {
                            await onCreateNotebookFromFiles(createNotebookFromFiles, title, templateId, description);
                        } else if (createNotebookFromFiles && createNotebookFromFiles.length === 1 && onCreateNotebookFromFile) {
                             await onCreateNotebookFromFile(createNotebookFromFiles[0].id, createNotebookFromFiles[0].fileName, title, templateId, description);
                        } else {
                            // Use standard creation
                            await onCreateNotebook(title, templateId, description);
                        }
                    }}
                    initialFiles={createNotebookFromFiles}
                    disabled={disabled}
                />
            )}

            {/* Notebook Context Menu */}
            {showNotebookContextMenu && !disabled && createPortal(
                <div
                    className="fixed bg-white shadow-lg rounded-lg py-1 z-[9999]"
                    style={{ top: notebookContextMenuPos.y, left: notebookContextMenuPos.x }}
                    onClick={(e) => e.stopPropagation()}
                >
                    {notebookSelect.selectedCount <= 1 ? (
                        <>
                            {onCopyNotebook && (
                                <button
                                    className="block w-full text-left px-4 py-2 text-sm hover:bg-gray-100"
                                    onClick={handleCopyNotebookInternal}
                                >
                                    Copy
                                </button>
                            )}
                            {onRenameNotebook && (
                                <button
                                    className="block w-full text-left px-4 py-2 text-sm hover:bg-gray-100"
                                    onClick={handleRenameNotebookInternal}
                                >
                                    Rename
                                </button>
                            )}
                            {onDeleteNotebook && (
                                <button
                                    className="block w-full text-left px-4 py-2 text-sm text-red-600 hover:bg-gray-100"
                                    onClick={handleDeleteNotebookInternal}
                                >
                                    Delete
                                </button>
                            )}
                        </>
                    ) : (
                        <>
                            {(onDeleteNotebooks || onDeleteNotebook) && (
                                <button
                                    className="block w-full text-left px-4 py-2 text-sm text-red-600 hover:bg-gray-100"
                                    onClick={handleDeleteNotebooksBatch}
                                >
                                    Delete {notebookSelect.selectedCount} Notebooks
                                </button>
                            )}
                        </>
                    )}
                </div>,
                document.body
            )}

            {/* Confirmation Dialogs */}
            <ConfirmationDialog
                isOpen={showDeleteLinkConfirm}
                onClose={handleDeleteLinkCancel}
                onConfirm={handleDeleteLinkConfirm}
                title="Confirm Deletion"
                message="Are you sure you want to delete this link?"
                confirmText="Delete"
                cancelText="Cancel"
            />
            <ConfirmationDialog
                isOpen={showDeleteNotebookConfirm}
                onClose={handleDeleteNotebookCancel}
                onConfirm={handleDeleteNotebookConfirm}
                title="Confirm Deletion"
                message={notebookSelect.selectedCount > 1 && !notebookToDelete
                    ? `Are you sure you want to delete ${notebookSelect.selectedCount} notebooks?`
                    : "Are you sure you want to delete this notebook?"}
                confirmText="Delete"
                cancelText="Cancel"
            />
        </>
    );
} 