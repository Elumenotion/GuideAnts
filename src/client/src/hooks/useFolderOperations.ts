import { useState, useCallback } from 'react';
import { ProjectFolderDto, CreateFolderDto, UpdateFolderDto, FolderTreeDto, MoveFolderDto, MoveFileDto } from '../types/project';
import { api } from '../services/api';

interface UseFolderOperationsResult {
    folderTree: FolderTreeDto | null;
    folders: ProjectFolderDto[];
    isLoading: boolean;
    error: string | null;
    fetchFolderTree: () => Promise<void>;
    fetchFolders: () => Promise<void>;
    createFolder: (folderData: CreateFolderDto) => Promise<ProjectFolderDto>;
    updateFolder: (folderId: string, folderData: UpdateFolderDto) => Promise<ProjectFolderDto>;
    deleteFolder: (folderId: string) => Promise<void>;
    moveFolder: (folderId: string, moveData: MoveFolderDto) => Promise<ProjectFolderDto>;
    moveFile: (fileId: string, moveData: MoveFileDto) => Promise<void>;
    uploadFiles: (files: File[], folderId?: string) => Promise<any[]>;
}

export function useFolderOperations(projectId: string): UseFolderOperationsResult {
    const [folderTree, setFolderTree] = useState<FolderTreeDto | null>(null);
    const [folders, setFolders] = useState<ProjectFolderDto[]>([]);
    const [isLoading, setIsLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const handleError = (err: unknown, operation: string) => {
        const errorMessage = err instanceof Error ? err.message : `Failed to ${operation}`;
        setError(errorMessage);
        console.error(`${operation} error:`, err);
        throw err;
    };

    const fetchFolderTree = useCallback(async () => {
        if (!projectId || !api.projects?.folders) return;
        
        setIsLoading(true);
        setError(null);
        
        try {
            const tree = await api.projects.folders.getFolderTree(projectId);
            setFolderTree(tree);
        } catch (err) {
            handleError(err, 'fetch folder tree');
        } finally {
            setIsLoading(false);
        }
    }, [projectId]);

    const fetchFolders = useCallback(async () => {
        if (!projectId || !api.projects?.folders) return;
        
        setIsLoading(true);
        setError(null);
        
        try {
            const folderList = await api.projects.folders.getFolders(projectId);
            setFolders(folderList);
        } catch (err) {
            handleError(err, 'fetch folders');
        } finally {
            setIsLoading(false);
        }
    }, [projectId]);

    const createFolder = useCallback(async (folderData: CreateFolderDto): Promise<ProjectFolderDto> => {
        if (!projectId) throw new Error('Project ID is required');
        if (!api.projects?.folders) throw new Error('Folders API not available');
        
        setError(null);
        
        try {
            const newFolder = await api.projects.folders.createFolder(projectId, folderData);
            
            // Refresh data after successful creation
            await Promise.all([fetchFolderTree(), fetchFolders()]);
            
            return newFolder;
        } catch (err) {
            handleError(err, 'create folder');
            throw err;
        }
    }, [projectId, fetchFolderTree, fetchFolders]);

    const updateFolder = useCallback(async (folderId: string, folderData: UpdateFolderDto): Promise<ProjectFolderDto> => {
        if (!projectId) throw new Error('Project ID is required');
        if (!api.projects?.folders) throw new Error('Folders API not available');
        
        setError(null);
        
        try {
            const updatedFolder = await api.projects.folders.updateFolder(projectId, folderId, folderData);
            
            // Refresh data after successful update
            // await Promise.all([fetchFolderTree(), fetchFolders()]);
            
            return updatedFolder;
        } catch (err) {
            handleError(err, 'update folder');
            throw err;
        }
    }, [projectId]);

    const deleteFolder = useCallback(async (folderId: string): Promise<void> => {
        if (!projectId) throw new Error('Project ID is required');
        if (!api.projects?.folders) throw new Error('Folders API not available');
        
        setError(null);
        
        try {
            await api.projects.folders.deleteFolder(projectId, folderId);
            
            // Refresh data after successful deletion
            await Promise.all([fetchFolderTree(), fetchFolders()]);
        } catch (err) {
            handleError(err, 'delete folder');
        }
    }, [projectId, fetchFolderTree, fetchFolders]);

    const moveFolder = useCallback(async (folderId: string, moveData: MoveFolderDto): Promise<ProjectFolderDto> => {
        if (!projectId) throw new Error('Project ID is required');
        if (!api.projects?.folders) throw new Error('Folders API not available');
        
        setError(null);
        
        try {
            const movedFolder = await api.projects.folders.moveFolder(projectId, folderId, moveData);
            
            // Refresh data after successful move
            await Promise.all([fetchFolderTree(), fetchFolders()]);
            
            return movedFolder;
        } catch (err) {
            handleError(err, 'move folder');
            throw err;
        }
    }, [projectId, fetchFolderTree, fetchFolders]);

    const moveFile = useCallback(async (fileId: string, moveData: MoveFileDto): Promise<void> => {
        if (!projectId) throw new Error('Project ID is required');
        if (!api.projects?.moveContentFile) throw new Error('Move file API not available');
        
        setError(null);
        

        
        try {
            await api.projects.moveContentFile(projectId, fileId, moveData);
            
            // Refresh folder tree to show updated file locations
            await fetchFolderTree();
        } catch (err) {
            handleError(err, 'move file');
        }
    }, [projectId, fetchFolderTree]);

    const uploadFiles = useCallback(async (files: File[], folderId?: string): Promise<any[]> => {
        if (!projectId) throw new Error('Project ID is required');
        
        setError(null);
        
        try {
            const results = await api.projects.uploadFiles(projectId, files, folderId);
            
            // Refresh folder tree to show new files
            await fetchFolderTree();
            
            return results;
        } catch (err) {
            handleError(err, 'upload files');
            throw err;
        }
    }, [projectId, fetchFolderTree]);

    return {
        folderTree,
        folders,
        isLoading,
        error,
        fetchFolderTree,
        fetchFolders,
        createFolder,
        updateFolder,
        deleteFolder,
        moveFolder,
        moveFile,
        uploadFiles
    };
} 