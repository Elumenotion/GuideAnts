import { useState } from 'react';
import RecentConversationsList from './RecentConversationsList';
import RecentProjectsList from './RecentProjectsList';
import { UserConversationsQuery } from '../../types/conversation';
import { useNavigate } from 'react-router-dom';

interface ProjectSummary {
  id: string;
  title: string;
  description: string;
  created: string;
}

interface TabbedContentAreaProps {
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
  onMostRecentChange?: (meta: { hasAny: boolean; projectId?: string; notebookId?: string }) => void;
}

const TabbedContentArea = ({
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
  onMostRecentChange,
}: TabbedContentAreaProps) => {
  const [activeTab, setActiveTab] = useState<'conversations' | 'projects'>('conversations');
  const navigate = useNavigate();

  const handleNavigateToAll = (query: UserConversationsQuery) => {
    const params = new URLSearchParams();
    if (query.search) params.append('search', query.search);
    if (query.sortBy) params.append('sortBy', query.sortBy);
    if (query.sortOrder) params.append('sortOrder', query.sortOrder);
    navigate(`/conversations?${params.toString()}`);
  };

  return (
    <div>
      {/* Tabs */}
      <div className="border-b border-gray-200 mb-4">
        <nav className="-mb-px flex space-x-8" data-tour-id="home.tabs">
          <button
            onClick={() => setActiveTab('conversations')}
            className={`py-2 px-1 border-b-2 font-medium text-sm ${
              activeTab === 'conversations'
                ? 'border-blue-500 text-blue-600'
                : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
            }`}
            data-tour-id="home.tabs.conversations"
          >
            Recent Conversations
          </button>
          <button
            onClick={() => setActiveTab('projects')}
            className={`py-2 px-1 border-b-2 font-medium text-sm ${
              activeTab === 'projects'
                ? 'border-blue-500 text-blue-600'
                : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
            }`}
            data-tour-id="home.tabs.projects"
          >
            Recent Projects
          </button>
        </nav>
      </div>

      {/* Tab Content */}
      <div>
        {activeTab === 'conversations' ? (
          <RecentConversationsList 
            pageSize={10}
            onNavigateToAll={handleNavigateToAll}
            onMostRecentChange={onMostRecentChange}
          />
        ) : (
          <RecentProjectsList
            projects={projects}
            loading={loading}
            menuOpen={menuOpen}
            contextMenuPosition={contextMenuPosition}
            copyLoading={copyLoading}
            deleteLoading={deleteLoading}
            showDeleteConfirm={showDeleteConfirm}
            projectToDelete={projectToDelete}
            onToggleMenu={onToggleMenu}
            onEdit={onEdit}
            onCopy={onCopy}
            onDelete={onDelete}
            onDeleteConfirm={onDeleteConfirm}
            onDeleteCancel={onDeleteCancel}
          />
        )}
      </div>
    </div>
  );
};

export default TabbedContentArea;

