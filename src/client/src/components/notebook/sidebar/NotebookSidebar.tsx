import React, { useState, useEffect, useMemo, useRef, useCallback } from 'react';
import { createPortal } from 'react-dom';
import { SidebarSection } from '../../common/SidebarSection';
import { NotebookFolderTree } from './NotebookFolderTree';
import { NotebookUploadDialog } from '../dialogs/NotebookUploadDialog';
import { CreateConversationDialog } from '../dialogs/CreateConversationDialog';
import { 
  NotebookSidebarSectionType, 
  NotebookSidebarSelectedItem, 
  NotebookFileDto, 
  NotebookFolderTreeDto, 
  CreateNotebookFolderDto,
  LinkDto 
} from '../../../types/notebook';
import { FolderTreeDto } from '../../../types/project';
import { useNotebook } from '../../../contexts/NotebookContext';
import { ConfirmationDialog } from '../../common/ConfirmationDialog';
import { useConversationListPolling } from '../../../hooks/useConversationListPolling';
import { useNotebookFilesPolling } from '../../../hooks/useNotebookFilesPolling';
import { useParams } from 'react-router-dom';
import { useMultiSelect } from '../../../hooks/useMultiSelect';
import { useListKeyboardNavigation } from '../../../hooks/useListKeyboardNavigation';
import { useSidebarKeyboardShortcuts } from '../../../hooks/useSidebarKeyboardShortcuts';
import { useSidebarSelectionCoordinator } from '../../../hooks/useSidebarSelectionCoordinator';
import { useLongPress } from '../../../hooks/useLongPress';
import { api } from '../../../services/api';

// Props for the extracted ConversationListItem component
interface ConversationListItemProps {
  convo: { id: string; title: string };
  isEditing: boolean;
  isSelected: boolean;
  isFocused: boolean;
  isOpen: boolean;
  isMobile: boolean;
  canEdit: boolean;
  editConversationTitle: string;
  conversationFocusIntentRef: React.MutableRefObject<boolean>;
  isSectionActive: boolean;
  onTitleChange: (value: string) => void;
  onSaveRename: () => void;
  onCancelRename: () => void;
  onItemSelect: (type: 'conversations', id: string) => void;
  onClickSelect: (id: string, e: React.MouseEvent) => void;
  onSetFocusedId: (id: string) => void;
  onKeyDown: (e: React.KeyboardEvent) => void;
  onContextMenu: (e: React.MouseEvent, id: string) => void;
  onSetSelection: (ids: string[]) => void;
}

/**
 * Individual conversation list item component.
 * Extracted to properly use the useLongPress hook (hooks can't be called in loops).
 */
function ConversationListItem({
  convo,
  isEditing,
  isSelected,
  isFocused,
  isOpen,
  isMobile,
  canEdit,
  editConversationTitle,
  conversationFocusIntentRef,
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
}: ConversationListItemProps) {
  // Long press handler for mobile context menu
  const longPressHandlers = useLongPress({
    onLongPress: (e) => {
      if (canEdit && isMobile) {
        // Select the item if not already selected
        if (!isSelected) {
          onSetSelection([convo.id]);
        }
        // Create a synthetic mouse event for the context menu handler
        onContextMenu({ 
          clientX: e.clientX, 
          clientY: e.clientY,
          preventDefault: () => {},
          stopPropagation: () => {},
        } as React.MouseEvent, convo.id);
      }
    },
    disabled: !canEdit || !isMobile,
    threshold: 500,
  });

  if (isEditing) {
    return (
      <div className="w-full">
        <input
          className="w-full border rounded px-1 py-0.5 text-sm"
          value={editConversationTitle}
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
          onClick={(e) => e.stopPropagation()}
        />
      </div>
    );
  }

  return (
    <div className="w-full">
      <button
        ref={el => {
          // Only apply focus if explicitly requested via keyboard navigation
          // This prevents focus-stealing when polling refreshes the list
          if (el && isFocused && conversationFocusIntentRef.current && isSectionActive) {
            el.focus();
            conversationFocusIntentRef.current = false;
          }
        }}
        tabIndex={isFocused ? 0 : -1}
        onClick={(e) => {
          // On mobile: single tap opens immediately
          // On desktop: single click selects, double-click opens
          if (isMobile) {
            onSetSelection([]); // Clear multi-selection when opening
            onSetFocusedId(convo.id); // Update visual focus indicator
            onItemSelect('conversations', convo.id);
          } else {
            onClickSelect(convo.id, e);
            onSetFocusedId(convo.id);
          }
        }}
        onDoubleClick={() => {
          if (!isMobile) {
            onSetSelection([]); // Clear multi-selection when opening
            onSetFocusedId(convo.id); // Update visual focus indicator
            onItemSelect('conversations', convo.id);
          }
        }}
        onKeyDown={onKeyDown}
        onContextMenu={(e) => {
          if (canEdit) {
            if (!isSelected) {
              onSetSelection([convo.id]);
            }
            onContextMenu(e, convo.id);
          }
        }}
        {...longPressHandlers}
        className={`w-full text-left py-1 px-2 text-sm ${
          isSelected ? 'bg-blue-100' : isOpen ? 'bg-blue-50' : 'hover:bg-gray-100'
        } ${isFocused ? 'ring-2 ring-blue-500 ring-inset' : ''} ${
          isOpen ? 'text-blue-600 font-medium' : ''
        } disabled:opacity-50 disabled:cursor-not-allowed`}
        title={isMobile ? "Tap to open, hold for options" : "Double-click or ENTER to open"}
      >
        {convo.title}
      </button>
    </div>
  );
}

