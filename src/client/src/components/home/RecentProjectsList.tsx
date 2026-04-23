import { useNavigate } from 'react-router-dom';
import { createPortal } from 'react-dom';
import { ConfirmationDialog } from '../common/ConfirmationDialog';

interface ProjectSummary {
  id: string;
  title: string;
  description: string;
  created: string;
}

interface RecentProjectsListProps {
  projects: ProjectSummary[];
  loading: boolean;
  menuOpen: string | null;
  contextMenuPosition: { x: number; y: number };
  copyLoading: string | null;
  deleteLoading: string | null;
  showDeleteConfirm: boolean;
  projectToDelete: string | null;
  onToggleMenu: (projectId: string, e: React.MouseEvent) => void;
  onEdit: (projectId: string, e: React.MouseEvent) => void;
  onCopy: (projectId: string, e: React.MouseEvent) => void;
  onDelete: (projectId: string, e: React.MouseEvent) => void;
  onDeleteConfirm: () => void;
  onDeleteCancel: () => void;
}

const RecentProjectsList = ({
  projects,
  loading,
  menuOpen,
  contextMenuPosition,
  copyLoading,
  deleteLoading,
  showDeleteConfirm,
  projectToDelete,
  onToggleMenu,
  onEdit,
  onCopy,
  onDelete,
  onDeleteConfirm,
  onDeleteCancel,
}: RecentProjectsListProps) => {
  const navigate = useNavigate();

  const isUserOwner = (_project: ProjectSummary) => {
    return true;
  };

  return (
    <>
      <div>
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-sm font-medium text-gray-700"></h2>
          <button onClick={() => navigate('/projects')} className="text-xs text-blue-600 hover:underline" data-tour-id="home.projects.see-all">
            See all
          </button>
        </div>
        <div className="space-y-2">
          {loading && (
            <div className="text-xs text-gray-500">Loading...</div>
          )}
          {!loading && projects.length === 0 && (
            <div className="text-xs text-gray-500">No recent projects yet.</div>
          )}
          {!loading && projects.map(project => (
            <div
              key={project.id}
              className="flex items-center justify-between p-4 bg-white rounded-lg shadow-sm hover:shadow-md transition-shadow duration-200 cursor-pointer"
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
                  onClick={(e) => onToggleMenu(project.id, e)}
                  className="p-2 hover:bg-gray-100 rounded-full"
                  data-menu-trigger
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
        >
          <div className="py-1">
            {(() => {
              const project = projects.find(p => p.id === menuOpen);
              if (!project || !isUserOwner(project)) return null;
              
              return (
                <>
                  <button
                    onClick={(e) => onEdit(project.id, e)}
                    className="w-full text-left px-4 py-2 text-sm text-gray-700 hover:bg-gray-100"
                  >
                    Edit
                  </button>
                  <button
                    onClick={(e) => onCopy(project.id, e)}
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
                    onClick={(e) => onDelete(project.id, e)}
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
          onClose={onDeleteCancel}
          onConfirm={onDeleteConfirm}
          title="Confirm Deletion"
          message={`Are you sure you want to delete project "${projects.find(p => p.id === projectToDelete)?.title}"? This action cannot be undone.`}
          confirmText="Delete"
          cancelText="Cancel"
        />
      )}
    </>
  );
};

export default RecentProjectsList;

