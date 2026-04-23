interface GuidesTabsProps {
  activeTab: 'guides' | 'assistants';
  onTabChange: (tab: 'guides' | 'assistants') => void;
  guidesCount?: number;
  assistantsCount?: number;
}

export function GuidesTabs({ activeTab, onTabChange, guidesCount, assistantsCount }: GuidesTabsProps) {
  return (
    <div className="bg-white border-b px-6" data-tour-id="guides.tabs.container">
      <div className="flex space-x-8">
        <button
          onClick={() => onTabChange('guides')}
          className={`py-3 text-sm font-medium border-b-2 transition-colors ${
            activeTab === 'guides'
              ? 'border-blue-500 text-blue-600'
              : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
          }`}
          data-tour-id="guides.tabs.guides"
        >
          Guides
          {guidesCount !== undefined && (
            <span className="ml-2 px-2 py-0.5 text-xs rounded-full bg-gray-100 text-gray-600">
              {guidesCount}
            </span>
          )}
        </button>
        <button
          onClick={() => onTabChange('assistants')}
          className={`py-3 text-sm font-medium border-b-2 transition-colors ${
            activeTab === 'assistants'
              ? 'border-blue-500 text-blue-600'
              : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
          }`}
          data-tour-id="guides.tabs.assistants"
        >
          Assistants
          {assistantsCount !== undefined && (
            <span className="ml-2 px-2 py-0.5 text-xs rounded-full bg-gray-100 text-gray-600">
              {assistantsCount}
            </span>
          )}
        </button>
      </div>
    </div>
  );
}

