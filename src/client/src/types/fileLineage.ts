export interface FileLineageEvent {
    id: string;
    action: string;
    fileKind: 'Project' | 'Notebook';
    fileId: string;
    versionNumber?: number | null;
    projectId: string;
    notebookId?: string | null;
    storagePath?: string | null;
    timestamp: string; // ISO string
    userId: string;
    userDisplayName?: string;
    fileName: string;
    fileVersionLabel?: string;
    notebookName?: string | null;
} 