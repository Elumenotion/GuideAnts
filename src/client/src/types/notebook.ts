import { ProjectFolderDto } from './project';
import { MessageDto } from './conversation';

// Notebook-specific types
export interface NotebookCell {
    id: string;
    type: 'code' | 'markdown' | 'text';
    content: string;
    language?: string; // For code cells
    output?: string; // For executed code cells
    metadata?: Record<string, any>;
    order: number;
    created: string;
    modified: string;
}

export interface NotebookSection {
    id: string;
    title: string;
    type: 'cells' | 'files' | 'variables' | 'outputs' | 'history';
    isExpanded: boolean;
    items?: any[]; // Type-specific items based on section type
}

export interface NotebookVariable {
    id: string;
    name: string;
    type: string;
    value: string;
    scope: 'global' | 'local';
    created: string;
}

export interface NotebookOutput {
    id: string;
    cellId: string;
    type: 'text' | 'image' | 'html' | 'json' | 'error';
    content: string;
    timestamp: string;
}

export interface NotebookHistory {
    id: string;
    cellId: string;
    action: 'execute' | 'edit' | 'delete' | 'create';
    content: string;
    timestamp: string;
    user: string;
}

export interface NotebookDetailsDto {
    id: string;
    title: string;
    description?: string;
    projectId: string;
    guideId: string;
    homePageFileId?: string;
    cells: NotebookCell[];
    created: string;
    modified: string;
    createdBy: string;
    modifiedBy: string;
    metadata?: Record<string, any>;
}

// Notebook operation DTOs
export interface CreateNotebookCellDto {
    type: NotebookCell['type'];
    content: string;
    language?: string;
    afterCellId?: string; // Insert after this cell
}

export interface UpdateNotebookCellDto {
    content?: string;
    language?: string;
    metadata?: Record<string, any>;
}

export interface ExecuteNotebookCellDto {
    cellId: string;
    context?: Record<string, any>;
}

export interface NotebookExecutionResult {
    cellId: string;
    output: string;
    error?: string;
    variables?: NotebookVariable[];
    executionTime: number;
}

// Notebook sidebar section types
export type NotebookSectionType = 'cells' | 'files' | 'variables' | 'outputs' | 'history';

export interface NotebookSelectedItem {
    type: NotebookSectionType | 'cell';
    id: string;
}

// Notebook state interfaces
export interface NotebookState {
    notebook: NotebookDetailsDto | null;
    selectedCell: NotebookCell | null;
    selectedItem: NotebookSelectedItem | null;
    expandedSections: Set<NotebookSectionType>;
    variables: NotebookVariable[];
    outputs: NotebookOutput[];
    history: NotebookHistory[];
    isExecuting: boolean;
    executingCellId: string | null;
    error: string | null;
    isLoading: boolean;
    isLoadingFiles: boolean;
    filesError: string | null;
    conversations: NotebookConversationDto[];
    isLoadingConversations: boolean;
    conversationsError: string | null;
    // Assistants (template-level data loaded at notebook level)
    assistants: Array<{ name: string; model?: string; avatarUrl: string }>;
    isLoadingAssistants: boolean;
    assistantsError: string | null;
}

// Notebook context interface
export interface NotebookContextValue {
    // State
    notebook: NotebookDetailsDto | null;
    projectId: string | undefined;  // The project this notebook belongs to
    notebookId: string | undefined;  // The current notebook ID
    selectedCell: NotebookCell | null;
    selectedItem: NotebookSelectedItem | null;
    expandedSections: Set<NotebookSectionType>;
    variables: NotebookVariable[];
    outputs: NotebookOutput[];
    history: NotebookHistory[];
    isExecuting: boolean;
    executingCellId: string | null;
    error: string | null;
    isLoading: boolean;
    
    isLoadingFiles: boolean;
    filesError: string | null;
    
    // Conversations (existing manual loading)
    conversations: NotebookConversationDto[];
    isLoadingConversations: boolean;
    conversationsError: string | null;
    
    // Assistants (template-level data loaded at notebook level)
    assistants: Array<{ name: string; model?: string; avatarUrl: string }>;
    isLoadingAssistants: boolean;
    assistantsError: string | null;
    
