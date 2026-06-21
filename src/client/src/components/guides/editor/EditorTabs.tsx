interface EditorTabsProps {
  activeTab: 'general' | 'configuration' | 'tools' | 'files' | 'environment' | 'crew' | 'auth';
  showCrew: boolean; // Control whether to show Crew tab (guides only)
  showAuth: boolean; // Control whether to show Auth tab (guides only)
  onTabChange: (tab: 'general' | 'configuration' | 'tools' | 'files' | 'environment' | 'crew' | 'auth') => void;
}

export function EditorTabs({ activeTab, showCrew, showAuth, onTabChange }: EditorTabsProps) {
  const tabs = [
    { id: 'general' as const, label: 'General' },
    { id: 'configuration' as const, label: 'Configuration' },
    { id: 'tools' as const, label: 'Tools' },
    { id: 'files' as const, label: 'Files' },
    { id: 'environment' as const, label: 'Environment' },
    ...(showAuth ? [{ id: 'auth' as const, label: 'Authentication' }] : []),
    ...(showCrew ? [{ id: 'crew' as const, label: 'Crew' }] : []),
  ];

  return (
    <div className="border-b border-gray-200 bg-white px-8" data-tour-id="guide.tabs.container">
      <div className="max-w-7xl mx-auto">
        <nav className="flex space-x-8" aria-label="Tabs">
          {tabs.map((tab) => (
            <button
              key={tab.id}
              type="button"
              onClick={() => onTabChange(tab.id)}
              className={`py-4 px-1 border-b-2 font-medium text-sm ${
                activeTab === tab.id
                  ? 'border-blue-500 text-blue-600'
                  : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
              }`}
              data-tour-id={
                tab.id === 'general' ? 'guide.tabs.general' :
                tab.id === 'configuration' ? 'guide.tabs.configuration' :
                tab.id === 'tools' ? 'guide.tabs.tools' :
                tab.id === 'files' ? 'guide.tabs.files' :
                tab.id === 'environment' ? 'guide.tabs.environment' :
                tab.id === 'auth' ? 'guide.tabs.auth' :
                'guide.tabs.crew'
              }
            >
              {tab.label}
            </button>
          ))}
        </nav>
      </div>
    </div>
  );
}

