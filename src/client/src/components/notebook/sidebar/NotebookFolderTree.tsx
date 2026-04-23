import React, { useState, useCallback, useEffect, useRef, useLayoutEffect, createContext, useContext, useMemo } from 'react';
import { createPortal } from 'react-dom';
import { FileIcon } from '../../common/FileIcon';
import { useParams } from 'react-router-dom';
import { notebookFilesApi } from '../../../services/notebookFiles';
import FullScreenEditor from '../../notebook/conversations/FullScreenEditor';
import { useMultiSelect, UseMultiSelectReturn } from '../../../hooks/useMultiSelect';
import { useListKeyboardNavigation } from '../../../hooks/useListKeyboardNavigation';
import { useSidebarKeyboardShortcuts } from '../../../hooks/useSidebarKeyboardShortcuts';
import { useLongPress } from '../../../hooks/useLongPress';

import { getContentTypeFromFileName, formatFileSize } from '../../../utils/fileUtils';
import { 
  NotebookFolderTreeDto, 
  NotebookFileDto, 
  CreateNotebookFolderDto,
  NotebookSidebarSelectedItem 
} from '../../../types/notebook';
import { ConfirmationDialog } from '../../common/ConfirmationDialog';
import { useToast } from '../../common/Toast';

// Context for sharing state down the tree
type TreeItem = 
    | { type: 'file'; data: NotebookFileDto; id: string }
    | { type: 'folder'; data: NotebookFolderTreeDto; id: string };

interface NotebookFolderTreeContextType {
    expandedIds: Set<string>;
    toggleExpansion: (id: string) => void;
    multiSelect: UseMultiSelectReturn<TreeItem>;
    focusedId: string | null;
    setFocusedId: (id: string | null) => void;
    handleKeyDown: (event: React.KeyboardEvent) => void;
    searchTerm?: string;
    renamingId?: string | null;
    setRenamingId: (id: string | null) => void;
    isActive: boolean;
    /** Ref that indicates focus should be programmatically applied. Check and reset to false after applying. */
    focusIntentRef: React.MutableRefObject<boolean>;
    /** Mobile detection for touch-friendly single-tap behavior */
    isMobile: boolean;
    /** Track locally-created empty folders that aren't yet in the server tree */
    localEmptyFolders: Set<string>;
    /** Add a locally-created empty folder path */
    addLocalEmptyFolder: (path: string) => void;
    /** Remove a locally-created empty folder (when deleted) */
    removeLocalEmptyFolder: (path: string) => void;
}

const NotebookFolderTreeContext = createContext<NotebookFolderTreeContextType | undefined>(undefined);

// Props for the NotebookFileItem component (extracted for useLongPress hook)
interface NotebookFileItemProps {
  file: NotebookFileDto;
  paddingLeft: number;
  canEdit: boolean;
  editingFileId: string | null;
  editingFileName: string;
  isIndexing?: boolean;
  onFileClick: (file: NotebookFileDto) => void;
  onFileContextMenu: (e: React.MouseEvent, file: NotebookFileDto) => void;
  onFileDragStart: (e: React.DragEvent, file: NotebookFileDto) => void;
  onFileDragEnd: (e: React.DragEvent) => void;
  onFileRenameKeyDown: (e: React.KeyboardEvent, fileId: string) => void;
  onSaveFileRename: (fileId: string) => void;
  onEditingFileNameChange: (value: string) => void;
}

/**
 * Individual file item component.
 * Extracted to properly use the useLongPress hook (hooks can't be called in loops).
 */
function NotebookFileItem({
  file,
  paddingLeft,
  canEdit,
  editingFileId,
  editingFileName,
  isIndexing = false,
  onFileClick,
  onFileContextMenu,
  onFileDragStart,
  onFileDragEnd,
  onFileRenameKeyDown,
  onSaveFileRename,
  onEditingFileNameChange,
}: NotebookFileItemProps) {
  const context = useContext(NotebookFolderTreeContext);
  const isSelected = context?.multiSelect.isSelected(file.id);
  const isFocused = context?.focusedId === file.id;
  const isMobile = context?.isMobile ?? false;

  // Long press handler for mobile context menu
  const longPressHandlers = useLongPress({
    onLongPress: (e) => {
      if (!isIndexing && isMobile) {
        // Select the file if not already selected
        if (context && !isSelected) {
          context.multiSelect.setSelection([file.id]);
        }
        // Create a synthetic mouse event for the context menu handler
        onFileContextMenu({ 
          clientX: e.clientX, 
          clientY: e.clientY,
          preventDefault: () => {},
          stopPropagation: () => {},
        } as React.MouseEvent, file);
      }
    },
    disabled: isIndexing || !isMobile,
    threshold: 500,
  });

  return (
    <div
      key={file.relativePath || file.id}
      draggable={canEdit && !isIndexing}
      tabIndex={isFocused ? 0 : -1}
      ref={el => {
        // Only apply focus if explicitly requested via keyboard navigation
        // This prevents focus-stealing when polling refreshes the tree
        if (el && isFocused && context?.focusIntentRef.current && context?.isActive) {
          el.focus();
          context.focusIntentRef.current = false;
        }
      }}
      onDragStart={(e) => onFileDragStart(e, file)}
      onDragEnd={onFileDragEnd}
      className={`group flex items-center py-1 px-2 text-sm cursor-pointer outline-none
        ${isSelected ? 'bg-blue-100' : 'hover:bg-gray-100'}
        ${isFocused ? 'ring-2 ring-blue-500 ring-inset' : ''}
        ${isIndexing ? 'opacity-50 cursor-not-allowed' : ''}
      `}
      style={{ paddingLeft: `${paddingLeft + 20}px` }}
      onClick={(e) => {
        if (!isIndexing) {
          // On mobile: single tap opens immediately
          // On desktop: single click selects, double-click opens
          if (isMobile) {
            onFileClick(file);
          } else {
            context?.multiSelect.handleClick(file.id, e);
            context?.setFocusedId(file.id);
          }
        }
      }}
      onDoubleClick={() => {
        if (!isIndexing && !isMobile) onFileClick(file);
      }}
      onKeyDown={(e) => context?.handleKeyDown(e)}
      onContextMenu={(e) => !isIndexing && onFileContextMenu(e, file)}
      {...longPressHandlers}
      data-tour-id="notebook.sidebar.file.item"
      data-file-id={file.id}
      title={isMobile ? "Tap to open, hold for options" : "Double-click or ENTER to open"}
    >
      <FileIcon contentType={getContentTypeFromFileName(file.fileName)} fileName={file.fileName} className="w-4 h-4 mr-2 flex-shrink-0" />
      {editingFileId === file.id ? (
        <input 
          type="text" 
          value={editingFileName} 
          onChange={(e) => onEditingFileNameChange(e.target.value)} 
          onBlur={() => onSaveFileRename(file.id)} 
          onKeyDown={(e) => onFileRenameKeyDown(e, file.id)} 
          className="flex-1 px-1 py-0 text-sm border border-blue-300 rounded focus:outline-none focus:ring-1 focus:ring-blue-500" 
          autoFocus 
        />
      ) : (
        <span className="truncate flex-grow" title={file.fileName}>{file.fileName}</span>
      )}
      <span className="text-xs text-gray-500 ml-2 flex-shrink-0">{formatFileSize(file.fileSize)}</span>
    </div>
  );
}

