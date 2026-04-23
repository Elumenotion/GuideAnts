import { useEffect, useState } from 'react';
import { AssistantDto, GlobalAssistantDto } from '../../../types/guides';
import { api } from '../../../services/api';
import { getApiOrigin } from '../../../config/apiConfig';

interface GuideCrewManagerProps {
  selectedAssistantIds: string[];
  onChange: (assistantIds: string[]) => void;
}

interface AssistantOption {
  id: string;
  name: string;
  description: string;
  avatarUrl?: string;
  isGlobal: boolean;
}

interface AssistantAvatarProps {
  avatarUrl?: string;
  name: string;
  isGlobal: boolean;
}

function AssistantAvatar({ avatarUrl, name, isGlobal }: AssistantAvatarProps) {
  const [avatarObjectUrl, setAvatarObjectUrl] = useState<string | null>(null);

  useEffect(() => {
    if (!avatarUrl) {
      setAvatarObjectUrl(null);
      return;
    }

    // If it's already an absolute URL, use it directly
    if (avatarUrl.startsWith('http://') || avatarUrl.startsWith('https://')) {
      setAvatarObjectUrl(avatarUrl);
      return;
    }

    let cancelled = false;

    const loadAvatar = async () => {
      try {
        const apiOrigin = getApiOrigin();
        const fullUrl = `${apiOrigin}${avatarUrl}`;
        const result = await api.utils.getAuthenticatedUrl(fullUrl);
        if (!cancelled) {
          setAvatarObjectUrl(result.objectUrl);
        }
      } catch (error) {
        if (!cancelled) {
          console.warn('Failed to load assistant avatar:', error);
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
  }, [avatarUrl]);

  if (avatarObjectUrl) {
    return (
      <img
        src={avatarObjectUrl}
        alt={name}
        className="w-10 h-10 rounded-full object-cover"
      />
    );
  }

  return (
    <div
      className={`w-10 h-10 rounded-full flex items-center justify-center text-white font-semibold ${
        isGlobal ? 'bg-gradient-to-br from-blue-500 to-purple-500' : 'bg-gradient-to-br from-green-500 to-teal-500'
      }`}
    >
      {name[0].toUpperCase()}
    </div>
  );
}

export function GuideCrewManager({ selectedAssistantIds, onChange }: GuideCrewManagerProps) {
  const [teamAssistants, setTeamAssistants] = useState<AssistantDto[]>([]);
  const [globalAssistants, setGlobalAssistants] = useState<GlobalAssistantDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [searchTerm, setSearchTerm] = useState('');

  useEffect(() => {
    loadAssistants();
  }, []);

  const loadAssistants = async () => {
    try {
      const [teamData, globalData] = await Promise.all([
        api.guides.assistants.list(),
        api.guides.catalogs.globalAssistants(),
      ]);
      setTeamAssistants(teamData);
      setGlobalAssistants(globalData.filter((a: GlobalAssistantDto) => a.isActive));
    } catch (error) {
      console.error('Failed to load assistants:', error);
    } finally {
      setLoading(false);
    }
  };

  // Convert to unified format
  const availableOptions: AssistantOption[] = [
    ...teamAssistants.map((a) => ({
      id: a.id,
      name: a.name,
      description: a.description,
      avatarUrl: a.avatarUrl,
      isGlobal: false,
    })),
    ...globalAssistants.map((a) => ({
      id: a.id,
      name: a.name,
      description: a.description,
      avatarUrl: a.avatarUrl,
      isGlobal: true,
    })),
  ];

  // Filter out already selected
  const availableFiltered = availableOptions
    .filter((a) => !selectedAssistantIds.includes(a.id))
    .filter((a) =>
      searchTerm ? a.name.toLowerCase().includes(searchTerm.toLowerCase()) : true
    );

  const selectedOptions = selectedAssistantIds
    .map((id) => availableOptions.find((a) => a.id === id))
    .filter((a): a is AssistantOption => !!a);

  const handleAdd = (id: string) => {
    onChange([...selectedAssistantIds, id]);
  };

  const handleRemove = (id: string) => {
    onChange(selectedAssistantIds.filter((aid) => aid !== id));
  };

  const handleMoveUp = (index: number) => {
    if (index === 0) return;
    const newIds = [...selectedAssistantIds];
    [newIds[index - 1], newIds[index]] = [newIds[index], newIds[index - 1]];
    onChange(newIds);
  };

  const handleMoveDown = (index: number) => {
    if (index === selectedAssistantIds.length - 1) return;
    const newIds = [...selectedAssistantIds];
    [newIds[index], newIds[index + 1]] = [newIds[index + 1], newIds[index]];
    onChange(newIds);
  };

  return (
    <div className="space-y-4" data-tour-id="guide.crew.section">
      <h2 className="text-lg font-semibold text-gray-900" data-tour-id="guide.crew.header">Crew Members</h2>
      
      <div className="bg-blue-50 border border-blue-200 rounded-md p-4 text-sm text-blue-900">
        <p>
          <strong>Note:</strong> Your guide works standalone by default. Add crew members to enable multi-agent collaboration.
          When exported, global assistants are linked by name, while custom assistants are included in the export.
        </p>
      </div>

      {loading ? (
        <div className="text-sm text-gray-500">Loading assistants...</div>
      ) : (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
          {/* Available Panel */}
          <div className="border border-gray-300 rounded-md overflow-hidden" data-tour-id="guide.crew.available.panel">
            <div className="bg-gray-50 px-4 py-3 border-b border-gray-300">
              <h3 className="text-sm font-medium text-gray-700">Available Assistants</h3>
            </div>
            <div className="p-3 border-b border-gray-300">
              <input
                type="text"
                placeholder="Search assistants..."
                value={searchTerm}
                onChange={(e) => setSearchTerm(e.target.value)}
                className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                data-tour-id="guide.crew.search"
              />
            </div>
            <div className="max-h-96 overflow-y-auto" data-tour-id="guide.crew.available.list">
              {availableFiltered.length === 0 ? (
                <div className="p-4 text-center text-sm text-gray-500">
                  {searchTerm ? 'No matching assistants' : 'All assistants are already in the crew'}
                </div>
              ) : (
                <div className="divide-y divide-gray-200">
                  {availableFiltered.map((assistant) => (
                    <div
                      key={assistant.id}
                      className="p-3 hover:bg-gray-50 flex items-center justify-between group"
                    >
                      <div className="flex items-center gap-3 flex-1 min-w-0">
                        <AssistantAvatar
                          avatarUrl={assistant.avatarUrl}
                          name={assistant.name}
                          isGlobal={assistant.isGlobal}
                        />
                        <div className="flex-1 min-w-0">
                          <div className="flex items-center gap-2">
                            <span className="text-sm font-medium text-gray-900 truncate">
                              {assistant.name}
                            </span>
                            <span
                              className={`text-xs px-2 py-0.5 rounded ${
                                assistant.isGlobal
                                  ? 'bg-blue-100 text-blue-700'
                                  : 'bg-green-100 text-green-700'
                              }`}
                            >
                              {assistant.isGlobal ? 'Global' : 'Custom'}
                            </span>
                          </div>
                          <p className="text-xs text-gray-500 truncate">{assistant.description}</p>
                        </div>
                      </div>
                      <button
                        type="button"
                        onClick={() => handleAdd(assistant.id)}
                        className="ml-2 opacity-0 group-hover:opacity-100 transition-opacity text-blue-600 hover:text-blue-700 p-1"
                        title="Add to crew"
                        data-tour-id="guide.crew.add"
                      >
                        <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
                        </svg>
                      </button>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>

          {/* Selected Panel */}
          <div className="border border-gray-300 rounded-md overflow-hidden" data-tour-id="guide.crew.selected.panel">
            <div className="bg-blue-50 px-4 py-3 border-b border-blue-300">
              <h3 className="text-sm font-medium text-blue-900">
                Selected Members ({selectedOptions.length})
              </h3>
            </div>
            <div className="max-h-[25rem] overflow-y-auto" data-tour-id="guide.crew.selected.list">
              {selectedOptions.length === 0 ? (
                <div className="p-4 text-center text-sm text-gray-500">
                  No crew members yet. Add assistants from the left panel.
                </div>
              ) : (
                <div className="divide-y divide-gray-200">
                  {selectedOptions.map((assistant, index) => (
                    <div key={assistant.id} className="p-3 flex items-center gap-2 hover:bg-gray-50">
                      {/* Reorder buttons */}
                      <div className="flex flex-col gap-0.5">
                        <button
                          type="button"
                          onClick={() => handleMoveUp(index)}
                          disabled={index === 0}
                          className="p-0.5 text-gray-400 hover:text-gray-600 disabled:opacity-30 disabled:cursor-not-allowed"
                          title="Move up"
                          data-tour-id="guide.crew.reorder.up"
                        >
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 15l7-7 7 7" />
                          </svg>
                        </button>
                        <button
                          type="button"
                          onClick={() => handleMoveDown(index)}
                          disabled={index === selectedOptions.length - 1}
                          className="p-0.5 text-gray-400 hover:text-gray-600 disabled:opacity-30 disabled:cursor-not-allowed"
                          title="Move down"
                          data-tour-id="guide.crew.reorder.down"
                        >
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
                          </svg>
                        </button>
                      </div>

                      {/* Avatar */}
                      <AssistantAvatar
                        avatarUrl={assistant.avatarUrl}
                        name={assistant.name}
                        isGlobal={assistant.isGlobal}
                      />

                      {/* Info */}
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center gap-2">
                          <span className="text-sm font-medium text-gray-900 truncate">
                            {assistant.name}
                          </span>
                          <span
                            className={`text-xs px-2 py-0.5 rounded ${
                              assistant.isGlobal
                                ? 'bg-blue-100 text-blue-700'
                                : 'bg-green-100 text-green-700'
                            }`}
                          >
                            {assistant.isGlobal ? 'Global' : 'Custom'}
                          </span>
                        </div>
                      </div>

                      {/* Remove button */}
                      <button
                        type="button"
                        onClick={() => handleRemove(assistant.id)}
                        className="text-red-600 hover:text-red-700 p-1"
                        title="Remove from crew"
                        data-tour-id="guide.crew.remove"
                      >
                        <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                        </svg>
                      </button>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

