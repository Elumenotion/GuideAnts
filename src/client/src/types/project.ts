export interface ProjectNotebook {
    id: string;
    title: string;
    description?: string;
    guideId: string;
    lastActivity?: string;
}

export interface ProjectContentFile {
    id: string;
    fileName: string;
    path: string;
    relativePath: string;
    contentType: string;
    index: boolean;
    documentId: string;
    created: string;
    fileSize: number;
    folderId?: string;
    folderPath?: string;
    latestVersion?: number;
}

export interface ContentFileDto {
    id: string;
    contentType: string;
    fileName: string;
    path: string;
    relativePath: string;
    index: boolean;
    documentId: string;
    created: string;
    fileSize: number;
    folderId?: string;
    folderPath?: string;
}

export interface ProjectFolderDto {
    id: string;
    name: string;
    relativePath: string;
    projectId: string;
    parentFolderId?: string;
    created: string;
    subFolders: ProjectFolderDto[];
    files: ContentFileDetailsDto[];
}

export interface ContentFileDetailsDto {
    id: string;
    contentType: string;
    fileName: string;
    path: string;
    relativePath: string;
    index: boolean;
    documentId: string;
    created: string;
    fileSize: number;
    folderId?: string;
    folderPath?: string;
}

export interface CreateFolderDto {
    name: string;
    parentFolderId?: string;
}

export interface UpdateFolderDto {
    name?: string;
    parentFolderId?: string;
}

export interface FolderTreeDto {
    id?: string;
    name: string;
    relativePath: string;
    subFolders: FolderTreeDto[];
    files: ProjectContentFile[];
}

export interface MoveFolderDto {
    newParentFolderId?: string;
}

export interface MoveFileDto {
    destinationFolderId?: string | null;
}

export interface ProjectLink {
    id: string;
    url: string;
    projectId?: string;
}

export interface ProjectArtifact {
    id: string;
    placeholder: string;
}

export interface ProjectDetailsDto {
    id: string;
    title: string;
    description: string;
    created: string;
    notebooks: ProjectNotebook[];
    contentFiles: ProjectContentFile[];
    folders: ProjectFolderDto[];
    links: ProjectLink[];
    homePageContentFileId?: string;
    semiStructuredDatas: ProjectArtifact[];
    canCreateContent: boolean;
}

export type SectionType = 'notebooks' | 'contentFiles' | 'links' | 'artifacts' | 'guideAuthorization';

export interface SelectedItem {
    type: SectionType;
    id: string;
}

export interface UseProjectDetailsResult {
    project: ProjectDetailsDto | null;
    error: string | null;
    isLoading: boolean;
    selectedItem: SelectedItem | null;
    expandedSections: Set<SectionType>;
    setSelectedItem: (item: SelectedItem | null) => void;
    toggleSection: (section: SectionType) => void;
    refreshProject: () => Promise<void>;
}

export interface CreateNotebookDto {
    title: string;
    description?: string;
}

/**
 * Lightweight template summary for selection dialogs.
 * Does not include home page content or other heavy details.
 */
export interface NotebookTemplateSummaryDto {
    id: string;
    templateName: string;
    description: string;
    avatarUrl: string;
    /** True if this template has auth providers with optional or required user configuration */
    hasConfigurableAuth?: boolean;
}

/**
 * Full template details including home page content.
 * Returned when fetching a specific template by ID.
 */
export interface NotebookTemplateDto {
    id: string;
    templateName: string;
    description: string;
    avatarUrl: string;
    conversationStarters: string[];
    defaultAssistant?: string;
    homeContent?: string | null;
    authProviders?: NotebookAuthProviderDto[];
}

export type NotebookAuthType = 'oauth' | 'service_http';

export type NotebookUserConfigPolicy = 'none' | 'optional' | 'required';

export interface NotebookAuthProviderDto {
    id: string;
    authType: NotebookAuthType;
    clientId?: string;
    scopes?: string[];
    tenant?: string;
    valueTemplate?: string;
    headerName?: string;
    userConfigPolicy: NotebookUserConfigPolicy;
}

export interface UpdateNotebookDto {
    title: string;
    description?: string;
} 