interface NotebookFolderTreeProps {
  tree: NotebookFolderTreeDto | null;
  notebookName?: string;
  selectedItem: NotebookSidebarSelectedItem | null;
  onItemSelect: (type: 'notebookFiles', id: string) => void;
  onCreateFolder?: (parentFolderPath: string | undefined, folderData: CreateNotebookFolderDto) => Promise<void>;
  onRenameFolder?: (folderPath: string, newName: string) => Promise<void>;
  onDeleteFolder?: (folderPath: string) => Promise<void>;
  onMoveFile?: (fileId: string, destinationFolderPath: string | undefined) => Promise<void>;
  onDeleteFile?: (fileId: string) => Promise<void>;
  onRenameFile?: (fileId: string, newName: string) => Promise<void>;
  onUploadToFolder?: (folderPath?: string) => void;
  onPublishToProject?: (files: NotebookFileDto[]) => void;
  onPreviewFile?: (file: NotebookFileDto) => void;
  canEdit: boolean;
  searchTerm?: string;
  // Home page functionality
  homePageFileId?: string;
  onSetHomePage?: (fileId: string | null) => void;
  
  // Coordination props
  activeSection?: string;
  onSectionActivate?: (section: string) => void;
}

interface NotebookFolderNodeProps {
  folder: NotebookFolderTreeDto;
  level: number;
  notebookName?: string;
  selectedItem: NotebookSidebarSelectedItem | null;
  onItemSelect: (type: 'notebookFiles', id: string) => void;
  onCreateFolder?: (parentFolderPath: string | undefined, folderData: CreateNotebookFolderDto) => Promise<void>;
  onRenameFolder?: (folderPath: string, newName: string) => Promise<void>;
  onDeleteFolder?: (folderPath: string) => Promise<void>;
  onMoveFile?: (fileId: string, destinationFolderPath: string | undefined) => Promise<void>;
  onDeleteFile?: (fileId: string) => Promise<void>;
  onRenameFile?: (fileId: string, newName: string) => Promise<void>;
  onUploadToFolder?: (folderPath?: string) => void;
  onPublishToProject?: (files: NotebookFileDto[]) => void;
  onPreviewFile?: (file: NotebookFileDto) => void;
  // Home page functionality
  homePageFileId?: string;
  onSetHomePage?: (fileId: string | null) => void;
  canEdit: boolean;
  registerOpenMenu: (closer: () => void) => void;
  searchTerm?: string;
}

