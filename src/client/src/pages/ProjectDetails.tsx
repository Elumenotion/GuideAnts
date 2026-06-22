import { useParams, useNavigate, useLocation } from 'react-router-dom';
import { useProject } from '../contexts/ProjectContext';
import { useFolderOperations } from '../hooks/useFolderOperations';
import { ProjectLayout } from '../components/layouts/ProjectLayout';
import { SidebarContainer } from '../components/layouts/SidebarContainer';
import { ProjectSidebar } from '../components/project/sidebar/ProjectSidebar';
import {
    ContentFileContent,
    LinkContent,
    ArtifactContent
} from '../components/project/content';
import { AddLinkForm } from '../components/project/content/AddLinkForm';
import { AddFolderContent } from '../components/project/content/AddFolderContent';
import { SectionType, CreateFolderDto } from '../types/project';
import { api } from '../services/api';
import { useState, useEffect, useCallback, useMemo } from 'react';
import LoadingSpinner from '../components/LoadingSpinner';
import ErrorScreen from '../components/ErrorScreen';
import { FileInUseDialog } from '../components/common/FileInUseDialog';
import { FileUsageInNotebookDto } from '../types/api';
import { useProjectFilesPolling } from '../hooks/useProjectFilesPolling';
import { useProjectNotebooksPolling } from '../hooks/useProjectNotebooksPolling';
import { useToast } from '../components/common/Toast';
import MarkdownViewer from '../components/common/MarkdownViewer';
import DefaultProjectHomeMd from '../content/default-project-home.md?raw';
import ProjectGuideAuthContent from '../components/project/content/ProjectGuideAuthContent';
import { useRegisterTour } from '../tour/useRegisterTour';



