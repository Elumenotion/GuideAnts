import { useState, useEffect } from 'react';
import type { GuideDto, PublishedGuideDto } from '../../types/guides';
import { getApiOrigin } from '../../config/apiConfig';
import { api } from '../../services/api';

interface GuideCardProps {
  guide: GuideDto;
  publishedGuide?: PublishedGuideDto | null;
  onEdit: (id: string) => void;
  onDelete: (id: string) => void;
  onPublish?: (id: string) => void;
  onManagePublish?: (guideId: string, publishedGuide: PublishedGuideDto) => void;
  onExport?: (id: string) => void;
  onReport?: (id: string) => void;
}

export function GuideCard({ guide, publishedGuide, onEdit, onDelete, onPublish, onManagePublish, onExport, onReport }: GuideCardProps) {
  const [avatarObjectUrl, setAvatarObjectUrl] = useState<string | null>(null);

  const formatDate = (dateString: string) => {
    const date = new Date(dateString);
    return date.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
  };

  const publishedBadgeTitle = publishedGuide
    ? publishedGuide.active
      ? publishedGuide.authMode === 'AppIdentity'
        ? 'Published with GuideAnts app identity auth — Click to manage'
        : 'Click to manage'
      : 'Inactive - Click to reactivate'
    : undefined;

  // Determine button behavior
  const handlePublishClick = () => {
    if (publishedGuide) {
      // Already published - manage it
      onManagePublish?.(guide.id, publishedGuide);
    } else {
      // Not published - publish it
      onPublish?.(guide.id);
    }
  };

  // Load authenticated avatar
  useEffect(() => {
    if (!guide.avatarUrl) {
      setAvatarObjectUrl(null);
      return;
    }

    // If it's already an absolute URL, use it directly
    if (guide.avatarUrl.startsWith('http://') || guide.avatarUrl.startsWith('https://')) {
      setAvatarObjectUrl(guide.avatarUrl);
      return;
    }

    let cancelled = false;

    const loadAvatar = async () => {
      try {
        // Backend returns URLs like "/api/teams/.../avatar"
        // API_BASE_URL includes /api, so extract origin before joining resource path
        const apiOrigin = getApiOrigin();
        const fullUrl = `${apiOrigin}${guide.avatarUrl}`;
        const result = await api.utils.getAuthenticatedUrl(fullUrl);
        if (!cancelled) {
          setAvatarObjectUrl(result.objectUrl);
        }
      } catch (error) {
        if (!cancelled) {
          console.warn('Failed to load guide avatar:', error);
        }
      }
    };

    loadAvatar();

    return () => {
      cancelled = true;
      if (avatarObjectUrl && avatarObjectUrl.startsWith('blob:')) {
        URL.revokeObjectURL(avatarObjectUrl);
      }
    };
  }, [guide.avatarUrl]);

  return (
    <div className="bg-white rounded-lg border border-gray-200 hover:border-gray-300 hover:shadow-md transition-all" data-tour-id="guides.card.guide">
      <div className="p-5">
        {/* Avatar and Title */}
        <div className="flex items-start gap-3 mb-3">
          <div className="flex-shrink-0">
            {avatarObjectUrl ? (
              <img
                src={avatarObjectUrl}
                alt={guide.name}
                className="w-12 h-12 rounded-lg object-cover"
              />
            ) : (
              <div className="w-12 h-12 rounded-lg bg-gradient-to-br from-blue-500 to-purple-600 flex items-center justify-center">
                <svg className="w-6 h-6 text-white" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                </svg>
              </div>
            )}
          </div>
          <div className="flex-1 min-w-0">
            <div className="flex items-center gap-2">
              <h3 className="text-lg font-semibold text-gray-900 truncate">{guide.name}</h3>
              {publishedGuide && (
                <span 
                  className={`px-2 py-0.5 text-xs font-medium rounded cursor-pointer flex-shrink-0 ${
                    publishedGuide.active 
                      ? 'bg-green-100 text-green-700 hover:bg-green-200' 
                      : 'bg-gray-100 text-gray-600 hover:bg-gray-200'
                  }`}
                  onClick={handlePublishClick}
                  title={publishedBadgeTitle}
                >
                  {publishedGuide.active ? '✓ Published' : 'Inactive'}
                </span>
              )}
            </div>
            {guide.modelName && (
              <p className="text-xs text-gray-500 mt-0.5">Model: {guide.modelName}</p>
            )}
          </div>
        </div>

        {/* Description */}
        <p className="text-sm text-gray-600 mb-4 line-clamp-2 min-h-[2.5rem]">
          {guide.description || 'No description provided'}
        </p>

        {/* Stats */}
        <div className="flex items-center gap-4 text-xs text-gray-500 mb-4 pb-4 border-b">
          <span className="flex items-center gap-1">
            <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
            </svg>
            {guide.toolCount} tools
          </span>
          <span className="flex items-center gap-1">
            <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0zm6 3a2 2 0 11-4 0 2 2 0 014 0zM7 10a2 2 0 11-4 0 2 2 0 014 0z" />
            </svg>
            {guide.crewMemberCount} crew
            {guide.crewMemberCount === 0 && (
              <span className="ml-1 px-1.5 py-0.5 text-[10px] font-medium bg-amber-50 text-amber-700 rounded">
                Standalone
              </span>
            )}
          </span>
        </div>

        {/* Footer with date and actions */}
        <div className="flex items-center justify-between">
          <span className="text-xs text-gray-400">
            {formatDate(guide.updated || guide.created)}
          </span>
          <div className="flex gap-1">
            <button
              onClick={() => onEdit(guide.id)}
              className="p-2 text-gray-600 hover:text-blue-600 hover:bg-blue-50 rounded transition-colors"
              title="Edit"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
              </svg>
            </button>
            {onPublish && (
              <button
                onClick={handlePublishClick}
                className={`p-2 rounded transition-colors ${
                  publishedGuide
                    ? 'text-gray-600 hover:text-green-600 hover:bg-green-50'
                    : 'text-gray-600 hover:text-green-600 hover:bg-green-50'
                }`}
                title={publishedGuide ? 'Manage Publishing' : 'Publish'}
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-8l-4-4m0 0L8 8m4-4v12" />
                </svg>
              </button>
            )}
            {onReport && (
              <button
                onClick={() => onReport(guide.id)}
                className="p-2 text-gray-600 hover:text-emerald-600 hover:bg-emerald-50 rounded transition-colors"
                title="Usage Report"
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
                </svg>
              </button>
            )}
            {onExport && (
              <button
                onClick={() => onExport(guide.id)}
                className="p-2 text-gray-600 hover:text-purple-600 hover:bg-purple-50 rounded transition-colors"
                title="Export"
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                </svg>
              </button>
            )}
            <button
              onClick={() => onDelete(guide.id)}
              className="p-2 text-gray-600 hover:text-red-600 hover:bg-red-50 rounded transition-colors"
              title="Delete"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
              </svg>
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

