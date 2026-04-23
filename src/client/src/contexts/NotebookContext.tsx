import React, { createContext, useContext, useReducer, useCallback, ReactNode, useEffect, useMemo } from 'react';
import { 
    NotebookState, 
    NotebookContextValue, 
    NotebookDetailsDto,
    NotebookCell,
    NotebookSelectedItem,
    NotebookSectionType,
    CreateNotebookCellDto,
    UpdateNotebookCellDto,
    NotebookVariable,
    NotebookOutput,
    NotebookHistory,
    CreateNotebookFolderDto,
    NotebookConversationDto
} from '../types/notebook';
import { api } from '../services/api';
import { useToast } from '../components/common/Toast';
import { notebookFilesApi } from '../services/notebookFiles';
import { resolveAgainstApiBase } from '../config/apiConfig';

// Action types
type NotebookAction =
    | { type: 'SET_LOADING'; payload: boolean }
    | { type: 'SET_NOTEBOOK'; payload: NotebookDetailsDto }
    | { type: 'SET_ERROR'; payload: string | null }
    | { type: 'SET_SELECTED_CELL'; payload: NotebookCell | null }
    | { type: 'SET_SELECTED_ITEM'; payload: NotebookSelectedItem | null }
    | { type: 'TOGGLE_SECTION'; payload: NotebookSectionType }
    | { type: 'SET_VARIABLES'; payload: NotebookVariable[] }
    | { type: 'SET_OUTPUTS'; payload: NotebookOutput[] }
    | { type: 'SET_HISTORY'; payload: NotebookHistory[] }
    | { type: 'SET_EXECUTING'; payload: { isExecuting: boolean; cellId?: string } }
    | { type: 'UPDATE_CELL'; payload: { cellId: string; updates: Partial<NotebookCell> } }
    | { type: 'ADD_CELL'; payload: NotebookCell }
    | { type: 'REMOVE_CELL'; payload: string }
    | { type: 'SET_FILES_LOADING'; payload: boolean }
    | { type: 'SET_FILES_ERROR'; payload: string | null }
    | { type: 'SET_CONVERSATIONS'; payload: NotebookConversationDto[] }
    | { type: 'SET_CONVERSATIONS_LOADING'; payload: boolean }
    | { type: 'SET_CONVERSATIONS_ERROR'; payload: string | null }
    | { type: 'ADD_CONVERSATION'; payload: NotebookConversationDto }
    | { type: 'UPDATE_CONVERSATION'; payload: { id: string; updates: Partial<NotebookConversationDto> } }
    | { type: 'REMOVE_CONVERSATION'; payload: string }
    | { type: 'REMOVE_CONVERSATIONS'; payload: string[] }
    | { type: 'SET_ASSISTANTS'; payload: Array<{ name: string; model?: string; avatarUrl: string, id?: string }> }
    | { type: 'SET_ASSISTANTS_LOADING'; payload: boolean }
    | { type: 'SET_ASSISTANTS_ERROR'; payload: string | null }
    | { type: 'RESET' };

// Initial state
const initialState: NotebookState = {
    notebook: null,
    selectedCell: null,
    selectedItem: null,
    expandedSections: new Set(['cells', 'variables', 'outputs']),
    variables: [],
    outputs: [],
    history: [],
    isExecuting: false,
    executingCellId: null,
    error: null,
    isLoading: true,
    isLoadingFiles: false,
    filesError: null,
    conversations: [],
    isLoadingConversations: false,
    conversationsError: null,
    assistants: [],
    isLoadingAssistants: false,
    assistantsError: null,
};