export interface NotebookSidebarProps {
  // Notebook-specific data (folderTree prop deprecated - uses polling internally)
  folderTree?: NotebookFolderTreeDto | null;
  notebookName?: string;
  
  // Collaborator context
  collaboratorCount?: number;
  
  // Project data for file selection
  projectFolderTree?: FolderTreeDto | null;
  
  // Notebook file operations
  onUploadFiles?: (files: File[], folderId?: string) => Promise<void>;
  onCreateFolder?: (parentFolderPath: string | undefined, folderData: CreateNotebookFolderDto) => Promise<void>;
  onRenameFolder?: (folderPath: string, newName: string) => Promise<void>;
  onDeleteFolder?: (folderPath: string) => Promise<void>;
  onMoveFile?: (fileId: string, destinationFolderPath: string | undefined) => Promise<void>;
  onDeleteFile?: (fileId: string) => Promise<void>;
  onRenameFile?: (fileId: string, newName: string) => Promise<void>;
  onCopyFromProject?: (contentFileId: string, version?: number) => Promise<void>;
  onPublishToProject?: (files: NotebookFileDto[]) => void;
  onPreviewFile?: (file: NotebookFileDto) => void;
  
  // Links (from project, passed via props)
  links: LinkDto[];
  onDeleteLink?: (linkId: string) => Promise<void>;
  
  // Conversation operations
  onConversationDeleted?: (deletedConversationId: string) => void;
  onConversationsDeleted?: (deletedConversationIds: string[]) => void;
  
  // Home page functionality
  homePageFileId?: string;
  onSetHomePage?: (fileId: string | null) => void;
  
  // UI state
  canEdit: boolean;
    isCollapsed?: boolean;
  expandedSections: Set<NotebookSidebarSectionType>;
  selectedItem: NotebookSidebarSelectedItem | null;
  onSectionToggle: (section: NotebookSidebarSectionType) => void;
  onItemSelect: (type: NotebookSidebarSectionType, id: string) => void;
}

