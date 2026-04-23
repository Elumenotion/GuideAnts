interface InterfaceTabProps {
  displayMode: 'full' | 'last-turn';
  setDisplayMode: (mode: 'full' | 'last-turn') => void;
  commandMode: boolean;
  setCommandMode: (mode: boolean) => void;
  showTurnNavigation: boolean;
  setShowTurnNavigation: (show: boolean) => void;
  collapsible: boolean;
  setCollapsible: (collapsible: boolean) => void;
}

export function InterfaceTab({
  displayMode,
  setDisplayMode,
  commandMode,
  setCommandMode,
  showTurnNavigation,
  setShowTurnNavigation,
  collapsible,
  setCollapsible
}: InterfaceTabProps) {
  return (
    <div className="space-y-6">
      <h3 className="text-lg font-medium text-gray-900">Interface Configuration</h3>
      
      <div className="space-y-4">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Display Mode</label>
          <div className="flex gap-4">
            <label className="flex items-center gap-2 cursor-pointer">
              <input
                type="radio"
                name="displayMode"
                value="full"
                checked={displayMode === 'full'}
                onChange={(e) => setDisplayMode(e.target.value as 'full')}
                className="text-blue-600 focus:ring-blue-500"
              />
              <span className="text-sm text-gray-700">Full Conversation</span>
            </label>
            <label className="flex items-center gap-2 cursor-pointer">
              <input
                type="radio"
                name="displayMode"
                value="last-turn"
                checked={displayMode === 'last-turn'}
                onChange={(e) => setDisplayMode(e.target.value as 'last-turn')}
                className="text-blue-600 focus:ring-blue-500"
              />
              <span className="text-sm text-gray-700">Last Turn Only</span>
            </label>
          </div>
          <p className="text-xs text-gray-500 mt-1">
            "Last Turn Only" is useful for minimal embedded widgets or focused Q&A interfaces.
          </p>
        </div>

        <div className="flex items-center justify-between py-3 border-b border-gray-100">
          <div>
            <span className="block text-sm font-medium text-gray-700">Command Mode</span>
            <span className="block text-xs text-gray-500">Treats each message as a new, independent request (stateless). Hides Undo and Restart controls.</span>
          </div>
          <label className="relative inline-flex items-center cursor-pointer">
            <input 
              type="checkbox" 
              checked={commandMode} 
              onChange={(e) => setCommandMode(e.target.checked)}
              className="sr-only peer" 
            />
            <div className="w-11 h-6 bg-gray-200 peer-focus:outline-none peer-focus:ring-4 peer-focus:ring-blue-300 rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all peer-checked:bg-blue-600"></div>
          </label>
        </div>

        <div className="flex items-center justify-between py-3 border-b border-gray-100">
          <div>
            <span className="block text-sm font-medium text-gray-700">Turn Navigation</span>
            <span className="block text-xs text-gray-500">Show controls to navigate between previous messages</span>
          </div>
          <label className="relative inline-flex items-center cursor-pointer">
            <input 
              type="checkbox" 
              checked={showTurnNavigation} 
              onChange={(e) => setShowTurnNavigation(e.target.checked)}
              className="sr-only peer" 
            />
            <div className="w-11 h-6 bg-gray-200 peer-focus:outline-none peer-focus:ring-4 peer-focus:ring-blue-300 rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all peer-checked:bg-blue-600"></div>
          </label>
        </div>

        <div className="flex items-center justify-between py-3 border-b border-gray-100">
          <div>
            <span className="block text-sm font-medium text-gray-700">Collapsible (Floating)</span>
            <span className="block text-xs text-gray-500">Enable floating/collapsible behavior (useful for embeddings)</span>
          </div>
          <label className="relative inline-flex items-center cursor-pointer">
            <input 
              type="checkbox" 
              checked={collapsible} 
              onChange={(e) => setCollapsible(e.target.checked)}
              className="sr-only peer" 
            />
            <div className="w-11 h-6 bg-gray-200 peer-focus:outline-none peer-focus:ring-4 peer-focus:ring-blue-300 rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all peer-checked:bg-blue-600"></div>
          </label>
        </div>
      </div>
    </div>
  );
}

