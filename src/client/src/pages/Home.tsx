import { useEffect, useMemo, useState, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { FiBarChart2, FiFolderPlus, FiTool } from 'react-icons/fi';
import { HomeButton } from '../components/common/HomeButton';
import { SettingsButton } from '../components/common/SettingsButton';
import { api } from '../services/api';
import { useToast } from '../components/common/Toast';
import ErrorScreen from '../components/ErrorScreen';
import LoadingSpinner from '../components/LoadingSpinner';
import TabbedContentArea from '../components/home/TabbedContentArea';
import EmptyStateVideo from '../components/home/EmptyStateVideo';
import AddAiServicesWizard from '../components/home/AddAiServicesWizard';
import { TourStartButton } from '../tour/TourStartButton';
import { useRegisterTour } from '../tour/useRegisterTour';
import { DEFAULT_CONVERSATION_TITLE } from '../constants/conversation';
import { CONNECTION_SECTION_NAME_SET } from './settings/constants/connectionSections';
import { useAuth } from '../contexts/AuthContext';
import { HeaderActionsBar } from '../components/common/HeaderActionsBar';
import { GuideAntsGuideButton } from '../features/guideantsGuide/GuideAntsGuideButton';
import { HeaderUserMenu } from '../components/common/HeaderUserMenu';
import { HeaderIconLinkButton } from '../components/common/HeaderIconLinkButton';

interface ProjectSummary {
  id: string;
  title: string;
  description: string;
  created: string;
}

const ADD_AI_SERVICES_WIZARD_DISMISS_KEY = 'guideants.firstLaunch.addAiServicesWizard.dismissed.v1';

interface HeaderIconActionButtonProps {
  title: string;
  icon: React.ReactNode;
  onClick: () => void;
}

function HeaderIconActionButton({ title, icon, onClick }: HeaderIconActionButtonProps) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={title}
      aria-label={title}
      className="h-10 w-10 border rounded-md transition-colors flex items-center justify-center hover:bg-gray-50 text-gray-700 border-gray-300 bg-white"
    >
      {icon}
      <span className="sr-only">{title}</span>
    </button>
  );
}

