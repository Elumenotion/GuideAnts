import React, { useState, useCallback, useEffect, useRef, useLayoutEffect, createContext, useContext, useMemo } from 'react';
import { FolderTreeDto, CreateFolderDto, ProjectFolderDto, ProjectContentFile } from '../../../types/project';
import { createPortal } from 'react-dom';
import { FileIcon } from '../../common/FileIcon';
import { api } from '../../../services/api';
import FullScreenEditor from '../../notebook/conversations/FullScreenEditor';
import { useMultiSelect, UseMultiSelectReturn } from '../../../hooks/useMultiSelect';
import { useListKeyboardNavigation } from '../../../hooks/useListKeyboardNavigation';
import { useSidebarKeyboardShortcuts } from '../../../hooks/useSidebarKeyboardShortcuts';
import { useLongPress } from '../../../hooks/useLongPress';

import { getContentTypeFromFileName, formatFileSize } from '../../../utils/fileUtils';
import { ConfirmationDialog } from '../../common/ConfirmationDialog';
import { useToast } from '../../common/Toast';

// Context for sharing state down the tree
type TreeItem = 
    | { type: 'file'; data: ProjectContentFile; id: string }
    | { type: 'folder'; data: FolderTreeDto; id: string };

interface FolderTreeContextType {
    expandedIds: Set<string>;
    toggleExpansion: (id: string) => void;
    multiSelect: UseMultiSelectReturn<TreeItem>;
    focusedId: string | null;
    setFocusedId: (id: string | null) => void;
    // We pass handleKeyDown to be attached to items
    handleKeyDown: (event: React.KeyboardEvent) => void;
    searchTerm?: string;
    renamingId: string | null;
    setRenamingId: (id: string | null) => void;
    isActive: boolean;
    /** Ref that indicates focus should be programmatically applied. Check and reset to false after applying. */
    focusIntentRef: React.MutableRefObject<boolean>;
    /** Mobile detection for touch-friendly single-tap behavior */
    isMobile: boolean;
}

const FolderTreeContext = createContext<FolderTreeContextType | undefined>(undefined);

// Props for the ProjectFileItem component (extracted for useLongPress hook)
interface ProjectFileItemProps {
  file: ProjectContentFile;
  paddingLeft: number;
  disabled: boolean;
  selectedFileId?: string;
  editingFileId: string | null;
  editingFileName: string;
  onFileSelect?: (fileId: string) => void;
  onFileContextMenu: (e: React.MouseEvent, file: ProjectContentFile) => void;
  onFileDragStart: (e: React.DragEvent, file: ProjectContentFile) => void;
  onFileDragEnd: (e: React.DragEvent) => void;
  onFileRenameKeyDown: (e: React.KeyboardEvent, fileId: string) => void;
  onSaveFileRename: (fileId: string) => void;
  onEditingFileNameChange: (value: string) => void;
}

/**
 * Individual file item component for project files.
 * Extracted to properly use the useLongPress hook (hooks can't be called in loops).
 */
function ProjectFileItem({
  file,
  paddingLeft,
  disabled,
  selectedFileId,
  editingFileId,
  editingFileName,
  onFileSelect,
  onFileContextMenu,
  onFileDragStart,
  onFileDragEnd,
  onFileRenameKeyDown,
  onSaveFileRename,
  onEditingFileNameChange,
}: ProjectFileItemProps) {
  const context = useContext(FolderTreeContext);
  const isSelected = context?.multiSelect.isSelected(file.id);
  const isFocused = context?.focusedId === file.id;
  const isOpen = selectedFileId === file.id;
  const isMobile = context?.isMobile ?? false;

  // Long press handler for mobile context menu
  const longPressHandlers = useLongPress({
    onLongPress: (e) => {
      if (!disabled && isMobile) {
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
    disabled: disabled || !isMobile,
    threshold: 500,
  });

  return (
    <div
      key={file.id}
      draggable={!disabled}
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
      onContextMenu={(e) => onFileContextMenu(e, file)}
      className={`
        flex items-center px-2 py-1 cursor-pointer text-sm outline-none
        ${isSelected ? 'bg-blue-100' : 'hover:bg-gray-100'}
        ${isFocused ? 'ring-2 ring-blue-500 ring-inset' : ''}
        ${isOpen ? 'text-blue-600' : ''}
      `}
      style={{ paddingLeft: `${paddingLeft + 20}px` }}
      onClick={(e) => {
        if (!disabled) {
          // On mobile: single tap opens immediately
          // On desktop: single click selects, double-click opens
          if (isMobile) {
            onFileSelect?.(file.id);
          } else {
            context?.multiSelect.handleClick(file.id, e);
            context?.setFocusedId(file.id);
          }
        }
      }}
      onDoubleClick={() => {
        if (!disabled && !isMobile) onFileSelect?.(file.id);
      }}
      onKeyDown={(e) => {
        if (disabled) return;
        context?.handleKeyDown(e);
      }}
      {...longPressHandlers}
      data-tour-id="sidebar.file.item"
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
        <span className="truncate flex-grow">{file.fileName}</span>
      )}
      <span className="text-xs text-gray-500 ml-2 flex-shrink-0">{formatFileSize(file.fileSize || 0)}</span>
    </div>
  );
}

interface FolderTreeProps {
    folderTree: FolderTreeDto | null;
    folders: ProjectFolderDto[];
    projectId: string;
    selectedFileId?: string;
    selectedFolderId?: string;
    onFileSelect?: (fileId: string) => void;
    onFolderSelect?: (folderId: string) => void;
    onCreateFolder?: (parentFolderId: string | undefined, folderData: CreateFolderDto) => Promise<void>;
    onRenameFolder?: (folderId: string, newName: string) => Promise<void>;
    onDeleteFolder?: (folderId: string) => Promise<void>;
    onMoveFile?: (fileId: string, destinationFolderId: string | undefined) => Promise<void>;
    onUploadToFolder?: (folderId?: string) => void;
    onDeleteFile?: (fileId: string) => Promise<void>;
    onDeleteFiles?: (fileIds: string[]) => Promise<void>;
    onRenameFile?: (fileId: string, newName: string) => Promise<void>;
    onCreateNotebookFromFile?: (fileId: string, fileName: string) => void;
    onCreateNotebookFromFiles?: (files: { id: string; fileName: string }[]) => void;
    onDownloadFiles?: (files: { id: string; fileName: string }[]) => void;
    resolveProjectFilePath?: (relativePath: string) => string | undefined;
    homePageContentFileId?: string;
    onSetHomePage?: (fileId: string | null) => Promise<void>;
    disabled?: boolean;
    searchTerm?: string;
    onFileContentChanged?: (fileId: string, newVersion: number) => void;
    