const NotebookFolderNodeComponent: React.FC<NotebookFolderNodeProps> = ({
  folder,
  level,
  notebookName,
  selectedItem,
  onItemSelect,
  onCreateFolder,
  onRenameFolder,
  onDeleteFolder,
  onMoveFile,
  onDeleteFile,
  onRenameFile,
  onUploadToFolder,
  onPublishToProject,
  onPreviewFile,
  canEdit,
  registerOpenMenu,
  searchTerm,
  homePageFileId,
  onSetHomePage
}) => {
  const { showToast } = useToast();
  const context = useContext(NotebookFolderTreeContext);

  // Helper function to check if a folder has matching descendants
  const hasMatchingDescendants = (folderNode: NotebookFolderTreeDto, term: string): boolean => {
    const hasMatchingFiles = folderNode.files.some(file => 
      file.fileName.toLowerCase().includes(term)
    );
    if (hasMatchingFiles) return true;
    return folderNode.subFolders.some(subFolder => 
      subFolder.name.toLowerCase().includes(term) || 
      hasMatchingDescendants(subFolder, term)
    );
  };

  // Filter function for search
  const filterBySearch = (searchTerm: string) => {
    if (!searchTerm?.trim()) {
      return {
        filteredSubFolders: folder.subFolders,
        filteredFiles: folder.files,
        shouldShow: true
      };
    }
    const term = searchTerm.toLowerCase().trim();
    const filteredSubFolders = folder.subFolders.filter(subFolder => {
      const nameMatches = subFolder.name.toLowerCase().includes(term);
      const hasMatchingChildren = hasMatchingDescendants(subFolder, term);
      return nameMatches || hasMatchingChildren;
    });
    const filteredFiles = folder.files.filter(file => 
      file.fileName.toLowerCase().includes(term)
    );
    const shouldShow = filteredSubFolders.length > 0 || filteredFiles.length > 0 || folder.name.toLowerCase().includes(term);
    return { filteredSubFolders, filteredFiles, shouldShow };
  };

  const { filteredSubFolders, filteredFiles, shouldShow } = filterBySearch(searchTerm || '');
  
  if (searchTerm && !shouldShow) {
    return null;
  }

  const isExpanded = context?.expandedIds.has(folder.relativePath || 'ROOT') ?? true;
  const toggleExpand = () => context?.toggleExpansion(folder.relativePath || 'ROOT');

  const [isEditing, setIsEditing] = useState(false);
  const [editName, setEditName] = useState(folder.name);
  const [showContextMenu, setShowContextMenu] = useState(false);
  const [showFileContextMenu, setShowFileContextMenu] = useState(false);
  const [contextMenuPosition, setContextMenuPosition] = useState({ x: 0, y: 0 });
  const [selectedContextFile, setSelectedContextFile] = useState<NotebookFileDto | null>(null);
  const [isCreatingSubfolder, setIsCreatingSubfolder] = useState(false);
  const [newSubfolderName, setNewSubfolderName] = useState('');
  const [dragOverFolder, setDragOverFolder] = useState<string | null>(null);
  const [editingFileId, setEditingFileId] = useState<string | null>(null);
  const [editingFileName, setEditingFileName] = useState('');
  const [isMdEditorOpen, setIsMdEditorOpen] = useState(false);
  const [mdEditorContent, setMdEditorContent] = useState('');
  const [mdEditorLoading, setMdEditorLoading] = useState(false);
  const [mdEditorError, setMdEditorError] = useState<string | undefined>(undefined);
  const [isCreatingMd, setIsCreatingMd] = useState(false);
  const [creatingMdFolderPath, setCreatingMdFolderPath] = useState<string>('');
  const [creatingMdFileName, setCreatingMdFileName] = useState<string>('');

  const { projectId, notebookId } = useParams<{ projectId: string; notebookId: string }>();

  const menuRef = useRef<HTMLDivElement | null>(null);

  useLayoutEffect(() => {
        if ((showContextMenu || showFileContextMenu) && menuRef.current) {
            const rect = menuRef.current.getBoundingClientRect();
            const margin = 8;
            let newX = contextMenuPosition.x;
            let newY = contextMenuPosition.y;
            if (rect.right + margin > window.innerWidth) {
                newX = Math.max(margin, newX - (rect.right + margin - window.innerWidth));
            }
            if (rect.bottom + margin > window.innerHeight) {
                newY = Math.max(margin, newY - (rect.bottom + margin - window.innerHeight));
            }
            if (newX !== contextMenuPosition.x || newY !== contextMenuPosition.y) {
                setContextMenuPosition({ x: newX, y: newY });
            }
        }
    }, [showContextMenu, showFileContextMenu, contextMenuPosition]);

  const [showDeleteFolderConfirm, setShowDeleteFolderConfirm] = useState(false);
  const [showDeleteFileConfirm, setShowDeleteFileConfirm] = useState(false);
  const [folderToDelete, setFolderToDelete] = useState<string | null>(null);
  const [fileToDelete, setFileToDelete] = useState<NotebookFileDto | null>(null);

  const displaySubFolders = filteredSubFolders;
  const displayFiles = filteredFiles;
  const hasChildren = displaySubFolders.length > 0 || displayFiles.length > 0;
  const paddingLeft = level === 0 ? 0 : level * 20 + 8;

  const handleToggleExpand = useCallback((e: React.MouseEvent) => {
    e.stopPropagation();
    toggleExpand();
  }, [toggleExpand]);

  const handleFileClick = useCallback((file: NotebookFileDto) => {
    onPreviewFile?.(file);
  }, [onPreviewFile]);

  const handleContextMenu = useCallback((e: React.MouseEvent) => {
    if (!canEdit) return;
    window.dispatchEvent(new Event('close-context-menus'));
    registerOpenMenu(() => {
      setShowContextMenu(false);
      setShowFileContextMenu(false);
    });
    e.preventDefault();
    e.stopPropagation();
    setContextMenuPosition({ x: e.clientX, y: e.clientY });
    setShowContextMenu(true);
  }, [canEdit, registerOpenMenu]);

  const handleStartRename = useCallback(() => {
    setIsEditing(true);
    setEditName(folder.name);
    setShowContextMenu(false);
  }, [folder.name]);

  const handleSaveRename = useCallback(async () => {
    if (editName.trim() && editName !== folder.name && folder.relativePath) {
      try {
        await onRenameFolder?.(folder.relativePath, editName.trim());
        setIsEditing(false);
        try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
      } catch (error) {
        console.error('Failed to rename folder:', error);
        showToast({ type: 'error', title: 'Failed to rename folder', message: 'Please try again.' });
      }
    } else {
      setIsEditing(false);
      setEditName(folder.name);
    }
  }, [editName, folder.name, folder.relativePath, onRenameFolder]);

  const handleCancelRename = useCallback(() => {
    setIsEditing(false);
    setEditName(folder.name);
  }, [folder.name]);

  // Handle renaming trigger from keyboard shortcut
  useEffect(() => {
      if (context?.renamingId) {
          // Check if this folder needs renaming
          if (context.renamingId === folder.relativePath) {
              handleStartRename();
              context.setRenamingId(null);
              return;
          }
          // Check if a file in this folder needs renaming
          const fileToRename = folder.files.find(f => f.id === context.renamingId);
          if (fileToRename) {
              setEditingFileId(context.renamingId);
              setEditingFileName(fileToRename.fileName);
              setShowFileContextMenu(false); // Ensure menu is closed
              context.setRenamingId(null);
          }
      }
  }, [context?.renamingId, folder.relativePath, folder.files, handleStartRename, context]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    // Stop propagation for all keys to prevent tree navigation while renaming
    e.stopPropagation();
    
    if (e.key === 'Enter') handleSaveRename();
    else if (e.key === 'Escape') handleCancelRename();
  }, [handleSaveRename, handleCancelRename]);

  const handleDeleteFolder = useCallback(async () => {
    if (!folder.relativePath || !onDeleteFolder) return;
    setFolderToDelete(folder.relativePath);
    setShowDeleteFolderConfirm(true);
    setShowContextMenu(false);
  }, [folder.relativePath, folder.name, onDeleteFolder]);

  const handleDeleteFolderConfirm = async () => {
    if (folderToDelete && onDeleteFolder) {
      try {
        await onDeleteFolder(folderToDelete);
        // Remove from local empty folders if it was tracked there
        context?.removeLocalEmptyFolder(folderToDelete);
        try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
      } catch (error) {
        console.error('Failed to delete folder:', error);
        showToast({ type: 'error', title: 'Failed to delete folder', message: 'Please try again.' });
      }
    }
    setShowDeleteFolderConfirm(false);
    setFolderToDelete(null);
  };

  const handleDeleteFolderCancel = () => {
    setShowDeleteFolderConfirm(false);
    setFolderToDelete(null);
  };

  const handleCreateSubfolder = useCallback(async () => {
    if (!newSubfolderName.trim() || !onCreateFolder) return;
    try {
      const folderName = newSubfolderName.trim();
      await onCreateFolder(folder.relativePath || undefined, { name: folderName });
      
      // Add to local empty folders so it appears immediately
      // The server created the physical directory, but won't return it in tree until it has files
      const newFolderPath = folder.relativePath ? `${folder.relativePath}/${folderName}` : folderName;
      
      if (context?.addLocalEmptyFolder) {
        context.addLocalEmptyFolder(newFolderPath);
      }
      
      setIsCreatingSubfolder(false);
      setNewSubfolderName('');
      // Don't refresh - the empty folder won't be in server response, rely on local state
    } catch (error) {
      console.error('Failed to create subfolder:', error);
      showToast({ type: 'error', title: 'Failed to create subfolder', message: 'Please try again.' });
    }
  }, [newSubfolderName, folder.relativePath, onCreateFolder, context]);

  const getUniqueMarkdownFileName = useCallback((proposedName: string) => {
    const ensureMdExt = (name: string) => name.toLowerCase().endsWith('.md') ? name : `${name}.md`;
    const sanitized = ensureMdExt(proposedName.trim() || 'Untitled.md');
    const existing = new Set(folder.files.map(f => f.fileName.toLowerCase()));
    if (!existing.has(sanitized.toLowerCase())) return sanitized;
    const dot = sanitized.lastIndexOf('.');
    const base = dot > 0 ? sanitized.substring(0, dot) : sanitized;
    const ext = dot > 0 ? sanitized.substring(dot) : '';
    let i = 2;
    let candidate = `${base} (${i})${ext}`;
    while (existing.has(candidate.toLowerCase())) {
      i += 1;
      candidate = `${base} (${i})${ext}`;
    }
    return candidate;
  }, [folder.files]);

  const handleNewMarkdownFile = useCallback(async () => {
    if (!canEdit || !projectId || !notebookId) return;
    setShowContextMenu(false);
    setMdEditorError(undefined);
    setMdEditorLoading(true);
    try {
      const finalName = getUniqueMarkdownFileName('New Markdown.md');
      const targetFolderPath = folder.relativePath || '';
      const title = finalName.replace(/\.md$/i, '');
      const initialContent = `# ${title}\n\n`;
      const file = new File([initialContent], finalName, { type: 'text/markdown' });
      const result = await notebookFilesApi.uploadFiles(projectId, notebookId, [file], targetFolderPath, false);
      const created = result?.find?.(f => f.fileName === finalName) || null;
      if (created) setSelectedContextFile(created);
      try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
      setMdEditorContent(initialContent);
      setIsMdEditorOpen(true);
      setIsCreatingMd(false);
      setCreatingMdFolderPath('');
      setCreatingMdFileName('');
    } catch (err) {
      setMdEditorError('Failed to create markdown file.');
    } finally {
      setMdEditorLoading(false);
    }
  }, [canEdit, projectId, notebookId, folder.relativePath, getUniqueMarkdownFileName]);

  const handleFileContextMenu = useCallback((e: React.MouseEvent, file: NotebookFileDto) => {
    window.dispatchEvent(new Event('close-context-menus'));
    registerOpenMenu(() => {
      setShowContextMenu(false);
      setShowFileContextMenu(false);
    });
    e.preventDefault();
    e.stopPropagation();
    
    // Auto-select
    if (context) {
        if (!context.multiSelect.isSelected(file.id)) {
            context.multiSelect.setSelection([file.id]);
        }
    }
    
    setSelectedContextFile(file);
    setContextMenuPosition({ x: e.clientX, y: e.clientY });
    setShowFileContextMenu(true);
  }, [registerOpenMenu, context]);

  const isMarkdownFile = (file?: NotebookFileDto | null) => {
    if (!file) return false;
    const ct = getContentTypeFromFileName(file.fileName);
    return file.fileName.toLowerCase().endsWith('.md') || ct === 'text/markdown' || ct === 'text/x-markdown';
  };

  const openMarkdownEditor = useCallback(async () => {
    if (!selectedContextFile || !projectId || !notebookId) return;
    if (!isMarkdownFile(selectedContextFile)) return;
    setShowFileContextMenu(false);
    setMdEditorError(undefined);
    setMdEditorLoading(true);
    try {
      const { blob } = await notebookFilesApi.getNotebookFileMarkdownContent(projectId, notebookId, selectedContextFile.id);
      const text = await blob.text();
      setMdEditorContent(text);
      setIsMdEditorOpen(true);
    } catch (err) {
      try {
        const blob = await notebookFilesApi.getNotebookFileContent(projectId, notebookId, selectedContextFile.relativePath, selectedContextFile.fileHash);
        const text = await blob.text();
        setMdEditorContent(text);
        setIsMdEditorOpen(true);
      } catch (e) {
        setMdEditorError('Failed to load markdown content.');
      }
    } finally {
      setMdEditorLoading(false);
    }
  }, [selectedContextFile, projectId, notebookId]);

  const closeMarkdownEditor = useCallback((_current?: string) => {
    setIsMdEditorOpen(false);
    setMdEditorError(undefined);
    setIsCreatingMd(false);
    setCreatingMdFolderPath('');
    setCreatingMdFileName('');
  }, []);

  const saveMarkdownEditor = useCallback(async (newContent: string) => {
    if (!projectId || !notebookId) return;
    setMdEditorLoading(true);
    setMdEditorError(undefined);
    try {
      let folderPath = '';
      let fileName = '';
      if (isCreatingMd) {
        folderPath = creatingMdFolderPath;
        fileName = creatingMdFileName;
      } else if (selectedContextFile) {
        const relPath = selectedContextFile.relativePath || selectedContextFile.fileName;
        folderPath = relPath.includes('/') ? relPath.substring(0, relPath.lastIndexOf('/')) : '';
        fileName = selectedContextFile.fileName;
      } else {
        throw new Error('No target file specified');
      }
      const markdownFile = new File([newContent], fileName, { type: 'text/markdown' });
      await notebookFilesApi.uploadFiles(projectId, notebookId, [markdownFile], folderPath, false);
      try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
      setIsMdEditorOpen(false);
      setIsCreatingMd(false);
      setCreatingMdFolderPath('');
      setCreatingMdFileName('');
    } catch (err) {
      setMdEditorError('Failed to save markdown file.');
    } finally {
      setMdEditorLoading(false);
    }
  }, [selectedContextFile, isCreatingMd, creatingMdFolderPath, creatingMdFileName, projectId, notebookId]);

  const handleDeleteFile = useCallback(async () => {
    if (!selectedContextFile || !onDeleteFile) {
      setShowFileContextMenu(false);
      return;
    }
    setFileToDelete(selectedContextFile);
    setShowDeleteFileConfirm(true);
    setShowFileContextMenu(false);
  }, [selectedContextFile, onDeleteFile]);

  const handleDeleteFileConfirm = async () => {
    if (fileToDelete && onDeleteFile) {
      try {
        await onDeleteFile(fileToDelete.id);
        try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
      } catch (error) {
        console.error('Failed to delete file:', error);
        showToast({ type: 'error', title: 'Failed to delete file', message: 'Please try again.' });
      }
    }
    setShowDeleteFileConfirm(false);
    setFileToDelete(null);
  };

  const handleDeleteFileCancel = () => {
    setShowDeleteFileConfirm(false);
    setFileToDelete(null);
  };

  const handleToggleHomePage = useCallback(() => {
    if (!onSetHomePage || !selectedContextFile) return;
    const isHome = homePageFileId === selectedContextFile.id;
    onSetHomePage(isHome ? null : selectedContextFile.id);
    setShowFileContextMenu(false);
  }, [onSetHomePage, selectedContextFile, homePageFileId]);

  const handleDownloadFile = useCallback(async () => {
    if (!selectedContextFile || !projectId || !notebookId) {
      setShowFileContextMenu(false);
      return;
    }
    try {
      const blob = await notebookFilesApi.getNotebookFileContent(projectId, notebookId, selectedContextFile.relativePath, selectedContextFile.fileHash);
      const url = window.URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = selectedContextFile.fileName;
      anchor.style.display = 'none';
      document.body.appendChild(anchor);
      anchor.click();
      document.body.removeChild(anchor);
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (err) {
      console.error('Failed to download file:', err);
      showToast({ type: 'error', title: 'Failed to download file', message: 'Please try again.' });
    } finally {
      setShowFileContextMenu(false);
    }
  }, [selectedContextFile, projectId, notebookId]);

  const handleDownloadFiles = useCallback(async (files: NotebookFileDto[]) => {
    if (!projectId || !notebookId || files.length === 0) {
      setShowFileContextMenu(false);
      return;
    }
    
    let successCount = 0;
    let errorCount = 0;
    
    for (const file of files) {
      try {
        const blob = await notebookFilesApi.getNotebookFileContent(projectId, notebookId, file.relativePath, file.fileHash);
        const url = window.URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = file.fileName;
        anchor.style.display = 'none';
        document.body.appendChild(anchor);
        anchor.click();
        document.body.removeChild(anchor);
        setTimeout(() => URL.revokeObjectURL(url), 1000);
        successCount++;
        // Small delay between downloads to prevent browser issues
        await new Promise(resolve => setTimeout(resolve, 100));
      } catch (err) {
        console.error(`Failed to download file ${file.fileName}:`, err);
        errorCount++;
      }
    }
    
    if (errorCount > 0) {
      showToast({ 
        type: 'error', 
        title: 'Some downloads failed', 
        message: `Downloaded ${successCount} of ${files.length} files.` 
      });
    }
    
    setShowFileContextMenu(false);
  }, [projectId, notebookId, showToast]);

  const handleStartFileRename = useCallback(() => {
    if (selectedContextFile) {
      setEditingFileId(selectedContextFile.id);
      setEditingFileName(selectedContextFile.fileName);
      setShowFileContextMenu(false);
    }
  }, [selectedContextFile]);

  const handleSaveFileRename = useCallback(async (fileId: string) => {
    const file = folder.files.find(f => f.id === fileId);
    if (!file || !editingFileName.trim() || editingFileName === file.fileName || !onRenameFile) {
      setEditingFileId(null);
      setEditingFileName('');
      return;
    }
    try {
      await onRenameFile(fileId, editingFileName.trim());
      setEditingFileId(null);
      setEditingFileName('');
      try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
    } catch (error) {
      console.error('Failed to rename file:', error);
      showToast({ type: 'error', title: 'Failed to rename file', message: 'Please try again.' });
    }
  }, [folder.files, editingFileName, onRenameFile]);

  const handleCancelFileRename = useCallback(() => {
    setEditingFileId(null);
    setEditingFileName('');
  }, []);

  const handleFileRenameKeyDown = useCallback((e: React.KeyboardEvent, fileId: string) => {
    // Stop propagation for all keys to prevent tree navigation while renaming
    e.stopPropagation();
    
    if (e.key === 'Enter') {
      e.preventDefault();
      handleSaveFileRename(fileId);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      handleCancelFileRename();
    }
  }, [handleSaveFileRename, handleCancelFileRename]);

  const handleDragOver = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setDragOverFolder(folder.relativePath || '');
  }, [folder.relativePath]);

  const handleDragLeave = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    if (!e.currentTarget.contains(e.relatedTarget as Node)) {
      setDragOverFolder(null);
    }
  }, []);

  const handleDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setDragOverFolder(null);
    const fileId = e.dataTransfer.getData('text/plain');
    const originFolderId = e.dataTransfer.getData('application/x-origin-folder');
    if (fileId && originFolderId !== folder.relativePath && canEdit) {
      const isRootFolder = !folder.relativePath || folder.relativePath === '' || level === 0;
      const destinationFolderId = isRootFolder ? undefined : folder.relativePath;
      onMoveFile?.(fileId, destinationFolderId);
      try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
    }
  }, [folder.relativePath, canEdit, onMoveFile, level]);

  const handleFileDragStart = useCallback((e: React.DragEvent, file: NotebookFileDto) => {
    if (!e.dataTransfer) return;
    e.dataTransfer.setData('text/plain', file.id);
    e.dataTransfer.setData('application/x-origin-folder', folder.relativePath || '');
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('application/x-notebook-file-id', file.id);
    e.dataTransfer.setData('application/x-notebook-file-name', file.fileName);
    (e.currentTarget as HTMLElement).style.opacity = '0.5';
  }, [folder.relativePath]);

  const handleFileDragEnd = useCallback((e: React.DragEvent) => {
    (e.currentTarget as HTMLElement).style.opacity = '1';
  }, []);

  const isRootFolder = !folder.relativePath || folder.relativePath === '' || level === 0;

  return (
    <>
      <div className="folder-tree-item">
        <div
          className={`group flex items-center py-0.5 px-2 text-sm cursor-pointer hover:bg-gray-100 ${
            selectedItem?.type === 'notebookFiles' && selectedItem.id === (folder.relativePath || '')
              ? 'bg-blue-100 text-blue-600'
              : ''
          }  ${
            dragOverFolder === (folder.relativePath || '') ? 'ring-2 ring-blue-400 bg-blue-50' : ''
          }`}
          style={{ paddingLeft: `${paddingLeft}px` }}
          onClick={(e) => {
             // Single click on folder row toggles expansion (common file explorer UX)
             toggleExpand();
             if (folder.relativePath && context) {
                 context.multiSelect.handleClick(folder.relativePath, e);
                 context.setFocusedId(folder.relativePath);
             }
          }}
          onDoubleClick={(e) => {
              // Double click also toggles (for users who double-click by habit)
              // Note: Removed handleFolderClick() - folders should NOT navigate away
              handleToggleExpand(e);
          }}
          onContextMenu={handleContextMenu}
          onDragOver={handleDragOver}
          onDragLeave={handleDragLeave}
          onDrop={handleDrop}
          data-tour-id={isRootFolder ? 'notebook.folder.root' : undefined}
        >
          {hasChildren && (
            <button onClick={handleToggleExpand} className="mr-1 text-gray-500 hover:text-gray-700">
              {isExpanded ? (
                <svg className="w-3 h-3" fill="currentColor" viewBox="0 0 20 20"><path fillRule="evenodd" d="M5.293 7.293a1 1 0 011.414 0L10 10.586l3.293-3.293a1 1 0 111.414 1.414l-4 4a1 1 0 01-1.414 0l-4-4a1 1 0 010-1.414z" clipRule="evenodd" /></svg>
              ) : (
                <svg className="w-3 h-3" fill="currentColor" viewBox="0 0 20 20"><path fillRule="evenodd" d="M7.293 14.707a1 1 0 010-1.414L10.586 10 7.293 6.707a1 1 0 011.414-1.414l4 4a1 1 0 010 1.414l-4 4a1 1 0 01-1.414 0z" clipRule="evenodd" /></svg>
              )}
            </button>
          )}
          {!hasChildren && <div className="w-3 mr-1" />}
          <svg className="w-4 h-4 mr-2 text-yellow-500" fill="currentColor" viewBox="0 0 20 20"><path d="M2 6a2 2 0 012-2h5l2 2h5a2 2 0 012 2v6a2 2 0 01-2 2H4a2 2 0 01-2-2V6z" /></svg>
          {isEditing ? (
            <input type="text" value={editName} onChange={(e) => setEditName(e.target.value)} onBlur={handleSaveRename} onKeyDown={handleKeyDown} className="flex-1 px-1 py-0 text-sm border border-blue-300 rounded focus:outline-none focus:ring-1 focus:ring-blue-500" autoFocus />
          ) : (
            <span className="flex-1 truncate" title={level === 0 ? notebookName || folder.name : folder.name}>{level === 0 ? notebookName || folder.name : folder.name}</span>
          )}
          {canEdit && (
            <div className="opacity-0 group-hover:opacity-100 transition-opacity flex space-x-1">
              {onCreateFolder && <button onClick={(e) => { e.stopPropagation(); setIsCreatingSubfolder(true); }} className="p-1 text-gray-400 hover:text-blue-600" title="Create subfolder"><svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24"><line x1="12" y1="5" x2="12" y2="19"></line><line x1="5" y1="12" x2="19" y2="12"></line></svg></button>}
              {onUploadToFolder && <button onClick={(e) => { e.stopPropagation(); onUploadToFolder(folder.relativePath); }} className="p-1 text-gray-400 hover:text-green-600" title="Upload to this folder"><svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path><polyline points="17 8 12 3 7 8"></polyline><line x1="12" y1="3" x2="12" y2="15"></line></svg></button>}
            </div>
          )}
        </div>

        {isExpanded && (hasChildren || isCreatingSubfolder) && (
          <div className="folder-children">
            {isCreatingSubfolder && (
              <div className="flex items-center py-0.5 px-2 text-sm" style={{ paddingLeft: `${(level + 1) * 20 + 8}px` }}>
                <div className="w-3 mr-1" />{/* Spacer to align with expand button */}
                <svg className="w-4 h-4 mr-2 text-yellow-500" fill="currentColor" viewBox="0 0 20 20"><path d="M2 6a2 2 0 012-2h5l2 2h5a2 2 0 012 2v6a2 2 0 01-2 2H4a2 2 0 01-2-2V6z" /></svg>
                <input type="text" value={newSubfolderName} onChange={(e) => setNewSubfolderName(e.target.value)} onBlur={handleCreateSubfolder} onKeyDown={(e) => { e.stopPropagation(); if (e.key === 'Enter') handleCreateSubfolder(); else if (e.key === 'Escape') { setIsCreatingSubfolder(false); setNewSubfolderName(''); } }} placeholder="Folder name..." className="flex-1 px-1 py-0 text-sm border border-blue-300 rounded focus:outline-none focus:ring-1 focus:ring-blue-500" autoFocus />
              </div>
            )}
            {[...displaySubFolders].sort((a, b) => a.name.localeCompare(b.name)).map(subFolder => (
              <NotebookFolderNode
                key={subFolder.relativePath || subFolder.name}
                folder={subFolder}
                level={level + 1}
                notebookName={notebookName}
                selectedItem={selectedItem}
                onItemSelect={onItemSelect}
                onCreateFolder={onCreateFolder}
                onRenameFolder={onRenameFolder}
                onDeleteFolder={onDeleteFolder}
                onMoveFile={onMoveFile}
                onDeleteFile={onDeleteFile}
                onRenameFile={onRenameFile}
                onUploadToFolder={onUploadToFolder}
                onPublishToProject={onPublishToProject}
                onPreviewFile={onPreviewFile}
                canEdit={canEdit}
                registerOpenMenu={registerOpenMenu}
                searchTerm={searchTerm}
                homePageFileId={homePageFileId}
                onSetHomePage={onSetHomePage}
              />
            ))}
            {[...displayFiles].sort((a, b) => a.fileName.localeCompare(b.fileName)).map(file => (
              <NotebookFileItem
                key={file.relativePath || file.id}
                file={file}
                paddingLeft={paddingLeft}
                canEdit={canEdit}
                editingFileId={editingFileId}
                editingFileName={editingFileName}
                isIndexing={false}
                onFileClick={handleFileClick}
                onFileContextMenu={handleFileContextMenu}
                onFileDragStart={handleFileDragStart}
                onFileDragEnd={handleFileDragEnd}
                onFileRenameKeyDown={handleFileRenameKeyDown}
                onSaveFileRename={handleSaveFileRename}
                onEditingFileNameChange={setEditingFileName}
              />
            ))}
          </div>
        )}
      </div>

      {showContextMenu && canEdit && createPortal(
        <div ref={menuRef} className="fixed bg-white shadow-lg rounded-lg py-1 z-[9999]" style={{ top: contextMenuPosition.y, left: contextMenuPosition.x }} onClick={(e) => e.stopPropagation()} onFocus={(e) => e.stopPropagation()} data-tour-id="notebook.folder.context-menu">
          {onRenameFolder && <button className="block w-full text-left px-4 py-2 text-sm hover:bg-gray-100" onClick={handleStartRename}>Rename</button>}
          <button className="block w-full text-left px-4 py-2 text-sm hover:bg-gray-100" onClick={handleNewMarkdownFile}>New Markdown File</button>
          {onCreateFolder && <button className="block w-full text-left px-4 py-2 text-sm hover:bg-gray-100" onClick={() => { setIsCreatingSubfolder(true); setShowContextMenu(false); }}>Create Subfolder</button>}
          {onUploadToFolder && <button className="block w-full text-left px-4 py-2 text-sm hover:bg-gray-100" onClick={() => { setShowContextMenu(false); onUploadToFolder(folder.relativePath); }}>Upload Files</button>}
          {onDeleteFolder && <button className={`block w-full text-left px-4 py-2 text-sm ${hasChildren ? 'text-gray-400 cursor-not-allowed' : 'text-red-600 hover:bg-gray-100'}`} onClick={hasChildren ? undefined : handleDeleteFolder} disabled={hasChildren} title={hasChildren ? 'Cannot delete folder with contents' : 'Delete folder'}>Delete</button>}
        </div>,
        document.body
      )}

      {showFileContextMenu && createPortal(
        <div ref={menuRef} className="fixed bg-white border border-gray-300 rounded-md shadow-lg py-1 z-[9999] min-w-[160px]" style={{ top: contextMenuPosition.y, left: contextMenuPosition.x }} onClick={(e) => e.stopPropagation()} onFocus={(e) => e.stopPropagation()} data-tour-id="notebook.file.context-menu">
          {context?.multiSelect.selectedCount && context.multiSelect.selectedCount > 1 ? (
              (() => {
                  const selectedItems = context.multiSelect.getSelectedItems();
                  const selectedFiles = selectedItems.filter(item => item.type === 'file');
                  const fileCount = selectedFiles.length;
                  return (
                  <>
                      {canEdit && onPublishToProject && fileCount > 0 && (
                          <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={() => {
                              const filesToPublish = selectedFiles.map(item => item.data as NotebookFileDto);
                              onPublishToProject(filesToPublish);
                              setShowFileContextMenu(false);
                          }}>
                              Publish {fileCount} File{fileCount > 1 ? 's' : ''} to Project
                          </button>
                      )}
                      {fileCount > 0 && (
                          <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={() => {
                              const filesToDownload = selectedFiles.map(item => item.data as NotebookFileDto);
                              handleDownloadFiles(filesToDownload);
                          }}>
                              Download {fileCount} File{fileCount > 1 ? 's' : ''}
                          </button>
                      )}
                      {canEdit && onDeleteFile && (
                      <button className="block w-full text-left px-4 py-1.5 text-sm text-red-600 hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={async () => {
                          if (!context) return;
                          const selectedItems = context.multiSelect.getSelectedItems();
                          const fileIds = selectedItems.filter(i => i.type === 'file').map(i => i.id);
                          const folderIds = selectedItems.filter(i => i.type === 'folder').map(i => i.id);
                          
                          // TODO: Handle folder delete if supported here or via parent
                          // Current props: onDeleteFile (single), onDeleteFolder (single)
                          
                          for (const id of fileIds) {
                              await onDeleteFile(id);
                          }
                          
                          if (folderIds.length > 0 && onDeleteFolder) {
                              for (const path of folderIds) await onDeleteFolder(path);
                          }

                          try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
                          context.multiSelect.clearSelection();
                          setShowFileContextMenu(false);
                      }}>
                          Delete {context.multiSelect.selectedCount} Items
                      </button>
                  )}
              </>
                  );
              })()
          ) : (
              <>
                  {canEdit && isMarkdownFile(selectedContextFile) && <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={openMarkdownEditor}>Edit</button>}
                  {onPreviewFile && <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={() => { if (selectedContextFile) onPreviewFile(selectedContextFile); setShowFileContextMenu(false); }}>Preview</button>}
                  {canEdit && onPublishToProject && <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={() => { if (selectedContextFile) onPublishToProject([selectedContextFile]); setShowFileContextMenu(false); }}>Publish to Project</button>}
                  <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={handleDownloadFile}>Download</button>
                  {canEdit && onRenameFile && <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={handleStartFileRename}>Rename</button>}
                  {canEdit && onSetHomePage && selectedContextFile && <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={handleToggleHomePage}>{homePageFileId === selectedContextFile.id ? 'Clear as Home Page' : 'Set as Notebook Home Page'}</button>}
                  {canEdit && <button className="block w-full text-left px-4 py-1.5 text-sm text-red-600 hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={handleDeleteFile}>Delete</button>}
              </>
          )}
        </div>,
        document.body
      )}

      {isMdEditorOpen && selectedContextFile && createPortal(
        <FullScreenEditor content={mdEditorContent} onSave={saveMarkdownEditor} onCancel={closeMarkdownEditor} mode={'edit'} title={`Edit File: ${selectedContextFile.fileName}`} placeholder={selectedContextFile.fileName} submitLabel={'Save'} isLoading={mdEditorLoading} error={mdEditorError} projectId={projectId} notebookId={notebookId} basePath={selectedContextFile.relativePath.includes('/') ? selectedContextFile.relativePath.substring(0, selectedContextFile.relativePath.lastIndexOf('/')) : undefined} />,
        document.body
      )}

      <ConfirmationDialog isOpen={showDeleteFolderConfirm} onClose={handleDeleteFolderCancel} onConfirm={handleDeleteFolderConfirm} title="Confirm Delete Folder" message={`Are you sure you want to delete the folder "${folder.name}" and all its contents? This action cannot be undone.`} confirmText="Delete" cancelText="Cancel" />
      <ConfirmationDialog isOpen={showDeleteFileConfirm} onClose={handleDeleteFileCancel} onConfirm={handleDeleteFileConfirm} title="Confirm Delete File" message={`Are you sure you want to delete "${selectedContextFile?.fileName}"? This action cannot be undone.`} confirmText="Delete" cancelText="Cancel" />
    </>
  );
};