const Home = () => {
  const navigate = useNavigate();
  const { showToast } = useToast();
  const { role } = useAuth();
  const [projects, setProjects] = useState<ProjectSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [quickStartLoading, setQuickStartLoading] = useState(false);
  const [menuOpen, setMenuOpen] = useState<string | null>(null);
  const [copyLoading, setCopyLoading] = useState<string | null>(null);
  const [deleteLoading, setDeleteLoading] = useState<string | null>(null);
  const [contextMenuPosition, setContextMenuPosition] = useState({ x: 0, y: 0 });
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [projectToDelete, setProjectToDelete] = useState<string | null>(null);
  const [quickStartTarget, setQuickStartTarget] = useState<{ projectId: string; notebookId: string } | null>(null);
  const [showAddAiServicesWizard, setShowAddAiServicesWizard] = useState(false);

  const SCREEN_ID = 'home';
  const canCreateContent = role === 'Admin' || role === 'Contributor';
  const isAdmin = role === 'Admin';

  const fetchProjects = async () => {
    try {
      // Clear any previous error before attempting to fetch again
      setError(null);
      const data = await api.projects.getUserProjects();
      setProjects(data);
    } catch (err) {
      // Detect network errors so we can show the full-page ErrorScreen
      const errorMessage = err instanceof Error ? err.message : 'Failed to load recent projects';
      setError(errorMessage);
      console.error('Failed to load recent projects:', err);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchProjects();
  }, []);

  useEffect(() => {
    let cancelled = false;

    const probeFirstLaunchWizardState = async () => {
      let dismissalEnabled = false;
      try {
        dismissalEnabled = typeof window !== 'undefined'
          && window.localStorage.getItem(ADD_AI_SERVICES_WIZARD_DISMISS_KEY) === '1';
      } catch {
        dismissalEnabled = false;
      }

      if (dismissalEnabled) {
        return;
      }

      if (!isAdmin) {
        setShowAddAiServicesWizard(false);
        return;
      }

      try {
        const [sectionSummaries, models] = await Promise.all([
          api.settings.getSections(),
          api.settings.getModels(),
        ]);
        if (cancelled) {
          return;
        }

        const configuredConnections = sectionSummaries.filter(
          (section) => CONNECTION_SECTION_NAME_SET.has(section.sectionName)
            && section.readinessStatus === 'configured'
        );
        // Local-only deployments are valid; if either side is present we
        // should not force onboarding on every home-page visit.
        const needsWizard = configuredConnections.length === 0 && models.length === 0;
        if (needsWizard) {
          setShowAddAiServicesWizard(true);
        }
      } catch (probeError) {
        console.warn('First-launch wizard probe skipped due to settings fetch error.', probeError);
      }
    };

    void probeFirstLaunchWizardState();

    return () => {
      cancelled = true;
    };
  }, [isAdmin]);

  const persistAddAiServicesWizardDismissal = useCallback(() => {
    try {
      if (typeof window !== 'undefined') {
        window.localStorage.setItem(ADD_AI_SERVICES_WIZARD_DISMISS_KEY, '1');
      }
    } catch {
      // best effort only
    }
  }, []);

  const handleDismissAddAiServicesWizard = useCallback((persistDismissal: boolean) => {
    if (persistDismissal) {
      persistAddAiServicesWizardDismissal();
    }
    setShowAddAiServicesWizard(false);
  }, [persistAddAiServicesWizardDismissal]);

  const handleOpenSettingsFromAddAiServicesWizard = useCallback((persistDismissal: boolean) => {
    if (persistDismissal) {
      persistAddAiServicesWizardDismissal();
    }
    setShowAddAiServicesWizard(false);
    navigate('/settings');
  }, [navigate, persistAddAiServicesWizardDismissal]);


  // Close context menu when clicking outside
  useEffect(() => {
    if (!menuOpen) return;

    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as Element;
      if (!target.closest('[data-context-menu]') && !target.closest('[data-menu-trigger]')) {
        setMenuOpen(null);
      }
    };

    const handleEscapeKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setMenuOpen(null);
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    document.addEventListener('keydown', handleEscapeKey);
    
    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
      document.removeEventListener('keydown', handleEscapeKey);
    };
  }, [menuOpen]);

  // Dynamically adjust the number of visible recent projects to better utilize vertical space
  const [visibleProjectCount, setVisibleProjectCount] = useState(10);

  useEffect(() => {
    const computeVisibleCount = () => {
      // Approximate available space below the header/options
      const headerAndControlsApprox = 380; // px
      const rowHeightApprox = 64; // px per project row (padding + text)
      const available = Math.max(0, window.innerHeight - headerAndControlsApprox);
      const count = Math.max(6, Math.min(24, Math.floor(available / rowHeightApprox)));
      setVisibleProjectCount(count);
    };

    computeVisibleCount();
    window.addEventListener('resize', computeVisibleCount);
    return () => window.removeEventListener('resize', computeVisibleCount);
  }, []);

  const recent = useMemo(() => projects.slice(0, visibleProjectCount), [projects, visibleProjectCount]);
  const shouldPulseAttention = !loading && projects.length === 0;

  const handleMostRecentChange = useCallback((meta: { hasAny: boolean; projectId?: string; notebookId?: string }) => {
    if (meta.hasAny && meta.projectId && meta.notebookId) {
      setQuickStartTarget({ projectId: meta.projectId, notebookId: meta.notebookId });
    } else {
      setQuickStartTarget(null);
    }
  }, []);

  const handleEdit = (projectId: string, e: React.MouseEvent) => {
    e.stopPropagation();
    navigate(`/projects/${projectId}/edit`);
    setMenuOpen(null);
  };

  const handleCopy = async (projectId: string, e: React.MouseEvent) => {
    e.stopPropagation();
    setCopyLoading(projectId);
    try {
      await api.projects.copyProject(projectId);
      
      // Refresh the projects list
      const data = await api.projects.getUserProjects();
      setProjects(data);
      
      // Show success message
      const successMessage = document.createElement('div');
      successMessage.className = 'fixed top-4 right-4 bg-green-500 text-white px-4 py-2 rounded shadow-lg z-50';
      successMessage.textContent = 'Project copied successfully';
      document.body.appendChild(successMessage);
      setTimeout(() => successMessage.remove(), 3000);
      
    } catch (err) {
      console.error('Copy project error:', err);
      
      // Extract meaningful error message
      let errorMessage = 'Failed to copy project';
      if (err instanceof Error) {
        errorMessage = err.message;
      } else if (typeof err === 'object' && err !== null && 'message' in err) {
        errorMessage = String(err.message);
      }
      
      // Show error message
      const errorDiv = document.createElement('div');
      errorDiv.className = 'fixed top-4 right-4 bg-red-500 text-white px-4 py-2 rounded shadow-lg z-50 max-w-md';
      errorDiv.innerHTML = `
        <div class="font-semibold">Copy Failed</div>
        <div class="text-sm mt-1">${errorMessage}</div>
      `;
      document.body.appendChild(errorDiv);
      setTimeout(() => errorDiv.remove(), 5000);
      
    } finally {
      setCopyLoading(null);
      setMenuOpen(null);
    }
  };


  const handleDelete = async (projectId: string, e: React.MouseEvent) => {
    e.stopPropagation();
    
    // Show confirmation dialog instead of using window.confirm
    setProjectToDelete(projectId);
    setShowDeleteConfirm(true);
    setMenuOpen(null);
  };

  const handleDeleteConfirm = async () => {
    if (!projectToDelete) return;
    
    setDeleteLoading(projectToDelete);
    setShowDeleteConfirm(false);
    
    try {
      await api.projects.deleteProject(projectToDelete);
      
      // Remove the project from the local state
      setProjects(projects.filter(p => p.id !== projectToDelete));
      
      // Show success message
      const successMessage = document.createElement('div');
      successMessage.className = 'fixed top-4 right-4 bg-green-500 text-white px-4 py-2 rounded shadow-lg';
      successMessage.textContent = 'Project deleted successfully';
      document.body.appendChild(successMessage);
      setTimeout(() => successMessage.remove(), 3000);
      
    } catch (err) {
      console.error('Delete project error:', err);
    } finally {
      setDeleteLoading(null);
      setProjectToDelete(null);
    }
  };

  const handleDeleteCancel = () => {
    setShowDeleteConfirm(false);
    setProjectToDelete(null);
  };

  const toggleMenu = (projectId: string, e: React.MouseEvent) => {
    e.stopPropagation();
    
    if (menuOpen === projectId) {
      setMenuOpen(null);
    } else {
      // Calculate position for the context menu
      const rect = e.currentTarget.getBoundingClientRect();
      const menuWidth = 192; // w-48 = 12rem = 192px
      const menuHeight = 120; // Approximate height for 3 menu items
      
      let x = rect.right - menuWidth;
      let y = rect.bottom + 8;
      
      // Ensure menu doesn't go off-screen
      if (x < 8) x = 8; // Minimum left margin
      if (x + menuWidth > window.innerWidth - 8) x = window.innerWidth - menuWidth - 8; // Maximum right margin
      if (y + menuHeight > window.innerHeight - 8) y = rect.top - menuHeight - 8; // Show above if below viewport
      
      setContextMenuPosition({ x, y });
      setMenuOpen(projectId);
    }
  };

  const handleQuickStart = useCallback(async () => {
    if (!canCreateContent) {
      showToast({
        type: 'warning',
        title: 'Read-only account',
        message: 'Your role does not allow creating new content.',
      });
      return;
    }

    try {
      setQuickStartLoading(true);
      if (quickStartTarget) {
        // Create a new conversation in the most recent notebook
        const convo = await api.projects.notebooks.conversations.create(
          quickStartTarget.projectId,
          quickStartTarget.notebookId,
          DEFAULT_CONVERSATION_TITLE
        );
        navigate(`/projects/${quickStartTarget.projectId}/notebooks/${quickStartTarget.notebookId}`, {
          state: { conversationId: convo.id }
        });
      } else {
        // Default behavior: create a new project + notebook + conversation
        const result = await api.quickStart.create();
        navigate(`/projects/${result.projectId}/notebooks/${result.notebookId}`, {
          state: { conversationId: result.conversationId }
        });
      }
    } catch (error: any) {
      console.error('Quick Start failed:', error);
      if (quickStartTarget) {
        showToast({
          type: 'error',
          title: 'Could not start a chat in your latest notebook',
          message: 'It may have been moved or deleted. Try again, or create a new project from the top toolbar.'
        });
      } else {
        showToast({ type: 'error', title: 'Quick Start failed', message: 'Please try again.' });
      }
    } finally {
      setQuickStartLoading(false);
    }
  }, [canCreateContent, navigate, showToast, quickStartTarget]);

  // Home page tour registration
  useRegisterTour(SCREEN_ID, [
    {
      target: '[data-tour-id="home.quick-start"]',
      content: 'Start a <strong>Quick Start</strong> project to jump straight into a new notebook and conversation.'
    },
    {
      target: '[data-tour-id="home.actions.new-project"]',
      content: 'Create a <strong>new project</strong> to organize notebooks, files, and collaborators.'
    },
    {
      target: '[data-tour-id="home.actions.usage"]',
      content: 'Review <strong>Usage</strong> and costs for your owned projects.'
    },
    {
      target: '[data-tour-id="home.tabs"]',
      content: 'Switch between <strong>Recent Conversations</strong> and <strong>Recent Projects</strong> here.'
    },
    {
      target: '[data-tour-id="home.conversations.see-all"]',
      content: 'Click <strong>See all</strong> to browse all your conversations with filters and sorting.'
    },
    {
      target: '[data-tour-id="home.projects.see-all"]',
      content: 'Click <strong>See all</strong> to browse all your projects.'
    }
  ]);

  if (loading) {
    return (
      <div className="h-full overflow-auto bg-gray-50 p-8">
        <LoadingSpinner message="Loading recent conversations and projects..." />
      </div>
    );
  }

  // Show error screen if there's a persistent error
  if (error && error.toLowerCase().includes('failed to fetch')) {
    return (
      <ErrorScreen
        error={error}
        title="Connection Problem"
        onRetry={fetchProjects}
        onBack={() => {
          // Clear error and remain on the home route (already here)
          setError(null);
          navigate('/');
        }}
      />
    );
  }

  return (
    <div className="h-full overflow-auto bg-gray-50 p-8">
      <div className="max-w-7xl mx-auto">
        <div className="mb-8 flex items-center justify-between gap-2">
          <div className="flex min-w-0 flex-1 items-center">
            <img src="./guide.png" alt="GuideAnts" className="w-12 h-12 mr-4 shrink-0" />
            <div className="min-w-0">
              <h1 className="text-xl font-semibold">GuideAnts Notebooks</h1>
              <p className="text-sm text-gray-600">AI for people</p>
            </div>
          </div>
          <HeaderActionsBar>
            <GuideAntsGuideButton />
            {canCreateContent ? (
              <HeaderIconLinkButton
                to="/new-project"
                title="New Project"
                icon={<FiFolderPlus className="h-4 w-4" />}
                tourId="home.actions.new-project"
              />
            ) : null}
            {isAdmin ? (
              <HeaderIconLinkButton
                to="/usage"
                title="Usage"
                icon={<FiBarChart2 className="h-4 w-4" />}
                tourId="home.actions.usage"
              />
            ) : null}
            {isAdmin ? (
              <HeaderIconActionButton
                title="Setup Wizard"
                icon={<FiTool className="h-4 w-4" />}
                onClick={() => setShowAddAiServicesWizard(true)}
              />
            ) : null}
            <HomeButton />
            <SettingsButton />
            <HeaderUserMenu />
            <TourStartButton
              screenId={SCREEN_ID}
              inline
              className={shouldPulseAttention ? 'animate-tour-pulse-5s' : ''}
            />
          </HeaderActionsBar>
        </div>

        <div className="mb-8">
          <button
            onClick={handleQuickStart}
            disabled={quickStartLoading || !canCreateContent}
            className={`w-full px-4 py-2 text-sm font-medium rounded-md transition-colors duration-200 bg-blue-600 text-white hover:bg-blue-700 disabled:bg-blue-400 ${shouldPulseAttention ? 'animate-gentle-pulse-5s' : ''}`}
            data-tour-id="home.quick-start"
          >
            {quickStartLoading
              ? (quickStartTarget ? 'Starting chat in latest notebook...' : 'Creating Quick Start Project...')
              : (!canCreateContent
                  ? 'Quick Start is disabled for Reader accounts'
                  : (quickStartTarget
                      ? 'Quick Start - Start a new chat in your latest notebook'
                      : 'Quick Start - Start a new project with a notebook and chat'))
            }
          </button>
        </div>

        {/* Recent Projects/Conversations Area */}
        {projects.length === 0 ? (
          <EmptyStateVideo />
        ) : (
          <TabbedContentArea
            projects={recent}
            loading={loading}

            menuOpen={menuOpen}
            contextMenuPosition={contextMenuPosition}
            copyLoading={copyLoading}
            deleteLoading={deleteLoading}
            showDeleteConfirm={showDeleteConfirm}
            projectToDelete={projectToDelete}
            onToggleMenu={toggleMenu}
            onEdit={handleEdit}
            onCopy={handleCopy}
            onDelete={handleDelete}
            onDeleteConfirm={handleDeleteConfirm}
            onDeleteCancel={handleDeleteCancel}
            onMostRecentChange={handleMostRecentChange}
          />
        )}
      </div>
      <AddAiServicesWizard
        isOpen={isAdmin && showAddAiServicesWizard}
        onDismiss={handleDismissAddAiServicesWizard}
        onOpenSettings={handleOpenSettingsFromAddAiServicesWizard}
      />
    </div>
  );
};

export default Home;


