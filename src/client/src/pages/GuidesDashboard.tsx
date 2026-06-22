import { useState, useEffect, useMemo } from 'react';
import { useParams, useNavigate, useSearchParams } from 'react-router-dom';
import { useProject } from '../contexts/ProjectContext';
import { api } from '../services/api';
import type { GuideDto, AssistantDto, PublishedGuideDto, PublishGuideDto, UpdatePublishedGuideDto } from '../types/guides';
import { useToast } from '../components/common/Toast';
import { ProjectLayout } from '../components/layouts/ProjectLayout';
import { SidebarContainer } from '../components/layouts/SidebarContainer';
import { ProjectSidebar } from '../components/project/sidebar/ProjectSidebar';
import { SectionType } from '../types/project';
import LoadingSpinner from '../components/LoadingSpinner';
import ErrorScreen from '../components/ErrorScreen';
import { GuidesHeader } from '../components/guides/GuidesHeader';
import { GuidesFilterBar } from '../components/guides/GuidesFilterBar';
import { GuidesTabs } from '../components/guides/GuidesTabs';
import { GuideCard } from '../components/guides/GuideCard';
import { AssistantCard } from '../components/guides/AssistantCard';
import { EmptyState } from '../components/guides/EmptyState';
import { ConfirmationDialog } from '../components/common/ConfirmationDialog';
import { PublishGuideDialog } from '../components/guides/PublishGuideDialog';
import { useProjectFilesPolling } from '../hooks/useProjectFilesPolling';
import { useProjectNotebooksPolling } from '../hooks/useProjectNotebooksPolling';
import { useFolderOperations } from '../hooks/useFolderOperations';
import { CreateFolderDto } from '../types/project';
import { useRegisterTour } from '../tour/useRegisterTour';

