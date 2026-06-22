import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { createPortal } from 'react-dom';
import { api } from '../services/api';
import LoadingSpinner from '../components/LoadingSpinner';
import { HeaderActionsBar } from '../components/common/HeaderActionsBar';
import { GuideAntsGuideButton } from '../features/guideantsGuide/GuideAntsGuideButton';
import { HomeButton } from '../components/common/HomeButton';
import { SettingsButton } from '../components/common/SettingsButton';
import ErrorScreen from '../components/ErrorScreen';
import { ConfirmationDialog } from '../components/common/ConfirmationDialog';
import { TourStartButton } from '../tour/TourStartButton';
import { useRegisterTour } from '../tour/useRegisterTour';

// TypeScript declaration for Electron window is now in src/types/electron.d.ts

interface Project {
  id: string;
  title: string;
  description: string;
  created: string;
}

const Projects = () => {
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [menuOpen, setMenuOpen] = useState<string | null>(null);
  const [copyLoading, setCopyLoading] = useState<string | null>(null);
  const [deleteLoading, setDeleteLoading] = useState<string | null>(null);
  const [contextMenuPosition, setContextMenuPosition] = useState({ x: 0, y: 0 });
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [projectToDelete, setProjectToDelete] = useState<string | null>(null);
  const navigate = useNavigate();

  const fetchProjects = async () => {
    try {
      // Clear any previous error before attempting to fetch again
      setError(null);
      const userProjects = await api.projects.getUserProjects();
      // Preserve server ordering (most recently used first)
      setProjects(userProjects);
    } catch (err) {
      // Detect network errors so we can keep the full-page ErrorScreen
      const errorMessage = err instanceof Error ? err.message : 'Failed to load projects';
      setError(errorMessage);
      console.error(err);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchProjects();
  }, []);

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

  // Register tour for projects list
  useRegisterTour('projects-list', [
    {
      target: '[data-tour-id="projects.new"]',
      content: 'Create a <strong>New Project</strong> to organize notebooks, files, and collaborators.'
    },
    {
      target: '[data-tour-id="projects.list"]',
      content: 'Your <strong>projects</strong> appear here. Click a project to open its details.'
    },
    {
      target: '[data-tour-id="projects.list"]',
      content: 'Open the <strong>project menu</strong> to edit, copy, or delete.',
      onHighlight: () => {
        const menuBtn = document.querySelector('[data-tour-id="projects.item.menu"]') as HTMLElement | null;
        if (menuBtn) {
          menuBtn.click();
          setTimeout(() => {
            const portal = document.querySelector('[data-context-menu]') as HTMLElement | null;
            if (portal) {
              portal.style.zIndex = '10001';
            }
          }, 100);
        }
      },
      onDeselect: () => {
        // Close with Escape to match component behavior
        const esc = new KeyboardEvent('keydown', { key: 'Escape' });
        document.dispatchEvent(esc);
      }
    }
  ]);

  // Handle window focus events for Electron
  useEffect(() => {
    const handleWindowFocus = () => {
      // Ensure the window has focus and can receive input
      if (window.electron) {
        // Force focus to the document body to ensure input fields can receive focus
        document.body.focus();
      }
    };

    // Listen for window focus events from Electron main process
    if (window.electron) {
      window.electron.on('window-focus', handleWindowFocus);
      
      return () => {
        window.electron?.removeAllListeners('window-focus');
      };
    }
  }, []);

  const isUserOwner = (_project: Project) => {
    return true;
  };

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
      await fetchProjects();
      
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
      
      // Also set error state for the error panel
      setError(errorMessage);
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
    
    // Restore focus to the main window after dialog closes
    setTimeout(() => {
      if (window.electron) {
        // Focus the main window
        window.focus();
      }
    }, 100);
    
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
      const errorMessage = err instanceof Error ? err.message : 'Failed to delete project';
      setError(errorMessage);
      console.error('Delete project error:', err);
    } finally {
      setDeleteLoading(null);
      setProjectToDelete(null);
    }
  };

  const handleDeleteCancel = () => {
    setShowDeleteConfirm(false);
    setProjectToDelete(null);
    
    // Restore focus to the main window after dialog closes
    setTimeout(() => {
      if (window.electron) {
        // Focus the main window
        window.focus();
      }
    }, 100);
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


  if (loading) {
    return <LoadingSpinner message="Loading projects..." />;
  }

  // Show error screen if there's a persistent error
  if (error && error.toLowerCase().includes('failed to fetch')) {
    return (
      <ErrorScreen
        error={error}
        title="Connection Problem"
        onRetry={fetchProjects}
        onBack={() => {
          // Clear error and remain on the projects route (already here)
          setError(null);
          navigate('/projects');
        }}
      />
    );
  }

  return (
    <div className="h-full overflow-auto bg-gray-50 p-8">
      <div className="max-w-7xl mx-auto">
        {/* Header */}
        <div className="mb-6 flex items-center justify-between gap-2">
          <div className="flex min-w-0 flex-1 items-center">
            <img src="./guide.png" alt="GuideAnts" className="w-12 h-12 mr-4 shrink-0" />
            <div className="min-w-0">
              <h1 className="text-xl font-semibold">GuideAnts Notebooks</h1>
              <p className="text-sm text-gray-600">AI for people</p>
            </div>
          </div>
          <HeaderActionsBar>
            <GuideAntsGuideButton />
            <HomeButton />
            <SettingsButton />
            <TourStartButton screenId="projects-list" inline />
          </HeaderActionsBar>
        </div>

        {/* Error Message */}
        {error && (
          <div className="mb-4 rounded-md bg-red-50 p-4">
            <div className="flex">
              <div className="ml-3">
                <h3 className="text-sm font-medium text-red-800">Error</h3>
                <div className="mt-2 text-sm text-red-700">{error}</div>
              </div>
            </div>
          </div>
        )}

        {/* Projects List */}
        <div>
          <div className="flex items-center justify-between mb-4">
            <h2 className="text-sm font-medium text-gray-700">Projects</h2>
            <button
              onClick={() => navigate('/new-project')}
              className="px-3 py-1 text-sm font-medium text-white bg-blue-600 rounded hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-blue-500"
              data-tour-id="projects.new"
            >
              New Project
            </button>
          </div>
          <div className="space-y-2" data-tour-id="projects.list">
            {projects.map((project) => (
              <div
                key={project.id}
                className="flex items-center justify-between p-4 bg-white rounded-lg shadow-sm hover:shadow-md transition-shadow duration-200"
                data-tour-id="projects.item"
                onClick={() => navigate(`/projects/${project.id}`)}
              >
                <div className="flex items-center flex-grow">
                  <span className="text-gray-600 mr-3">📁</span>
                  <div>
                    <div className="text-sm font-medium">{project.title}</div>
                    <div className="text-xs text-gray-500">{project.description}</div>
                  </div>
                </div>
                <div className="relative">
                  <button
                    onClick={(e) => toggleMenu(project.id, e)}
                    className="p-2 hover:bg-gray-100 rounded-full"
                    data-menu-trigger
                    data-tour-id="projects.item.menu"
                  >
                    <span className="text-gray-600">⋯</span>
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>

        {/* Context Menu Portal */}
      {menuOpen && createPortal(
          <div
            className="fixed bg-white rounded-md shadow-lg z-[9999] border border-gray-200 w-48"
            style={{ 
              top: contextMenuPosition.y, 
              left: contextMenuPosition.x 
            }}
            onClick={(e) => e.stopPropagation()}
            data-context-menu
          data-tour-id="projects.context-menu"
          >
            <div className="py-1">
              {(() => {
                const project = projects.find(p => p.id === menuOpen);
                if (!project || !isUserOwner(project)) return null;
                
                return (
                  <>
                    <button
                      onClick={(e) => handleEdit(project.id, e)}
                      className="w-full text-left px-4 py-2 text-sm text-gray-700 hover:bg-gray-100"
                    >
                      Edit
                    </button>
                    <button
                      onClick={(e) => handleCopy(project.id, e)}
                      disabled={copyLoading === project.id}
                      className={`w-full text-left px-4 py-2 text-sm text-gray-700 hover:bg-gray-100 flex items-center justify-between ${
                        copyLoading === project.id ? 'opacity-50 cursor-not-allowed' : ''
                      }`}
                    >
                      <span>Copy</span>
                      {copyLoading === project.id && (
                        <span className="loading loading-spinner loading-xs"></span>
                      )}
                    </button>
                    <button
                      onClick={(e) => handleDelete(project.id, e)}
                      disabled={deleteLoading === project.id}
                      className={`w-full text-left px-4 py-2 text-sm text-red-600 hover:bg-gray-100 flex items-center justify-between ${
                        deleteLoading === project.id ? 'opacity-50 cursor-not-allowed' : ''
                      }`}
                    >
                      <span>Delete</span>
                      {deleteLoading === project.id && (
                        <span className="loading loading-spinner loading-xs"></span>
                      )}
                    </button>
                  </>
                );
              })()}
            </div>
          </div>,
          document.body
        )}

        {/* Confirmation Dialog */}
        {showDeleteConfirm && projectToDelete && (
          <ConfirmationDialog
            isOpen={showDeleteConfirm}
            onClose={handleDeleteCancel}
            onConfirm={handleDeleteConfirm}
            title="Confirm Deletion"
            message={`Are you sure you want to delete project "${projects.find(p => p.id === projectToDelete)?.title}"? This action cannot be undone.`}
            confirmText="Delete"
            cancelText="Cancel"
          />
        )}
      </div>
    </div>
  );
};

export default Projects; 