export default function ProjectDetails() {
    const { projectId: routeProjectId } = useParams<{ projectId: string }>();
    const navigate = useNavigate();
    const location = useLocation();
    const {
        projectId: contextProjectId,
        project,
        error,
        isLoading,
        selectedItem,
        expandedSections,
        setSelectedItem,
        toggleSection,
        refreshProject,
        renameFile,
        canEdit,
        isOwner,

    } = useProject();
    const projectId = routeProjectId ?? contextProjectId;

    const { showToast } = useToast();

    // Guided tour registration for Project Details
    const SCREEN_ID = 'projectDetails';
    useRegisterTour(SCREEN_ID, [
        {
            target: '[data-tour-id="project.sidebar"]',
            content: 'Welcome to your project! This <strong>sidebar</strong> helps you <strong>navigate</strong> and <strong>organize</strong> all your work. GuideAnts Notebooks helps you <strong>organize</strong>, <strong>collaborate</strong>, and <strong>create</strong> with structure and guidance.'
        },
        {
            target: '[data-tour-id="project.sidebar"]',
            content: 'A <strong>Project</strong> is the main container for all your work on a specific initiative or topic. Within each project, you can <strong>create</strong> and <strong>manage</strong> <strong>Notebooks</strong>, <strong>Files</strong>, and <strong>Collaborators</strong>.'
        },
        {
            target: '[data-tour-id="sidebar.section.notebooks"]',
            content: '📒 <strong>Notebooks</strong>: Find your notebooks here. Notebooks help you <strong>structure</strong> work into distinct areas (like research, planning, or content creation). Each notebook can have <strong>conversations</strong>, <strong>tasks</strong>, and its own set of <strong>files</strong>. Start a new notebook with the <strong>"New Notebook"</strong> button when available.'
        },
        {
            target: '[data-tour-id="sidebar.notebook.item"]',
            content: '<strong>Double-click</strong> to open a notebook. <strong>Right-click</strong> or press <strong>F2</strong> to <strong>Rename</strong>. Use <strong>Arrow keys</strong> to navigate and <strong>Ctrl/Cmd/Shift</strong> to select <strong>multiple notebooks</strong> for bulk actions.'
        },
        {
            target: '[data-tour-id="sidebar.section.files"]',
            content: '📁 <strong>Files</strong>: All project files are <strong>managed</strong> in the Files section. <strong>Upload</strong> files from your computer or <strong>move</strong> files in and out of notebooks. <strong>Preview</strong>, <strong>download</strong>, <strong>version history</strong>, and <strong>actions</strong> are available for each file.'
        },
        {
            target: '[data-tour-id="sidebar.folder.root"]',
            content: 'This <strong>context menu</strong> provides powerful options: <strong>Create new Markdown files</strong>, <strong>upload files</strong>, <strong>create subfolders</strong>, <strong>rename</strong>, or <strong>delete</strong>. <strong>Markdown files</strong> can be <strong>created</strong> and <strong>edited</strong> directly in the editor.',
            placement: 'right',
            popoverOffset: 20,
            onHighlight: () => {
                // Delay to let driver.js finish its event handling before opening menu
                setTimeout(() => {
                    const rootFolder = document.querySelector('[data-tour-id="sidebar.folder.root"]') as HTMLElement;
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
                            const menu = document.querySelector('[data-tour-id="folder.context-menu"]') as HTMLElement;
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
            target: '[data-tour-id="sidebar.file.item"]',
            content: 'The <strong>file context menu</strong> offers many options. Most importantly, you can <strong>set any file as your project\'s home page</strong>. This means when you or others visit the project, they\'ll see that file instead of the default home page. Perfect for <strong>customizing</strong> the <strong>welcome experience</strong>!',
            placement: 'right',
            popoverOffset: 20,
            onHighlight: () => {
                // Delay to let driver.js finish its event handling before opening menu
                setTimeout(() => {
                    const fileItem = document.querySelector('[data-tour-id="sidebar.file.item"]') as HTMLElement;
                    if (fileItem) {
                        const rect = fileItem.getBoundingClientRect();
                        const contextMenuEvent = new MouseEvent('contextmenu', {
                            bubbles: true,
                            cancelable: true,
                            view: window,
                            button: 2,
                            buttons: 2,
                            clientX: rect.left + rect.width / 2,
                            clientY: rect.top + rect.height / 2
                        });
                        fileItem.dispatchEvent(contextMenuEvent);
                        
                        // Ensure menu is above tour overlay
                        setTimeout(() => {
                            const menu = document.querySelector('[data-tour-id="file.context-menu"]') as HTMLElement;
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
            target: '[data-tour-id="sidebar.folder.root"]',
            content: '<strong>Double-click</strong> to open files. <strong>Right-click</strong> or press <strong>F2</strong> to <strong>Rename</strong>. Use <strong>Arrow keys</strong> to navigate and <strong>Ctrl/Cmd/Shift</strong> to select <strong>multiple files</strong> for bulk actions like <strong>move</strong> or <strong>delete</strong>.',
            placement: 'right',
            popoverOffset: 20,
            onHighlight: () => {
                // Delay to let driver.js finish its event handling before opening menu
                setTimeout(() => {
                    const rootFolder = document.querySelector('[data-tour-id="sidebar.folder.root"]') as HTMLElement;
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
                            const menu = document.querySelector('[data-tour-id="folder.context-menu"]') as HTMLElement;
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
            target: '[data-tour-id="sidebar.section.members"]',
            content: '👥 <strong>Collaborators</strong>: <strong>View</strong> and <strong>manage</strong> your project\'s collaborators. <strong>Invite</strong> others by email and <strong>assign</strong> them as <strong>Contributors</strong> (can edit) or <strong>Readers</strong> (can view). <strong>Project owners</strong> oversee <strong>access</strong>, <strong>usage</strong>, and <strong>billing</strong>.'
        },
        {
            target: '[data-tour-id="sidebar.section.guides"]',
            content: '🧭 <strong>Guides</strong>: <strong>Create</strong> and <strong>manage</strong> owner-level <strong>Guides</strong> and <strong>Assistants</strong> to standardize workflows across projects. Available to <strong>project owners</strong>.'
        },
        {
            target: '[data-tour-id="project.content"]',
            content: 'The main <strong>content area</strong> displays the selected item. Here you can <strong>view</strong> and <strong>edit</strong> files, <strong>manage</strong> collaborators, <strong>preview</strong> links, and <strong>work</strong> with your notebooks. When nothing is selected, you\'ll see the <strong>project homepage</strong>.'
        },
        {
            target: '[data-tour-id="project.content"]',
            content: '<strong>Next steps</strong>: <strong>Create</strong> a notebook to try meeting notes or explore guides. <strong>Upload</strong> a file to see how assistants can help analyze or enhance it. <strong>Right-click</strong> a file to <strong>set it as the project homepage</strong>. <strong>Invite</strong> collaborators to <strong>collaborate</strong>!'
        }
    ]);

    // Add sidebar polling hooks locally (moved from ProjectContext to prevent provider remounts)
    const {
        folderTree: sidebarFolderTree,
        refresh: refreshSidebarFiles
    } = useProjectFilesPolling({
        projectId: projectId || '',
        enabled: !!(projectId && project),
        pollInterval: 60000
    });

    const {
        notebooks: sidebarNotebooks,
        refresh: refreshSidebarNotebooks
    } = useProjectNotebooksPolling({
        projectId: projectId || '',
        enabled: !!(projectId && project),
        pollInterval: 60000
    });

    const [isAddingLink, setIsAddingLink] = useState(false);
    const [isAddingFolder, setIsAddingFolder] = useState(false);
    const [isEditing, setIsEditing] = useState(false);
    const [showFileInUseDialog, setShowFileInUseDialog] = useState(false);
    const [fileInUseData, setFileInUseData] = useState<{
        fileName: string;
        notebooksUsingFile: FileUsageInNotebookDto[];
    } | null>(null);

    // Add folder operations hook
    const {
        createFolder,
        updateFolder,
        deleteFolder,
        moveFile,
        uploadFiles: uploadFilesToFolder,
        fetchFolderTree
    } = useFolderOperations(projectId || '');

    // Stable, non-remounting default home content (bundled markdown)
    const defaultHomeContent = useMemo(() => (
        <div className="h-full w-full overflow-auto flex flex-col p-8">
            <MarkdownViewer text={DefaultProjectHomeMd} className="h-full p-4" inlineMode={true} />
        </div>
    ), []);

    // Load folder tree when project is available
    useEffect(() => {
        if (projectId) {
            fetchFolderTree();
        }
    }, [projectId, fetchFolderTree]);

    

    // Handle section/item navigation (for OAuth callback returns)
    useEffect(() => {
        const searchParams = new URLSearchParams(location.search);
        const section = searchParams.get('section');
        const item = searchParams.get('item');
        
        if (section && item && section === 'guideAuthorization') {
            setSelectedItem({ type: 'guideAuthorization', id: item });
            // Clean up the navigation parameters (keep oauthSuccess for toast)
            searchParams.delete('section');
            searchParams.delete('item');
            const newSearch = searchParams.toString();
            const newUrl = newSearch ? `${location.pathname}?${newSearch}` : location.pathname;
            navigate(newUrl, { replace: true });
        }
    }, [location.search, setSelectedItem, navigate, location.pathname]);

        // Auto-select project home page file if present and nothing is selected
    const [isAutoHome, setIsAutoHome] = useState(false);

    useEffect(() => {
        if (project && project.homePageContentFileId && !selectedItem) {
            setSelectedItem({ type: 'contentFiles', id: project.homePageContentFileId });
            setIsAutoHome(true);
        }
    }, [project, selectedItem, setSelectedItem]);

    // Stable callback for file deletion to prevent preview re-renders
    const handleFileDeleted = useCallback(async () => {
        await refreshProject();
        await fetchFolderTree();
        setSelectedItem(null);
    }, [refreshProject, fetchFolderTree, setSelectedItem]);

    const handleSetHomePage = async (fileId: string | null) => {
        if (!projectId) return;
        try {
            if (fileId) {
                await api.projects.setHomePage(projectId, fileId);
            } else {
                await api.projects.clearHomePage(projectId);
            }
            await refreshProject();
        } catch (error) {
            console.error('Failed to set project home page:', error);
            showToast({
                type: 'error',
                title: 'Failed to set project home page',
                message: 'Please try again.'
            });
        }
    };

    const handleFileUpload = async (files: FileList) => {
        if (!projectId) return;
        try {
            const uploadedFiles = await api.projects.uploadFiles(projectId, Array.from(files));
            await refreshProject();
            // Select the first uploaded file
            if (uploadedFiles.length > 0) {
                setSelectedItem({ type: 'contentFiles', id: uploadedFiles[0].id });
            }
        } catch (error) {
            console.error('Failed to upload files:', error);
            showToast({
                type: 'error',
                title: 'Failed to upload files',
                message: 'Please try again or contact support if the issue persists.'
            });
        }
    };

    // Folder operation handlers
    const handleCreateFolder = async (parentFolderId: string | undefined, folderData: CreateFolderDto) => {
        try {
            const folderDataWithParent = {
                ...folderData,
                parentFolderId
            };
            await createFolder(folderDataWithParent);
            // Refresh sidebar files immediately after creating folder
            refreshSidebarFiles();
        } catch (error) {
            console.error('Failed to create folder:', error);
            showToast({
                type: 'error',
                title: 'Failed to create folder',
                message: 'Please try again.'
            });
        }
    };

    const handleAddFolder = async (folderData: CreateFolderDto) => {
        try {
            await createFolder(folderData);
            await refreshProject();
            setIsAddingFolder(false);
        } catch (error) {
            console.error('Failed to create folder:', error);
            throw error; // Let AddFolderContent handle the error display
        }
    };

    const handleRenameFolder = async (folderId: string, newName: string) => {
        try {
            await updateFolder(folderId, { name: newName });
            // Refresh sidebar files immediately after renaming folder
            refreshSidebarFiles();
            // No need to refresh the whole project and cause a flash/reload
        } catch (error) {
            console.error('Failed to rename folder:', error);
            showToast({
                type: 'error',
                title: 'Failed to rename folder',
                message: 'Please try again.'
            });
        }
    };

    const handleDeleteFolder = async (folderId: string) => {
        try {
            await deleteFolder(folderId);
            // Refresh sidebar files immediately after deleting folder
            refreshSidebarFiles();
        } catch (error) {
            console.error('Failed to delete folder:', error);
            showToast({
                type: 'error',
                title: 'Failed to delete folder',
                message: 'Please try again.'
            });
        }
    };

    const handleMoveFile = async (fileId: string, destinationFolderId: string | undefined) => {
        try {
            await moveFile(fileId, { destinationFolderId });
            // Refresh sidebar files immediately after moving file
            refreshSidebarFiles();
        } catch (error) {
            console.error('Failed to move file:', error);
            showToast({
                type: 'error',
                title: 'Failed to move file',
                message: 'Please try again.'
            });
        }
    };

    const handleUploadFilesToFolder = async (files: File[], folderId?: string) => {
        try {
            const uploadedFiles = await uploadFilesToFolder(files, folderId);
            // Refresh sidebar files immediately after uploading files
            refreshSidebarFiles();
            // Select the first uploaded file
            if (uploadedFiles.length > 0) {
                setSelectedItem({ type: 'contentFiles', id: uploadedFiles[0].id });
            }
        } catch (error) {
            console.error('Failed to upload files:', error);
            showToast({
                type: 'error',
                title: 'Failed to upload files',
                message: 'Please try again.'
            });
        }
    };

    const handleItemSelect = (type: SectionType, id: string) => {
        if (type === 'notebooks') {
            // Navigate to notebook details page
            navigate(`/projects/${projectId}/notebooks/${id}`);
            return;
        }
        
        setSelectedItem({ type, id });
        setIsAutoHome(false);
        setIsAddingLink(false);
        setIsAddingFolder(false);
    };

    const handleDeleteFile = useCallback(async (fileId: string) => {
        if (!canEdit()) return;
        
        try {
            await api.projects.deleteContentFile(projectId || '', fileId);

            // Refresh sidebar files immediately after deleting file
            refreshSidebarFiles();

            // Clear selection if it was the deleted file
            if (selectedItem?.type === 'contentFiles' && selectedItem.id === fileId) {
                setSelectedItem(null);
            }
        } catch (error: any) {
            // Check if this is a file-in-use error
            if (error.isFileInUse && error.notebooksUsingFile) {
                // Find the file name from the project files
                const file = project?.contentFiles.find(f => f.id === fileId);
                setFileInUseData({
                    fileName: file?.fileName || 'Unknown File',
                    notebooksUsingFile: error.notebooksUsingFile
                });
                setShowFileInUseDialog(true);
            } else {
                console.error('Failed to delete file:', error);
                showToast({
                    type: 'error',
                    title: 'Failed to delete file',
                    message: 'Please try again.'
                });
            }
        }
    }, [canEdit, projectId, refreshSidebarFiles, refreshProject, selectedItem, setSelectedItem, project]);

    const handleRenameFile = async (fileId: string, newName: string) => {
        if (!canEdit()) return;
        
        try {
            await renameFile(fileId, newName);
            // Just refresh sidebar files, no need to refresh entire project which causes flash
            refreshSidebarFiles();
        } catch (error) {
            console.error('Failed to rename file:', error);
            showToast({
                type: 'error',
                title: 'Failed to rename file',
                message: 'Please try again.'
            });
        }
    };

    const handleDeleteLink = async (linkId: string) => {
        if (!canEdit()) return;
        try {
            await api.projects.deleteLink(projectId || '', linkId);
            await refreshProject();
            if (selectedItem?.type === 'links' && selectedItem.id === linkId) {
                setSelectedItem(null);
            }
        } catch (error) {
            console.error('Failed to delete link:', error);
            showToast({
                type: 'error',
                title: 'Failed to delete link',
                message: 'Please try again.'
            });
        }
    };

    // Notebook creation handler
    const handleCreateNotebook = async (title: string, guideId?: string, description?: string) => {
        if (!projectId) return;
        try {
            const notebook = await api.projects.createNotebook(projectId, { title, description, guideId });
            // Refresh sidebar notebooks immediately after creating
            refreshSidebarNotebooks();
            await refreshProject();
            navigate(`/projects/${projectId}/notebooks/${notebook.id}`);
        } catch (error) {
            console.error('Failed to create notebook:', error);
            showToast({
                type: 'error',
                title: 'Failed to create notebook',
                message: 'Please try again.'
            });
        }
    };

    const handleDeleteNotebook = async (notebookId: string) => {
        if (!projectId) return;
        try {
            await api.projects.deleteNotebook(projectId, notebookId);
            // Refresh sidebar notebooks immediately after deleting
            refreshSidebarNotebooks();
            await refreshProject(true);
            // clear selection if deleted
            if (selectedItem?.type === 'notebooks' && selectedItem.id === notebookId) {
                setSelectedItem(null);
            }
        } catch (error) {
            console.error('Failed to delete notebook:', error);
            showToast({
                type: 'error',
                title: 'Failed to delete notebook',
                message: 'Please try again.'
            });
        }
    };

    const handleDeleteNotebooks = async (notebookIds: string[]) => {
        if (!projectId) return;
        try {
            await Promise.all(notebookIds.map(id => api.projects.deleteNotebook(projectId, id)));
            refreshSidebarNotebooks();
            await refreshProject(true);
            if (selectedItem?.type === 'notebooks' && notebookIds.includes(selectedItem.id)) {
                setSelectedItem(null);
            }
        } catch (error) {
            console.error('Failed to delete notebooks:', error);
            showToast({
                type: 'error',
                title: 'Failed to delete notebooks',
                message: 'Please try again.'
            });
        }
    };

    const handleCopyNotebook = async (sourceNotebookId: string) => {
        if (!projectId || !project) return;
        const source = project.notebooks.find(n => n.id === sourceNotebookId);
        const title = source ? `Copy of ${source.title}` : 'Copied Notebook';
        try {
            const newNotebook = await api.projects.copyNotebook(projectId, {
                title,
                sourceNotebookId,
            });
            // Refresh sidebar notebooks immediately after copying
            refreshSidebarNotebooks();
            await refreshProject();
            setSelectedItem({ type: 'notebooks', id: newNotebook.id });
        } catch (error) {
            console.error('Failed to copy notebook:', error);
            showToast({
                type: 'error',
                title: 'Failed to copy notebook',
                message: 'Please try again.'
            });
        }
    };

    const handleRenameNotebook = async (notebookId: string, newTitle: string) => {
        if (!projectId) return;
        try {
            await api.projects.updateNotebook(projectId, notebookId, { title: newTitle });
            // Refresh sidebar notebooks immediately after renaming
            refreshSidebarNotebooks();
            // Don't refresh whole project to avoid flashing
            // await refreshProject();
        } catch (error) {
            console.error('Failed to rename notebook:', error);
            showToast({
                type: 'error',
                title: 'Failed to rename notebook',
                message: 'Please try again.'
            });
        }
    };

    // Create notebook from file handler (using standard creation + file copy)
    const handleCreateNotebookFromFile = async (
        fileId: string, 
        fileName: string,
        title: string, 
        guideId?: string,
        description?: string
    ) => {
        if (!projectId) return;
        
        try {
            // Step 1: Create notebook using the working API
            const notebook = await api.projects.createNotebook(projectId, { 
                title, 
                description, 
                guideId 
            });
            
            // Step 2: Copy the file to the new notebook
            await api.projects.notebooks.copyFileFromProject(
                projectId,
                notebook.id,
                fileId
            );
            
            // Step 3: Sync to ensure file is properly registered
            await api.projects.notebooks.syncFiles(projectId, notebook.id);
            
            // Refresh sidebar notebooks immediately after creating from file
            refreshSidebarNotebooks();
            // Refresh project and navigate to new notebook
            await refreshProject();
            
            // Navigate to the newly created notebook
            navigate(`/projects/${projectId}/notebooks/${notebook.id}`);
            
            // Show success message
            console.log(`Notebook "${notebook.title}" created with file "${fileName}"`);
            
        } catch (error) {
            console.error('Failed to create notebook from file:', error);
            
            // Refresh project to ensure UI consistency
            await refreshProject();
            
            showToast({
                type: 'error',
                title: 'Failed to create notebook from file',
                message: 'Please try again.'
            });
        }
    };

    const handleCreateNotebookFromFiles = async (
        files: { id: string; fileName: string }[], 
        title: string, 
        guideId?: string,
        description?: string
    ) => {
        if (!projectId) return;
        
        try {
            const notebook = await api.projects.createNotebook(projectId, { 
                title, 
                description, 
                guideId 
            });
            
            // Copy all files in parallel - each copy creates its own DB record
            await Promise.all(files.map(f => api.projects.notebooks.copyFileFromProject(
                projectId,
                notebook.id,
                f.id
            )));
            
            // Trigger a single sync after all copies complete to ensure consistency
            // This prevents race conditions from fire-and-forget syncs during parallel copies
            await api.projects.notebooks.syncFiles(projectId, notebook.id);
            
            refreshSidebarNotebooks();
            await refreshProject();
            
            navigate(`/projects/${projectId}/notebooks/${notebook.id}`);
            
        } catch (error) {
            console.error('Failed to create notebook from files:', error);
            await refreshProject();
            showToast({
                type: 'error',
                title: 'Failed to create notebook from files',
                message: 'Please try again.'
            });
        }
    };

    // Memoized lookup function for resolving relative paths to file IDs
    const resolveProjectFilePath = useCallback((relativePath: string): string | undefined => {
        if (!project) return undefined;
        
        // Try to find file by filename or relativePath field
        const file = project.contentFiles.find(f => 
            f.fileName === relativePath ||
            f.relativePath === relativePath ||
            f.fileName === relativePath.split('/').pop() // Try just the filename
        );
        
        return file?.id;
    }, [project?.contentFiles]); // Only recreate when contentFiles reference changes

    // Memoized content to prevent re-renders during sidebar polling
    const content = useMemo(() => {
        // TypeScript: guard against possibly null project during initial render
        if (!project) {
            return null;
        }
        if (isAddingLink) {
            return (
                <AddLinkForm
                    onAdd={async (url) => {
                        await api.projects.addLink(project.id, url);
                        await refreshProject();
                        setIsAddingLink(false);
                    }}
                    onCancel={() => setIsAddingLink(false)}
                />
            );
        }

        if (isAddingFolder) {
            return (
                <AddFolderContent
                    onAdd={handleAddFolder}
                    onCancel={() => setIsAddingFolder(false)}
                    folders={project.folders || []}
                    disabled={false}
                />
            );
        }

        if (!selectedItem) {
            return defaultHomeContent;
        }

        switch (selectedItem.type) {
            case 'notebooks': {
                // This should not be reached since notebooks now navigate to separate page
                return null;
            }
            case 'contentFiles': {
                const fileIdSel = selectedItem.id;
                return (
                    <ContentFileContent 
                        hideHeader={isAutoHome && fileIdSel === project.homePageContentFileId}
                        fileId={fileIdSel}
                        projectId={project.id}
                        canEdit={canEdit()}
                        onDelete={handleFileDeleted}
                        resolveProjectFilePath={resolveProjectFilePath}
                    />
                );
            }
            case 'links': {
                const link = project.links.find(l => l.id === selectedItem.id);
                return link ? (
                    <LinkContent
                        link={link}
                        onUpdate={async (linkId, updates) => {
                            await api.projects.updateLink(project.id, linkId, updates);
                            await refreshProject();
                        }}
                        onDelete={async (linkId) => {
                            await api.projects.deleteLink(project.id, linkId);
                            await refreshProject();
                            setSelectedItem(null);
                        }}
                        isEditing={isEditing}
                        onStartEdit={() => setIsEditing(true)}
                        onCancelEdit={() => setIsEditing(false)}
                        canEdit={canEdit()}
                    />
                ) : null;
            }
            case 'guideAuthorization': {
                return (
                    <ProjectGuideAuthContent
                        projectId={project.id}
                        templateId={selectedItem.id}
                        canEdit={isOwner()}
                    />
                );
            }
            case 'artifacts': {
                const artifact = project.semiStructuredDatas.find(a => a.id === selectedItem.id);
                return artifact ? <ArtifactContent artifact={artifact} canEdit={canEdit()} /> : null;
            }
            default:
                return <div>Unknown content type</div>;
        }
    }, [
        isAddingLink, isAddingFolder, selectedItem, project, 
        isAutoHome, canEdit, handleFileDeleted, handleItemSelect, 
        handleAddFolder, isEditing, setIsEditing,
        isOwner, refreshProject, setSelectedItem
    ]);



    if (isLoading) {
        return <LoadingSpinner message="Loading project details..." />;
    }

    if (error) {
        return (
            <ErrorScreen
                error={error}
                title="Failed to Load Project"
                onRetry={refreshProject}
                onBack={() => navigate('/')}
            />
        );
    }

    if (!project) {
        return <div className="text-center py-8">Project not found</div>;
    }

    return (
        <>
            <ProjectLayout
                project={project}
                onBack={() => navigate('/')}
                onEdit={() => navigate(`/projects/${project.id}/edit`)}
                canEdit={canEdit()}
                tourScreenId={SCREEN_ID}
                sidebar={
                    <SidebarContainer data-tour-id="project.sidebar">
                        <ProjectSidebar
                            project={project}
                            expandedSections={expandedSections}
                            selectedItem={selectedItem}
                            onSectionToggle={toggleSection}
                            onItemSelect={handleItemSelect}
                            onAddLink={() => canEdit() && setIsAddingLink(true)}
                            onUploadFiles={(files: FileList) => canEdit() && handleFileUpload(files)}
                            disabled={isEditing || isAddingLink || isAddingFolder}
                            isCurrentUserOwner={isOwner()}

                            // Folder management props  
                            folderTree={sidebarFolderTree}
                            notebooks={sidebarNotebooks}
                            onCreateFolder={canEdit() ? handleCreateFolder : undefined}
                            onRenameFolder={canEdit() ? handleRenameFolder : undefined}
                            onDeleteFolder={canEdit() ? handleDeleteFolder : undefined}
                            onMoveFile={canEdit() ? handleMoveFile : undefined}
                            onUploadFilesToFolder={canEdit() ? handleUploadFilesToFolder : undefined}
                            onDeleteFile={canEdit() ? handleDeleteFile : undefined}
                            onRenameFile={canEdit() ? handleRenameFile : undefined}
                            onDeleteLink={canEdit() ? handleDeleteLink : undefined}
                            onDeleteNotebook={canEdit() ? handleDeleteNotebook : undefined}
                            onDeleteNotebooks={canEdit() ? handleDeleteNotebooks : undefined}
                            onCopyNotebook={canEdit() ? handleCopyNotebook : undefined}
                            onRenameNotebook={canEdit() ? handleRenameNotebook : undefined}
                            onCreateNotebook={canEdit() ? handleCreateNotebook : undefined}
                            onCreateNotebookFromFile={canEdit() ? handleCreateNotebookFromFile : undefined}
                            onCreateNotebookFromFiles={canEdit() ? handleCreateNotebookFromFiles : undefined}
    
                                homePageContentFileId={project.homePageContentFileId}
                                onSetHomePage={canEdit() ? handleSetHomePage : undefined}
                                onFileContentChanged={(fileId, newVersion) => {
                                    // When sidebar saves a file, notify main content area if it's viewing that file
                                    window.dispatchEvent(new CustomEvent('file-version-updated', { 
                                        detail: { fileId, newVersion } 
                                    }));
                                }}
                        />
                    </SidebarContainer>
                }
                content={
                    <div data-tour-id="project.content" className="flex-1 overflow-auto">
                        {content}
                    </div>
                }
            />
            {fileInUseData && (
                <FileInUseDialog
                    isOpen={showFileInUseDialog}
                    onClose={() => {
                        setShowFileInUseDialog(false);
                        setFileInUseData(null);
                    }}
                    fileName={fileInUseData.fileName}
                    notebooksUsingFile={fileInUseData.notebooksUsingFile}
                    projectId={projectId || ''}
                />
            )}
        </>
    );
} 