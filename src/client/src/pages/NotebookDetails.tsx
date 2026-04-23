import { useParams, useNavigate, useLocation } from 'react-router-dom';
import { useProject } from '../contexts/ProjectContext';
import { NotebookProvider, useNotebook } from '../contexts/NotebookContext';
import { NotebookLayout } from '../components/layouts/NotebookLayout';
import { SidebarContainer } from '../components/layouts/SidebarContainer';
import { NotebookSidebar } from '../components/notebook/sidebar/NotebookSidebar';
import { NotebookContent } from '../components/notebook/content/NotebookContent';
import LoadingSpinner from '../components/LoadingSpinner';
import ErrorScreen from '../components/ErrorScreen';
import { NotebookSidebarSectionType, NotebookSidebarSelectedItem, NotebookFileDto, PublishNotebookFileDto, OriginFileInfoDto } from '../types/notebook';
import { NotebookTemplateDto } from '../types/project';
import { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import { useNotebookHeaderToolbar } from '../hooks/useNotebookHeaderToolbar';
import { NotebookServiceToolbar } from '../components/notebook/header-toolbar/NotebookServiceToolbar';
import { PublishToProjectDialog } from '../components/notebook/dialogs/PublishToProjectDialog';
import { notebookFilesApi } from '../services/notebookFiles';
import { FilePreviewOverlay } from '../components/notebook/content/FilePreviewOverlay';
import { api } from '../services/api';
import { ConversationPanel } from '../components/notebook/conversations/ConversationPanel';
import { ConversationProvider } from '../contexts/ConversationContext';
import { LlamaRuntimeModal } from '../components/notebook/conversations/LlamaRuntimeModal';
import { LlamaCrashedModal, LlamaCrashReason } from '../components/notebook/conversations/LlamaCrashedModal';
import MarkdownViewer from '../components/common/MarkdownViewer';
import { NotebookAuthInterstitial } from '../components/notebook/auth/NotebookAuthInterstitial';
import { checkNotebookAuthRequirements } from '../utils/notebookAuth';
import { useRegisterTour } from '../tour/useRegisterTour';
import { useNotebookFilesPolling } from '../hooks/useNotebookFilesPolling';
import { NotebookFolderTreeDto } from '../types/notebook';
import { useToast } from '../components/common/Toast';

// Inner component that uses notebook context
function NotebookDetailsContent() {
    const { projectId, notebookId } = useParams<{ projectId: string; notebookId: string }>();
    const navigate = useNavigate();
    const location = useLocation();
    const { project, canEdit, isLoading: projectLoading, error: projectError, refreshProject, folderTree: projectFolderTree } = useProject();
    const { showToast } = useToast();
    const { 
        notebook, 
        // folderTree removed - use polling hook in components that need it
        uploadFiles,
        createFolder,
        deleteFolder,
        renameFolder,
        deleteFile,
        renameFile,
        moveFile,
        copyFromProject,

        isLoading: isNotebookLoading,
        error: notebookError,
        filesError,
        conversations,
        assistants,
        setHomePageFile,
        clearHomePage
    } = useNotebook();

    const [publishDialogState, setPublishDialogState] = useState<{
        isOpen: boolean;
        notebookFiles: NotebookFileDto[];
        originFileInfoMap: Map<string, OriginFileInfoDto>;
    }>({ isOpen: false, notebookFiles: [], originFileInfoMap: new Map() });
    
    const [fileToPreview, setFileToPreview] = useState<NotebookFileDto | null>(null);

    // Use polling hook to get folder tree for file-by-path lookup
    const { folderTree } = useNotebookFilesPolling({
        projectId: projectId || '',
        notebookId: notebookId || '',
        enabled: Boolean(projectId && notebookId),
    });
    
    // Initialize activeConversationId from URL query param 'c' to persist across iOS resume/page refresh
    const [activeConversationId, setActiveConversationIdState] = useState<string | null>(() => {
        const urlParams = new URLSearchParams(window.location.search);
        return urlParams.get('c');
    });

    // Wrapper to sync activeConversationId to URL for persistence across iOS resume
    const setActiveConversationId = useCallback((id: string | null) => {
        setActiveConversationIdState(id);
        
        // Update URL with conversation ID for persistence
        const url = new URL(window.location.href);
        if (id) {
            url.searchParams.set('c', id);
        } else {
            url.searchParams.delete('c');
        }
        window.history.replaceState({}, '', url.toString());
    }, []);

    const [expandedSections, setExpandedSections] = useState<Set<NotebookSidebarSectionType>>(
        new Set(['conversations', 'notebookFiles'])
    );
    const [selectedItem, setSelectedItem] = useState<NotebookSidebarSelectedItem | null>(null);
    const [notebookTemplate, setNotebookTemplate] = useState<NotebookTemplateDto | null>(null);
    const [isAutoDisplayingHomePage, setIsAutoDisplayingHomePage] = useState(false);
    const [showAuthInterstitial, setShowAuthInterstitial] = useState(false);
    const [authCheckComplete, setAuthCheckComplete] = useState(false);
    const [navConversationApplied, setNavConversationApplied] = useState(false);
    const [homePageContent, setHomePageContent] = useState<string | null>(null);
    const [homePageFile, setHomePageFileState] = useState<NotebookFileDto | null>(null);
    // Track what home page we've already loaded to prevent unnecessary re-fetches
    const loadedHomePageRef = useRef<{ fileId: string; fileHash: string } | null>(null);
    // Keep a stable ref to folderTree for lookups without triggering effect re-runs
    const folderTreeRef = useRef<NotebookFolderTreeDto | null>(null);
    // Track if we've ever had a valid folder tree (for initial load trigger)
    const [folderTreeReady, setFolderTreeReady] = useState(false);
    
    // Update ref and track when folder tree first becomes available
    useEffect(() => {
        folderTreeRef.current = folderTree;
        if (folderTree && !folderTreeReady) {
            setFolderTreeReady(true);
        }
    }, [folderTree, folderTreeReady]);

    // Mobile Sidebar State
    const [isMobileSidebarOpen, setIsMobileSidebarOpen] = useState(false);

    const headerToolbar = useNotebookHeaderToolbar(notebookId, activeConversationId);
    const [headerIsMobile, setHeaderIsMobile] = useState<boolean>(() => window.innerWidth < 768);
    useEffect(() => {
        const f = () => setHeaderIsMobile(window.innerWidth < 768);
        f();
        window.addEventListener('resize', f);
        return () => window.removeEventListener('resize', f);
    }, []);
    const assistantByNameForToolbar = useMemo(() => {
        const m: Record<string, { id?: string }> = {};
        (assistants || []).forEach((a: { name?: string; id?: string }) => {
            if (a?.name) m[a.name] = { id: a.id };
        });
        return m;
    }, [assistants]);
    
    // Llama Runtime Modal State
    const [runtimeModalOpen, setRuntimeModalOpen] = useState(false);
    const [runtimeStatus, setRuntimeStatus] = useState<any>(null);
    const [targetAssistantId, setTargetAssistantId] = useState<string | undefined>(undefined);
    const [isPolling, setIsPolling] = useState(false);
    const runtimeCheckedNotebookRef = useRef<string | null>(null);

    // Llama runtime crash recovery (step 1 of 3):
    //   crash modal -> explicit restart -> model-load modal.
    // Populated from `llama-runtime-crashed` window events dispatched by
    // useStreamingEventHandler when the server SSE error includes a `local_llm_*` code.
    const [crashState, setCrashState] = useState<{
        reason: LlamaCrashReason;
        upstreamDetail: string | null;
    } | null>(null);

    const applyRuntimeStatus = useCallback((status: any, assistantId?: string) => {
        if (!status?.state) return;
        if (assistantId) {
            setTargetAssistantId(assistantId);
        }

        if (status.state === 'ready') {
            setRuntimeStatus(status);
            setIsPolling(false);
            setRuntimeModalOpen(false);
            return;
        }

        if (status.state === 'loading') {
            setRuntimeStatus(status);
            setRuntimeModalOpen(true);
            setIsPolling(true);
            return;
        }

        // Handle requires_load, failed, invalid (and any future non-ready states)
        setRuntimeStatus(status);
        setRuntimeModalOpen(true);
        setIsPolling(false);
    }, []);

    // Check llama runtime when notebook loads and assistants are available
    useEffect(() => {
        if (!projectId || !notebookId || !assistants || assistants.length === 0) return;
        if (runtimeCheckedNotebookRef.current === notebookId) return;

        runtimeCheckedNotebookRef.current = notebookId;
        api.projects.notebooks.conversations.checkLlamaRuntime(projectId, notebookId)
            .then((status: any) => {
                applyRuntimeStatus(status);
            })
            .catch((err: any) => {
                // Allow retry on next effect pass if this check failed
                runtimeCheckedNotebookRef.current = null;
                console.error('[llama-runtime] notebook-level check failed:', err);
                showToast({
                    type: 'error',
                    title: 'Local Runtime Check Failed',
                    message: err?.message || 'Unable to check local runtime status.'
                });
            });
    }, [projectId, notebookId, assistants, applyRuntimeStatus, showToast]);

    useEffect(() => {
        const handleRequiresLoad = (e: Event) => {
            const customEvent = e as CustomEvent;
            applyRuntimeStatus(customEvent.detail.runtimeStatus, customEvent.detail.assistantId);
        };

        const handleLoading = (e: Event) => {
            const customEvent = e as CustomEvent;
            applyRuntimeStatus({ state: 'loading', activeOperation: customEvent.detail.operation }, customEvent.detail.assistantId);
        };

        const handleCrashed = (e: Event) => {
            const customEvent = e as CustomEvent<{
                reason?: LlamaCrashReason;
                upstreamDetail?: string | null;
            }>;
            // Close the load modal if it happened to be open — the crash modal takes
            // precedence so the user isn't presented with both stacked.
            setRuntimeModalOpen(false);
            setIsPolling(false);
            setCrashState({
                reason: customEvent.detail?.reason ?? 'Crashed',
                upstreamDetail: customEvent.detail?.upstreamDetail ?? null
            });
        };

        window.addEventListener('llama-runtime-requires-load', handleRequiresLoad);
        window.addEventListener('llama-runtime-loading', handleLoading);
        window.addEventListener('llama-runtime-crashed', handleCrashed);

        return () => {
            window.removeEventListener('llama-runtime-requires-load', handleRequiresLoad);
            window.removeEventListener('llama-runtime-loading', handleLoading);
            window.removeEventListener('llama-runtime-crashed', handleCrashed);
        };
    }, [applyRuntimeStatus]);

    useEffect(() => {
        let pollInterval: NodeJS.Timeout;
        if (isPolling && runtimeStatus?.activeOperation?.operationId && projectId && notebookId) {
            pollInterval = setInterval(async () => {
                try {
                    const op = await api.projects.notebooks.conversations.pollLlamaRuntimeOperation(
                        projectId,
                        notebookId,
                        runtimeStatus.activeOperation.operationId
                    );
                    
                    if (op.state === 'ready') {
                        setIsPolling(false);
                        setRuntimeModalOpen(false);
                        setRuntimeStatus((prev: any) => ({ ...prev, state: 'ready', activeOperation: op }));
                    } else if (op.state === 'failed') {
                        setIsPolling(false);
                        setRuntimeStatus((prev: any) => ({ ...prev, state: 'failed', activeOperation: op }));
                        showToast({
                            type: 'error',
                            title: 'Model Load Failed',
                            message: op?.errorDetails || 'Local model load operation failed.'
                        });
                    } else {
                        setRuntimeStatus((prev: any) => ({ ...prev, state: 'loading', activeOperation: op }));
                    }
                } catch (error) {
                    console.error('Failed to poll runtime operation status:', error);
                    setIsPolling(false);
                    const errorMessage = error instanceof Error ? error.message : 'Failed to poll runtime operation status.';
                    setRuntimeStatus((prev: any) => ({
                        ...prev,
                        state: 'failed',
                        activeOperation: {
                            ...(prev?.activeOperation || {}),
                            state: 'failed',
                            errorDetails: errorMessage
                        }
                    }));
                    showToast({
                        type: 'error',
                        title: 'Model Load Poll Failed',
                        message: errorMessage
                    });
                }
            }, 2000);
        }
        return () => {
            if (pollInterval) clearInterval(pollInterval);
        };
    }, [isPolling, runtimeStatus?.activeOperation?.operationId, projectId, notebookId, showToast]);

    const handleStartLoad = async () => {
        if (!projectId || !notebookId) return;
        try {
            const op = await api.projects.notebooks.conversations.loadLlamaRuntime(projectId, notebookId, targetAssistantId);
            if (op?.state === 'ready') {
                setRuntimeStatus((prev: any) => ({ ...prev, state: 'ready', activeOperation: op }));
                setIsPolling(false);
                setRuntimeModalOpen(false);
                return;
            }
            if (op?.state === 'failed') {
                setRuntimeStatus((prev: any) => ({ ...prev, state: 'failed', activeOperation: op }));
                setIsPolling(false);
                setRuntimeModalOpen(true);
                showToast({
                    type: 'error',
                    title: 'Model Load Failed',
                    message: op?.errorDetails || 'Local model load operation failed.'
                });
                return;
            }

            setRuntimeStatus((prev: any) => ({ ...prev, state: 'loading', activeOperation: op }));
            setIsPolling(true);
        } catch (error) {
            console.error('Failed to start runtime load:', error);
            setIsPolling(false);
            const errorMessage = error instanceof Error ? error.message : 'Failed to start runtime load.';
            setRuntimeStatus((prev: any) => ({
                ...prev,
                state: 'failed',
                activeOperation: {
                    ...(prev?.activeOperation || {}),
                    state: 'failed',
                    errorDetails: errorMessage
                }
            }));
            showToast({
                type: 'error',
                title: 'Model Load Failed',
                message: errorMessage
            });
        }
    };

    const isLoading = projectLoading || isNotebookLoading;
    const error = projectError || notebookError || filesError;

    // Determine current tour screen based on display state
    // Each view has its own tour that includes relevant sidebar + content features
    const getTourScreenId = (): string => {
        if (fileToPreview) return 'notebook-file-preview';
        if (activeConversationId) return 'notebook-conversation';
        if (notebookTemplate?.homeContent) return 'notebook-home';
        return 'notebook-cells';
    };

    const currentTourScreenId = getTourScreenId();

    // Restore selectedItem when conversation ID is loaded from URL (e.g., after iOS resume)
    useEffect(() => {
        // If we have an activeConversationId from URL but selectedItem doesn't match, sync it
        if (activeConversationId && conversations && conversations.length > 0) {
            const conversationExists = conversations.some(c => c.id === activeConversationId);
            if (!conversationExists) {
                // Only clear if there isn't already a user-selected item
                if (!selectedItem) {
                    setActiveConversationId(null);
                    setSelectedItem(null);
                }
                return;
            }

            // Only update if selectedItem doesn't already match
            if (!selectedItem || selectedItem.type !== 'conversations' || selectedItem.id !== activeConversationId) {
                setSelectedItem({ type: 'conversations', id: activeConversationId });
            }
        }
    }, [activeConversationId, conversations, selectedItem, setActiveConversationId]);

    // Handle conversation ID from navigation state (consume once without overriding user selection)
    useEffect(() => {
        if (navConversationApplied) return;

        const conversationId = location.state?.conversationId;

        // No deep-link present: mark consumed and stop
        if (!conversationId) {
            setNavConversationApplied(true);
            return;
        }

        // Wait until conversations are loaded (and have items) to resolve the id
        if (!conversations || conversations.length === 0) return;

        // If the user already selected a conversation, do not override it
        if (activeConversationId) {
            setNavConversationApplied(true);
            // Clear navigation state so it won't re-apply on future refreshes
            navigate('.', { replace: true, state: {} });
            return;
        }

        // Apply the deep-linked conversation if it exists
        const conversation = conversations.find(c => c.id === conversationId);
        if (conversation) {
            setActiveConversationId(conversationId);
            setSelectedItem({ type: 'conversations', id: conversationId });
            // Mark as consumed and clear navigation state to prevent future overrides
            setNavConversationApplied(true);
            navigate('.', { replace: true, state: {} });
        }
        // If not found, keep state intact and wait for the next conversations refresh
    }, [navConversationApplied, location.state?.conversationId, conversations, activeConversationId, navigate, setActiveConversationId]);

    // Handle OAuth success from callback
    useEffect(() => {
        const urlParams = new URLSearchParams(location.search);
        const oauthSuccess = urlParams.get('oauthSuccess');
        
        if (oauthSuccess && notebookTemplate && projectId) {
            // Re-check auth requirements after OAuth success
            const authCheck = checkNotebookAuthRequirements(notebookTemplate, projectId);
            setShowAuthInterstitial(authCheck.needsAuth);
            
            // Clean up URL parameter
            const newUrl = new URL(window.location.href);
            newUrl.searchParams.delete('oauthSuccess');
            window.history.replaceState({}, '', newUrl.toString());
        }
    }, [location.search, notebookTemplate, projectId]);

    const handleBack = () => {
        navigate(`/projects/${projectId}`);
    };

    const handleSectionToggle = useCallback((section: NotebookSidebarSectionType) => {
        setExpandedSections(prev => {
            const newSet = new Set(prev);
            if (newSet.has(section)) {
                newSet.delete(section);
            } else {
                newSet.add(section);
            }
            return newSet;
        });
    }, []);

    const handleItemSelect = useCallback((type: NotebookSidebarSectionType, id: string) => {
        setSelectedItem({ type, id });
        if (type === 'conversations') {
            setActiveConversationId(id);
        } else {
            setActiveConversationId(null);
        }

        // Auto-close mobile sidebar on selection
        // Check if we are on mobile (matches the breakpoint in NotebookLayout)
        if (window.innerWidth < 768) {
            setIsMobileSidebarOpen(false);
        }
    }, []);

    const handleConversationDeleted = useCallback((deletedConversationId: string) => {
        // If the deleted conversation was the active one, we need to navigate to another conversation or clear the active state
        if (activeConversationId === deletedConversationId) {
            if (conversations && conversations.length > 0) {
                // Find a conversation that's not the deleted one
                const remainingConversation = conversations.find((c) => c.id !== deletedConversationId);
                if (remainingConversation) {
                    // Select the first remaining conversation
                    setActiveConversationId(remainingConversation.id);
                    setSelectedItem({ type: 'conversations', id: remainingConversation.id });
                } else {
                    // No conversations left, clear the active state
                    setActiveConversationId(null);
                    setSelectedItem(null);
                }
            } else {
                // No conversations left, clear the active state
                setActiveConversationId(null);
                setSelectedItem(null);
            }
        }
        
        // If the deleted conversation was selected in the sidebar (but not necessarily active), clear the selection
        if (selectedItem?.type === 'conversations' && selectedItem.id === deletedConversationId) {
            setSelectedItem(null);
        }
    }, [activeConversationId, selectedItem, conversations]);

    const handleSetHomePage = useCallback(async (fileId: string | null) => {
        if (fileId) {
            await setHomePageFile(fileId);
        } else {
            await clearHomePage();
        }
    }, [setHomePageFile, clearHomePage]);

    const handlePreviewFile = (file: NotebookFileDto) => {
        setFileToPreview(file);
        // Auto-close mobile sidebar
        if (window.innerWidth < 768) {
            setIsMobileSidebarOpen(false);
        }
    };

    // File preview disabled - requires folder tree which would cause remounts
    // Sidebar passes handlePreviewFile which receives the full file object
    const handlePreviewFileById = (_fileId: string) => {
        console.warn('Preview by ID disabled - use preview from sidebar context menu instead');
    };

    // Helper to find a file by relative path in the folder tree
    const findFileByPath = useCallback((tree: NotebookFolderTreeDto | null, relativePath: string): NotebookFileDto | null => {
        if (!tree) return null;
        
        // Check files in this folder
        for (const file of tree.files) {
            if (file.relativePath === relativePath || file.fileName === relativePath) {
                return file;
            }
        }
        
        // Check subfolders recursively
        for (const subFolder of tree.subFolders) {
            const found = findFileByPath(subFolder, relativePath);
            if (found) return found;
        }
        
        return null;
    }, []);

    // Helper to find a file by ID in the folder tree
    const findFileById = useCallback((tree: NotebookFolderTreeDto | null, fileId: string): NotebookFileDto | null => {
        if (!tree) return null;
        
        // Check files in this folder
        for (const file of tree.files) {
            if (file.id === fileId) {
                return file;
            }
        }
        
        // Check subfolders recursively
        for (const subFolder of tree.subFolders) {
            const found = findFileById(subFolder, fileId);
            if (found) return found;
        }
        
        return null;
    }, []);

    // Handler for previewing a file by its relative path (for turn file pills)
    const handlePreviewFileByPath = useCallback((relativePath: string) => {
        const file = findFileByPath(folderTree, relativePath);
        if (file) {
            handlePreviewFile(file);
        } else {
            console.warn(`File not found for path: ${relativePath}`);
        }
    }, [folderTree, findFileByPath]);

    // Handle navigation from within the preview overlay (e.g., clicking links in HTML)
    const handlePreviewNavigate = useCallback((path: string) => {
        console.log('[NotebookDetails] handlePreviewNavigate called:', path);
        const file = findFileByPath(folderTree, path);
        console.log('[NotebookDetails] Found file:', file ? file.relativePath : 'NOT FOUND');
        if (file) {
            setFileToPreview(file);
        }
    }, [folderTree, findFileByPath]);

    // Check if a file exists at a given path (for HTML resource resolution)
    const fileExistsInTree = useCallback((path: string): boolean => {
        return findFileByPath(folderTree, path) !== null;
    }, [folderTree, findFileByPath]);

    const handlePublishToProject = async (files: NotebookFileDto[]) => {
        const originFileInfoMap = new Map<string, OriginFileInfoDto>();
        
        // Fetch origin file information for files with lineage (in parallel)
        if (projectId && notebookId) {
            const fetchPromises = files
                .filter(file => file.originContentFileVersionId)
                .map(async (file) => {
                    try {
                        const originInfo = await notebookFilesApi.getOriginFileInfo(
                            projectId,
                            notebookId,
                            file.originContentFileVersionId!
                        );
                        return { fileId: file.id, originInfo };
                    } catch (error) {
                        console.error(`Failed to fetch origin file info for ${file.fileName}:`, error);
                        return null;
                    }
                });
            
            const results = await Promise.all(fetchPromises);
            results.forEach(result => {
                if (result) {
                    originFileInfoMap.set(result.fileId, result.originInfo);
                }
            });
        }
        
        setPublishDialogState({ isOpen: true, notebookFiles: files, originFileInfoMap });
        // Auto-close mobile sidebar
        if (window.innerWidth < 768) {
            setIsMobileSidebarOpen(false);
        }
    };

    const handlePublishSubmit = async (data: PublishNotebookFileDto): Promise<string> => {
        if (!projectId || !notebookId) throw new Error('Missing projectId or notebookId');
        try {
            const result = await notebookFilesApi.publishToProject(projectId, notebookId, data);
            console.log('Successfully published file:', result);
            return result.contentFileId;
        } catch (error) {
            console.error('Failed to publish file:', error);
            throw error; // Re-throw to be caught by the dialog
        }
    };

    const handlePublishComplete = async (publishedFileIds: string[]) => {
        if (!projectId) return;
        
        // Refresh the project context ONCE after all files are published
        await refreshProject();
        
        // Navigate back to project with the first published file selected
        const firstFileId = publishedFileIds[0];
        if (firstFileId) {
            navigate(`/projects/${projectId}?fileId=${firstFileId}`);
        } else {
            navigate(`/projects/${projectId}`);
        }
    };

    const handleEdit = () => {
        navigate(`/projects/${projectId}/notebooks/${notebookId}/edit`);
    };

    // Refresh project data when returning from edit or when component mounts
    // We don't need to do this on initial mount because ProjectProvider already loads it
    // We only need to refresh if we came from another route that might have changed it
    useEffect(() => {
        if (projectId && location.state?.refreshProject) {
            refreshProject();
            // Clear the state so we don't keep refreshing
            navigate('.', { replace: true, state: { ...location.state, refreshProject: undefined } });
        }
    }, [projectId, refreshProject, location.state, navigate]);

    // Fetch guide information (by id) including optional homeContent
    // This is now the ONLY place where the template is loaded for the notebook
    useEffect(() => {
        if (!notebook) return;
        const guideId = (notebook as any)?.guideId;
        if (!guideId) {
            // Hard invariant: Notebooks must have a guide. If missing, treat as invalid state.
            console.error('Notebook is missing required guideId:', notebook.id);
            setAuthCheckComplete(true);
            return;
        }
        (async () => {
            try {
                const tmpl = await api.projects.notebookTemplates.getById(guideId, projectId!);
                setNotebookTemplate(tmpl);
                
                // Check authentication requirements
                if (projectId) {
                    const authCheck = checkNotebookAuthRequirements(tmpl, projectId);
                    setShowAuthInterstitial(authCheck.needsAuth);
                    setAuthCheckComplete(true);
                }
            } catch (error) {
                console.error('Failed to fetch guide by id:', error);
                setAuthCheckComplete(true);
            }
        })();
    }, [notebook, projectId]);

    // Fetch home page file content when homePageFileId changes or file is updated
    // Uses refs to avoid re-running on every folderTree poll cycle
    useEffect(() => {
        const homePageFileId = notebook?.homePageFileId;
        
        if (!homePageFileId || !projectId || !notebookId) {
            // Clear state only if we had something loaded before
            if (loadedHomePageRef.current) {
                loadedHomePageRef.current = null;
                setHomePageContent(null);
                setHomePageFileState(null);
            }
            return;
        }

        // Use the ref to get current folder tree without triggering re-runs
        const currentFolderTree = folderTreeRef.current;
        if (!currentFolderTree) {
            return; // Wait for folder tree to load
        }

        const file = findFileById(currentFolderTree, homePageFileId);
        if (!file) {
            // File not found in tree - might still be loading or was deleted
            if (loadedHomePageRef.current?.fileId === homePageFileId) {
                loadedHomePageRef.current = null;
                setHomePageContent(null);
                setHomePageFileState(null);
            }
            return;
        }

        // Check if we already loaded this exact file (same ID and hash)
        if (loadedHomePageRef.current?.fileId === file.id && 
            loadedHomePageRef.current?.fileHash === file.fileHash) {
            return; // Already loaded, no need to re-fetch
        }

        // Mark as loaded and update state
        loadedHomePageRef.current = { fileId: file.id, fileHash: file.fileHash };
        setHomePageFileState(file);

        // Only fetch content for markdown files
        const isMarkdownFile = file.fileName.toLowerCase().endsWith('.md') || 
                               file.fileName.toLowerCase().endsWith('.markdown');
        
        if (!isMarkdownFile) {
            setHomePageContent(null);
            return;
        }

        // Fetch the markdown content
        (async () => {
            try {
                const blob = await notebookFilesApi.getNotebookFileContent(
                    projectId,
                    notebookId,
                    file.relativePath,
                    file.fileHash
                );
                const text = await blob.text();
                setHomePageContent(text);
            } catch (error) {
                console.error('Failed to fetch home page content:', error);
                setHomePageContent(null);
            }
        })();
        // Note: folderTree is accessed via ref, folderTreeReady triggers initial load only
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [notebook?.homePageFileId, projectId, notebookId, findFileById, folderTreeReady]);

    // Clear auto-display state when user manually selects something
    useEffect(() => {
        if ((activeConversationId || selectedItem) && isAutoDisplayingHomePage) {
            setIsAutoDisplayingHomePage(false);
        }
    }, [activeConversationId, selectedItem, isAutoDisplayingHomePage]);

    // Handle authentication completion
    const handleAuthComplete = useCallback(() => {
        setShowAuthInterstitial(false);
        // Re-check auth requirements in case user completed some but not all
        if (notebookTemplate && projectId) {
            const authCheck = checkNotebookAuthRequirements(notebookTemplate, projectId);
            if (!authCheck.needsAuth) {
                // All auth requirements satisfied, can proceed
                console.log('All authentication requirements satisfied');
            }
        }
    }, [notebookTemplate, projectId]);

    // Note: Sidebar tour is baked into each view-specific tour below
    // This allows us to customize sidebar explanations based on context

    // Register conversation tour (sidebar + conversation features)
    useRegisterTour('notebook-conversation', [
        {
            target: '[data-tour-id="notebook.sidebar.search"]',
            content: 'Use the <strong>search bar</strong> to quickly find conversations, files, and links in your notebook.',
            placement: 'right'
        },
        {
            target: '[data-tour-id="notebook.sidebar.conversations"]',
            content: 'The <strong>Conversations</strong> section shows all your AI chats. <strong>Double-click</strong> to open. Press <strong>F2</strong> to <strong>Rename</strong>. Use <strong>Right-click</strong> menus or <strong>multi-select</strong> (Ctrl/Shift) to delete conversations. Click the <strong>+ button</strong> to create a new one.',
            placement: 'right'
        },
        {
            target: '[data-tour-id="notebook.sidebar.files"]',
            content: 'The <strong>Files</strong> section contains documents you can <strong>drag and drop</strong> into your conversation to attach them to messages.',
            placement: 'right'
        },
        {
            target: '[data-tour-id="conversation.header"]',
            content: 'The conversation header shows the <strong>Stop button</strong> (when streaming), <strong>observers</strong> (who else is viewing), and the <strong>assistant selector</strong>.',
            placement: 'bottom'
        },
        {
            target: '[data-tour-id="conversation.assistant.selector"]',
            content: 'The <strong>guide can automatically use crew members</strong> to help you. For more control and better results, you can <strong>click here to select a specific crew member</strong> before sending your message. Each crew member specializes in different types of tasks.',
            placement: 'left',
            onHighlight: () => {
                // Delay to let driver.js finish its event handling
                setTimeout(() => {
                    const button = document.querySelector('[data-tour-id="conversation.assistant.selector"] button') as HTMLElement;
                    if (button) {
                        button.click();
                        
                        // Fix positioning to escape overflow:hidden ancestors
                        setTimeout(() => {
                            const dropdown = document.querySelector('[data-tour-id="conversation.assistant.selector.dropdown"]') as HTMLElement;
                            if (dropdown) {
                                const btnRect = button.getBoundingClientRect();
                                dropdown.style.position = 'fixed';
                                dropdown.style.top = `${btnRect.bottom + 4}px`;
                                dropdown.style.right = 'auto';
                                dropdown.style.left = `${btnRect.right - 320}px`;
                                dropdown.style.zIndex = '100001';
                            }
                        }, 10);
                    }
                }, 150);
            },
            onDeselect: () => {
                const dropdownPanel = document.querySelector('[data-tour-id="conversation.assistant.selector.dropdown"]');
                if (dropdownPanel) {
                    const button = document.querySelector('[data-tour-id="conversation.assistant.selector"] button') as HTMLElement;
                    if (button) {
                        button.click();
                    }
                }
            }
        },
        {
            target: '[data-tour-id="conversation.messages"]',
            content: 'This is your <strong>conversation history</strong>. Messages from you and the AI assistant appear here in chronological order.',
            placement: 'top'
        },
        {
            target: '[data-tour-id="conversation.editor"]',
            content: 'Use this <strong>rich text editor</strong> to compose your messages. You can <strong>format text</strong>, <strong>drag files from the sidebar</strong>, <strong>paste images</strong>, and type <strong>@</strong> to select a specific crew member for your message.',
            placement: 'top'
        }
    ]);

    // Register cells tour (sidebar + cells features)
    useRegisterTour('notebook-cells', [
        {
            target: '[data-tour-id="notebook.sidebar.search"]',
            content: 'Use the <strong>search bar</strong> to quickly find conversations, files, and links in your notebook.',
            placement: 'right'
        },
        {
            target: '[data-tour-id="notebook.sidebar.conversations"]',
            content: 'The <strong>Conversations</strong> section shows all your AI assistant chats. Click the + button to create a new conversation and start working with AI guidance.',
            placement: 'right'
        },
        {
            target: '[data-tour-id="notebook.folder.root"]',
            content: 'The <strong>Files</strong> section manages your documents. <strong>Double-click</strong> to preview or edit. Use <strong>Arrow keys</strong> to navigate. Press <strong>F2</strong> to <strong>Rename</strong> or <strong>Right-click</strong> for more options.',
            placement: 'right',
            popoverOffset: 20,
            onHighlight: () => {
                // Delay to let driver.js finish its event handling before opening menu
                setTimeout(() => {
                    const rootFolder = document.querySelector('[data-tour-id="notebook.folder.root"]') as HTMLElement;
                    if (rootFolder) {
                        const rect = rootFolder.getBoundingClientRect();
                        const contextMenuEvent = new MouseEvent('contextmenu', {
                            bubbles: true,
                            cancelable: true,
                            view: window,
                            button: 2,
                            buttons: 2,
                            clientX: rect.left + rect.width / 2,
                            clientY: rect.top + rect.height / 2
                        });
                        rootFolder.dispatchEvent(contextMenuEvent);
                        
                        // Ensure menu is above tour overlay
                        setTimeout(() => {
                            const menu = document.querySelector('[data-tour-id="notebook.folder.context-menu"]') as HTMLElement;
                            if (menu) {
                                menu.style.zIndex = '10001';
                            }
                        }, 50);
                    }
                }, 100);
            },
            onDeselect: () => {
                window.dispatchEvent(new Event('close-context-menus'));
            }
        },
        {
            target: '[data-tour-id="notebook.sidebar.files"]',
            content: 'Right-click the root folder to access the <strong>folder context menu</strong>: <strong>Upload files</strong>, <strong>create new markdown files</strong>, <strong>create subfolders</strong>, or <strong>rename/delete</strong> the folder.',
            placement: 'right',
            spotlightPadding: 0,
            popoverOffset: 10,
            popoverAlignOffset: 65,
            onHighlight: () => {
                // Delay to let driver.js finish its event handling before opening menu
                setTimeout(() => {
                    const rootFolder = document.querySelector('[data-tour-id="notebook.folder.root"]') as HTMLElement;
                    if (rootFolder) {
                        const rect = rootFolder.getBoundingClientRect();
                        const contextMenuEvent = new MouseEvent('contextmenu', {
                            bubbles: true,
                            cancelable: true,
                            view: window,
                            button: 2,
                            buttons: 2,
                            clientX: rect.left + rect.width / 2,
                            clientY: rect.top + rect.height / 2
                        });
                        rootFolder.dispatchEvent(contextMenuEvent);
                        
                        // Ensure menu is visible above tour overlay
                        setTimeout(() => {
                            const menu = document.querySelector('[data-tour-id="notebook.folder.context-menu"]');
                            if (menu) {
                                (menu as HTMLElement).style.zIndex = '10001';
                            }
                        }, 50);
                    }
                }, 100);
            },
            onDeselect: () => {
                // Close context menu when leaving this step
                window.dispatchEvent(new Event('close-context-menus'));
            }
        },
        {
            target: '[data-tour-id="notebook.cells.content"]',
            content: 'This is the <strong>notebook cells view</strong> where you can create and edit code, markdown, and text cells. Select a conversation from the sidebar to start working with AI assistance.',
            placement: 'top'
        }
    ]);

    // Register home tour (sidebar + home content)
    useRegisterTour('notebook-home', [
        {
            target: '[data-tour-id="notebook.sidebar.search"]',
            content: 'Use the <strong>search bar</strong> to quickly find conversations, files, and links in your notebook.',
            placement: 'right'
        },
        {
            target: '[data-tour-id="notebook.sidebar.conversations"]',
            content: 'The <strong>Conversations</strong> section shows all your AI assistant chats. Click the + button to create a new conversation and get started with this guide.',
            placement: 'right'
        },
        {
            target: '[data-tour-id="notebook.folder.root"]',
            content: 'The <strong>Files</strong> section manages your documents. <strong>Double-click</strong> to preview or edit. Use <strong>Arrow keys</strong> to navigate. Press <strong>F2</strong> to <strong>Rename</strong> or <strong>Right-click</strong> for more options.',
            placement: 'right',
            popoverOffset: 20,
            onHighlight: () => {
                // Delay to let driver.js finish its event handling before opening menu
                setTimeout(() => {
                    const rootFolder = document.querySelector('[data-tour-id="notebook.folder.root"]') as HTMLElement;
                    if (rootFolder) {
                        const rect = rootFolder.getBoundingClientRect();
                        const contextMenuEvent = new MouseEvent('contextmenu', {
                            bubbles: true,
                            cancelable: true,
                            view: window,
                            button: 2,
                            buttons: 2,
                            clientX: rect.left + rect.width / 2,
                            clientY: rect.top + rect.height / 2
                        });
                        rootFolder.dispatchEvent(contextMenuEvent);
                        
                        // Ensure menu is above tour overlay
                        setTimeout(() => {
                            const menu = document.querySelector('[data-tour-id="notebook.folder.context-menu"]') as HTMLElement;
                            if (menu) {
                                menu.style.zIndex = '10001';
                            }
                        }, 50);
                    }
                }, 100);
            },
            onDeselect: () => {
                window.dispatchEvent(new Event('close-context-menus'));
            }
        },
        {
            target: '[data-tour-id="notebook.sidebar.files"]',
            content: 'Right-click the root folder to access the <strong>folder context menu</strong>: <strong>Upload files</strong>, <strong>create new markdown files</strong>, <strong>create subfolders</strong>, or <strong>rename/delete</strong> the folder.',
            placement: 'right',
            spotlightPadding: 0,
            popoverOffset: 10,
            popoverAlignOffset: 65,
            onHighlight: () => {
                // Delay to let driver.js finish its event handling before opening menu
                setTimeout(() => {
                    const rootFolder = document.querySelector('[data-tour-id="notebook.folder.root"]') as HTMLElement;
                    if (rootFolder) {
                        const rect = rootFolder.getBoundingClientRect();
                        const contextMenuEvent = new MouseEvent('contextmenu', {
                            bubbles: true,
                            cancelable: true,
                            view: window,
                            button: 2,
                            buttons: 2,
                            clientX: rect.left + rect.width / 2,
                            clientY: rect.top + rect.height / 2
                        });
                        rootFolder.dispatchEvent(contextMenuEvent);
                        
                        // Ensure menu is visible above tour overlay
                        setTimeout(() => {
                            const menu = document.querySelector('[data-tour-id="notebook.folder.context-menu"]');
                            if (menu) {
                                (menu as HTMLElement).style.zIndex = '10001';
                            }
                        }, 50);
                    }
                }, 100);
            },
            onDeselect: () => {
                // Close context menu when leaving this step
                window.dispatchEvent(new Event('close-context-menus'));
            }
        },
        {
            target: '[data-tour-id="notebook.home.content"]',
            content: 'This is the <strong>notebook homepage</strong> with information about this guide template. You can customize the homepage by right-clicking any markdown file in the Files section and selecting "Set as Homepage".',
            placement: 'top'
        }
    ]);

    // Register file preview tour (when a file is opened from sidebar)
    useRegisterTour('notebook-file-preview', [
        {
            target: '[data-tour-id="file-preview.header"]',
            content: 'The <strong>file preview</strong> lets you view documents directly in your notebook without leaving the page.',
            placement: 'bottom'
        },
        {
            target: '[data-tour-id="file-preview.controls"]',
            content: 'Use these controls to <strong>edit</strong> (for markdown files), toggle <strong>full screen</strong>, or <strong>close</strong> the preview.',
            placement: 'left'
        },
        {
            target: '[data-tour-id="file-preview.tabs"]',
            content: 'For supported file types (PDFs, Word docs, etc.), you can view the <strong>original content</strong> or switch to <strong>extracted text</strong> for easier reading and AI analysis.',
            placement: 'bottom'
        },
        {
            target: '[data-tour-id="file-preview.content"]',
            content: 'The <strong>preview area</strong> displays your file content. Images, videos, PDFs, code, and markdown files are all supported.',
            placement: 'top'
        }
    ]);

    if (isLoading || !authCheckComplete) {
        return <LoadingSpinner message="Loading notebook..." />;
    }

    if (error) {
        return (
            <ErrorScreen
                error={error}
                title="Failed to Load Notebook"
                onRetry={() => window.location.reload()}
                onBack={handleBack}
            />
        );
    }

    if (!project) {
        return (
            <ErrorScreen
                error="Project not found"
                title="Project Not Found"
                onBack={() => navigate('/')}
            />
        );
    }

    if (!notebook) {
        return (
            <ErrorScreen
                error="Notebook not found"
                title="Notebook Not Found"
                onBack={handleBack}
            />
        );
    }

    // Find the notebook in the project to get its updated title and description
    const projectNotebook = project.notebooks.find(n => n.id === notebookId);
    const notebookTitle = projectNotebook?.title || notebook.title || 'Untitled Notebook';

    // Create a notebook object with updated data from project context
    const notebookWithUpdatedData = notebook ? {
        ...notebook,
        title: projectNotebook?.title || notebook.title,
        description: projectNotebook?.description || notebook.description,
        guideId: projectNotebook?.guideId ?? (notebook as any).guideId
    } : notebook;

    // Show auth interstitial if needed
    if (showAuthInterstitial && notebookTemplate && projectId && notebookId) {
        return (
            <NotebookAuthInterstitial
                projectId={projectId}
                notebookId={notebookId}
                notebookTitle={notebookTitle}
                template={notebookTemplate}
                onAuthComplete={handleAuthComplete}
            />
        );
    }

    return (
        <>
        <NotebookLayout
            project={project}
            notebook={notebookWithUpdatedData}
            notebookTemplate={notebookTemplate || undefined}
            onBack={handleBack}
            onEdit={canEdit() ? handleEdit : undefined}
            canEdit={canEdit()}
            tourScreenId={currentTourScreenId}
            headerCenter={
                projectId && notebookId ? (
                    <NotebookServiceToolbar
                        projectId={projectId}
                        notebookId={notebookId}
                        conversationId={activeConversationId}
                        data={headerToolbar.data}
                        isLoading={headerToolbar.isLoading}
                        isMobile={headerIsMobile}
                        onRefresh={() => headerToolbar.refresh()}
                        inFlight={headerToolbar.inFlight}
                        setInFlight={headerToolbar.setInFlight}
                        assistantByName={assistantByNameForToolbar}
                        onMobileOpen={() => setIsMobileSidebarOpen(false)}
                    />
                ) : null
            }
            // Mobile Sidebar Props
            isMobileSidebarOpen={isMobileSidebarOpen}
            onMobileSidebarToggle={() => setIsMobileSidebarOpen(!isMobileSidebarOpen)}
            onMobileSidebarClose={() => setIsMobileSidebarOpen(false)}
            sidebar={
                <SidebarContainer>
                    <NotebookSidebar 
                        folderTree={undefined}
                        projectFolderTree={projectFolderTree}
                        notebookName={notebookTitle}
                        collaboratorCount={1}
                        links={project.links || []}
                        expandedSections={expandedSections}
                        selectedItem={selectedItem}
                        onSectionToggle={handleSectionToggle}
                        onItemSelect={handleItemSelect}
                        onUploadFiles={uploadFiles}
                        onCreateFolder={createFolder}
                        onDeleteFolder={deleteFolder}
                        onRenameFolder={renameFolder}
                        onDeleteFile={deleteFile}
                        onRenameFile={renameFile}
                        onMoveFile={moveFile}
                        onCopyFromProject={copyFromProject}
                        onPublishToProject={handlePublishToProject}
                        onPreviewFile={handlePreviewFile}
                        
                        onConversationDeleted={handleConversationDeleted}
                        canEdit={canEdit()}
                        homePageFileId={notebook?.homePageFileId}
                        onSetHomePage={handleSetHomePage}
                    />
                </SidebarContainer>
            }
            content={
                <>
                    {activeConversationId ? (
                        <ConversationProvider
                          key={activeConversationId}
                          projectId={projectId!}
                          notebookId={notebookId!}
                          conversationId={activeConversationId}
                          guideId={(notebookWithUpdatedData as any)?.guideId}
                          assistants={assistants}
                          notebookTemplate={notebookTemplate || undefined}
                          onPreviewFile={handlePreviewFileById}
                          onPreviewFileByPath={handlePreviewFileByPath}
                        >
                          <ConversationPanel 
                            key={activeConversationId}
                            projectId={projectId!}
                            notebookId={notebookId!} 
                            conversationId={activeConversationId} 
                            canEdit={canEdit()}
                            collaboratorCount={1}
                            isRuntimeLoading={isPolling}
                            onNewConversation={(newConvoId) => handleItemSelect('conversations', newConvoId)}
                          />
                        </ConversationProvider>
                ) : homePageContent ? (
                    // Display user-set home page (markdown file)
                    <div className="h-full w-full flex flex-col mt-4 p-8" data-tour-id="notebook.home.content">
                        <div className="flex-1 overflow-auto">
                            <MarkdownViewer 
                                text={homePageContent} 
                                className="h-full p-4 overflow-auto" 
                                inlineMode={true}
                                projectId={projectId}
                                notebookId={notebookId}
                                basePath={homePageFile?.relativePath?.includes('/') 
                                    ? homePageFile.relativePath.substring(0, homePageFile.relativePath.lastIndexOf('/'))
                                    : undefined}
                            />
                        </div>
                    </div>
                ) : homePageFile ? (
                    // Display user-set home page (non-markdown file via preview)
                    <FilePreviewOverlay
                        file={homePageFile}
                        projectId={projectId!}
                        notebookId={notebookId!}
                        onClose={() => {}}
                        isEmbedded={true}
                        onNavigate={handlePreviewNavigate}
                        fileExists={fileExistsInTree}
                    />
                ) : notebookTemplate?.homeContent ? (
                    // Display guide template's home content
                    <div className="h-full w-full flex flex-col mt-4 p-8" data-tour-id="notebook.home.content">
                        <div className="flex-1 overflow-auto">
                            <MarkdownViewer text={notebookTemplate.homeContent} className="h-full p-4 overflow-auto" inlineMode={true} />
                        </div>
                    </div>
                ) : (
                    <NotebookContent 
                        notebook={projectNotebook || { id: notebookId!, title: notebookTitle, guideId: '' }}
                        canEdit={canEdit()}
                    />
                )}
                </>
            }
        />
        <LlamaRuntimeModal
            isOpen={runtimeModalOpen}
            onClose={() => {
                if (!isPolling) setRuntimeModalOpen(false);
            }}
            status={runtimeStatus}
            onStartLoad={handleStartLoad}
            isPolling={isPolling}
        />
        <LlamaCrashedModal
            isOpen={crashState !== null}
            reason={crashState?.reason ?? 'Crashed'}
            upstreamDetail={crashState?.upstreamDetail ?? null}
            onClose={() => setCrashState(null)}
            onRestart={async () => {
                if (!projectId || !notebookId) {
                    throw new Error('Notebook context is not available.');
                }
                await api.projects.notebooks.conversations.restartLlamaRuntime(projectId, notebookId);
            }}
            onAfterRestart={() => {
                // Transition: crash modal -> model-load modal. We flip the runtime status into
                // requires_load so the existing LlamaRuntimeModal surfaces the normal model
                // picker and the user explicitly re-loads the model. We do *not* auto-load the
                // previously-selected alias: OOMs tend to recur with the same config, so the
                // user gets a chance to pick a smaller model or change context size first.
                setCrashState(null);
                runtimeCheckedNotebookRef.current = null;
                const runtimeStatusPayload = { state: 'requires_load' };
                window.dispatchEvent(new CustomEvent('llama-runtime-requires-load', {
                    detail: {
                        runtimeStatus: runtimeStatusPayload,
                        assistantId: targetAssistantId
                    }
                }));
            }}
        />
        {fileToPreview && (
            <FilePreviewOverlay
                file={fileToPreview}
                projectId={projectId!}
                notebookId={notebookId!}
                onClose={() => setFileToPreview(null)}
                onNavigate={handlePreviewNavigate}
                fileExists={fileExistsInTree}
            />
        )}
        <PublishToProjectDialog
            isOpen={publishDialogState.isOpen}
            onClose={() => setPublishDialogState({ isOpen: false, notebookFiles: [], originFileInfoMap: new Map() })}
            notebookFiles={publishDialogState.notebookFiles}
            projectFolders={project?.folders || []}
            projectTitle={project?.title || 'Project'}
            onPublish={handlePublishSubmit}
            onComplete={handlePublishComplete}
            originFileInfoMap={publishDialogState.originFileInfoMap}
        />
        </>
    );
}

// Main component with providers
export default function NotebookDetails() {
    const { projectId, notebookId } = useParams<{ projectId: string; notebookId: string }>();

    if (!projectId || !notebookId) {
        return (
            <ErrorScreen
                error="Invalid notebook URL"
                title="Invalid URL"
                onBack={() => window.history.back()}
            />
        );
    }

    return (
        <NotebookProvider notebookId={notebookId} projectId={projectId}>
            <NotebookDetailsContent />
        </NotebookProvider>
    );
}