// Reducer
function notebookReducer(state: NotebookState, action: NotebookAction): NotebookState {
    switch (action.type) {
        case 'SET_LOADING':
            return { ...state, isLoading: action.payload };
        
        case 'SET_NOTEBOOK':
            return { 
                ...state, 
                notebook: action.payload, 
                error: null, 
                isLoading: false 
            };
        
        case 'SET_ERROR':
            return { ...state, error: action.payload, isLoading: false };
        
        case 'SET_SELECTED_CELL':
            return { ...state, selectedCell: action.payload };
        
        case 'SET_SELECTED_ITEM':
            return { ...state, selectedItem: action.payload };
        
        case 'TOGGLE_SECTION': {
            const newSet = new Set(state.expandedSections);
            if (newSet.has(action.payload)) {
                newSet.delete(action.payload);
            } else {
                newSet.add(action.payload);
            }
            return { ...state, expandedSections: newSet };
        }
        
        case 'SET_VARIABLES':
            return { ...state, variables: action.payload };
        
        case 'SET_OUTPUTS':
            return { ...state, outputs: action.payload };
        
        case 'SET_HISTORY':
            return { ...state, history: action.payload };
        
        case 'SET_EXECUTING':
            return { 
                ...state, 
                isExecuting: action.payload.isExecuting,
                executingCellId: action.payload.cellId || null
            };
        
        case 'UPDATE_CELL':
            if (!state.notebook) return state;
            return {
                ...state,
                notebook: {
                    ...state.notebook,
                    cells: state.notebook.cells.map(cell =>
                        cell.id === action.payload.cellId
                            ? { ...cell, ...action.payload.updates }
                            : cell
                    )
                }
            };
        
        case 'ADD_CELL':
            if (!state.notebook) return state;
            return {
                ...state,
                notebook: {
                    ...state.notebook,
                    cells: [...state.notebook.cells, action.payload].sort((a, b) => a.order - b.order)
                }
            };
        
        case 'REMOVE_CELL':
            if (!state.notebook) return state;
            return {
                ...state,
                notebook: {
                    ...state.notebook,
                    cells: state.notebook.cells.filter(cell => cell.id !== action.payload)
                },
                selectedCell: state.selectedCell?.id === action.payload ? null : state.selectedCell
            };

        // File-related actions
        case 'SET_FILES_LOADING':
            return { ...state, isLoadingFiles: action.payload };
        
        case 'SET_FILES_ERROR':
            return { ...state, filesError: action.payload };
        
        case 'SET_CONVERSATIONS':
            return { ...state, conversations: action.payload };
        
        case 'SET_CONVERSATIONS_LOADING':
            return { ...state, isLoadingConversations: action.payload };
        
        case 'SET_CONVERSATIONS_ERROR':
            return { ...state, conversationsError: action.payload };
        
        case 'ADD_CONVERSATION':
            return { ...state, conversations: [action.payload, ...state.conversations] };
        
        case 'UPDATE_CONVERSATION':
            return { ...state, conversations: state.conversations.map(c => c.id === action.payload.id ? { ...c, ...action.payload.updates } : c) };
        
        case 'REMOVE_CONVERSATION':
            return { ...state, conversations: state.conversations.filter(c => c.id !== action.payload) };

        case 'REMOVE_CONVERSATIONS':
            return { ...state, conversations: state.conversations.filter(c => !action.payload.includes(c.id)) };
        
        case 'SET_ASSISTANTS':
            return { ...state, assistants: action.payload };
        
        case 'SET_ASSISTANTS_LOADING':
            return { ...state, isLoadingAssistants: action.payload };
        
        case 'SET_ASSISTANTS_ERROR':
            return { ...state, assistantsError: action.payload };
        
        case 'RESET':
            return initialState;
        
        default:
            return state;
    }
}

const NotebookContext = createContext<NotebookContextValue | undefined>(undefined);

// Provider component
interface NotebookProviderProps {
    children: ReactNode;
    notebookId?: string;
    projectId?: string;
}

