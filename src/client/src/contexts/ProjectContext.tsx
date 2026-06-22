import React, { createContext, useContext, useReducer, useCallback, ReactNode } from 'react';
import { ProjectDetailsDto, SelectedItem, SectionType, FolderTreeDto } from '../types/project';
import { api } from '../services/api';
import { useAuth } from './AuthContext';

// State interface
interface ProjectState {
    project: ProjectDetailsDto | null;
    error: string | null;
    isLoading: boolean;
    selectedItem: SelectedItem | null;
    expandedSections: Set<SectionType>;
    folderTree: FolderTreeDto | null;
    currentUserEmail: string | null;
}

// Action types
type ProjectAction =
    | { type: 'SET_LOADING'; payload: boolean }
    | { type: 'SET_PROJECT'; payload: ProjectDetailsDto }
    | { type: 'SET_ERROR'; payload: string | null }
    | { type: 'SET_SELECTED_ITEM'; payload: SelectedItem | null }
    | { type: 'TOGGLE_SECTION'; payload: SectionType }
    | { type: 'SET_FOLDER_TREE'; payload: FolderTreeDto | null }
    | { type: 'SET_CURRENT_USER_EMAIL'; payload: string | null }
    | { type: 'RESET' };

// Initial state
const initialState: ProjectState = {
    project: null,
    error: null,
    isLoading: true,
    selectedItem: null,
    expandedSections: new Set(['notebooks', 'contentFiles', 'artifacts', 'guideAuthorization']),
    folderTree: null,
    currentUserEmail: null,
};

// Reducer
function projectReducer(state: ProjectState, action: ProjectAction): ProjectState {
    switch (action.type) {
        case 'SET_LOADING':
            return { ...state, isLoading: action.payload };
        
        case 'SET_PROJECT':
            return { ...state, project: action.payload, error: null, isLoading: false };
        
        case 'SET_ERROR':
            return { ...state, error: action.payload, isLoading: false };
        
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
        
        case 'SET_FOLDER_TREE':
            return { ...state, folderTree: action.payload };
        
        case 'SET_CURRENT_USER_EMAIL':
            return { ...state, currentUserEmail: action.payload };
        
        case 'RESET':
            return initialState;
        
        default:
            return state;
    }
}

// Context interface
interface ProjectContextValue {
    // State
    projectId: string | undefined;
    project: ProjectDetailsDto | null;
    error: string | null;
    isLoading: boolean;
    selectedItem: SelectedItem | null;
    expandedSections: Set<SectionType>;
    folderTree: FolderTreeDto | null;
    currentUserEmail: string | null;
    
    // Actions
    setSelectedItem: (item: SelectedItem | null) => void;
    toggleSection: (section: SectionType) => void;
    refreshProject: (silent?: boolean) => Promise<void>;
    loadProject: (projectId: string, silent?: boolean) => Promise<void>;
    setFolderTree: (tree: FolderTreeDto | null) => void;
    renameFile: (fileId: string, newName: string) => Promise<void>;

    
    // Computed values
    canEdit: () => boolean;
    isOwner: () => boolean;
    
    // Utility functions
    reset: () => void;
}

const ProjectContext = createContext<ProjectContextValue | undefined>(undefined);

// Provider component
interface ProjectProviderProps {
    children: ReactNode;
    projectId?: string;
}

export function ProjectProvider({ children, projectId }: ProjectProviderProps) {
    const { user, role } = useAuth();
    const [state, dispatch] = useReducer(projectReducer, initialState);



    // Load project data
    const loadProject = useCallback(async (id: string, silent: boolean = false) => {
        if (!silent) {
            dispatch({ type: 'SET_LOADING', payload: true });
        }
        try {
            const [projectDetails, projectFolders, folderTree] = await Promise.all([
                api.projects.getProjectDetails(id),
                api.projects.folders.getFolders(id),
                api.projects.folders.getFolderTree(id)
            ]);
            
            const projectWithFolders = {
                ...projectDetails,
                folders: projectFolders
            };
            
            dispatch({ type: 'SET_PROJECT', payload: projectWithFolders });
            dispatch({ type: 'SET_FOLDER_TREE', payload: folderTree });
        } catch (err) {
            const errorMessage = err instanceof Error ? err.message : 'Failed to fetch project details';
            dispatch({ type: 'SET_ERROR', payload: errorMessage });
            console.error('Load project error:', err);
        }
    }, []);

    // Refresh current project
    const refreshProject = useCallback(async (silent: boolean = false) => {
        if (!projectId) return;
        await loadProject(projectId, silent);
    }, [projectId, loadProject]);

    // Action creators
    const setSelectedItem = useCallback((item: SelectedItem | null) => {
        dispatch({ type: 'SET_SELECTED_ITEM', payload: item });
    }, []);

    const toggleSection = useCallback((section: SectionType) => {
        dispatch({ type: 'TOGGLE_SECTION', payload: section });
    }, []);

    const setFolderTree = useCallback((tree: FolderTreeDto | null) => {
        dispatch({ type: 'SET_FOLDER_TREE', payload: tree });
    }, []);

    const reset = useCallback(() => {
        dispatch({ type: 'RESET' });
    }, []);

    // File operations
    const renameFile = useCallback(async (fileId: string, newName: string) => {
        if (!projectId) return;
        
        try {
            await api.projects.renameContentFile(projectId, fileId, newName);
            
            // Do NOT refresh the whole project here. The consumer (ProjectDetails) will handle
            // the targeted refresh via polling hooks.
            // await refreshProject();
            
        } catch (error: any) {
            const errorMessage = error.message || 'Failed to rename file';
            dispatch({ type: 'SET_ERROR', payload: errorMessage });
            throw error;
        }
    }, [projectId]);



    const canEdit = React.useCallback(() => role === 'Admin' || role === 'Contributor', [role]);
    const isOwner = React.useCallback(() => role === 'Admin', [role]);

    React.useEffect(() => {
        dispatch({ type: 'SET_CURRENT_USER_EMAIL', payload: user?.email ?? null });
    }, [user?.email]);

    // Load project on mount or projectId change
    React.useEffect(() => {
        if (projectId) {
            loadProject(projectId);
        } else {
            reset();
        }
    }, [projectId, loadProject, reset]);

    const contextValue: ProjectContextValue = {
        // State
        projectId,
        project: state.project,
        error: state.error,
        isLoading: state.isLoading,
        selectedItem: state.selectedItem,
        expandedSections: state.expandedSections,
        folderTree: state.folderTree,
        currentUserEmail: state.currentUserEmail,
        
        // Actions
        setSelectedItem,
        toggleSection,
        refreshProject,
        loadProject,
        setFolderTree,
        renameFile,

        
        // Computed values
        canEdit,
        isOwner,
        
        // Utility functions
        reset,
    };

    return (
        <ProjectContext.Provider value={contextValue}>
            {children}
        </ProjectContext.Provider>
    );
}

// Hook to use the context
export function useProject() {
    const context = useContext(ProjectContext);
    if (context === undefined) {
        throw new Error('useProject must be used within a ProjectProvider');
    }
    return context;
} 