    // Actions
    loadNotebook: (notebookId: string) => Promise<void>;
    createCell: (data: CreateNotebookCellDto) => Promise<void>;
    updateCell: (cellId: string, data: UpdateNotebookCellDto) => Promise<void>;
    deleteCell: (cellId: string) => Promise<void>;
    executeCell: (cellId: string) => Promise<void>;
    moveCell: (cellId: string, targetIndex: number) => Promise<void>;
    
    // File operations
    loadNotebookFiles: () => Promise<void>;
    uploadFiles: (files: File[], folderId?: string, shouldIndex?: boolean) => Promise<void>;
    createFolder: (parentFolderPath: string | undefined, folderData: CreateNotebookFolderDto) => Promise<void>;
    deleteFile: (fileId: string) => Promise<void>;
    deleteFolder: (folderPath: string) => Promise<void>;
    renameFolder: (folderPath: string, newName: string) => Promise<void>;
    renameFile: (fileId: string, newName: string) => Promise<void>;
    moveFile: (fileId: string, destinationFolderPath: string | undefined) => Promise<void>;
    copyFromProject: (contentFileId: string, version?: number) => Promise<void>;

    
    // Selection management
    setSelectedCell: (cell: NotebookCell | null) => void;
    setSelectedItem: (item: NotebookSelectedItem | null) => void;
    toggleSection: (section: NotebookSectionType) => void;
    
    // Utility functions
    refreshNotebook: () => Promise<void>;
    reset: () => void;
    
    // Conversations
    loadConversations: () => Promise<void>;
    createConversation: (title: string) => Promise<NotebookConversationDto | null>;
    renameConversation: (id: string, title: string) => Promise<void>;
    deleteConversation: (id: string) => Promise<void>;
    deleteConversations: (ids: string[]) => Promise<void>;
    
    // Home page management
    setHomePageFile: (fileId: string) => Promise<void>;
    clearHomePage: () => Promise<void>;
}

// Navigation types
export interface NotebookNavigation {
    projectId: string;
    notebookId: string;
    projectTitle: string;
    notebookTitle: string;
    canGoBack: boolean;
    goBack: () => void;
    goToProject: () => void;
}

// New notebook file management types
export interface NotebookFileDto {
  id: string;
  fileName: string;
  relativePath: string;
  fileSize: number;
  lastModifiedUtc: string;
  fileHash: string;
  originContentFileVersionId?: string; // lineage tracking
  index: boolean;
  isIndexed: boolean;
}

export interface NotebookFolderDto {
  id: string;
  name: string;
  parentFolderId?: string;
  relativePath: string;
}

export interface NotebookFolderTreeDto {
  id?: string;
  name: string;
  relativePath: string;
  subFolders: NotebookFolderTreeDto[];
  files: NotebookFileDto[];
}

export interface PublishNotebookFileDto {
  notebookFileId: string;
  destinationFolderId?: string;
}

export interface PublishNotebookFileResultDto {
  contentFileId: string;
  fileName: string;
  versionNumber: number;
  isNewFile: boolean;
  relativePath: string;
}

export interface OriginFileInfoDto {
  fileName: string;
  folderPath: string;
  contentFileId: string;
  versionNumber: number;
}

export interface PublishToProjectDialogProps {
  isOpen: boolean;
  onClose: () => void;
  notebookFiles: NotebookFileDto[];
  projectFolders: ProjectFolderDto[];
  projectTitle: string;
  onPublish: (data: PublishNotebookFileDto) => Promise<string>; // Returns contentFileId
  onComplete?: (publishedFileIds: string[]) => void; // Called after all files are published
  originFileInfoMap?: Map<string, OriginFileInfoDto>;
}

export interface CreateNotebookFolderDto {
  name: string;
  parentFolderId?: string;
}

export type NotebookSidebarSectionType = 'notebookFiles' | 'conversations' | 'links';

export interface NotebookSidebarSelectedItem {
  type: NotebookSidebarSectionType;
  id: string;
}

export interface LinkDto {
  id: string;
  url: string;
}

export interface NotebookConversationDto {
    id: string;
    title: string;
    assistantName?: string;
    created: string;
    lastActivity?: string;
}

export interface NotebookConversationWithMessagesDto extends NotebookConversationDto {
  messages: MessageDto[];
}