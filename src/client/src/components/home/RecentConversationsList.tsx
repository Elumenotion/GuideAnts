import { useState, useEffect, useCallback, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import { UserConversationDto, UserConversationsQuery } from '../../types/conversation';
import { api } from '../../services/api';

interface RecentConversationsListProps {
  pageSize?: number;
  onNavigateToAll?: (query: UserConversationsQuery) => void;
  onMostRecentChange?: (meta: { hasAny: boolean; projectId?: string; notebookId?: string }) => void;
}

const RecentConversationsList = ({ 
  pageSize = 10,
  onNavigateToAll,
  onMostRecentChange,
}: RecentConversationsListProps) => {
  const navigate = useNavigate();
  const [conversations, setConversations] = useState<UserConversationDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [totalPages, setTotalPages] = useState(0);
  const [totalCount, setTotalCount] = useState(0);
  const [search, setSearch] = useState('');
  const [searchInput, setSearchInput] = useState('');
  const [sortBy, setSortBy] = useState<'date' | 'project' | 'notebook'>('date');
  const [sortOrder, setSortOrder] = useState<'asc' | 'desc'>('desc');
  const hasReportedMostRecentRef = useRef(false);

  const fetchConversations = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const query: UserConversationsQuery = {
        page,
        pageSize,
        search: search || undefined,
        sortBy,
        sortOrder,
      };
      const result = await api.conversations.getUserConversations(query);
      setConversations(result.items);
      setTotalPages(result.totalPages);
      setTotalCount(result.totalCount);

      // Report most recent notebook meta once on initial default load (date desc, page 1)
      if (!hasReportedMostRecentRef.current && page === 1 && sortBy === 'date' && sortOrder === 'desc') {
        hasReportedMostRecentRef.current = true;
        if (onMostRecentChange) {
          if (result.items.length > 0) {
            const top = result.items[0];
            onMostRecentChange({ hasAny: true, projectId: top.projectId, notebookId: top.notebookId });
          } else {
            onMostRecentChange({ hasAny: false });
          }
        }
      }
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'Failed to load conversations';
      setError(errorMessage);
      console.error('Failed to load conversations:', err);
    } finally {
      setLoading(false);
    }
  }, [page, pageSize, search, sortBy, sortOrder]);

  useEffect(() => {
    fetchConversations();
  }, [fetchConversations]);

  // Debounce search input
  useEffect(() => {
    const timer = setTimeout(() => {
      setSearch(searchInput);
      setPage(1); // Reset to first page when search changes
    }, 300);
    return () => clearTimeout(timer);
  }, [searchInput]);

  const handleSortChange = (value: string) => {
    if (value === 'date-newest') {
      setSortBy('date');
      setSortOrder('desc');
    } else if (value === 'date-oldest') {
      setSortBy('date');
      setSortOrder('asc');
    } else if (value === 'project-az') {
      setSortBy('project');
      setSortOrder('asc');
    } else if (value === 'project-za') {
      setSortBy('project');
      setSortOrder('desc');
    } else if (value === 'notebook-az') {
      setSortBy('notebook');
      setSortOrder('asc');
    } else if (value === 'notebook-za') {
      setSortBy('notebook');
      setSortOrder('desc');
    }
    setPage(1); // Reset to first page when sort changes
  };

  const handleRowClick = (conversation: UserConversationDto) => {
    navigate(`/projects/${conversation.projectId}/notebooks/${conversation.notebookId}`, {
      state: { conversationId: conversation.id }
    });
  };

  const handleSeeAll = () => {
    const query: UserConversationsQuery = {
      page: 1,
      search: search || undefined,
      sortBy,
      sortOrder,
    };
    if (onNavigateToAll) {
      onNavigateToAll(query);
    } else {
      const params = new URLSearchParams();
      if (query.search) params.append('search', query.search);
      if (query.sortBy) params.append('sortBy', query.sortBy);
      if (query.sortOrder) params.append('sortOrder', query.sortOrder);
      navigate(`/conversations?${params.toString()}`);
    }
  };

  const formatDate = (dateString: string) => {
    // API returns UTC. Ensure proper UTC parsing - if string doesn't have timezone, assume UTC
    let parsedDate: Date;
    if (dateString.endsWith('Z') || dateString.includes('+') || dateString.includes('-', 10)) {
      // Has timezone info, parse directly
      parsedDate = new Date(dateString);
    } else {
      // No timezone info - assume UTC and append 'Z'
      parsedDate = new Date(dateString + 'Z');
    }
    
    const now = new Date();
    const timeDiffMs = now.getTime() - parsedDate.getTime();
    
    // If date is in the future (more than 1 minute), show formatted date
    // This handles clock skew and parsing errors
    if (timeDiffMs < -60000) {
      return parsedDate.toLocaleDateString();
    }
    
    // Calculate time differences (timeDiffMs is positive for past dates)
    const diffSeconds = Math.floor(timeDiffMs / 1000);
    const diffMinutes = Math.floor(timeDiffMs / (1000 * 60));
    const diffHours = Math.floor(timeDiffMs / (1000 * 60 * 60));
    const diffDays = Math.floor(timeDiffMs / (1000 * 60 * 60 * 24));
    
    // Get local date components for calendar day comparison
    const dateYear = parsedDate.getFullYear();
    const dateMonth = parsedDate.getMonth();
    const dateDay = parsedDate.getDate();
    const nowYear = now.getFullYear();
    const nowMonth = now.getMonth();
    const nowDay = now.getDate();
    
    // Check if same calendar day (in local timezone)
    const isSameDay = dateYear === nowYear && dateMonth === nowMonth && dateDay === nowDay;
    
    // Check if previous calendar day
    const prevDate = new Date(nowYear, nowMonth, nowDay - 1);
    const isYesterday = dateYear === prevDate.getFullYear() && 
                       dateMonth === prevDate.getMonth() && 
                       dateDay === prevDate.getDate();
    
    if (isSameDay) {
      if (diffSeconds < 60) {
        return 'Just now';
      } else if (diffMinutes < 60) {
        return `${diffMinutes} minute${diffMinutes > 1 ? 's' : ''} ago`;
      } else {
        return `${diffHours} hour${diffHours > 1 ? 's' : ''} ago`;
      }
    } else if (isYesterday) {
      return 'Yesterday';
    } else if (diffDays >= 1 && diffDays < 7) {
      return `${diffDays} day${diffDays > 1 ? 's' : ''} ago`;
    } else {
      return parsedDate.toLocaleDateString();
    }
  };

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-sm font-medium text-gray-700"></h2>
        <button onClick={handleSeeAll} className="text-xs text-blue-600 hover:underline" data-tour-id="home.conversations.see-all">
          See all
        </button>
      </div>

      {/* Search and Sort Controls */}
      <div className="mb-4 space-y-2">
        <div className="flex items-center gap-2">
          <input
            type="text"
            placeholder="Search conversations..."
            value={searchInput}
            onChange={(e) => setSearchInput(e.target.value)}
            className="flex-1 px-3 py-2 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
          <select
            value={`${sortBy}-${sortOrder === 'asc' ? 'az' : sortBy === 'date' ? 'newest' : 'za'}`}
            onChange={(e) => handleSortChange(e.target.value)}
            className="px-3 py-2 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
          >
            <option value="date-newest">Date (Newest First)</option>
            <option value="date-oldest">Date (Oldest First)</option>
            <option value="project-az">Project (A-Z)</option>
            <option value="project-za">Project (Z-A)</option>
            <option value="notebook-az">Notebook (A-Z)</option>
            <option value="notebook-za">Notebook (Z-A)</option>
          </select>
        </div>
      </div>

      {/* Table */}
      <div className="bg-white rounded-lg shadow-sm">
        {loading ? (
          <div className="p-8 text-center text-sm text-gray-500">
            <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-blue-600 mx-auto mb-2"></div>
            Loading conversations...
          </div>
        ) : error ? (
          <div className="p-8 text-center text-sm text-red-600">
            {error}
          </div>
        ) : conversations.length === 0 ? (
          <div className="p-8 text-center text-sm text-gray-500">
            {search ? 'No conversations found matching your search.' : 'No conversations yet.'}
          </div>
        ) : (
          <>
            <div className="overflow-x-auto">
              <table className="w-full">
                <thead className="bg-gray-50 border-b border-gray-200">
                  <tr>
                    <th className="px-4 py-3 text-left text-xs font-medium text-gray-700 uppercase tracking-wider">Title</th>
                    <th className="px-4 py-3 text-left text-xs font-medium text-gray-700 uppercase tracking-wider">Notebook</th>
                    <th className="px-4 py-3 text-left text-xs font-medium text-gray-700 uppercase tracking-wider">Project</th>
                    <th className="px-4 py-3 text-left text-xs font-medium text-gray-700 uppercase tracking-wider">Date</th>
                  </tr>
                </thead>
                <tbody className="bg-white divide-y divide-gray-200">
                  {conversations.map((conversation) => (
                    <tr
                      key={conversation.id}
                      onClick={() => handleRowClick(conversation)}
                      className="hover:bg-gray-50 cursor-pointer transition-colors"
                    >
                      <td className="px-4 py-3 text-sm font-medium text-gray-900">
                        {conversation.title || 'Untitled'}
                      </td>
                      <td className="px-4 py-3 text-sm text-gray-600">
                        {conversation.notebookTitle}
                      </td>
                      <td className="px-4 py-3 text-sm text-gray-600">
                        {conversation.projectTitle}
                      </td>
                      <td className="px-4 py-3 text-sm text-gray-500">
                        {formatDate(conversation.lastActivity || conversation.created)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            {/* Pagination */}
            {totalPages > 1 && (
              <div className="px-4 py-3 border-t border-gray-200 flex items-center justify-between">
                <div className="text-xs text-gray-600">
                  Page {page} of {totalPages} ({totalCount} total)
                </div>
                <div className="flex gap-2">
                  <button
                    onClick={() => setPage(p => Math.max(1, p - 1))}
                    disabled={page === 1}
                    className={`px-3 py-1 text-xs rounded-md ${
                      page === 1
                        ? 'bg-gray-100 text-gray-400 cursor-not-allowed'
                        : 'bg-white border border-gray-300 text-gray-700 hover:bg-gray-50'
                    }`}
                  >
                    Previous
                  </button>
                  <button
                    onClick={() => setPage(p => Math.min(totalPages, p + 1))}
                    disabled={page === totalPages}
                    className={`px-3 py-1 text-xs rounded-md ${
                      page === totalPages
                        ? 'bg-gray-100 text-gray-400 cursor-not-allowed'
                        : 'bg-white border border-gray-300 text-gray-700 hover:bg-gray-50'
                    }`}
                  >
                    Next
                  </button>
                </div>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
};

export default RecentConversationsList;