export default function GuidesDashboard() {
  const { projectId } = useParams<{ projectId: string }>();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const {
    project,
    error: projectError,
    isLoading: projectLoading,
    selectedItem,
    expandedSections,
    setSelectedItem,
    toggleSection,
    isOwner,
    renameFile
  } = useProject();
  const { showToast } = useToast();

  const activeTab = (searchParams.get('tab') || 'guides') as 'guides' | 'assistants';
  const [guides, setGuides] = useState<GuideDto[]>([]);
  const [assistants, setAssistants] = useState<AssistantDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [searchTerm, setSearchTerm] = useState('');
  const [deleteConfirmation, setDeleteConfirmation] = useState<{
    type: 'guide' | 'assistant';
    id: string;
    name: string;
  } | null>(null);

  // Publishing state
  const [publishedGuides, setPublishedGuides] = useState<Map<string, PublishedGuideDto>>(new Map());
  const [publishDialogState, setPublishDialogState] = useState<{
    guide: GuideDto;
    publishedGuide?: PublishedGuideDto;
  } | null>(null);

  // Add sidebar polling hooks for folder tree and notebooks
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

  // Folder operations hook
  const {
    createFolder,
    updateFolder,
    deleteFolder,
    moveFile,
    uploadFiles: uploadFilesToFolder,
  } = useFolderOperations(projectId || '');

  useEffect(() => {
    const loadData = async () => {
      setLoading(true);
      try {
        const [guidesData, assistantsData] = await Promise.all([
          api.guides.guides.list(projectId),
          api.guides.assistants.list(),
        ]);
        setGuides(guidesData);
        setAssistants(assistantsData);
      } catch (error: any) {
        showToast({ type: 'error', title: 'Failed to load data', message: error.message });
      } finally {
        setLoading(false);
      }
    };

    loadData();
  }, [projectId]);

  // Load published guides for current project
  useEffect(() => {
    const loadPublishedGuides = async () => {
      if (!projectId || guides.length === 0) return;

      try {
        const results = await Promise.all(
          guides.map(guide => 
            api.guides.guides.getPublishedInProject(guide.id, projectId)
              .catch(err => {
                console.error('Failed to get publish status for guide', guide.id, err);
                return null;
              })
          )
        );

        const newMap = new Map<string, PublishedGuideDto>();
        results.forEach((pubGuide, index) => {
          if (pubGuide) {
            newMap.set(guides[index].id, pubGuide);
          }
        });

        setPublishedGuides(newMap);
      } catch (error: any) {
        console.error('Failed to load published guides:', error);
        // Don't show toast for this, it's not critical
      }
    };

    loadPublishedGuides();
  }, [projectId, guides]);

  // Filter guides and assistants based on search term
  const filteredGuides = useMemo(() => {
    if (!searchTerm) return guides;
    const term = searchTerm.toLowerCase();
    return guides.filter(
      (g) =>
        g.name.toLowerCase().includes(term) ||
        g.description.toLowerCase().includes(term) ||
        g.modelName?.toLowerCase().includes(term)
    );
  }, [guides, searchTerm]);

  const filteredAssistants = useMemo(() => {
    if (!searchTerm) return assistants;
    const term = searchTerm.toLowerCase();
    return assistants.filter(
      (a) =>
        a.name.toLowerCase().includes(term) ||
        a.description.toLowerCase().includes(term) ||
        a.modelName?.toLowerCase().includes(term) ||
        a.crewNames.some((c) => c.toLowerCase().includes(term))
    );
  }, [assistants, searchTerm]);

  const handleCreateGuide = () => {
    navigate(`/projects/${projectId}/guides/guide/new`);
  };

  const handleCreateAssistant = () => {
    navigate(`/projects/${projectId}/guides/assistant/new`);
  };

  const handleStartGuideGuide = async () => {
    if (!projectId) return;
    try {
      // Find the template named "The Guide Guide"
      const templates = await api.projects.notebookTemplates.getAll(projectId);
      const guideGuide = templates.find(t => (t.templateName || '').trim() === 'The Guide Guide');
      if (!guideGuide) {
        showToast({ type: 'error', title: 'Template not found', message: 'The Guide Guide template is unavailable.' });
        return;
      }

      // Create a notebook using that template
      const notebook = await api.projects.createNotebook(projectId, {
        title: 'The Guide Guide',
        guideId: guideGuide.id,
      });

      // Create an initial conversation and navigate to it
      const convo = await api.projects.notebooks.conversations.create(projectId, notebook.id, 'New Conversation');
      refreshSidebarNotebooks();
      navigate(`/projects/${projectId}/notebooks/${notebook.id}`, { state: { conversationId: convo.id } });
    } catch (error: any) {
      showToast({ type: 'error', title: 'Failed to start The Guide Guide', message: error?.message || 'Please try again.' });
    }
  };

  const handleEditGuide = (guideId: string) => {
    navigate(`/projects/${projectId}/guides/guide/${guideId}`);
  };

  const handleReportGuide = (guideId: string) => {
    navigate(`/projects/${projectId}/guides/guide/${guideId}/usage`);
  };

  const handleEditAssistant = (assistantId: string) => {
    navigate(`/projects/${projectId}/guides/assistant/${assistantId}`);
  };

  const handleReportAssistant = (assistantId: string) => {
    navigate(`/projects/${projectId}/guides/assistant/${assistantId}/usage`);
  };

  const handleDeleteGuide = async (guideId: string) => {
    const guide = guides.find((g) => g.id === guideId);
    if (!guide) return;
    
    setDeleteConfirmation({
      type: 'guide',
      id: guideId,
      name: guide.name,
    });
  };

  const handleConfirmDelete = async () => {
    if (!deleteConfirmation) return;

    try {
      if (deleteConfirmation.type === 'guide') {
        await api.guides.guides.delete(deleteConfirmation.id);
        setGuides(guides.filter((g) => g.id !== deleteConfirmation.id));
        showToast({ type: 'success', title: 'Guide deleted successfully' });
      } else {
        await api.guides.assistants.delete(deleteConfirmation.id);
        setAssistants(assistants.filter((a) => a.id !== deleteConfirmation.id));
        showToast({ type: 'success', title: 'Assistant deleted successfully' });
      }
    } catch (error: any) {
      showToast({ 
        type: 'error', 
        title: `Failed to delete ${deleteConfirmation.type}`, 
        message: error.message 
      });
    } finally {
      setDeleteConfirmation(null);
    }
  };

  const handleDeleteAssistant = async (assistantId: string) => {
    const assistant = assistants.find((a) => a.id === assistantId);
    if (!assistant) return;
    
    setDeleteConfirmation({
      type: 'assistant',
      id: assistantId,
      name: assistant.name,
    });
  };

  // Tour: Guides Dashboard
  useRegisterTour('guides.dashboard', [
    {
      target: '[data-tour-id="guides.header.title"]',
      content: 'Manage your Guides and Assistants here.',
      placement: 'bottom'
    },
    {
      target: '[data-tour-id="guides.header.createGuide"]',
      content: 'Create a new Guide. Start here for primary workflows.',
      placement: 'left'
    },
    {
      target: '[data-tour-id="guides.header.importGuide"]',
      content: 'Import a Guide from a .zip export.',
      placement: 'left'
    },
    {
      target: '[data-tour-id="guides.tabs.container"]',
      content: 'Switch between Guides and Assistants.',
      placement: 'bottom'
    },
    {
      target: '[data-tour-id="guides.filter.search"]',
      content: 'Search by name, description, or model.',
      placement: 'bottom'
    },
    {
      target: '[data-tour-id="guides.card.guide"]',
      content: 'Each card shows model, tools, crew, and actions.',
      placement: 'top'
    }
  ]);

  // Handle publish
  const handlePublishGuide = (guideId: string) => {
    const guide = guides.find(g => g.id === guideId);
    if (guide) {
      setPublishDialogState({ guide });
    }
  };

  // Handle manage publish
  const handleManagePublish = (guideId: string, publishedGuide: PublishedGuideDto) => {
    const guide = guides.find(g => g.id === guideId);
    if (guide) {
      setPublishDialogState({ guide, publishedGuide });
    }
  };

  // Submit publish
  const handlePublishSubmit = async (config: Omit<PublishGuideDto, 'projectId'>) => {
    if (!publishDialogState || !projectId) return;

    try {
      const publishDto: PublishGuideDto = {
        ...config,
        projectId,
      };

      const result = await api.guides.guides.publish(
        publishDialogState.guide.id,
        publishDto
      );

      // Update published guides map
      setPublishedGuides(prev => new Map(prev).set(publishDialogState.guide.id, result));
      
      showToast({
        type: 'success',
        title: 'Guide Published',
        message: `${publishDialogState.guide.name} has been published successfully.`
      });

      setPublishDialogState({
        guide: publishDialogState.guide,
        publishedGuide: result,
      });
    } catch (error: any) {
      showToast({
        type: 'error',
        title: 'Publish Failed',
        message: error.message
      });
    }
  };

  const handlePublishedGuideUpdated = (updated: PublishedGuideDto) => {
    if (!publishDialogState) return;
    setPublishedGuides(prev => new Map(prev).set(publishDialogState.guide.id, updated));
    setPublishDialogState({
      guide: publishDialogState.guide,
      publishedGuide: updated,
    });
  };

  const handleUpdatePublish = async (config: UpdatePublishedGuideDto) => {
    if (!publishDialogState?.publishedGuide) return;

    try {
      const result = await api.guides.guides.updatePublished(
        publishDialogState.guide.id,
        publishDialogState.publishedGuide.id,
        config
      );

      setPublishedGuides(prev => new Map(prev).set(publishDialogState.guide.id, result));
      
      showToast({
        type: 'success',
        title: 'Updated',
        message: 'Publishing configuration updated.'
      });

      setPublishDialogState(null);
    } catch (error: any) {
      showToast({ type: 'error', title: 'Update Failed', message: error.message });
    }
  };

  // Deactivate published guide
  const handleDeactivatePublish = async () => {
    if (!publishDialogState?.publishedGuide) return;

    try {
      await api.guides.guides.deactivatePublished(
        publishDialogState.guide.id,
        publishDialogState.publishedGuide.id
      );

      // Update state to mark as inactive
      const updatedPublishedGuide = { ...publishDialogState.publishedGuide, active: false };
      setPublishedGuides(prev => new Map(prev).set(publishDialogState.guide.id, updatedPublishedGuide));

      showToast({
        type: 'success',
        title: 'Deactivated',
        message: 'Published guide has been deactivated.'
      });

      setPublishDialogState(null);
    } catch (error: any) {
      showToast({ type: 'error', title: 'Deactivation Failed', message: error.message });
    }
  };

  // Reactivate published guide
  const handleReactivatePublish = async () => {
    if (!publishDialogState?.publishedGuide) return;

    try {
      const result = await api.guides.guides.reactivatePublished(
        publishDialogState.guide.id,
        publishDialogState.publishedGuide.id
      );

      setPublishedGuides(prev => new Map(prev).set(publishDialogState.guide.id, result));

      showToast({
        type: 'success',
        title: 'Reactivated',
        message: 'Published guide has been reactivated and is now accessible to external users.'
      });

      setPublishDialogState(null);
    } catch (error: any) {
      showToast({ type: 'error', title: 'Reactivation Failed', message: error.message });
    }
  };

  const handleExportGuide = async (guideId: string) => {
    try {
      const blob = await api.guides.guides.export(guideId);
      const guide = guides.find((g) => g.id === guideId);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `guide-${guide?.name || guideId}-${Date.now()}.zip`;
      a.click();
      URL.revokeObjectURL(url);
      showToast({ type: 'success', title: 'Guide exported successfully' });
    } catch (error: any) {
      showToast({ type: 'error', title: 'Failed to export guide', message: error.message });
    }
  };

  const handleImportGuide = async (file: File) => {
    try {
      const result = await api.guides.guides.import(file);
      const warnings = Array.isArray(result?.warnings) ? result.warnings : [];
      showToast({
        type: warnings.length > 0 ? 'warning' : 'success',
        title: warnings.length > 0 ? 'Guide imported with warnings' : 'Guide imported successfully',
        message: warnings.length > 0 ? warnings.join('\n') : undefined,
        duration: warnings.length > 0 ? 12000 : undefined
      });
      const guidesData = await api.guides.guides.list(projectId);
      setGuides(guidesData);
    } catch (error: any) {
      showToast({ type: 'error', title: 'Failed to import guide', message: error.message });
    }
  };

  const handleImportAssistant = async (file: File) => {
    try {
      const result = await api.guides.assistants.import(file);
      const warnings = Array.isArray(result?.warnings) ? result.warnings : [];
      showToast({
        type: warnings.length > 0 ? 'warning' : 'success',
        title: warnings.length > 0 ? 'Assistant imported with warnings' : 'Assistant imported successfully',
        message: warnings.length > 0 ? warnings.join('\n') : undefined,
        duration: warnings.length > 0 ? 12000 : undefined
      });
      const assistantsData = await api.guides.assistants.list();
      setAssistants(assistantsData);
    } catch (error: any) {
      showToast({ type: 'error', title: 'Failed to import assistant', message: error.message });
    }
  };

  const handleExportAssistant = async (assistantId: string) => {
    try {
      const blob = await api.guides.assistants.export(assistantId);
      const assistant = assistants.find((a) => a.id === assistantId);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `assistant-${assistant?.name || assistantId}-${Date.now()}.zip`;
      a.click();
      URL.revokeObjectURL(url);
      showToast({ type: 'success', title: 'Assistant exported successfully' });
    } catch (error: any) {
      showToast({ type: 'error', title: 'Failed to export assistant', message: error.message });
    }
  };

  const handleItemSelect = (type: SectionType, id: string) => {
    if (type === 'notebooks') {
      navigate(`/projects/${projectId}/notebooks/${id}`);
      return;
    }
    
    setSelectedItem({ type, id });
    navigate(`/projects/${projectId}`);
  };

  // Folder/File operation handlers
  const handleCreateFolder = async (parentFolderId: string | undefined, folderData: CreateFolderDto) => {
    try {
      const folderDataWithParent = {
        ...folderData,
        parentFolderId
      };
      await createFolder(folderDataWithParent);
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

  const handleRenameFolder = async (folderId: string, newName: string) => {
    try {
      await updateFolder(folderId, { name: newName });
      refreshSidebarFiles();
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
      await uploadFilesToFolder(files, folderId);
      refreshSidebarFiles();
    } catch (error) {
      console.error('Failed to upload files:', error);
      showToast({
        type: 'error',
        title: 'Failed to upload files',
        message: 'Please try again.'
      });
    }
  };

  const handleDeleteFile = async (fileId: string) => {
    try {
      await api.projects.deleteContentFile(projectId || '', fileId);
      refreshSidebarFiles();
    } catch (error) {
      console.error('Failed to delete file:', error);
      showToast({
        type: 'error',
        title: 'Failed to delete file',
        message: 'Please try again.'
      });
    }
  };

  const handleRenameFile = async (fileId: string, newName: string) => {
    if (!project) return;
    try {
      await renameFile(fileId, newName);
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
    try {
      await api.projects.deleteLink(projectId || '', linkId);
    } catch (error) {
      console.error('Failed to delete link:', error);
      showToast({
        type: 'error',
        title: 'Failed to delete link',
        message: 'Please try again.'
      });
    }
  };

  // Notebook operation handlers
  const handleCreateNotebook = async (title: string, guideId?: string, description?: string) => {
    if (!projectId) return;
    try {
      const notebook = await api.projects.createNotebook(projectId, { title, description, guideId });
      refreshSidebarNotebooks();
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
      refreshSidebarNotebooks();
    } catch (error) {
      console.error('Failed to delete notebook:', error);
      showToast({
        type: 'error',
        title: 'Failed to delete notebook',
        message: 'Please try again.'
      });
    }
  };

  const handleCopyNotebook = async (sourceNotebookId: string) => {
    if (!projectId || !project) return;
    const source = project.notebooks.find(n => n.id === sourceNotebookId);
    const title = source ? `Copy of ${source.title}` : 'Copied Notebook';
    try {
      await api.projects.copyNotebook(projectId, {
        title,
        sourceNotebookId,
      });
      refreshSidebarNotebooks();
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
      refreshSidebarNotebooks();
    } catch (error) {
      console.error('Failed to rename notebook:', error);
      showToast({
        type: 'error',
        title: 'Failed to rename notebook',
        message: 'Please try again.'
      });
    }
  };

  const handleCreateNotebookFromFile = async (
    fileId: string, 
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
      
      await api.projects.notebooks.copyFileFromProject(projectId, notebook.id, fileId);
      refreshSidebarNotebooks();
      navigate(`/projects/${projectId}/notebooks/${notebook.id}`);
    } catch (error) {
      console.error('Failed to create notebook from file:', error);
      showToast({
        type: 'error',
        title: 'Failed to create notebook from file',
        message: 'Please try again.'
      });
    }
  };

  const handleSetHomePage = async (fileId: string | null) => {
    if (!projectId) return;
    try {
      if (fileId) {
        await api.projects.setHomePage(projectId, fileId);
      } else {
        await api.projects.clearHomePage(projectId);
      }
    } catch (error) {
      console.error('Failed to set project home page:', error);
      showToast({
        type: 'error',
        title: 'Failed to set project home page',
        message: 'Please try again.'
      });
    }
  };

  if (projectLoading) {
    return <LoadingSpinner message="Loading project..." />;
  }

  if (projectError || !project) {
    return (
      <ErrorScreen
        error={projectError || 'Project not found'}
        title="Failed to Load Project"
        onRetry={() => window.location.reload()}
        onBack={() => navigate('/')}
      />
    );
  }

  const content = (
    <div className="h-full flex flex-col -m-4" data-tour-id="guides.page.container">
      <GuidesHeader
        activeTab={activeTab}
        onCreateGuide={handleCreateGuide}
        onCreateAssistant={handleCreateAssistant}
        onImportGuide={handleImportGuide}
        onImportAssistant={handleImportAssistant}
      />

      <GuidesFilterBar
        searchTerm={searchTerm}
        onSearchChange={setSearchTerm}
      />

      <GuidesTabs
        activeTab={activeTab}
        onTabChange={(tab) => setSearchParams({ tab })}
        guidesCount={guides.length}
        assistantsCount={assistants.length}
      />

      <div className="flex-1 overflow-auto p-6 bg-gray-50" data-tour-id="guides.list.container">
        {loading ? (
          <div className="flex items-center justify-center h-full">
            <LoadingSpinner message="Loading..." />
          </div>
        ) : activeTab === 'guides' ? (
          filteredGuides.length === 0 ? (
            searchTerm ? (
              <EmptyState type="search" />
            ) : (
              <EmptyState type="guides" onCreateGuide={handleCreateGuide} onStartGuideGuide={handleStartGuideGuide} />
            )
          ) : (
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4" data-tour-id="guides.list.guides">
              {filteredGuides.map((guide) => (
                <GuideCard
                  key={guide.id}
                  guide={guide}
                  publishedGuide={publishedGuides.get(guide.id)}
                  onEdit={handleEditGuide}
                  onDelete={handleDeleteGuide}
                  onPublish={handlePublishGuide}
                  onManagePublish={handleManagePublish}
                  onExport={handleExportGuide}
                  onReport={handleReportGuide}
                />
              ))}
            </div>
          )
        ) : (
          filteredAssistants.length === 0 ? (
            searchTerm ? (
              <EmptyState type="search" />
            ) : (
              <EmptyState type="assistants" onCreateAssistant={handleCreateAssistant} />
            )
          ) : (
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4" data-tour-id="guides.list.assistants">
              {filteredAssistants.map((assistant) => (
                <AssistantCard
                  key={assistant.id}
                  assistant={assistant}
                  onEdit={handleEditAssistant}
                  onDelete={handleDeleteAssistant}
                  onExport={handleExportAssistant}
                  onReport={handleReportAssistant}
                />
              ))}
            </div>
          )
        )}
      </div>
    </div>
  );

  const sidebar = (
    <SidebarContainer>
      <ProjectSidebar
        project={project}
        expandedSections={expandedSections}
        selectedItem={selectedItem}
        onSectionToggle={toggleSection}
        onItemSelect={handleItemSelect}
        disabled={false}
        isCurrentUserOwner={isOwner()}
        // Folder tree and notebooks from polling
        folderTree={sidebarFolderTree}
        notebooks={sidebarNotebooks}
        // Folder/file operations
        onCreateFolder={handleCreateFolder}
        onRenameFolder={handleRenameFolder}
        onDeleteFolder={handleDeleteFolder}
        onMoveFile={handleMoveFile}
        onUploadFilesToFolder={handleUploadFilesToFolder}
        onDeleteFile={handleDeleteFile}
        onRenameFile={handleRenameFile}
        onDeleteLink={handleDeleteLink}
        onDeleteNotebook={handleDeleteNotebook}
        onCopyNotebook={handleCopyNotebook}
        onRenameNotebook={handleRenameNotebook}
        onCreateNotebook={handleCreateNotebook}
        onCreateNotebookFromFile={handleCreateNotebookFromFile}
        homePageContentFileId={project.homePageContentFileId}
        onSetHomePage={handleSetHomePage}
      />
    </SidebarContainer>
  );

  return (
    <>
      <ProjectLayout
        project={project}
        sidebar={sidebar}
        content={content}
        onBack={() => navigate('/')}
        canEdit={false}
        title="Guides & Assistants"
        subtitle="Manage guides and assistants"
        tourScreenId="guides.dashboard"
      />
      
      {/* Delete Confirmation Dialog */}
      <ConfirmationDialog
        isOpen={!!deleteConfirmation}
        onClose={() => setDeleteConfirmation(null)}
        onConfirm={handleConfirmDelete}
        title={`Delete ${deleteConfirmation?.type === 'guide' ? 'Guide' : 'Assistant'}`}
        message={`Are you sure you want to delete "${deleteConfirmation?.name}"? This action cannot be undone.`}
        confirmText="Delete"
        cancelText="Cancel"
        confirmButtonClass="bg-red-600 hover:bg-red-700 text-white"
      />

      {/* Publish/Edit Dialog */}
      {publishDialogState && (
        <PublishGuideDialog
          guideName={publishDialogState.guide.name}
          guideId={publishDialogState.guide.id}
          publishedGuide={publishDialogState.publishedGuide || null}
          onPublish={publishDialogState.publishedGuide ? undefined : handlePublishSubmit}
          onUpdate={publishDialogState.publishedGuide ? handleUpdatePublish : undefined}
          onDeactivate={publishDialogState.publishedGuide ? handleDeactivatePublish : undefined}
          onReactivate={publishDialogState.publishedGuide ? handleReactivatePublish : undefined}
          onCancel={() => setPublishDialogState(null)}
          onPublishedGuideUpdated={handlePublishedGuideUpdated}
        />
      )}
    </>
  );
}