    // Coordination props
    activeSection?: string;
    onSectionActivate?: (section: string) => void;
}

interface FolderNodeProps {
    folder: FolderTreeDto;
    level: number;
    // Props passed via recursion
    folders: ProjectFolderDto[];
    projectId: string;
    selectedFileId?: string;
    selectedFolderId?: string;
    onFileSelect?: (fileId: string) => void;
    onFolderSelect?: (folderId: string) => void;
    onCreateFolder?: (parentFolderId: string | undefined, folderData: CreateFolderDto) => Promise<void>;
    onRenameFolder?: (folderId: string, newName: string) => Promise<void>;
    onDeleteFolder?: (folderId: string) => Promise<void>;
    onMoveFile?: (fileId: string, destinationFolderId: string | undefined) => Promise<void>;
    onUploadToFolder?: (folderId?: string) => void;
    onDeleteFile?: (fileId: string) => Promise<void>;
    onDeleteFiles?: (fileIds: string[]) => Promise<void>;
    onRenameFile?: (fileId: string, newName: string) => Promise<void>;
    onCreateNotebookFromFile?: (fileId: string, fileName: string) => void;
    onCreateNotebookFromFiles?: (files: { id: string; fileName: string }[]) => void;
    resolveProjectFilePath?: (relativePath: string) => string | undefined;
    homePageContentFileId?: string;
    onSetHomePage?: (fileId: string | null) => Promise<void>;
    disabled?: boolean;
    registerOpenMenu: (closer: () => void) => void;
    searchTerm?: string;
    onFileContentChanged?: (fileId: string, newVersion: number) => void;
}