function NotebookSidebarInner({ 
  folderTree,
  notebookName,
  collaboratorCount,
  projectFolderTree,
  onUploadFiles,
  onCreateFolder,
  onRenameFolder,
  onDeleteFolder,
  onMoveFile,
  onDeleteFile,
  onRenameFile,
  onCopyFromProject,
  onPublishToProject,
  onPreviewFile,
  links,
  onDeleteLink,
  onConversationDeleted,
  onConversationsDeleted,
  homePageFileId,
  onSetHomePage,
  canEdit, 
    isCollapsed = false,
  expandedSections,
  selectedItem,
  onSectionToggle,
  onItemSelect
}: NotebookSidebarProps) {
  const [showUploadDialog, setShowUploadDialog] = useState(false);
  const [uploadTargetFolderId, setUploadTargetFolderId] = useState<string | undefined>();

  // Context menu state
  const [showLinkContextMenu, setShowLinkContextMenu] = useState(false);
  const [showConversationContextMenu, setShowConversationContextMenu] = useState(false);
  const [linkContextMenuPos, setLinkContextMenuPos] = useState({ x: 0, y: 0 });
  const [conversationContextMenuPos, setConversationContextMenuPos] = useState({ x: 0, y: 0 });
  const [selectedContextLinkId, setSelectedContextLinkId] = useState<string | null>(null);
  const [selectedContextConversationId, setSelectedContextConversationId] = useState<string | null>(null);

  // Confirmation dialog state
  const [showDeleteLinkConfirm, setShowDeleteLinkConfirm] = useState(false);
  const [showDeleteConversationConfirm, setShowDeleteConversationConfirm] = useState(false);
  const [linkToDelete, setLinkToDelete] = useState<string | null>(null);
  const [conversationToDelete, setConversationToDelete] = useState<string | null>(null);

  // Conversation inline rename state
  const [editingConversationId, setEditingConversationId] = useState<string | null>(null);
  const [editConversationTitle, setEditConversationTitle] = useState('');

  // Search state
  const [searchTerm, setSearchTerm] = useState('');
  const [conversationSort, setConversationSort] = useState<'recent' | 'alpha'>('recent');
  const lastSortKeyRef = useRef<string | null>(null);

  // Mobile detection for touch-friendly interactions
  const [isMobile, setIsMobile] = useState(false);
  useEffect(() => {
    const checkMobile = () => setIsMobile(window.innerWidth < 768);
    checkMobile();
    window.addEventListener('resize', checkMobile);
    return () => window.removeEventListener('resize', checkMobile);
  }, []);

  const { 
    createConversation, 
    renameConversation, 
    deleteConversation,
    deleteConversations 
  } = useNotebook();

  // Get IDs directly from URL params to avoid waiting for notebook context to load
  const { projectId, notebookId } = useParams<{ projectId: string; notebookId: string }>();

  // Add sidebar conversation polling locally (moved from NotebookContext to prevent provider remounts)  
  const {
    conversations,
    refresh: refreshConversations
  } = useConversationListPolling({
    projectId: projectId || '',
    notebookId: notebookId || '',
    enabled: !!(projectId && notebookId),
    pollInterval: collaboratorCount === 1 ? 60000 : 10000
  });

  // Independent polling for notebook files to avoid re-mounting conversations
  const {
    folderTree: polledFolderTree,
    refresh: refreshNotebookFiles,
  } = useNotebookFilesPolling({
    projectId: projectId || '',
    notebookId: notebookId || '',
    enabled: !!(projectId && notebookId),
    pollInterval: collaboratorCount === 1 ? 60000 : 10000,
  });

  const effectiveFolderTree = polledFolderTree ?? folderTree;


  // Listen for manual refresh events (e.g., after title auto-generation)
  useEffect(() => {
    const handler = () => {
      try { refreshConversations(); } catch {}
    };
    window.addEventListener('refresh-conversations', handler);
    return () => window.removeEventListener('refresh-conversations', handler);
  }, [refreshConversations]);

  // Listen for immediate notebook files refresh events triggered by conversation completion
  useEffect(() => {
    const handler = () => {
      try { refreshNotebookFiles(); } catch {}
    };
    window.addEventListener('refresh-notebook-files', handler);
    return () => window.removeEventListener('refresh-notebook-files', handler);
  }, [refreshNotebookFiles]);

  // Listen for refresh-conversations event (e.g., when a new conversation is created from mobile header)
  useEffect(() => {
    const handler = () => {
      try { refreshConversations(); } catch {}
    };
    window.addEventListener('refresh-conversations', handler);
    return () => window.removeEventListener('refresh-conversations', handler);
  }, [refreshConversations]);

  // TODO: Phase 2 - Implement these operations
  // These are passed from the context but not yet implemented in the UI
  React.useEffect(() => {
    // Placeholder to avoid unused variable warnings
    if (onCopyFromProject) {
      // These will be implemented in Phase 2 with context menus and dialogs
    }
  }, [onCopyFromProject]);

  // Clear search when sidebar collapses
  useEffect(() => {
    if (isCollapsed) {
      setSearchTerm('');
    }
  }, [isCollapsed]);

  // Context menu cleanup - Fixed to handle both link and conversation menus
  useEffect(() => {
    const handleClickOutside = () => {
      setShowLinkContextMenu(false);
      setShowConversationContextMenu(false);
    };

    const globalCloseHandler = () => {
      setShowLinkContextMenu(false);
      setShowConversationContextMenu(false);
    };

    document.addEventListener('click', handleClickOutside);
    window.addEventListener('close-context-menus', globalCloseHandler);

    return () => {
      document.removeEventListener('click', handleClickOutside);
      window.removeEventListener('close-context-menus', globalCloseHandler);
    };
  }, []);

  const handleUploadToFolder = React.useCallback((folderId?: string) => {
    setUploadTargetFolderId(folderId);
    setShowUploadDialog(true);
  }, []);

  const handleUploadDialogSubmit = React.useCallback(async (files: File[], folderId?: string) => {
    if (onUploadFiles) {
      await onUploadFiles(files, folderId);
      try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
    }
    setShowUploadDialog(false);
  }, [onUploadFiles]);

  // Wrap file operations to trigger refresh after completion
  const handleDeleteFile = React.useCallback(async (fileId: string) => {
    if (onDeleteFile) {
      await onDeleteFile(fileId);
      refreshNotebookFiles();
    }
  }, [onDeleteFile, refreshNotebookFiles]);

  const handleRenameFile = React.useCallback(async (fileId: string, newName: string) => {
    if (onRenameFile) {
      await onRenameFile(fileId, newName);
      refreshNotebookFiles();
    }
  }, [onRenameFile, refreshNotebookFiles]);

  const handleMoveFile = React.useCallback(async (fileId: string, destinationFolderPath: string | undefined) => {
    if (onMoveFile) {
      await onMoveFile(fileId, destinationFolderPath);
      refreshNotebookFiles();
    }
  }, [onMoveFile, refreshNotebookFiles]);

  const handleCopyFromProjectSubmit = React.useCallback(async (files: { fileId: string; version?: number }[], _folderId?: string) => {
    if (onCopyFromProject) {
      for (const file of files) {
        await onCopyFromProject(file.fileId, file.version);
      }
      try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
    }
    setShowUploadDialog(false);
  }, [onCopyFromProject]);

  // No-op wrappers: direct event-based refresh is handled within tree/file ops

  // Context menu helpers for Links
  const handleLinkContextMenu = (e: React.MouseEvent, linkId: string) => {
    if (!canEdit) return;
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
      }
    }
    setShowDeleteLinkConfirm(false);
    setLinkToDelete(null);
  };

  const handleDeleteLinkCancel = () => {
    setShowDeleteLinkConfirm(false);
    setLinkToDelete(null);
  };

  // Conversation context handlers
  const handleConversationContextMenu = (e: React.MouseEvent, convoId: string) => {
    if (!canEdit) return;
    window.dispatchEvent(new Event('close-context-menus'));

    e.preventDefault();
    e.stopPropagation();
    setSelectedContextConversationId(convoId);
    setConversationContextMenuPos({ x: e.clientX, y: e.clientY });
    setShowConversationContextMenu(true);
  };

  const handleCopyConversationInternal = async () => {
    if (!selectedContextConversationId) {
      setShowConversationContextMenu(false);
      return;
    }
    const convo = conversations.find(c => c.id === selectedContextConversationId);
    if (!convo) {
      setShowConversationContextMenu(false);
      return;
    }
    try {
      await createConversation(`${convo.title} (Copy)`);
      // Refresh conversations list immediately after successful creation
      refreshConversations();
    } catch (error) {
      console.error('Failed to copy conversation:', error);
    } finally {
      setShowConversationContextMenu(false);
    }
  };

  const handleSaveConversationAsMarkdown = async () => {
    if (!selectedContextConversationId || !projectId || !notebookId) {
      setShowConversationContextMenu(false);
      return;
    }
    try {
      await api.projects.notebooks.conversations.saveAs(projectId, notebookId, selectedContextConversationId);
      // Refresh notebook files to show new file
      refreshNotebookFiles();
      setShowConversationContextMenu(false);
    } catch (error) {
      console.error('Failed to save conversation:', error);
      setShowConversationContextMenu(false);
    }
  };

  const handleDeleteConversationInternal = async () => {
    if (!selectedContextConversationId) {
      setShowConversationContextMenu(false);
      return;
    }
    setConversationToDelete(selectedContextConversationId);
    setShowDeleteConversationConfirm(true);
    setShowConversationContextMenu(false);
  };

  const handleDeleteConversationConfirm = async () => {
    if (conversationToDelete) {
      try {
        await deleteConversation(conversationToDelete);
        // Notify parent component that a conversation was deleted
        onConversationDeleted?.(conversationToDelete);
        // Refresh conversations list immediately after successful deletion
        refreshConversations();
      } catch (error) {
        console.error('Failed to delete conversation:', error);
      }
    } else if (conversationSelect.selectedCount > 0) {
        const idsToDelete = Array.from(conversationSelect.selectedIds);
        try {
            await deleteConversations(idsToDelete);
            onConversationsDeleted?.(idsToDelete);
            conversationSelect.clearSelection();
            refreshConversations();
        } catch (error) {
            console.error('Failed to delete conversations:', error);
        }
    }
    setShowDeleteConversationConfirm(false);
    setConversationToDelete(null);
  };

  const handleDeleteConversationCancel = () => {
    setShowDeleteConversationConfirm(false);
    setConversationToDelete(null);
  };

  const startRenameConversation = (conversationId: string) => {
    const convo = conversations.find(c => c.id === conversationId);
    if (!convo) return;
    setEditingConversationId(conversationId);
    setEditConversationTitle(convo.title);
    setShowConversationContextMenu(false);
  };

  const saveRenameConversation = async () => {
    if (!editingConversationId) {
      cancelRenameConversation();
      return;
    }
    const trimmed = editConversationTitle.trim();
    const convo = (conversations || []).find(c => c.id === editingConversationId);
    if (!trimmed || trimmed === convo?.title) {
      cancelRenameConversation();
      return;
    }
    try {
      await renameConversation(editingConversationId, trimmed);
      // Refresh conversations list immediately after successful rename
      refreshConversations();
    } catch (error) {
      console.error('Failed to rename conversation:', error);
    } finally {
      cancelRenameConversation();
    }
  };

  const cancelRenameConversation = () => {
    setEditingConversationId(null);
    setEditConversationTitle('');
  };

  const handleRenameConversationInternal = () => {
    if (selectedContextConversationId) {
      startRenameConversation(selectedContextConversationId);
    } else {
      setShowConversationContextMenu(false);
    }
  };

  // Filter functions based on search term
  const filterBySearch = (searchTerm: string) => {
    if (!searchTerm.trim()) return { showAll: true };
    
    const term = searchTerm.toLowerCase().trim();
    
    // File search is now handled by the folder tree component
    const filteredFiles: NotebookFileDto[] = [];
    
    const filteredLinks = links.filter(l => {
      const hostname = new URL(l.url).hostname.toLowerCase();
      return hostname.includes(term) || l.url.toLowerCase().includes(term);
    });

    const filteredConversations = (conversations || []).filter(c => c.title.toLowerCase().includes(term));

    // Check if there are any matching files in the folder tree
    const hasMatchingFiles = effectiveFolderTree ? hasMatchingFilesInTree(effectiveFolderTree, term) : false;

    return {
      showAll: false,
      files: filteredFiles,
      links: filteredLinks,
      conversations: filteredConversations,
      searchTerm: term,
      hasResults: hasMatchingFiles || filteredLinks.length > 0 || filteredConversations.length > 0
    };
  };

  // Helper function to check if there are matching files in the folder tree
  const hasMatchingFilesInTree = (tree: NotebookFolderTreeDto, term: string): boolean => {
    // Check files in current folder
    const hasMatchingFiles = tree.files.some(file => 
      file.fileName.toLowerCase().includes(term)
    );
    
    if (hasMatchingFiles) return true;

    // Check subfolders recursively
    return tree.subFolders.some(subFolder => 
      subFolder.name.toLowerCase().includes(term) || 
      hasMatchingFilesInTree(subFolder, term)
    );
  };

  const searchResults = filterBySearch(searchTerm);

  // --- Multi-Select & Keyboard Support ---
  const conversationSortKey = useMemo(() => {
    if (!projectId || !notebookId) return null;
    return `notebook.sidebar.conversationSort:${projectId}:${notebookId}`;
  }, [projectId, notebookId]);

  useEffect(() => {
    if (!conversationSortKey) return;
    const stored = localStorage.getItem(conversationSortKey);
    if (stored === 'recent' || stored === 'alpha') {
      setConversationSort(stored);
    } else {
      setConversationSort('recent');
    }
    lastSortKeyRef.current = conversationSortKey;
  }, [conversationSortKey]);

  useEffect(() => {
    if (!conversationSortKey) return;
    if (lastSortKeyRef.current !== conversationSortKey) return;
    localStorage.setItem(conversationSortKey, conversationSort);
  }, [conversationSortKey, conversationSort]);

  const parseActivityTime = useCallback((value?: string) => {
    if (!value) return 0;
    if (value.endsWith('Z') || value.includes('+') || value.includes('-', 10)) {
      return new Date(value).getTime();
    }
    return new Date(`${value}Z`).getTime();
  }, []);

  const sortedConversations = useMemo(() => {
    const base = searchResults.showAll ? (conversations || []) : (searchResults.conversations || []);
    const sorted = [...base];
    const titleCompare = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: 'base' });

    sorted.sort((a, b) => {
      if (conversationSort === 'alpha') {
        const byTitle = titleCompare(a.title, b.title);
        if (byTitle !== 0) return byTitle;
        const byActivity = parseActivityTime(b.lastActivity || b.created) - parseActivityTime(a.lastActivity || a.created);
        if (byActivity !== 0) return byActivity;
        return a.id.localeCompare(b.id);
      }

      const byActivity = parseActivityTime(b.lastActivity || b.created) - parseActivityTime(a.lastActivity || a.created);
      if (byActivity !== 0) return byActivity;
      const byTitle = titleCompare(a.title, b.title);
      if (byTitle !== 0) return byTitle;
      return a.id.localeCompare(b.id);
    });

    return sorted;
  }, [searchResults, conversations, conversationSort, parseActivityTime]);

  const selectionCoordinator = useSidebarSelectionCoordinator({
      sections: ['conversations', 'notebookFiles']
  });

  const conversationSelect = useMultiSelect({
      items: sortedConversations,
      getId: c => c.id,
      onSelectionChange: (ids) => {
          if (ids.size > 0) {
              selectionCoordinator.activateSection('conversations');
          }
      }
  });

  // Clear conversation selection if another section becomes active
  useEffect(() => {
      if (selectionCoordinator.activeSection !== 'conversations') {
          conversationSelect.clearSelection();
          conversationNav.setFocusedId(null);
      }
  }, [selectionCoordinator.activeSection]);

  const conversationNav = useListKeyboardNavigation({
      items: sortedConversations,
      getId: c => c.id,
      onNavigate: (id) => {
          conversationSelect.clearSelection(); // Clear multi-selection when opening via keyboard
          // Note: focusedId stays on the opened item for keyboard navigation continuity
          onItemSelect('conversations', id);
      },
      onSelectionChange: (id, shift) => {
           conversationSelect.handleClick(id, { 
               shiftKey: shift, 
               ctrlKey: false, 
               metaKey: false, 
               preventDefault: () => {}, 
               stopPropagation: () => {} 
           } as any);
           selectionCoordinator.activateSection('conversations');
      }
  });
  const conversationFocusIntentRef = conversationNav.focusIntentRef;

  const handleDeleteConversationsBatch = () => {
    if (!deleteConversations || conversationSelect.selectedCount === 0) return;
    setShowDeleteConversationConfirm(true);
  };

  useSidebarKeyboardShortcuts({
      isActive: selectionCoordinator.isSectionActive('conversations'),
      onDelete: handleDeleteConversationsBatch,
      onSelectAll: () => conversationSelect.selectAll(),
      onClearSelection: () => conversationSelect.clearSelection(),
      onRename: () => {
           if (conversationSelect.selectedCount === 1) {
               const id = Array.from(conversationSelect.selectedIds)[0];
               startRenameConversation(id);
           }
      }
  });

  const [showCreateConvoDialog, setShowCreateConvoDialog] = useState(false);

    if (isCollapsed) {
        return (
            <div className="w-full h-full bg-gray-50 border-r">
                {/* Collapsed state - could show minimal icons */}
            </div>
        );
    }

        return (
    <>
      <div className="h-full border-r bg-white flex flex-col min-h-0">
        {/* Search Bar */}
        <div className="p-3 border-b" data-tour-id="notebook.sidebar.search">
            <div className="flex items-center">
                <div className="relative flex-1">
                    <input
                        type="text"
                        placeholder="Search files and links..."
                        value={searchTerm}
                        onChange={(e) => setSearchTerm(e.target.value)}
                        className="w-full pl-8 pr-4 py-2 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 disabled:opacity-50 disabled:cursor-not-allowed"
                        
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

            <div className="flex-1 overflow-y-auto p-2">
          {/* No Results Message */}
          {!searchResults.showAll && !searchResults.hasResults && (
            <div className="p-4 text-center text-gray-500">
              <p className="text-sm">No results found for "{searchTerm}"</p>
              <p className="text-xs mt-1">Try searching for files or links</p>
            </div>
          )}

          {/* Conversations Section */}
          <SidebarSection<NotebookSidebarSectionType>
            title="Conversations"
            type="conversations"
            isExpanded={expandedSections.has('conversations')}
            onToggle={() => onSectionToggle('conversations')}
            onSelect={onItemSelect}
            selectedItem={selectedItem}
            items={[]}
            headerActions={
              <div className="flex items-center gap-1 mr-2">
                <button
                  type="button"
                  onClick={() => setConversationSort('recent')}
                  className={`px-2 py-0.5 text-xs rounded border ${
                    conversationSort === 'recent'
                      ? 'bg-blue-600 text-white border-blue-600'
                      : 'bg-white text-gray-600 border-gray-300 hover:bg-gray-50'
                  }`}
                  aria-pressed={conversationSort === 'recent'}
                  title="Sort by last activity"
                >
                  Recent
                </button>
                <button
                  type="button"
                  onClick={() => setConversationSort('alpha')}
                  className={`px-2 py-0.5 text-xs rounded border whitespace-nowrap ${
                    conversationSort === 'alpha'
                      ? 'bg-blue-600 text-white border-blue-600'
                      : 'bg-white text-gray-600 border-gray-300 hover:bg-gray-50'
                  }`}
                  aria-pressed={conversationSort === 'alpha'}
                  title="Sort alphabetically"
                >
                  A-Z
                </button>
              </div>
            }
            onAdd={() => setShowCreateConvoDialog(true)}
            showAddButton={canEdit}
            data-tour-id="notebook.sidebar.conversations"
          >
            {sortedConversations.map(convo => (
              <ConversationListItem
                key={convo.id}
                convo={convo}
                isEditing={editingConversationId === convo.id}
                isSelected={conversationSelect.isSelected(convo.id)}
                isFocused={conversationNav.focusedId === convo.id}
                isOpen={selectedItem?.type === 'conversations' && selectedItem.id === convo.id}
                isMobile={isMobile}
                canEdit={canEdit}
                editConversationTitle={editConversationTitle}
                conversationFocusIntentRef={conversationFocusIntentRef}
                isSectionActive={selectionCoordinator.isSectionActive('conversations')}
                onTitleChange={setEditConversationTitle}
                onSaveRename={saveRenameConversation}
                onCancelRename={cancelRenameConversation}
                onItemSelect={onItemSelect}
                onClickSelect={conversationSelect.handleClick}
                onSetFocusedId={conversationNav.setFocusedId}
                onKeyDown={conversationNav.handleKeyDown}
                onContextMenu={handleConversationContextMenu}
                onSetSelection={conversationSelect.setSelection}
              />
            ))}
          </SidebarSection>

          {/* Files Section */}
          <SidebarSection<NotebookSidebarSectionType>
            title="Files"
            type="notebookFiles"
            isExpanded={expandedSections.has('notebookFiles')}
            onToggle={() => onSectionToggle('notebookFiles')}
            onSelect={onItemSelect}
                    selectedItem={selectedItem}
            items={[]}
            onOpenUploadDialog={canEdit ? () => setShowUploadDialog(true) : undefined}
            noIndentChildren={true}
            data-tour-id="notebook.sidebar.files"
          >
            {effectiveFolderTree && (
              <NotebookFolderTree
                tree={effectiveFolderTree}
                notebookName={notebookName}
                selectedItem={selectedItem}
                onItemSelect={onItemSelect}
                onCreateFolder={canEdit ? onCreateFolder : undefined}
                onRenameFolder={canEdit ? onRenameFolder : undefined}
                onDeleteFolder={canEdit ? onDeleteFolder : undefined}
                onMoveFile={canEdit ? handleMoveFile : undefined}
                onDeleteFile={canEdit ? handleDeleteFile : undefined}
                onRenameFile={canEdit ? handleRenameFile : undefined}
                onUploadToFolder={canEdit ? handleUploadToFolder : undefined}
                onPublishToProject={canEdit ? onPublishToProject : undefined}
                onPreviewFile={onPreviewFile}
                canEdit={canEdit}
                searchTerm={searchResults.showAll ? undefined : searchResults.searchTerm}
                homePageFileId={homePageFileId}
                onSetHomePage={onSetHomePage}
                activeSection={selectionCoordinator.activeSection || undefined}
                onSectionActivate={selectionCoordinator.activateSection}
              />
            )}
          </SidebarSection>

          {/* Links Section - Hidden */}
          {false && (
            <SidebarSection<NotebookSidebarSectionType>
              title="Links"
              type="links"
              isExpanded={expandedSections.has('links')}
              onToggle={() => onSectionToggle('links')}
              onSelect={onItemSelect}
                      selectedItem={selectedItem}
              items={[]}
                  >
              {(searchResults.showAll 
                ? [...links].sort((a, b) => new URL(a.url).hostname.localeCompare(new URL(b.url).hostname))
                : [...(searchResults.links || [])].sort((a, b) => new URL(a.url).hostname.localeCompare(new URL(b.url).hostname))
              ).map(link => (
                <button
                  key={link.id}
                  onClick={() => onItemSelect('links', link.id)}
                  onContextMenu={(e) => handleLinkContextMenu(e, link.id)}
                  className={`w-full text-left py-1 px-2 text-sm ${
                    selectedItem?.type === 'links' && selectedItem.id === link.id ? 'text-blue-600' : ''
                  } disabled:opacity-50 disabled:cursor-not-allowed`}
                  >
                  {new URL(link.url).hostname}
                </button>
              ))}
            </SidebarSection>
          )}
                                </div>
                            </div>

      {/* Upload Dialog */}
      {showUploadDialog && (
        <NotebookUploadDialog
          isOpen={showUploadDialog}
          onClose={() => setShowUploadDialog(false)}
          onUpload={handleUploadDialogSubmit}
          onCopyFromProject={handleCopyFromProjectSubmit}
          disabled={!canEdit}
          initialFolderId={uploadTargetFolderId}
          folderTree={effectiveFolderTree}
          projectFolderTree={projectFolderTree}
        />
      )}

      {/* Link Context Menu */}
      {showLinkContextMenu && canEdit && createPortal(
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

      {/* Create Conversation Dialog */}
      <CreateConversationDialog
        isOpen={showCreateConvoDialog}
        onClose={() => setShowCreateConvoDialog(false)}
        onCreate={async (title) => {
          const newConvo = await (createConversation as any)(title);
          // Refresh conversations list immediately after successful creation
          refreshConversations();
          // Clear any multi-selection to avoid visual confusion
          conversationSelect.clearSelection();
          if (newConvo && newConvo.id) {
            conversationNav.setFocusedId(newConvo.id); // Update visual focus indicator
            onItemSelect('conversations', newConvo.id);
          } else {
            // fallback: pick first after state update
            setTimeout(() => {
              const latest = conversations[0];
              if (latest) {
                conversationNav.setFocusedId(latest.id); // Update visual focus indicator
                onItemSelect('conversations', latest.id);
              }
            }, 0);
          }
        }}
        disabled={!canEdit}
      />

      {/* Conversation Context Menu */}
      {showConversationContextMenu && canEdit && createPortal(
        <div
          className="fixed bg-white shadow-lg rounded-lg py-1 z-[9999]"
          style={{ top: conversationContextMenuPos.y, left: conversationContextMenuPos.x }}
          onClick={(e) => e.stopPropagation()}
        >
          {conversationSelect.selectedCount <= 1 ? (
            <>
              <button
                className="block w-full text-left px-4 py-2 text-sm hover:bg-gray-100"
                onClick={handleCopyConversationInternal}
              >
                Copy
              </button>
              <button
                className="block w-full text-left px-4 py-2 text-sm hover:bg-gray-100"
                onClick={handleSaveConversationAsMarkdown}
              >
                Save Conversation
              </button>
              <button
                className="block w-full text-left px-4 py-2 text-sm hover:bg-gray-100"
                onClick={handleRenameConversationInternal}
              >
                Rename
              </button>
              <button
                className="block w-full text-left px-4 py-2 text-sm text-red-600 hover:bg-gray-100"
                onClick={handleDeleteConversationInternal}
              >
                Delete
              </button>
            </>
          ) : (
            <button
                className="block w-full text-left px-4 py-2 text-sm text-red-600 hover:bg-gray-100"
                onClick={handleDeleteConversationsBatch}
            >
                Delete {conversationSelect.selectedCount} Conversations
            </button>
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
        isOpen={showDeleteConversationConfirm}
        onClose={handleDeleteConversationCancel}
        onConfirm={handleDeleteConversationConfirm}
        title="Confirm Deletion"
        message={conversationSelect.selectedCount > 1 && !conversationToDelete
            ? `Are you sure you want to delete ${conversationSelect.selectedCount} conversations?`
            : "Are you sure you want to delete this conversation?"}
        confirmText="Delete"
        cancelText="Cancel"
      />
    </>
    );
}

export const NotebookSidebar = React.memo(NotebookSidebarInner);