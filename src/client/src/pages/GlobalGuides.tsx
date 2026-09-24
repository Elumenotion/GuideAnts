import { useState, useEffect, useMemo } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { api } from '../services/api';
import type { GuideDto, AssistantDto } from '../types/guides';
import { useToast } from '../components/common/Toast';
import LoadingSpinner from '../components/LoadingSpinner';
import { GuideCard } from '../components/guides/GuideCard';
import { AssistantCard } from '../components/guides/AssistantCard';
import { GuidesHeader } from '../components/guides/GuidesHeader';
import { GuidesFilterBar } from '../components/guides/GuidesFilterBar';
import { GuidesTabs } from '../components/guides/GuidesTabs';
import { EmptyState } from '../components/guides/EmptyState';
import { ConfirmationDialog } from '../components/common/ConfirmationDialog';
import { HeaderActionsBar } from '../components/common/HeaderActionsBar';
import { GuideAntsGuideButton } from '../features/guideantsGuide/GuideAntsGuideButton';
import { HomeButton } from '../components/common/HomeButton';
import { GuidesButton } from '../components/common/GuidesButton';
import { SettingsButton } from '../components/common/SettingsButton';
import { TourStartButton } from '../tour/TourStartButton';
import { useRegisterTour } from '../tour/useRegisterTour';

/**
 * Global (all-projects) Guides & Assistants dashboard.
 *
 * Same shape as the project-scoped GuidesDashboard (tabs, cards, import/export,
 * search) minus project scoping: guides/assistants are listed across every
 * project, and the cards carry no publishing UI (publishing is per-project and
 * lives in the project-scoped dashboard/editor). Editing a guide here opens the
 * global editor, whose Environment tab manages the guide default environment —
 * inherited by every project that does not override it.
 */
export default function GlobalGuides() {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
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

  const loadData = async () => {
    setLoading(true);
    try {
      const [guidesData, assistantsData] = await Promise.all([
        api.guides.guides.list(),
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

  useEffect(() => {
    loadData();
  }, []);

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
        a.crewNames?.some((c) => c.toLowerCase().includes(term))
    );
  }, [assistants, searchTerm]);

  const handleCreateGuide = () => {
    navigate('/guides/guide/new');
  };

  const handleCreateAssistant = () => {
    navigate('/guides/assistant/new');
  };

  const handleEditGuide = (guideId: string) => {
    navigate(`/guides/guide/${guideId}`);
  };

  const handleEditAssistant = (assistantId: string) => {
    navigate(`/guides/assistant/${assistantId}`);
  };

  const handleReportGuide = (guideId: string) => {
    navigate(`/guides/guide/${guideId}/usage`);
  };

  const handleReportAssistant = (assistantId: string) => {
    navigate(`/guides/assistant/${assistantId}/usage`);
  };

  const handleDeleteGuide = (guideId: string) => {
    const guide = guides.find((g) => g.id === guideId);
    if (!guide) return;
    setDeleteConfirmation({ type: 'guide', id: guideId, name: guide.name });
  };

  const handleDeleteAssistant = (assistantId: string) => {
    const assistant = assistants.find((a) => a.id === assistantId);
    if (!assistant) return;
    setDeleteConfirmation({ type: 'assistant', id: assistantId, name: assistant.name });
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
      setDeleteConfirmation(null);
    } catch (error: any) {
      showToast({
        type: 'error',
        title: 'Delete failed',
        message: error.message,
      });
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

  const handleImportGuide = async (file: File) => {
    try {
      const result = await api.guides.guides.import(file);
      const warnings = Array.isArray(result?.warnings) ? result.warnings : [];
      showToast({
        type: warnings.length > 0 ? 'warning' : 'success',
        title: warnings.length > 0 ? 'Guide imported with warnings' : 'Guide imported successfully',
        message: warnings.length > 0 ? warnings.join('\n') : undefined,
        duration: warnings.length > 0 ? 12000 : undefined,
      });
      const guidesData = await api.guides.guides.list();
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
        duration: warnings.length > 0 ? 12000 : undefined,
      });
      const assistantsData = await api.guides.assistants.list();
      setAssistants(assistantsData);
    } catch (error: any) {
      showToast({ type: 'error', title: 'Failed to import assistant', message: error.message });
    }
  };

  useRegisterTour('guides.global', [
    {
      target: '[data-tour-id="guides.header.title"]',
      content: 'All guides and assistants, across every project.',
      placement: 'bottom',
    },
    {
      target: '[data-tour-id="guides.tabs.guides"]',
      content: 'Guides tab: manage guides and their default environment.',
      placement: 'bottom',
    },
    {
      target: '[data-tour-id="guides.tabs.assistants"]',
      content: 'Assistants tab: manage assistants available to guides.',
      placement: 'bottom',
    },
  ]);

  return (
    <div className="h-full overflow-auto bg-gray-50 p-4 md:p-8">
      <div className="max-w-7xl mx-auto">
        {/* Header */}
        <div className="mb-6 flex items-center justify-between gap-2">
          <div className="flex min-w-0 flex-1 items-center">
            <img src="./guide.png" alt="GuideAnts" className="w-12 h-12 mr-4 shrink-0" />
            <div className="min-w-0">
              <h1 className="text-xl font-semibold">Guides &amp; Assistants</h1>
              <p className="text-sm text-gray-600">All guides and assistants, across all projects.</p>
            </div>
          </div>
          <HeaderActionsBar>
            <GuideAntsGuideButton />
            <HomeButton />
            <GuidesButton />
            <SettingsButton />
            <TourStartButton screenId="guides.global" inline />
          </HeaderActionsBar>
        </div>

        <GuidesHeader
          activeTab={activeTab}
          onCreateGuide={handleCreateGuide}
          onCreateAssistant={handleCreateAssistant}
          onImportGuide={handleImportGuide}
          onImportAssistant={handleImportAssistant}
        />

        <GuidesFilterBar searchTerm={searchTerm} onSearchChange={setSearchTerm} />

        <GuidesTabs
          activeTab={activeTab}
          onTabChange={(tab) => setSearchParams({ tab })}
          guidesCount={guides.length}
          assistantsCount={assistants.length}
        />

        <div className="p-6">
          {loading ? (
            <div className="flex items-center justify-center h-40">
              <LoadingSpinner message="Loading..." />
            </div>
          ) : activeTab === 'guides' ? (
            filteredGuides.length === 0 ? (
              searchTerm ? (
                <EmptyState type="search" />
              ) : (
                <EmptyState type="guides" onCreateGuide={handleCreateGuide} />
              )
            ) : (
              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
                {filteredGuides.map((guide) => (
                  <GuideCard
                    key={guide.id}
                    guide={guide}
                    onEdit={handleEditGuide}
                    onDelete={handleDeleteGuide}
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
              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
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

      <ConfirmationDialog
        isOpen={deleteConfirmation !== null}
        title={`Delete ${deleteConfirmation?.type === 'guide' ? 'Guide' : 'Assistant'}`}
        message={`Are you sure you want to delete "${deleteConfirmation?.name ?? ''}"? This action cannot be undone.`}
        confirmText="Delete"
        onConfirm={handleConfirmDelete}
        onClose={() => setDeleteConfirmation(null)}
      />
    </div>
  );
}
