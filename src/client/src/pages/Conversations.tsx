import { useEffect, useState, useCallback } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { UserConversationDto, UserConversationsQuery } from '../types/conversation';
import { api } from '../services/api';
import LoadingSpinner from '../components/LoadingSpinner';
import { HeaderActionsBar } from '../components/common/HeaderActionsBar';
import { GuideAntsGuideButton } from '../features/guideantsGuide/GuideAntsGuideButton';
import { HomeButton } from '../components/common/HomeButton';
import { SettingsButton } from '../components/common/SettingsButton';
import ErrorScreen from '../components/ErrorScreen';
import { TourStartButton } from '../tour/TourStartButton';
import { useRegisterTour } from '../tour/useRegisterTour';

const Conversations = () => {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const [conversations, setConversations] = useState<UserConversationDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [totalPages, setTotalPages] = useState(0);
  const [totalCount, setTotalCount] = useState(0);
  const [pageSize, setPageSize] = useState(20);
  const [isAutoPageSize, setIsAutoPageSize] = useState(true);
  const [search, setSearch] = useState('');
  const [searchInput, setSearchInput] = useState('');
  const [sortBy, setSortBy] = useState<'date' | 'project' | 'notebook'>('date');
  const [sortOrder, setSortOrder] = useState<'asc' | 'desc'>('desc');

  // Helper to compute a responsive page size based on viewport height
  const computeAutoPageSize = () => {
    const headerAndControlsApprox = 320; // px reserved for header/search/controls
    const rowHeightApprox = 56; // px per table row
    const available = Math.max(0, window.innerHeight - headerAndControlsApprox);
    const rows = Math.max(10, Math.floor(available / rowHeightApprox));
    // Map to our supported page size options
    if (rows <= 10) return 10;
    if (rows <= 20) return 20;
    if (rows <= 50) return 50;
    return 100;
  };

  // Read initial state from URL params
  useEffect(() => {
    const pageParam = searchParams.get('page');
    const pageSizeParam = searchParams.get('pageSize');
    const searchParam = searchParams.get('search');
    const sortByParam = searchParams.get('sortBy');
    const sortOrderParam = searchParams.get('sortOrder');

    if (pageParam) setPage(parseInt(pageParam, 10));
    if (pageSizeParam) {
      setPageSize(parseInt(pageSizeParam, 10));
      setIsAutoPageSize(false);
    } else {
      const autoSize = computeAutoPageSize();
      setPageSize(autoSize);
      setIsAutoPageSize(true);
    }
    if (searchParam) {
      setSearch(searchParam);
      setSearchInput(searchParam);
    }
    if (sortByParam && ['date', 'project', 'notebook'].includes(sortByParam)) {
      setSortBy(sortByParam as 'date' | 'project' | 'notebook');
    }
    if (sortOrderParam && ['asc', 'desc'].includes(sortOrderParam)) {
      setSortOrder(sortOrderParam as 'asc' | 'desc');
    }
  }, []); // Only run on mount

  // Keep page size responsive on resize (only while in auto mode)
  useEffect(() => {
    if (!isAutoPageSize) return;
    const onResize = () => {
      const autoSize = computeAutoPageSize();
      setPageSize(prev => (prev === autoSize ? prev : autoSize));
    };
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, [isAutoPageSize]);

  // Update URL params when state changes
  useEffect(() => {
    const params = new URLSearchParams();
    if (page > 1) params.set('page', page.toString());
    if (pageSize !== 20) params.set('pageSize', pageSize.toString());
    if (search) params.set('search', search);
    if (sortBy !== 'date') params.set('sortBy', sortBy);
    if (sortOrder !== 'desc') params.set('sortOrder', sortOrder);
    setSearchParams(params, { replace: true });
  }, [page, pageSize, search, sortBy, sortOrder, setSearchParams]);

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

  const renderPageNumbers = () => {
    const pages = [];
    const maxVisible = 5;
    let start = Math.max(1, page - Math.floor(maxVisible / 2));
    let end = Math.min(totalPages, start + maxVisible - 1);
    
    if (end - start < maxVisible - 1) {
      start = Math.max(1, end - maxVisible + 1);
    }

    if (start > 1) {
      pages.push(
        <button
          key={1}
          onClick={() => setPage(1)}
          className="px-3 py-1 text-sm border border-gray-300 rounded-md hover:bg-gray-50"
        >
          1
        </button>
      );
      if (start > 2) {
        pages.push(<span key="ellipsis1" className="px-2">...</span>);
      }
    }

    for (let i = start; i <= end; i++) {
      pages.push(
        <button
          key={i}
          onClick={() => setPage(i)}
          className={`px-3 py-1 text-sm border rounded-md ${
            i === page
              ? 'bg-blue-600 text-white border-blue-600'
              : 'border-gray-300 hover:bg-gray-50'
          }`}
        >
          {i}
        </button>
      );
    }

    if (end < totalPages) {
      if (end < totalPages - 1) {
        pages.push(<span key="ellipsis2" className="px-2">...</span>);
      }
      pages.push(
        <button
          key={totalPages}
          onClick={() => setPage(totalPages)}
          className="px-3 py-1 text-sm border border-gray-300 rounded-md hover:bg-gray-50"
        >
          {totalPages}
        </button>
      );
    }

    return pages;
  };

  // Register tour for conversations list
  useRegisterTour('conversations-list', [
    {
      target: '[data-tour-id="conversations.search"]',
      content: 'Search your <strong>conversations</strong> by title, notebook, or project.'
    },
    {
      target: '[data-tour-id="conversations.sort"]',
      content: 'Sort results by <strong>date</strong>, <strong>project</strong>, or <strong>notebook</strong>.'
    },
    {
      target: '[data-tour-id="conversations.page-size"]',
      content: 'Adjust the <strong>page size</strong> to see more or fewer results per page.'
    },
    {
      target: '[data-tour-id="conversations.results.count"]',
      content: 'See how many <strong>conversations</strong> match your filters.'
    },
    {
      target: '[data-tour-id="conversations.table"]',
      content: 'Click any <strong>row</strong> to open the notebook conversation.'
    }
  ]);

  if (loading && conversations.length === 0) {
    return <LoadingSpinner />;
  }

  if (error && error.toLowerCase().includes('failed to fetch')) {
    return (
      <ErrorScreen
        error={error}
        title="Connection Problem"
        onRetry={fetchConversations}
        onBack={() => navigate('/')}
      />
    );
  }


  return (
    <div className="h-full overflow-auto bg-gray-50 p-8">
      <div className="max-w-7xl mx-auto">
        <div className="mb-6 flex items-center justify-between gap-2">
          <h1 className="min-w-0 flex-1 truncate text-xl font-semibold text-gray-900 sm:text-2xl">All Conversations</h1>
          <HeaderActionsBar>
            <GuideAntsGuideButton />
            <HomeButton />
            <SettingsButton />
            <TourStartButton screenId="conversations-list" inline />
          </HeaderActionsBar>
        </div>

        {/* Search and Sort Controls */}
        <div className="mb-6 space-y-4">
          <div className="flex items-center gap-4">
            <div className="flex-1">
              <input
                type="text"
                placeholder="Search conversations by title, notebook, or project..."
                value={searchInput}
                onChange={(e) => setSearchInput(e.target.value)}
                className="w-full px-4 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                data-tour-id="conversations.search"
              />
            </div>
            <select
              value={`${sortBy}-${sortOrder === 'asc' ? 'az' : sortBy === 'date' ? 'newest' : 'za'}`}
              onChange={(e) => handleSortChange(e.target.value)}
              className="px-4 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              data-tour-id="conversations.sort"
            >
              <option value="date-newest">Date (Newest First)</option>
              <option value="date-oldest">Date (Oldest First)</option>
              <option value="project-az">Project (A-Z)</option>
              <option value="project-za">Project (Z-A)</option>
              <option value="notebook-az">Notebook (A-Z)</option>
              <option value="notebook-za">Notebook (Z-A)</option>
            </select>
            <select
              value={pageSize}
              onChange={(e) => {
                setPageSize(parseInt(e.target.value, 10));
                setPage(1);
                setIsAutoPageSize(false);
              }}
              className="px-4 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              data-tour-id="conversations.page-size"
            >
              <option value={10}>10 per page</option>
              <option value={20}>20 per page</option>
              <option value={50}>50 per page</option>
              <option value={100}>100 per page</option>
            </select>
          </div>
        </div>

        {/* Results Count */}
        {!loading && (
          <div className="mb-4 text-sm text-gray-600" data-tour-id="conversations.results.count">
            Showing {conversations.length > 0 ? (page - 1) * pageSize + 1 : 0} to{' '}
            {Math.min(page * pageSize, totalCount)} of {totalCount} conversations
          </div>
        )}

        {/* Table */}
        <div className="bg-white rounded-lg shadow-sm overflow-hidden" data-tour-id="conversations.table">
          {loading && conversations.length === 0 ? (
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
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-700 uppercase tracking-wider">Title</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-700 uppercase tracking-wider">Notebook</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-700 uppercase tracking-wider">Project</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-700 uppercase tracking-wider">Date</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white divide-y divide-gray-200">
                    {conversations.map((conversation) => (
                      <tr
                        key={conversation.id}
                        onClick={() => handleRowClick(conversation)}
                        className="hover:bg-gray-50 cursor-pointer transition-colors"
                      >
                        <td className="px-6 py-4 text-sm font-medium text-gray-900">
                          {conversation.title || 'Untitled'}
                        </td>
                        <td className="px-6 py-4 text-sm text-gray-600">
                          {conversation.notebookTitle}
                        </td>
                        <td className="px-6 py-4 text-sm text-gray-600">
                          {conversation.projectTitle}
                        </td>
                        <td className="px-6 py-4 text-sm text-gray-500">
                          {formatDate(conversation.lastActivity || conversation.created)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              {/* Pagination */}
              {totalPages > 1 && (
                <div className="px-6 py-4 border-t border-gray-200 flex items-center justify-between">
                  <div className="text-sm text-gray-600">
                    Page {page} of {totalPages}
                  </div>
                  <div className="flex items-center gap-2">
                    <button
                      onClick={() => setPage(p => Math.max(1, p - 1))}
                      disabled={page === 1}
                      className={`px-3 py-1 text-sm rounded-md ${
                        page === 1
                          ? 'bg-gray-100 text-gray-400 cursor-not-allowed'
                          : 'bg-white border border-gray-300 text-gray-700 hover:bg-gray-50'
                      }`}
                    >
                      Previous
                    </button>
                    <div className="flex items-center gap-1">
                      {renderPageNumbers()}
                    </div>
                    <button
                      onClick={() => setPage(p => Math.min(totalPages, p + 1))}
                      disabled={page === totalPages}
                      className={`px-3 py-1 text-sm rounded-md ${
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
    </div>
  );
};

export default Conversations;