export function NotebookProvider({ children, notebookId, projectId }: NotebookProviderProps) {
    const [state, dispatch] = useReducer(notebookReducer, initialState);
    const { showToast } = useToast();



    // Load notebook data
    const loadNotebook = useCallback(async (id: string) => {
        dispatch({ type: 'SET_LOADING', payload: true });
        try {
            const notebook = await api.projects.notebooks.getNotebook(projectId!, id);
            dispatch({ type: 'SET_NOTEBOOK', payload: notebook });
            
            // Load assistants if the notebook has a guideId
            if (notebook.guideId) {
                loadAssistants(notebook.guideId);
            }
        } catch (err) {
            const errorMessage = err instanceof Error ? err.message : 'Failed to fetch notebook details';
            dispatch({ type: 'SET_ERROR', payload: errorMessage });
            console.error('Load notebook error:', err);
        }
    }, [projectId]);

    // Load assistants for the notebook's template
    const loadAssistants = useCallback(async (guideId: string) => {
        if (!projectId) return;
        
        dispatch({ type: 'SET_ASSISTANTS_LOADING', payload: true });
        try {
            const assistantList = await api.projects.notebookTemplates.getAssistants(guideId, projectId);
            // Normalize assistants to a consistent shape and resolve avatar URLs to absolute with projectId
            const normalized = assistantList.map((a: any) => {
                let avatarUrl: string = a.avatarUrl;
                if (avatarUrl && !avatarUrl.startsWith('http')) {
                    // Append projectId to avatar URL for owner-scoped loading
                    const urlWithProject = avatarUrl.includes('?') 
                        ? `${avatarUrl}&projectId=${projectId}`
                        : `${avatarUrl}?projectId=${projectId}`;
                    avatarUrl = resolveAgainstApiBase(urlWithProject).toString();
                }
                return {
                    name: a.name,
                    model: a.modelDeploymentId ?? a.model ?? '',
                    avatarUrl,
                    id: a.id
                };
            });
            console.log('✅ NotebookContext loaded assistants:', normalized.map((a: any) => a.name));
            dispatch({ type: 'SET_ASSISTANTS', payload: normalized });
        } catch (err) {
            const errorMessage = err instanceof Error ? err.message : 'Failed to load assistants';
            dispatch({ type: 'SET_ASSISTANTS_ERROR', payload: errorMessage });
            console.error('Load assistants error:', err);
        } finally {
            dispatch({ type: 'SET_ASSISTANTS_LOADING', payload: false });
        }
    }, [projectId]);

    // Load notebook files (folder tree only)
    const loadNotebookFiles = useCallback(async () => {
        if (!projectId || !notebookId) return;
        
        dispatch({ type: 'SET_FILES_LOADING', payload: true });
        try {
            // Note: No longer stores in context state - use useNotebookFilesPolling hook instead
            // Just trigger a sync to ensure backend is up-to-date
            await api.projects.notebooks.getNotebookFolderTree(projectId, notebookId);
        } catch (err) {
            const errorMessage = err instanceof Error ? err.message : 'Failed to load notebook files';
            dispatch({ type: 'SET_FILES_ERROR', payload: errorMessage });
        } finally {
            dispatch({ type: 'SET_FILES_LOADING', payload: false });
        }
    }, [projectId, notebookId]);

    // File operations
    const uploadFiles = useCallback(async (files: File[], folderId?: string) => {
        if (!projectId || !notebookId) return;
        
        try {
            await api.projects.notebooks.uploadNotebookFiles(
                projectId, 
                notebookId, 
                files, 
                folderId || '', 
                false // indexing is server-managed; always false from client
            );
            
            // Refresh folder tree after upload
            await loadNotebookFiles();
        } catch (err) {
            const errorMessage = err instanceof Error ? err.message : 'Failed to upload files';
            dispatch({ type: 'SET_FILES_ERROR', payload: errorMessage });
            throw err;
        }
    }, [projectId, notebookId, loadNotebookFiles]);

    const createFolder = useCallback(async (parentFolderPath: string | undefined, folderData: CreateNotebookFolderDto) => {
        if (!projectId || !notebookId) return;
        
        try {
            // parentFolderPath is the relativePath of the parent folder in notebook system
            const folderPath = parentFolderPath ? `${parentFolderPath}/${folderData.name}` : folderData.name;
            await api.projects.notebooks.createNotebookFolder(projectId, notebookId, folderPath);
            
            // Trigger refresh - actual tree managed by polling hook
            await loadNotebookFiles();
        } catch (err) {
            const errorMessage = err instanceof Error ? err.message : 'Failed to create folder';
            dispatch({ type: 'SET_FILES_ERROR', payload: errorMessage });
            throw err;
        }
    }, [projectId, notebookId, loadNotebookFiles]);

    const deleteFolder = useCallback(async (folderPath: string) => {
        if (!projectId || !notebookId) return;
        
        try {
            await notebookFilesApi.delete(projectId, notebookId, folderPath);
            
            // Refresh files and folder tree
            await loadNotebookFiles();
        } catch (err) {
            const status = (err as any)?.status as number | undefined;
            const body = (err as any)?.body as any;
            const message = (body?.message) || (err instanceof Error ? err.message : 'Failed to delete folder');
            if (status === 409) {
                showToast({ type: 'error', title: 'Cannot delete folder', message });
                return;
            }
            dispatch({ type: 'SET_FILES_ERROR', payload: message });
            throw err;
        }
    }, [projectId, notebookId, loadNotebookFiles]);

    const renameFolder = useCallback(async (folderPath: string, newName: string) => {
        if (!projectId || !notebookId) return;
        
        try {
            await api.projects.notebooks.renameNotebookItem(projectId, notebookId, folderPath, newName);
            
            // Refresh files and folder tree
            await loadNotebookFiles();
        } catch (err) {
            const errorMessage = err instanceof Error ? err.message : 'Failed to rename folder';
            dispatch({ type: 'SET_FILES_ERROR', payload: errorMessage });
            throw err;
        }
    }, [projectId, notebookId, loadNotebookFiles]);

    const deleteFile = useCallback(async (fileId: string) => {
        if (!projectId || !notebookId) return;
        
        try {
            // Use by-ID API endpoint - no tree lookup needed
            await api.projects.notebooks.deleteNotebookFileById(projectId, notebookId, fileId);
            // Don't refresh here - let caller handle refresh
        } catch (err) {
            const status = (err as any)?.status as number | undefined;
            const body = (err as any)?.body as any;
            const message = (body?.message) || (err instanceof Error ? err.message : 'Failed to delete file');
            if (status === 409) {
                showToast({ type: 'error', title: 'Cannot delete file', message });
                return;
            }
            dispatch({ type: 'SET_FILES_ERROR', payload: message });
            throw err;
        }
    }, [projectId, notebookId, showToast]);

    const renameFile = useCallback(async (fileId: string, newName: string) => {
        if (!projectId || !notebookId) return;
        
        try {
            // Use by-ID API endpoint - no tree lookup needed
            await api.projects.notebooks.renameNotebookFileById(projectId, notebookId, fileId, newName);
            // Don't refresh here - let caller handle refresh
        } catch (err) {
            const errorMessage = err instanceof Error ? err.message : 'Failed to rename file';
            dispatch({ type: 'SET_FILES_ERROR', payload: errorMessage });
            throw err;
        }
    }, [projectId, notebookId]);

    const moveFile = useCallback(async (fileId: string, destinationFolderPath: string | undefined) => {
        if (!projectId || !notebookId) return;
        
        try {
            // Use by-ID API endpoint - no tree lookup or optimistic updates needed
            await api.projects.notebooks.moveNotebookFileById(projectId, notebookId, fileId, destinationFolderPath);
            // Don't refresh here - let caller handle refresh
        } catch (err) {
            const errorMessage = err instanceof Error ? err.message : 'Failed to move file';
            dispatch({ type: 'SET_FILES_ERROR', payload: errorMessage });
            throw err;
        }
    }, [projectId, notebookId]);

    const copyFromProject = useCallback(async (contentFileId: string, version?: number) => {
        if (!projectId || !notebookId) return;
        
        try {
            await api.projects.notebooks.copyFileFromProject(
                projectId, 
                notebookId, 
                contentFileId, 
                version
            );
            
            // Refresh folder tree after copy
            await loadNotebookFiles();
        } catch (err) {
            const errorMessage = err instanceof Error ? err.message : 'Failed to copy file from project';
            dispatch({ type: 'SET_FILES_ERROR', payload: errorMessage });
            throw err;
        }
    }, [projectId, notebookId, loadNotebookFiles]);

    // Refresh current notebook
    const refreshNotebook = useCallback(async () => {
        if (!notebookId) return;
        await loadNotebook(notebookId);
    }, [notebookId, loadNotebook]);

    // Cell operations
    const createCell = useCallback(async (data: CreateNotebookCellDto) => {
        if (!state.notebook) return;
        
        try {
            // TODO: Replace with actual API call
            // const newCell = await api.notebooks.createCell(state.notebook.id, data);
            
            // Mock implementation
            const newCell: NotebookCell = {
                id: `cell-${Date.now()}`,
                type: data.type,
                content: data.content,
                language: data.language,
                order: state.notebook.cells.length + 1,
                created: new Date().toISOString(),
                modified: new Date().toISOString()
            };
            
            dispatch({ type: 'ADD_CELL', payload: newCell });
        } catch (err) {
            console.error('Create cell error:', err);
            dispatch({ type: 'SET_ERROR', payload: 'Failed to create cell' });
        }
    }, [state.notebook]);

    const updateCell = useCallback(async (cellId: string, data: UpdateNotebookCellDto) => {
        try {
            // TODO: Replace with actual API call
            // await api.notebooks.updateCell(cellId, data);
            
            dispatch({ 
                type: 'UPDATE_CELL', 
                payload: { 
                    cellId, 
                    updates: { 
                        ...data, 
                        modified: new Date().toISOString() 
                    } 
                } 
            });
        } catch (err) {
            console.error('Update cell error:', err);
            dispatch({ type: 'SET_ERROR', payload: 'Failed to update cell' });
        }
    }, []);

    const deleteCell = useCallback(async (cellId: string) => {
        try {
            // TODO: Replace with actual API call
            // await api.notebooks.deleteCell(cellId);
            
            dispatch({ type: 'REMOVE_CELL', payload: cellId });
        } catch (err) {
            console.error('Delete cell error:', err);
            dispatch({ type: 'SET_ERROR', payload: 'Failed to delete cell' });
        }
    }, []);

    const executeCell = useCallback(async (cellId: string) => {
        const cell = state.notebook?.cells.find(c => c.id === cellId);
        if (!cell || cell.type !== 'code') return;

        dispatch({ type: 'SET_EXECUTING', payload: { isExecuting: true, cellId } });
        
        try {
            // TODO: Replace with actual API call
            // const result = await api.notebooks.executeCell(cellId);
            
            // Mock execution
            await new Promise(resolve => setTimeout(resolve, 1000));
            
            const mockOutput = `Output for: ${cell.content}`;
            dispatch({ 
                type: 'UPDATE_CELL', 
                payload: { 
                    cellId, 
                    updates: { 
                        output: mockOutput,
                        modified: new Date().toISOString()
                    } 
                } 
            });
        } catch (err) {
            console.error('Execute cell error:', err);
            dispatch({ type: 'SET_ERROR', payload: 'Failed to execute cell' });
        } finally {
            dispatch({ type: 'SET_EXECUTING', payload: { isExecuting: false } });
        }
    }, [state.notebook]);

    const moveCell = useCallback(async (cellId: string, targetIndex: number) => {
        // TODO: Implement cell reordering
        console.log('Move cell:', cellId, 'to index:', targetIndex);
    }, []);

    // Selection management
    const setSelectedCell = useCallback((cell: NotebookCell | null) => {
        dispatch({ type: 'SET_SELECTED_CELL', payload: cell });
    }, []);

    const setSelectedItem = useCallback((item: NotebookSelectedItem | null) => {
        dispatch({ type: 'SET_SELECTED_ITEM', payload: item });
    }, []);

    const toggleSection = useCallback((section: NotebookSectionType) => {
        dispatch({ type: 'TOGGLE_SECTION', payload: section });
    }, []);

    const reset = useCallback(() => {
        dispatch({ type: 'RESET' });
    }, []);

    // Load notebook on mount or notebookId change
    React.useEffect(() => {
        if (notebookId) {
            loadNotebook(notebookId);
        } else {
            reset();
        }
    }, [notebookId, loadNotebook, reset]);

    // Note: We no longer automatically load notebook files or project folder tree here on mount.
    // The notebook files are loaded via the useNotebookFilesPolling hook in NotebookDetails.
    // The project folder tree is loaded by ProjectProvider.
    // Manual file operations (upload, create folder, etc.) will still call loadNotebookFiles to refresh.





    const loadConversations = useCallback(async () => {
        if (!projectId || !notebookId) return;
        dispatch({ type: 'SET_CONVERSATIONS_LOADING', payload: true });
        try {
            const convos = await api.projects.notebooks.conversations.getAll(projectId, notebookId);
            dispatch({ type: 'SET_CONVERSATIONS', payload: convos });
        } catch (err) {
            const msg = err instanceof Error ? err.message : 'Failed to load conversations';
            dispatch({ type: 'SET_CONVERSATIONS_ERROR', payload: msg });
            console.error('Load conversations error:', err);
        } finally {
            dispatch({ type: 'SET_CONVERSATIONS_LOADING', payload: false });
        }
    }, [projectId, notebookId]);

    const createConversation = useCallback(async (title: string) => {
        if (!projectId || !notebookId) return null;
        try {
            const convo = await api.projects.notebooks.conversations.create(projectId, notebookId, title);
            dispatch({ type: 'ADD_CONVERSATION', payload: convo as any });
            return convo as any;
        } catch (err) {
            console.error('Create conversation error:', err);
            throw err;
        }
    }, [projectId, notebookId]);

    const renameConversation = useCallback(async (id: string, title: string) => {
        if (!projectId || !notebookId) return;
        try {
            await api.projects.notebooks.conversations.rename(projectId, notebookId, id, title);
            dispatch({ type: 'UPDATE_CONVERSATION', payload: { id, updates: { title } } });
        } catch (err) {
            console.error('Rename conversation error:', err);
            throw err;
        }
    }, [projectId, notebookId]);

    const deleteConversation = useCallback(async (id: string) => {
        if (!projectId || !notebookId) return;
        try {
            await api.projects.notebooks.conversations.delete(projectId, notebookId, id);
            dispatch({ type: 'REMOVE_CONVERSATION', payload: id });
        } catch (err) {
            console.error('Delete conversation error:', err);
            throw err;
        }
    }, [projectId, notebookId]);

    const deleteConversations = useCallback(async (ids: string[]) => {
        if (!projectId || !notebookId) return;
        try {
            // Delete sequentially to avoid overwhelmed server or use bulk if available
            // Assuming sequential for now
            await Promise.all(ids.map(id => api.projects.notebooks.conversations.delete(projectId, notebookId!, id)));
            dispatch({ type: 'REMOVE_CONVERSATIONS', payload: ids });
        } catch (err) {
            console.error('Delete conversations error:', err);
            throw err;
        }
    }, [projectId, notebookId]);

    // Home page management
    const setHomePageFile = useCallback(async (fileId: string) => {
        if (!projectId || !notebookId) return;
        await api.projects.notebooks.setHomePageFile(projectId, notebookId, fileId);
        await refreshNotebook(); // Refresh to get updated homePageFileId
    }, [projectId, notebookId, refreshNotebook]);

    const clearHomePage = useCallback(async () => {
        if (!projectId || !notebookId) return;
        await api.projects.notebooks.clearHomePage(projectId, notebookId);
        await refreshNotebook();
    }, [projectId, notebookId, refreshNotebook]);

    // call loadConversations after notebook load effect at end of provider initialization (maybe use useEffect)
    useEffect(() => {
        if (notebookId) {
            loadConversations();
        }
    }, [notebookId, loadConversations]);

    const contextValue: NotebookContextValue = useMemo(() => ({
        // State
        notebook: state.notebook,
        projectId: projectId,  // Expose projectId from props
        notebookId: notebookId,  // Expose notebookId from props
        selectedCell: state.selectedCell,
        selectedItem: state.selectedItem,
        expandedSections: state.expandedSections,
        variables: state.variables,
        outputs: state.outputs,
        history: state.history,
        isExecuting: state.isExecuting,
        executingCellId: state.executingCellId,
        error: state.error,
        isLoading: state.isLoading,
        
        isLoadingFiles: state.isLoadingFiles,
        filesError: state.filesError,
        
        // Actions
        loadNotebook,
        createCell,
        updateCell,
        deleteCell,
        executeCell,
        moveCell,
        
        // File operations
        loadNotebookFiles,
        uploadFiles,
        createFolder,
        deleteFolder,
        renameFolder,
        deleteFile,
        renameFile,
        moveFile,
        copyFromProject,

        
        // Selection management
        setSelectedCell,
        setSelectedItem,
        toggleSection,
        
        // Utility functions
        refreshNotebook,
        reset,
        
        // Conversation-related methods (existing manual loading)
        loadConversations,
        createConversation,
        renameConversation,
        deleteConversation,
        deleteConversations,
        conversations: state.conversations,
        isLoadingConversations: state.isLoadingConversations,
        conversationsError: state.conversationsError,
        
        // Assistants (template-level data)
        assistants: state.assistants,
        isLoadingAssistants: state.isLoadingAssistants,
        assistantsError: state.assistantsError,
        
        // Home page management
        setHomePageFile,
        clearHomePage,
    }), [
        state,
        projectId,
        notebookId,
        loadNotebook,
        createCell,
        updateCell,
        deleteCell,
        executeCell,
        moveCell,
        loadNotebookFiles,
        uploadFiles,
        createFolder,
        deleteFolder,
        renameFolder,
        deleteFile,
        renameFile,
        moveFile,
        copyFromProject,
        setSelectedCell,
        setSelectedItem,
        toggleSection,
        refreshNotebook,
        reset,
        loadConversations,
        createConversation,
        renameConversation,
        deleteConversation,
        deleteConversations,
        setHomePageFile,
        clearHomePage
    ]);

    return (
        <NotebookContext.Provider value={contextValue}>
            {children}
        </NotebookContext.Provider>
    );
}

// Hook to use the context
export function useNotebook() {
    const context = useContext(NotebookContext);
    if (context === undefined) {
        throw new Error('useNotebook must be used within a NotebookProvider');
    }
    return context;
} 