const FolderNode: React.FC<FolderNodeProps> = ({
    folder,
    level,
    folders,
    projectId,
    selectedFileId,
    selectedFolderId,
    onFileSelect,
    onFolderSelect,
    onCreateFolder,
    onRenameFolder,
    onDeleteFolder,
    onMoveFile,
    onUploadToFolder,
    onDeleteFile,
    onDeleteFiles,
    onRenameFile,
    onCreateNotebookFromFile,
    onCreateNotebookFromFiles,
    resolveProjectFilePath,
    homePageContentFileId,
    onSetHomePage,
    disabled = false,
    registerOpenMenu,
    searchTerm,
    onFileContentChanged,
}) => {
    const { showToast } = useToast();
    const context = useContext(FolderTreeContext);
    
    // Helper function to check if a folder has matching descendants
    const hasMatchingDescendants = (folderNode: FolderTreeDto, term: string): boolean => {
        const hasMatchingFiles = folderNode.files.some(file => 
            file.fileName.toLowerCase().includes(term)
        );
        if (hasMatchingFiles) return true;
        return folderNode.subFolders.some(subFolder => 
            subFolder.name.toLowerCase().includes(term) || 
            hasMatchingDescendants(subFolder, term)
        );
    };

    // Local files state to reflect optimistic additions
    const [localFiles, setLocalFiles] = useState<ProjectContentFile[]>(folder.files);
    useEffect(() => {
        setLocalFiles(folder.files);
    }, [folder.files]);

    const sourceFilesForSearch = localFiles;
    const scopedFilterBySearch = (term: string) => {
        if (!term?.trim()) {
            return {
                filteredSubFolders: folder.subFolders,
                filteredFiles: sourceFilesForSearch,
                shouldShow: true
            };
        }
        const searchLower = term.toLowerCase().trim();
        const filteredSubFolders = folder.subFolders.filter(sub =>
            sub.name.toLowerCase().includes(searchLower) || hasMatchingDescendants(sub, searchLower)
        );
        const filteredFiles = sourceFilesForSearch.filter(f => f.fileName.toLowerCase().includes(searchLower));
        const shouldShow = filteredSubFolders.length > 0 || filteredFiles.length > 0 || folder.name.toLowerCase().includes(searchLower);
        return { filteredSubFolders, filteredFiles, shouldShow };
    };

    const { filteredSubFolders, filteredFiles, shouldShow } = scopedFilterBySearch(searchTerm || '');
    
    // Use expanded state from context or fallback
    const isExpanded = context?.expandedIds.has(folder.id || 'ROOT') ?? true;
    const toggleExpand = () => context?.toggleExpansion(folder.id || 'ROOT');

    const [isEditing, setIsEditing] = useState(false);
    const [editName, setEditName] = useState(folder.name);
    const [showContextMenu, setShowContextMenu] = useState(false);
    const [showFileContextMenu, setShowFileContextMenu] = useState(false);
    const [contextMenuPosition, setContextMenuPosition] = useState({ x: 0, y: 0 });
    const [selectedContextFile, setSelectedContextFile] = useState<ProjectContentFile | null>(null);
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
    const [creatingMdFolderId, setCreatingMdFolderId] = useState<string | undefined>(undefined);
    const [creatingMdFileName, setCreatingMdFileName] = useState<string>('');

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
    const [showBatchDeleteConfirm, setShowBatchDeleteConfirm] = useState(false);
    const [folderToDelete, setFolderToDelete] = useState<string | null>(null);
    const [fileToDelete, setFileToDelete] = useState<ProjectContentFile | null>(null);

    const hasChildren = filteredSubFolders.length > 0 || filteredFiles.length > 0;
    const paddingLeft = level === 0 ? 0 : level * 20 + 8;

    const handleFolderClick = useCallback(() => {
        if (!disabled && folder.id) {
            onFolderSelect?.(folder.id);
        }
    }, [disabled, folder.id, onFolderSelect]);

    const handleToggleExpand = useCallback((e: React.MouseEvent) => {
        e.stopPropagation();
        toggleExpand();
    }, [toggleExpand]);

    const handleContextMenu = useCallback((e: React.MouseEvent) => {
        if (disabled) return;
        window.dispatchEvent(new Event('close-context-menus'));
        registerOpenMenu(() => {
            setShowContextMenu(false);
            setShowFileContextMenu(false);
        });
        e.preventDefault();
        e.stopPropagation();
        setContextMenuPosition({ x: e.clientX, y: e.clientY });
        setShowContextMenu(true);
    }, [disabled, registerOpenMenu]);

    const handleStartRename = useCallback(() => {
        setIsEditing(true);
        setEditName(folder.name);
        setShowContextMenu(false);
    }, [folder.name]);

    useEffect(() => {
        if (context?.renamingId) {
            // Check if this folder needs renaming
            if (context.renamingId === folder.id) {
                handleStartRename();
                context.setRenamingId(null);
                return;
            }
            // Check if a file in this folder needs renaming
            const fileToRename = localFiles.find(f => f.id === context.renamingId);
            if (fileToRename) {
                setEditingFileId(context.renamingId);
                setEditingFileName(fileToRename.fileName);
                setShowFileContextMenu(false); // Ensure menu is closed
                context.setRenamingId(null);
            }
        }
    }, [context?.renamingId, folder.id, localFiles, handleStartRename, context]);

    const handleSaveRename = useCallback(async () => {
        if (editName.trim() && editName !== folder.name && folder.id) {
            try {
                await onRenameFolder?.(folder.id, editName.trim());
                setIsEditing(false);
            } catch (error) {
                console.error('Failed to rename folder:', error);
                showToast({ type: 'error', title: 'Failed to rename folder', message: 'Please try again.' });
            }
        } else {
            setIsEditing(false);
            setEditName(folder.name);
        }
    }, [editName, folder.name, folder.id, onRenameFolder]);

    const handleCancelRename = useCallback(() => {
        setIsEditing(false);
        setEditName(folder.name);
    }, [folder.name]);

    const handleKeyDownFolderRename = useCallback((e: React.KeyboardEvent) => {
        if (e.key === 'Enter') handleSaveRename();
        else if (e.key === 'Escape') handleCancelRename();
    }, [handleSaveRename, handleCancelRename]);

    const handleCreateSubfolder = useCallback(() => {
        setShowContextMenu(false);
        setIsCreatingSubfolder(true);
        setNewSubfolderName('');
        // Ensure expanded if not already
        if (!isExpanded) toggleExpand();
    }, [isExpanded, toggleExpand]);

    const handleSaveNewSubfolder = useCallback(async () => {
        if (!newSubfolderName.trim()) {
            setIsCreatingSubfolder(false);
            return;
        }
        let parentId: string | undefined;
        if (level === 0) parentId = undefined;
        else if (folder.id) parentId = folder.id;
        else {
            const match = folders.find(f => f.relativePath === folder.relativePath);
            parentId = match?.id;
        }
        try {
            await onCreateFolder?.(parentId, { name: newSubfolderName.trim() });
        } finally {
            setIsCreatingSubfolder(false);
        }
    }, [newSubfolderName, folder, onCreateFolder, level, folders]);

    const handleKeyDownNewSubfolder = useCallback((e: React.KeyboardEvent) => {
        if (e.key === 'Enter') handleSaveNewSubfolder();
        else if (e.key === 'Escape') setIsCreatingSubfolder(false);
    }, [handleSaveNewSubfolder]);

    const isMarkdownFileName = (fileName: string, contentType?: string) => {
        const lower = fileName.toLowerCase();
        return lower.endsWith('.md') || lower.endsWith('.markdown') || contentType === 'text/markdown' || contentType === 'text/x-markdown';
    };

    const getUniqueMarkdownFileName = useCallback((proposedName: string) => {
        const ensureMdExt = (name: string) => name.toLowerCase().endsWith('.md') ? name : `${name}.md`;
        const sanitized = ensureMdExt(proposedName.trim() || 'Untitled.md');
        const existing = new Set(localFiles.map(f => f.fileName.toLowerCase()));
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
    }, [localFiles]);

    const handleDeleteFolder = useCallback(async () => {
        setShowContextMenu(false);
        if (folder.id) {
            setFolderToDelete(folder.id);
            setShowDeleteFolderConfirm(true);
        }
    }, [folder.id]);

    const handleDeleteFolderConfirm = async () => {
        if (folderToDelete && onDeleteFolder) {
            try {
                await onDeleteFolder(folderToDelete);
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

    const handleDragOver = useCallback((e: React.DragEvent) => {
        e.preventDefault();
        e.stopPropagation();
        setDragOverFolder(folder.id || '');
    }, [folder.id]);

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
        if (fileId && originFolderId !== folder.id && !disabled) {
            const isRootFolder = !folder.id || folder.relativePath === '' || level === 0;
            const resolvedId = folder.id || folders.find(f => f.relativePath === folder.relativePath)?.id;
            const destinationFolderId = isRootFolder ? undefined : resolvedId;
            onMoveFile?.(fileId, destinationFolderId);
        }
    }, [folder.id, folder.relativePath, folders, disabled, onMoveFile, level]);

    const handleFileDragStart = useCallback((e: React.DragEvent, file: ProjectContentFile) => {
        e.dataTransfer.setData('text/plain', file.id);
        e.dataTransfer.setData('application/x-origin-folder', folder.id || '');
        e.dataTransfer.effectAllowed = 'move';
        (e.currentTarget as HTMLElement).style.opacity = '0.5';
    }, [folder.id]);

    const handleFileDragEnd = useCallback((e: React.DragEvent) => {
        (e.currentTarget as HTMLElement).style.opacity = '1';
    }, []);

    const handleFileContextMenu = useCallback((e: React.MouseEvent, file: ProjectContentFile) => {
        if (disabled) return;
        window.dispatchEvent(new Event('close-context-menus'));
        registerOpenMenu(() => {
            setShowContextMenu(false);
            setShowFileContextMenu(false);
        });
        e.preventDefault();
        e.stopPropagation();
        
        // Auto-select if not already selected (for single selection case), or if not part of multi-select
        if (context) {
            const isSelected = context.multiSelect.isSelected(file.id);
            if (!isSelected) {
                context.multiSelect.setSelection([file.id]);
            }
        }
        
        setContextMenuPosition({ x: e.clientX, y: e.clientY });
        setSelectedContextFile(file);
        setShowFileContextMenu(true);
        setShowContextMenu(false);
    }, [disabled, registerOpenMenu, context]);

    const handleDeleteFileWrapper = useCallback(async () => {
        if (selectedContextFile && onDeleteFile) {
            setFileToDelete(selectedContextFile);
            setShowDeleteFileConfirm(true);
            setShowFileContextMenu(false);
        }
    }, [selectedContextFile, onDeleteFile]);

    const handleDeleteFileConfirm = async () => {
        if (fileToDelete && onDeleteFile) {
            try {
                await onDeleteFile(fileToDelete.id);
            } catch (error) {
                console.error('Failed to delete file:', error);
            }
        }
        setShowDeleteFileConfirm(false);
        setFileToDelete(null);
    };

    const handleDeleteFileCancel = () => {
        setShowDeleteFileConfirm(false);
        setFileToDelete(null);
    };

    const handleBatchDeleteConfirm = async () => {
        if (!context) {
            setShowBatchDeleteConfirm(false);
            return;
        }
        const selectedItems = context.multiSelect.getSelectedItems();
        const fileIds = selectedItems.filter(i => i.type === 'file').map(i => i.id);
        const folderIds = selectedItems.filter(i => i.type === 'folder').map(i => i.id);
        
        if (fileIds.length > 0) {
            if (onDeleteFiles) {
                await onDeleteFiles(fileIds);
            } else if (onDeleteFile) {
                for (const id of fileIds) await onDeleteFile(id);
            }
        }
        
        if (folderIds.length > 0 && onDeleteFolder) {
            for (const id of folderIds) await onDeleteFolder(id);
        }

        context.multiSelect.clearSelection();
        setShowBatchDeleteConfirm(false);
    };

    const handleBatchDeleteCancel = () => {
        setShowBatchDeleteConfirm(false);
    };

    const openMarkdownEditor = useCallback(async () => {
        if (!selectedContextFile) return;
        if (!isMarkdownFileName(selectedContextFile.fileName, selectedContextFile.contentType)) return;
        setShowFileContextMenu(false);
        setMdEditorError(undefined);
        setMdEditorLoading(true);
        try {
            const result = await api.projects.getContentFileContent(projectId, selectedContextFile.id, selectedContextFile.latestVersion);
            const text = await result.blob.text();
            setMdEditorContent(text);
            setIsMdEditorOpen(true);
        } catch (err) {
            setMdEditorError('Failed to load markdown content.');
        } finally {
            setMdEditorLoading(false);
        }
    }, [selectedContextFile, projectId]);

    const handleNewMarkdownFile = useCallback(async () => {
        setShowContextMenu(false);
        setMdEditorError(undefined);
        setMdEditorLoading(true);
        try {
            const finalName = getUniqueMarkdownFileName('New Markdown.md');
            const title = finalName.replace(/\.md$/i, '');
            const initialContent = `# ${title}\n\n`;
            const file = new File([initialContent], finalName, { type: 'text/markdown' });
            const targetFolderId = level === 0 ? undefined : folder.id;
            const results = await api.projects.uploadFiles(projectId, [file], targetFolderId);
            const created = Array.isArray(results) && results.length > 0 ? results[0] as any : null;
            const newFile: ProjectContentFile = created ? {
                id: created.id,
                fileName: created.fileName || finalName,
                path: created.relativePath || finalName,
                relativePath: created.relativePath || finalName,
                contentType: created.contentType || 'text/markdown',
                index: created.index ?? false,
                documentId: created.documentId || '',
                created: created.created || new Date().toISOString(),
                fileSize: created.fileSize ?? 0,
                folderId: created.folderId || targetFolderId,
                folderPath: created.folderPath,
                latestVersion: (created as any)?.latestVersion,
            } : {
                id: `${Date.now()}`,
                fileName: finalName,
                path: finalName,
                relativePath: finalName,
                contentType: 'text/markdown',
                index: false,
                documentId: '',
                created: new Date().toISOString(),
                fileSize: 0,
                folderId: targetFolderId,
                folderPath: undefined,
                latestVersion: undefined,
            };
            setLocalFiles(prev => {
                const exists = prev.some(f => f.fileName.toLowerCase() === newFile.fileName.toLowerCase());
                return exists ? prev : [...prev, newFile];
            });
            setCreatingMdFolderId(targetFolderId);
            setCreatingMdFileName(finalName);
            setIsCreatingMd(true);
            setMdEditorContent(initialContent);
            setIsMdEditorOpen(true);
        } catch (err) {
            setMdEditorError('Failed to create markdown file.');
        } finally {
            setMdEditorLoading(false);
        }
    }, [projectId, folder.id, level, getUniqueMarkdownFileName]);

    const closeMarkdownEditor = useCallback((_current?: string) => {
        setIsMdEditorOpen(false);
        setIsCreatingMd(false);
        setCreatingMdFolderId(undefined);
        setCreatingMdFileName('');
        setMdEditorError(undefined);
    }, []);

    const saveMarkdownEditor = useCallback(async (newContent: string) => {
        setMdEditorLoading(true);
        setMdEditorError(undefined);
        try {
            let folderIdForUpload: string | undefined = undefined;
            let fileNameForUpload: string = '';
            if (isCreatingMd) {
                folderIdForUpload = creatingMdFolderId;
                fileNameForUpload = creatingMdFileName;
            } else if (selectedContextFile) {
                folderIdForUpload = selectedContextFile.folderId || undefined;
                fileNameForUpload = selectedContextFile.fileName;
            } else {
                throw new Error('No target file specified');
            }
            const markdownFile = new File([newContent], fileNameForUpload, { type: 'text/markdown' });
            const uploadResults = await api.projects.uploadFiles(projectId, [markdownFile], folderIdForUpload);
            const newVersion = uploadResults[0]?.latestVersion;
            if (newVersion !== undefined && selectedContextFile) {
                setSelectedContextFile(prev => prev ? { ...prev, latestVersion: newVersion } : null);
                setLocalFiles(prev => prev.map(f => 
                    f.id === selectedContextFile.id ? { ...f, latestVersion: newVersion } : f
                ));
                onFileContentChanged?.(selectedContextFile.id, newVersion);
            }
            setIsMdEditorOpen(false);
            setIsCreatingMd(false);
            setCreatingMdFolderId(undefined);
            setCreatingMdFileName('');
        } catch (err) {
            setMdEditorError('Failed to save markdown file.');
        } finally {
            setMdEditorLoading(false);
        }
    }, [projectId, isCreatingMd, creatingMdFolderId, creatingMdFileName, selectedContextFile]);

    const handleDownloadSelectedFile = useCallback(async () => {
        if (!selectedContextFile) {
            setShowFileContextMenu(false);
            return;
        }
        try {
            const result = await api.projects.getContentFileContent(projectId, selectedContextFile.id);
            const url = window.URL.createObjectURL(result.blob);
            const anchor = document.createElement('a');
            anchor.href = url;
            const bestName = result.fileName && result.fileName.toLowerCase() !== 'unknown' ? result.fileName : selectedContextFile.fileName;
            anchor.download = bestName;
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
    }, [selectedContextFile, projectId]);

    const handleCreateNotebookFromFile = useCallback(() => {
        if (selectedContextFile && onCreateNotebookFromFile) {
            onCreateNotebookFromFile(selectedContextFile.id, selectedContextFile.fileName);
            setShowFileContextMenu(false);
        }
    }, [selectedContextFile, onCreateNotebookFromFile]);

    const handleStartFileRename = useCallback(() => {
        if (selectedContextFile) {
            setEditingFileId(selectedContextFile.id);
            setEditingFileName(selectedContextFile.fileName);
            setShowFileContextMenu(false);
        }
    }, [selectedContextFile]);

    const handleSaveFileRename = useCallback(async (fileId: string) => {
        const file = localFiles.find(f => f.id === fileId);
        if (editingFileName.trim() && file && editingFileName !== file.fileName) {
            try {
                await onRenameFile?.(fileId, editingFileName.trim());
                setEditingFileId(null);
                setEditingFileName('');
                setLocalFiles(prev => prev.map(f => f.id === fileId ? { ...f, fileName: editingFileName.trim() } : f));
            } catch (error) {
                console.error('Failed to rename file:', error);
                showToast({ type: 'error', title: 'Failed to rename file', message: 'Please try again.' });
            }
        } else {
            setEditingFileId(null);
            setEditingFileName('');
        }
    }, [editingFileName, localFiles, onRenameFile]);

    const handleCancelFileRename = useCallback(() => {
        setEditingFileId(null);
        setEditingFileName('');
    }, []);

    const handleFileRenameKeyDown = useCallback((e: React.KeyboardEvent, fileId: string) => {
        if (e.key === 'Enter') handleSaveFileRename(fileId);
        else if (e.key === 'Escape') handleCancelFileRename();
    }, [handleSaveFileRename, handleCancelFileRename]);

    const handleToggleHomePage = useCallback(() => {
        if (!onSetHomePage || !selectedContextFile) return;
        const isHome = homePageContentFileId === selectedContextFile.id;
        onSetHomePage(isHome ? null : selectedContextFile.id);
        setShowFileContextMenu(false);
    }, [onSetHomePage, selectedContextFile, homePageContentFileId]);

    // Early return check AFTER all hooks are called
    if (searchTerm && searchTerm.trim() && !shouldShow) {
        return null;
    }

    return (
        <>
            <div className="folder-tree-item">
                <div
                    className={`group flex items-center py-0.5 px-2 text-sm cursor-pointer hover:bg-gray-100 ${
                        selectedFolderId === folder.id ? 'bg-blue-100 text-blue-600' : ''
                    } ${disabled ? 'opacity-50 cursor-not-allowed' : ''} ${
                        dragOverFolder === folder.id ? 'ring-2 ring-blue-400 bg-blue-50' : ''
                    }`}
                    style={{ paddingLeft: `${paddingLeft}px` }}
                    onClick={(e) => {
                        if (!disabled) {
                            // On mobile: single tap toggles expansion
                            // On desktop: single click selects folder
                            if (context?.isMobile) {
                                handleToggleExpand(e);
                            } else if (folder.id && context) {
                                context.multiSelect.handleClick(folder.id, e);
                                context.setFocusedId(folder.id);
                            }
                        }
                    }}
                    onDoubleClick={(e) => {
                        if (!disabled && !context?.isMobile) {
                            handleFolderClick();
                            handleToggleExpand(e);
                        }
                    }}
                    onContextMenu={handleContextMenu}
                    onDragOver={handleDragOver}
                    onDragLeave={handleDragLeave}
                    onDrop={handleDrop}
                    {...(level === 0 ? { 'data-tour-id': 'sidebar.folder.root' } : {})}
                >
                    {hasChildren && (
                        <button onClick={handleToggleExpand} className="mr-1 text-gray-500 hover:text-gray-700" disabled={disabled}>
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
                        <input type="text" value={editName} onChange={(e) => setEditName(e.target.value)} onBlur={handleSaveRename} onKeyDown={handleKeyDownFolderRename} className="flex-1 px-1 py-0 text-sm border border-blue-300 rounded focus:outline-none focus:ring-1 focus:ring-blue-500" autoFocus />
                    ) : (
                        <span className="flex-1 truncate" title={folder.name}>{folder.name}</span>
                    )}
                    {!disabled && (
                        <div className="opacity-0 group-hover:opacity-100 transition-opacity flex space-x-1">
                            {onCreateFolder && <button onClick={(e) => { e.stopPropagation(); handleCreateSubfolder(); }} className="p-1 text-gray-400 hover:text-blue-600" title="Create subfolder"><svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24"><line x1="12" y1="5" x2="12" y2="19"></line><line x1="5" y1="12" x2="19" y2="12"></line></svg></button>}
                            {onUploadToFolder && <button onClick={(e) => { e.stopPropagation(); const isRootFolder = level === 0; const resolvedId = folder.id || folders.find(f => f.relativePath === folder.relativePath)?.id; onUploadToFolder(isRootFolder ? undefined : resolvedId); }} className="p-1 text-gray-400 hover:text-green-600" title="Upload to this folder"><svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path><polyline points="17 8 12 3 7 8"></polyline><line x1="12" y1="3" x2="12" y2="15"></line></svg></button>}
                        </div>
                    )}
                </div>

                {isExpanded && (hasChildren || isCreatingSubfolder) && (
                    <div className="folder-children">
                        {[...filteredSubFolders].sort((a, b) => a.name.localeCompare(b.name)).map((subFolder) => (
                            <FolderNode
                                key={subFolder.id || subFolder.relativePath}
                                folder={subFolder}
                                level={level + 1}
                                folders={folders}
                                projectId={projectId}
                                selectedFileId={selectedFileId}
                                selectedFolderId={selectedFolderId}
                                onFileSelect={onFileSelect}
                                onFolderSelect={onFolderSelect}
                                onCreateFolder={onCreateFolder}
                                resolveProjectFilePath={resolveProjectFilePath}
                                onRenameFolder={onRenameFolder}
                                onDeleteFolder={onDeleteFolder}
                                onMoveFile={onMoveFile}
                                onUploadToFolder={onUploadToFolder}
                                onDeleteFile={onDeleteFile}
                                onDeleteFiles={onDeleteFiles}
                                onRenameFile={onRenameFile}
                                onCreateNotebookFromFile={onCreateNotebookFromFile}
                                onCreateNotebookFromFiles={onCreateNotebookFromFiles}
                                homePageContentFileId={homePageContentFileId}
                                onSetHomePage={onSetHomePage}
                                disabled={disabled}
                                registerOpenMenu={registerOpenMenu}
                                searchTerm={searchTerm}
                                onFileContentChanged={onFileContentChanged}
                            />
                        ))}
                        {isCreatingSubfolder && !disabled && (
                            <div className="flex items-center py-0.5 px-2 text-sm" style={{ paddingLeft: `${paddingLeft + 20}px` }}>
                                <svg className="w-4 h-4 mr-2 text-yellow-500" fill="currentColor" viewBox="0 0 20 20"><path d="M2 6a2 2 0 012-2h5l2 2h5a2 2 0 012 2v6a2 2 0 01-2 2H4a2 2 0 01-2-2V6z" /></svg>
                                <input type="text" value={newSubfolderName} onChange={(e) => setNewSubfolderName(e.target.value)} onBlur={handleSaveNewSubfolder} onKeyDown={handleKeyDownNewSubfolder} className="flex-1 px-1 py-0 text-sm border border-blue-300 rounded focus:outline-none focus:ring-1 focus:ring-blue-500" autoFocus />
                            </div>
                        )}
                        {[...filteredFiles].sort((a, b) => a.fileName.localeCompare(b.fileName)).map(file => (
                            <ProjectFileItem
                                key={file.id}
                                file={file}
                                paddingLeft={paddingLeft}
                                disabled={disabled}
                                selectedFileId={selectedFileId}
                                editingFileId={editingFileId}
                                editingFileName={editingFileName}
                                onFileSelect={onFileSelect}
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

            {showContextMenu && !disabled && createPortal(
                <div ref={menuRef} className="fixed bg-white shadow-lg rounded-lg py-1 z-[9999]" style={{ top: contextMenuPosition.y, left: contextMenuPosition.x }} data-tour-id="folder.context-menu" onClick={(e) => e.stopPropagation()} onFocus={(e) => e.stopPropagation()}>
                    <button className="block w-full text-left px-4 py-2 text-sm hover:bg-gray-100" onClick={handleNewMarkdownFile}>New Markdown File</button>
                    {onRenameFolder && <button className="block w-full text-left px-4 py-2 text-sm hover:bg-gray-100" onClick={handleStartRename}>Rename</button>}
                    {onCreateFolder && <button className="block w-full text-left px-4 py-2 text-sm hover:bg-gray-100" onClick={handleCreateSubfolder}>Create Subfolder</button>}
                    {onUploadToFolder && <button className="block w-full text-left px-4 py-2 text-sm hover:bg-gray-100" onClick={() => { setShowContextMenu(false); const isRootFolder = level === 0; const targetFolderId = isRootFolder ? undefined : folder.id; onUploadToFolder?.(targetFolderId); }}>Upload Files</button>}
                    {folder.id && onDeleteFolder && <button className={`block w-full text-left px-4 py-2 text-sm hover:bg-gray-100 ${hasChildren ? 'opacity-50 cursor-not-allowed' : 'text-red-600'}`} onClick={hasChildren ? undefined : handleDeleteFolder} disabled={hasChildren}>Delete</button>}
                </div>,
                document.body
            )}

            {showFileContextMenu && !disabled && createPortal(
                <div ref={menuRef} data-tour-id="file.context-menu" className="fixed bg-white border border-gray-300 rounded-md shadow-lg py-1 z-[9999] min-w-[160px]" style={{ top: contextMenuPosition.y, left: contextMenuPosition.x }} onClick={(e) => e.stopPropagation()} onFocus={(e) => e.stopPropagation()}>
                    {context?.multiSelect.selectedCount && context.multiSelect.selectedCount > 1 ? (
                        <>
                            {/* Multi-select context menu */}
                            {onCreateNotebookFromFiles && (
                                <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={() => {
                                    if (!context) return;
                                    const selectedItems = context.multiSelect.getSelectedItems();
                                    const files = selectedItems.filter(i => i.type === 'file').map(i => ({ id: i.id, fileName: (i.data as ProjectContentFile).fileName }));
                                    
                                    if (files.length > 0) {
                                        onCreateNotebookFromFiles(files);
                                    }
                                    setShowFileContextMenu(false);
                                }}>
                                    Create Notebook from {context.multiSelect.selectedCount} Files
                                </button>
                            )}
                            <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={async () => {
                                if (!context) return;
                                const selectedItems = context.multiSelect.getSelectedItems();
                                setShowFileContextMenu(false);
                                
                                for (const item of selectedItems) {
                                    if (item.type === 'file') {
                                        const file = item.data as ProjectContentFile;
                                        try {
                                            const result = await api.projects.getContentFileContent(projectId, file.id);
                                            const url = window.URL.createObjectURL(result.blob);
                                            const anchor = document.createElement('a');
                                            anchor.href = url;
                                            const bestName = result.fileName && result.fileName.toLowerCase() !== 'unknown' ? result.fileName : file.fileName;
                                            anchor.download = bestName;
                                            anchor.style.display = 'none';
                                            document.body.appendChild(anchor);
                                            anchor.click();
                                            document.body.removeChild(anchor);
                                            setTimeout(() => URL.revokeObjectURL(url), 1000);
                                            await new Promise(r => setTimeout(r, 200));
                                        } catch (err) {
                                            console.error(`Failed to download file ${file.fileName}:`, err);
                                        }
                                    }
                                }
                            }}>
                                Download {context.multiSelect.selectedCount} Items
                            </button>
                            <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap text-red-600" onClick={() => {
                                setShowFileContextMenu(false);
                                setShowBatchDeleteConfirm(true);
                            }}>
                                Delete {context.multiSelect.selectedCount} Items
                            </button>
                        </>
                    ) : (
                        <>
                            {selectedContextFile && isMarkdownFileName(selectedContextFile.fileName, selectedContextFile.contentType) && <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={openMarkdownEditor}>Edit</button>}
                            {onCreateNotebookFromFile && <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={handleCreateNotebookFromFile}>Create Notebook from File</button>}
                            {onSetHomePage && selectedContextFile && <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={handleToggleHomePage}>{homePageContentFileId === selectedContextFile.id ? 'Clear as Home Page' : 'Set Project Home Page'}</button>}
                            {onRenameFile && <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={handleStartFileRename}>Rename</button>}
                            <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap" onClick={handleDownloadSelectedFile}>Download</button>
                            {onDeleteFile && <button className="block w-full text-left px-4 py-1.5 text-sm hover:bg-gray-100 cursor-pointer whitespace-nowrap text-red-600" onClick={handleDeleteFileWrapper}>Delete</button>}
                        </>
                    )}
                </div>,
                document.body
            )}

            {isMdEditorOpen && <FullScreenEditor content={mdEditorContent} onSave={saveMarkdownEditor} onCancel={closeMarkdownEditor} mode={'edit'} title={selectedContextFile?.fileName ? `Edit File: ${selectedContextFile.fileName}` : creatingMdFileName ? `Create File: ${creatingMdFileName}` : 'Edit File'} placeholder={selectedContextFile?.fileName || creatingMdFileName || 'Markdown'} submitLabel={'Save'} isLoading={mdEditorLoading} error={mdEditorError} projectId={projectId} notebookId={undefined} resolveProjectFilePath={resolveProjectFilePath} />}
            <ConfirmationDialog isOpen={showDeleteFolderConfirm} onClose={handleDeleteFolderCancel} onConfirm={handleDeleteFolderConfirm} title="Confirm Deletion" message={`Are you sure you want to delete the folder "${folder.name}"? This action cannot be undone.`} confirmText="Delete" cancelText="Cancel" />
            <ConfirmationDialog isOpen={showDeleteFileConfirm} onClose={handleDeleteFileCancel} onConfirm={handleDeleteFileConfirm} title="Confirm Deletion" message={`Are you sure you want to delete "${selectedContextFile?.fileName}"? This action cannot be undone.`} confirmText="Delete" cancelText="Cancel" />
            <ConfirmationDialog isOpen={showBatchDeleteConfirm} onClose={handleBatchDeleteCancel} onConfirm={handleBatchDeleteConfirm} title="Confirm Deletion" message={`Are you sure you want to delete ${context?.multiSelect.selectedCount || 0} item${(context?.multiSelect.selectedCount || 0) > 1 ? 's' : ''}? This action cannot be undone.`} confirmText="Delete" cancelText="Cancel" />
        </>
    );
};

export const FolderTree: React.FC<FolderTreeProps> = ({
    folderTree,
    folders,
    projectId,
    selectedFileId,
    selectedFolderId,
    onFileSelect,
    onFolderSelect,
    onCreateFolder,
    onRenameFolder,
    onDeleteFolder,
    onMoveFile,
    onUploadToFolder,
    onDeleteFile,
    onDeleteFiles,
    onRenameFile,
    onCreateNotebookFromFile,
    onCreateNotebookFromFiles,
    resolveProjectFilePath,
    homePageContentFileId,
    onSetHomePage,
    disabled = false,
    searchTerm,
    onFileContentChanged,
    activeSection,
    onSectionActivate
}) => {
    // Shared state
    const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set(['ROOT']));
    
    // Initialize expandedIds with root folder ID if present
    useEffect(() => {
        if (folderTree?.id) {
            setExpandedIds(prev => new Set(prev).add(folderTree.id || 'ROOT'));
        }
    }, [folderTree?.id]);

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

    // Helper to get visible items (folders AND files) for navigation and multi-select
    const getVisibleItems = useCallback((node: FolderTreeDto): TreeItem[] => {
        let results: TreeItem[] = [];
        
        const hasMatchingDescendants = (n: FolderTreeDto, term: string): boolean => {
             const lower = term.toLowerCase();
             if (n.files.some(f => f.fileName.toLowerCase().includes(lower))) return true;
             return n.subFolders.some(sub => sub.name.toLowerCase().includes(lower) || hasMatchingDescendants(sub, term));
        };
        
        const processNode = (n: FolderTreeDto) => {
            const showMe = !searchTerm?.trim() || n.name.toLowerCase().includes(searchTerm.toLowerCase()) || hasMatchingDescendants(n, searchTerm);
            if (!showMe) return;

            // Add the folder itself (except root if we don't want it selectable/navigable, but typically root is not in the list passed to processNode if we start inside? 
            // Actually recursion usually starts with children. But here we passed root. 
            // The root folder is usually a container. 
            // Let's check FolderNode rendering. It renders "FolderNode" for root.
            // But we usually want to navigate *inside* the tree.
            // If n is root, we usually don't select it.
            // Let's assume root is handled separately or we filter it.
            if (n.id && n.id !== 'ROOT' && n.id !== folderTree?.id) { 
                 results.push({ type: 'folder', data: n, id: n.id });
            }

            if (expandedIds.has(n.id || 'ROOT')) {
                // Process subfolders
                [...n.subFolders]
                    .sort((a, b) => a.name.localeCompare(b.name))
                    .forEach(sub => processNode(sub));
                // Process files
                [...n.files]
                    .sort((a, b) => a.fileName.localeCompare(b.fileName))
                    .forEach(f => {
                        if (!searchTerm?.trim() || f.fileName.toLowerCase().includes(searchTerm.toLowerCase())) {
                            results.push({ type: 'file', data: f, id: f.id });
                        }
                    });
            }
        };
        
        // Start processing from root's children, or root itself? 
        // FolderNode implementation renders root folder content.
        // We probably want items *inside* the root.
        if (node) {
             if (expandedIds.has(node.id || 'ROOT')) {
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
    }, [expandedIds, searchTerm, folderTree?.id]);

    const visibleItems = useMemo(() => folderTree ? getVisibleItems(folderTree) : [], [folderTree, getVisibleItems]);

    // Selection hooks
    const multiSelect = useMultiSelect<TreeItem>({
        items: visibleItems,
        getId: item => item.id,
        onSelectionChange: (ids) => {
            if (ids.size > 0 && onSectionActivate) {
                onSectionActivate('contentFiles');
            }
        }
    });

    const listNav = useListKeyboardNavigation<TreeItem>({
        items: visibleItems,
        getId: item => item.id,
        onNavigate: (id) => {
            const item = visibleItems.find(i => i.id === id);
            if (item?.type === 'file') {
                onFileSelect?.(id);
            } else if (item?.type === 'folder') {
                toggleExpansion(id);
                onFolderSelect?.(id);
            }
        },
        onSelectionChange: (id, shift) => {
            multiSelect.handleClick(id, { shiftKey: shift, ctrlKey: false } as any);
            onSectionActivate?.('contentFiles');
        }
    });

    // Clear selection if not active
    useEffect(() => {
        if (activeSection !== 'contentFiles') {
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

        // Prioritize batch file delete
        if (fileIds.length > 0) {
            if (onDeleteFiles) {
                await onDeleteFiles(fileIds);
            } else if (onDeleteFile) {
                for (const id of fileIds) await onDeleteFile(id);
            }
        }

        // Serial folder delete (batch not supported usually)
        if (folderIds.length > 0 && onDeleteFolder) {
            for (const id of folderIds) {
                await onDeleteFolder(id);
            }
        }
        
        multiSelect.clearSelection();
        setShowBatchDeleteConfirm(false);
    }, [multiSelect, onDeleteFiles, onDeleteFile, onDeleteFolder]);

    const handleDeleteSelected = useCallback(async () => {
        if (multiSelect.selectedCount === 0) return;
        setShowBatchDeleteConfirm(true);
    }, [multiSelect]);

    const closeCurrentMenuRef = useRef<(() => void) | null>(null);
    const [renamingId, setRenamingId] = useState<string | null>(null);

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

    useSidebarKeyboardShortcuts({
        isActive: activeSection === 'contentFiles',
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

    // Context value
    const contextValue: FolderTreeContextType = {
        expandedIds,
        toggleExpansion,
        multiSelect,
        focusedId: listNav.focusedId,
        setFocusedId: listNav.setFocusedId,
        handleKeyDown: listNav.handleKeyDown,
        searchTerm,
        renamingId,
        setRenamingId,
        isActive: activeSection === 'contentFiles',
        focusIntentRef: listNav.focusIntentRef,
        isMobile
    };

    return (
        <FolderTreeContext.Provider value={contextValue}>
            <div className="folder-tree overflow-auto" onContextMenuCapture={(e)=>e.preventDefault()}>
                <FolderNode
                    folder={folderTree!}
                    level={0}
                    folders={folders}
                    projectId={projectId}
                    selectedFileId={selectedFileId}
                    selectedFolderId={selectedFolderId}
                    onFileSelect={onFileSelect}
                    onFolderSelect={onFolderSelect}
                    onCreateFolder={onCreateFolder}
                    resolveProjectFilePath={resolveProjectFilePath}
                    onRenameFolder={onRenameFolder}
                    onDeleteFolder={onDeleteFolder}
                    onMoveFile={onMoveFile}
                    onUploadToFolder={onUploadToFolder}
                    onDeleteFile={onDeleteFile}
                    onDeleteFiles={onDeleteFiles}
                    onRenameFile={onRenameFile}
                    onCreateNotebookFromFile={onCreateNotebookFromFile}
                    onCreateNotebookFromFiles={onCreateNotebookFromFiles}
                    homePageContentFileId={homePageContentFileId}
                    onSetHomePage={onSetHomePage}
                    disabled={disabled}
                    registerOpenMenu={registerOpenMenu}
                    searchTerm={searchTerm}
                    onFileContentChanged={onFileContentChanged}
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
        </FolderTreeContext.Provider>
    );
};