const areNodesEqual = (prev: NotebookFolderNodeProps, next: NotebookFolderNodeProps) => {
  // Context-based memoization might require stricter checks, but since context changes trigger re-render of consumers,
  // we can rely on React to handle it. However, if parent props change, we re-render.
  // The props list is long.
  return (
    prev.folder === next.folder &&
    prev.level === next.level &&
    prev.notebookName === next.notebookName &&
    prev.selectedItem?.type === next.selectedItem?.type &&
    prev.selectedItem?.id === next.selectedItem?.id &&
    prev.canEdit === next.canEdit &&
    prev.searchTerm === next.searchTerm &&
    prev.homePageFileId === next.homePageFileId
    // Function props usually stable or we accept re-render.
  );
};

const NotebookFolderNode = React.memo(NotebookFolderNodeComponent, areNodesEqual);

const NotebookFolderTreeComponent: React.FC<NotebookFolderTreeProps> = ({
  tree,
  notebookName,
  selectedItem,
  onItemSelect,
  onCreateFolder,
  onRenameFolder,
  onDeleteFolder,
  onMoveFile,
  onDeleteFile,
  onRenameFile,
  onUploadToFolder,
  onPublishToProject,
  onPreviewFile,
  canEdit,
  searchTerm,
  homePageFileId,
  onSetHomePage,
  activeSection,
  onSectionActivate
}) => {
  // Helper to collect all folder paths recursively for initial expansion
  const collectAllFolderPaths = useCallback((node: NotebookFolderTreeDto): string[] => {
    const paths: string[] = [node.relativePath || 'ROOT'];
    for (const subFolder of node.subFolders) {
      paths.push(...collectAllFolderPaths(subFolder));
    }
    return paths;
  }, []);

  // Shared state - initialize with all folders expanded on first load only
  // We use a ref to track if this is the initial mount
  const isInitialMount = useRef(true);
  const [expandedIds, setExpandedIds] = useState<Set<string>>(() => {
    if (tree) {
      return new Set(collectAllFolderPaths(tree));
    }
    return new Set(['ROOT']);
  });
  
  // Only expand all folders on initial mount when tree first becomes available
  // After that, preserve user's collapse/expand state across refreshes
  useEffect(() => {
    if (tree && isInitialMount.current) {
      const allPaths = collectAllFolderPaths(tree);
      setExpandedIds(new Set(allPaths));
      isInitialMount.current = false;
    }
  }, [tree, collectAllFolderPaths]);

  const toggleExpansion = useCallback((id: string) => {
      setExpandedIds(prev => {
          const next = new Set(prev);
          if (next.has(id)) next.delete(id);
          else next.add(id);
          return next;
      });
  }, []);

  // Mobile detection for touch-friendly interactions
  const [isMobile, setIsMobile] = useState(false);
  useEffect(() => {
    const checkMobile = () => setIsMobile(window.innerWidth < 768);
    checkMobile();
    window.addEventListener('resize', checkMobile);
    return () => window.removeEventListener('resize', checkMobile);
  }, []);

  // Track locally-created empty folders until they appear in server tree
  const [localEmptyFolders, setLocalEmptyFolders] = useState<Set<string>>(new Set());
  
  const addLocalEmptyFolder = useCallback((path: string) => {
    setLocalEmptyFolders(prev => new Set([...prev, path]));
    // Also expand the new folder and its parents
    setExpandedIds(prev => {
      const next = new Set(prev);
      next.add(path);
      // Expand parent folders too
      const parts = path.split('/');
      let current = '';
      for (let i = 0; i < parts.length - 1; i++) {
        current = current ? `${current}/${parts[i]}` : parts[i];
        next.add(current);
      }
      return next;
    });
  }, []);

  const removeLocalEmptyFolder = useCallback((path: string) => {
    setLocalEmptyFolders(prev => {
      if (!prev.has(path)) return prev;
      const next = new Set(prev);
      next.delete(path);
      // Also remove any child folders
      for (const p of prev) {
        if (p.startsWith(path + '/')) {
          next.delete(p);
        }
      }
      return next;
    });
  }, []);

  // Prune local folders that now exist in server tree (they have files now)
  useEffect(() => {
    if (!tree || localEmptyFolders.size === 0) return;
    
    const serverFolderPaths = new Set<string>();
    const collectServerFolders = (node: NotebookFolderTreeDto) => {
      if (node.relativePath) serverFolderPaths.add(node.relativePath);
      node.subFolders.forEach(collectServerFolders);
    };
    collectServerFolders(tree);
    
    // Remove local folders that now exist in server tree (they now have files)
    // Keep local folders that are NOT in the server tree
    setLocalEmptyFolders(prev => {
      const next = new Set<string>();
      for (const path of prev) {
        if (!serverFolderPaths.has(path)) {
          next.add(path); // Keep - not in server tree (still empty)
        }
      }
      return next.size !== prev.size ? next : prev;
    });
  }, [tree, localEmptyFolders.size]);

  // Helper to merge local empty folders into a tree node
  const mergeLocalFolders = useCallback((node: NotebookFolderTreeDto): NotebookFolderTreeDto => {
    if (localEmptyFolders.size === 0) return node;
    
    // Find local folders that belong in this node
    // Root node has relativePath: "" (empty string)
    const isRoot = !node.relativePath || node.relativePath === '';
    const nodePrefix = isRoot ? '' : node.relativePath + '/';
    const localFoldersForNode: string[] = [];
    
    for (const path of localEmptyFolders) {
      // Check if this local folder is a direct child of current node
      if (isRoot) {
        // Root level - direct children have no slashes
        if (!path.includes('/')) {
          localFoldersForNode.push(path);
        }
      } else if (path.startsWith(nodePrefix)) {
        const remainder = path.slice(nodePrefix.length);
        if (!remainder.includes('/')) {
          localFoldersForNode.push(path);
        }
      }
    }
    
    // Recursively process existing subfolders
    const mergedSubFolders = node.subFolders.map(sub => mergeLocalFolders(sub));
    
    // Add local empty folders that don't already exist
    const existingPaths = new Set(mergedSubFolders.map(f => f.relativePath));
    for (const localPath of localFoldersForNode) {
      if (!existingPaths.has(localPath)) {
        const name = localPath.includes('/') ? localPath.split('/').pop()! : localPath;
        mergedSubFolders.push({
          name,
          relativePath: localPath,
          subFolders: [],
          files: []
        });
      }
    }
    
    return {
      ...node,
      subFolders: mergedSubFolders
    };
  }, [localEmptyFolders]);

  // Merge local folders into tree for rendering
  const effectiveTree = useMemo(() => {
    if (!tree) return null;
    if (localEmptyFolders.size === 0) return tree;
    return mergeLocalFolders(tree);
  }, [tree, localEmptyFolders, mergeLocalFolders]);

  const getVisibleItems = useCallback((node: NotebookFolderTreeDto): TreeItem[] => {
      let results: TreeItem[] = [];
      const hasMatchingDescendants = (n: NotebookFolderTreeDto, term: string): boolean => {
           const lower = term.toLowerCase();
           if (n.files.some(f => f.fileName.toLowerCase().includes(lower))) return true;
           return n.subFolders.some(sub => sub.name.toLowerCase().includes(lower) || hasMatchingDescendants(sub, term));
      };
      
      const processNode = (n: NotebookFolderTreeDto) => {
          const showMe = !searchTerm?.trim() || n.name.toLowerCase().includes(searchTerm.toLowerCase()) || hasMatchingDescendants(n, searchTerm);
          if (!showMe) return;

          // Add folder itself if not root
          if (n.relativePath && n.relativePath !== '' && n.relativePath !== 'ROOT') {
               results.push({ type: 'folder', data: n, id: n.relativePath });
          }

          if (expandedIds.has(n.relativePath || 'ROOT')) {
              [...n.subFolders]
                  .sort((a, b) => a.name.localeCompare(b.name))
                  .forEach(sub => processNode(sub));
              [...n.files]
                  .sort((a, b) => a.fileName.localeCompare(b.fileName))
                  .forEach(f => {
                      if (!searchTerm?.trim() || f.fileName.toLowerCase().includes(searchTerm.toLowerCase())) {
                          results.push({ type: 'file', data: f, id: f.id });
                      }
                  });
          }
      };
      
      if (node) {
          // Process children of root?
          // If node is root, we usually start inside.
          if (expandedIds.has(node.relativePath || 'ROOT')) {
             [...node.subFolders]
                  .sort((a, b) => a.name.localeCompare(b.name))
                  .forEach(sub => processNode(sub));
             [...node.files]
                  .sort((a, b) => a.fileName.localeCompare(b.fileName))
                  .forEach(f => {
                      if (!searchTerm?.trim() || f.fileName.toLowerCase().includes(searchTerm.toLowerCase())) {
                          results.push({ type: 'file', data: f, id: f.id });
                      }
                  });
          }
      }
      return results;
  }, [expandedIds, searchTerm]);

  const visibleItems = useMemo(() => effectiveTree ? getVisibleItems(effectiveTree) : [], [effectiveTree, getVisibleItems]);

  const multiSelect = useMultiSelect<TreeItem>({
      items: visibleItems,
      getId: item => item.id,
      onSelectionChange: (ids) => {
          if (ids.size > 0 && onSectionActivate) {
              onSectionActivate('notebookFiles');
          }
      }
  });

  const listNav = useListKeyboardNavigation<TreeItem>({
      items: visibleItems,
      getId: item => item.id,
      onNavigate: (id) => {
          const item = visibleItems.find(i => i.id === id);
          if (item?.type === 'file') {
              if (onPreviewFile) onPreviewFile(item.data);
          } else if (item?.type === 'folder') {
              // Only toggle expansion for folders - don't navigate away
              toggleExpansion(id);
          }
      },
      onSelectionChange: (id, shift) => {
          multiSelect.handleClick(id, { shiftKey: shift, ctrlKey: false } as any);
          onSectionActivate?.('notebookFiles');
      }
  });

  useEffect(() => {
      if (activeSection !== 'notebookFiles') {
          multiSelect.clearSelection();
          listNav.setFocusedId(null);
      }
  }, [activeSection, multiSelect, listNav]);

  const [showBatchDeleteConfirm, setShowBatchDeleteConfirm] = useState(false);

  const performBatchDelete = useCallback(async () => {
        if (multiSelect.selectedCount === 0) return;
        
        const selectedItems = multiSelect.getSelectedItems();
        const fileIds = selectedItems.filter(i => i.type === 'file').map(i => i.id);
        const folderIds = selectedItems.filter(i => i.type === 'folder').map(i => i.id);

        if (fileIds.length > 0 && onDeleteFile) {
            for (const id of fileIds) await onDeleteFile(id);
        }

        if (folderIds.length > 0 && onDeleteFolder) {
            for (const path of folderIds) await onDeleteFolder(path);
        }
        
        try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
        multiSelect.clearSelection();
        setShowBatchDeleteConfirm(false);
    }, [multiSelect, onDeleteFile, onDeleteFolder]);

  const handleDeleteSelected = useCallback(async () => {
        if (multiSelect.selectedCount === 0) return;
        setShowBatchDeleteConfirm(true);
    }, [multiSelect]);

    const [renamingId, setRenamingId] = useState<string | null>(null);

    useSidebarKeyboardShortcuts({
        isActive: activeSection === 'notebookFiles',
        onDelete: handleDeleteSelected,
        onSelectAll: () => multiSelect.selectAll(),
        onClearSelection: () => multiSelect.clearSelection(),
        onRename: () => {
             if (multiSelect.selectedCount === 1) {
                 const item = multiSelect.getSelectedItems()[0];
                 if ((item.type === 'file' && onRenameFile) || (item.type === 'folder' && onRenameFolder)) {
                     setRenamingId(item.id);
                 }
             }
        }
    });

  const contextValue: NotebookFolderTreeContextType = {
      expandedIds,
      toggleExpansion,
      multiSelect,
      focusedId: listNav.focusedId,
      setFocusedId: listNav.setFocusedId,
      handleKeyDown: listNav.handleKeyDown,
      searchTerm,
      renamingId,
      setRenamingId,
      isActive: activeSection === 'notebookFiles',
      focusIntentRef: listNav.focusIntentRef,
      isMobile,
      localEmptyFolders,
      addLocalEmptyFolder,
      removeLocalEmptyFolder
  };

  const closeCurrentMenuRef = useRef<(() => void) | null>(null);
  const registerOpenMenu = useCallback((closer: () => void) => {
    if (closeCurrentMenuRef.current) closeCurrentMenuRef.current();
    closeCurrentMenuRef.current = closer;
  }, []);

  useEffect(() => {
    const handleClose = () => {
        if (closeCurrentMenuRef.current) {
            closeCurrentMenuRef.current();
            closeCurrentMenuRef.current = null;
        }
    };
    
    window.addEventListener('click', handleClose);
    window.addEventListener('focusin', handleClose);
    window.addEventListener('close-context-menus', handleClose);
    
    return () => {
        window.removeEventListener('click', handleClose);
        window.removeEventListener('focusin', handleClose);
        window.removeEventListener('close-context-menus', handleClose);
    };
  }, []);

  // Helper to find a file by relative path and get its parent folder paths
  const findFileAndParents = useCallback((node: NotebookFolderTreeDto, relativePath: string, parentPaths: string[] = []): { file: NotebookFileDto; parentPaths: string[] } | null => {
    // Check files in this folder
    for (const file of node.files) {
      if (file.relativePath === relativePath || file.fileName === relativePath) {
        return { file, parentPaths: [...parentPaths, node.relativePath || 'ROOT'] };
      }
    }
    
    // Check subfolders recursively
    for (const subFolder of node.subFolders) {
      const found = findFileAndParents(subFolder, relativePath, [...parentPaths, node.relativePath || 'ROOT']);
      if (found) return found;
    }
    
    return null;
  }, []);

  // Listen for external file selection requests (from turn file pills)
  useEffect(() => {
    const handleSelectFile = (event: Event) => {
      const customEvent = event as CustomEvent<{ relativePath: string }>;
      const { relativePath } = customEvent.detail;
      
      if (!tree || !relativePath) return;
      
      // Find the file and its parent folders
      const result = findFileAndParents(tree, relativePath);
      if (!result) {
        console.warn(`File not found for path: ${relativePath}`);
        return;
      }
      
      const { file, parentPaths } = result;
      
      // Expand all parent folders to make the file visible
      setExpandedIds(prev => {
        const next = new Set(prev);
        for (const path of parentPaths) {
          next.add(path);
        }
        return next;
      });
      
      // Select the file
      multiSelect.setSelection([file.id]);
      
      // Set focus on the file
      listNav.setFocusedId(file.id);
      
      // Activate the notebookFiles section
      onSectionActivate?.('notebookFiles');
      
      // Scroll the file into view after a brief delay for DOM update
      setTimeout(() => {
        const fileElement = document.querySelector(`[data-file-id="${file.id}"]`);
        if (fileElement) {
          fileElement.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
      }, 100);
    };
    
    window.addEventListener('select-notebook-file', handleSelectFile);
    
    return () => {
      window.removeEventListener('select-notebook-file', handleSelectFile);
    };
  }, [tree, findFileAndParents, multiSelect, listNav, onSectionActivate]);

  if (!effectiveTree) {
    return (
      <div className="p-4 text-center text-gray-500">
        <p className="text-sm">No files available</p>
      </div>
    );
  }

  return (
    <NotebookFolderTreeContext.Provider value={contextValue}>
    <div className="folder-tree overflow-auto" onContextMenuCapture={(e)=>e.preventDefault()}>
      <NotebookFolderNode
        folder={effectiveTree}
        level={0}
        notebookName={notebookName}
        selectedItem={selectedItem}
        onItemSelect={onItemSelect}
        onCreateFolder={onCreateFolder}
        onRenameFolder={onRenameFolder}
        onDeleteFolder={onDeleteFolder}
        onMoveFile={onMoveFile}
        onDeleteFile={onDeleteFile}
        onRenameFile={onRenameFile}
        onUploadToFolder={onUploadToFolder}
        onPublishToProject={onPublishToProject}
        onPreviewFile={onPreviewFile}
        canEdit={canEdit}
        registerOpenMenu={registerOpenMenu}
        searchTerm={searchTerm}
        homePageFileId={homePageFileId}
        onSetHomePage={onSetHomePage}
      />
    </div>
    <ConfirmationDialog 
        isOpen={showBatchDeleteConfirm} 
        onClose={() => setShowBatchDeleteConfirm(false)} 
        onConfirm={performBatchDelete} 
        title="Confirm Deletion" 
        message={`Are you sure you want to delete ${multiSelect.selectedCount} item${multiSelect.selectedCount > 1 ? 's' : ''}? This action cannot be undone.`} 
        confirmText="Delete" 
        cancelText="Cancel" 
    />
    </NotebookFolderTreeContext.Provider>
  );
}; 

export const NotebookFolderTree = React.memo(NotebookFolderTreeComponent);
