interface GuidesHeaderProps {
  activeTab: 'guides' | 'assistants';
  onCreateGuide: () => void;
  onCreateAssistant: () => void;
  onImportGuide?: (file: File) => void;
  onImportAssistant?: (file: File) => void;
}

export function GuidesHeader({ 
  activeTab, 
  onCreateGuide, 
  onCreateAssistant, 
  onImportGuide,
  onImportAssistant 
}: GuidesHeaderProps) {
  const handleImportGuideClick = () => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.zip';
    input.onchange = (e) => {
      const file = (e.target as HTMLInputElement).files?.[0];
      if (file && onImportGuide) {
        onImportGuide(file);
      }
    };
    input.click();
  };

  const handleImportAssistantClick = () => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.zip';
    input.onchange = (e) => {
      const file = (e.target as HTMLInputElement).files?.[0];
      if (file && onImportAssistant) {
        onImportAssistant(file);
      }
    };
    input.click();
  };

  return (
    <div className="bg-white border-b px-6 py-4" data-tour-id="guides.header.container">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900" data-tour-id="guides.header.title">Guides & Assistants</h1>
          <p className="mt-1 text-sm text-gray-500">
            Create and manage guides with AI assistants for you
          </p>
        </div>
        <div className="flex gap-2">
          {activeTab === 'guides' ? (
            <>
              {onImportGuide && (
                <button
                  onClick={handleImportGuideClick}
                  className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50 flex items-center gap-2"
                  data-tour-id="guides.header.importGuide"
                >
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 16a4 4 0 01-.88-7.903A5 5 0 1115.9 6L16 6a5 5 0 011 9.9M15 13l-3-3m0 0l-3 3m3-3v12" />
                  </svg>
                  Import
                </button>
              )}
              <button
                onClick={onCreateGuide}
                className="px-4 py-2 text-sm font-medium text-white bg-blue-600 rounded-md hover:bg-blue-700 flex items-center gap-2"
                data-tour-id="guides.header.createGuide"
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
                </svg>
                Create Guide
              </button>
            </>
          ) : (
            <>
              {onImportAssistant && (
                <button
                  onClick={handleImportAssistantClick}
                  className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50 flex items-center gap-2"
                  data-tour-id="guides.header.importAssistant"
                >
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 16a4 4 0 01-.88-7.903A5 5 0 1115.9 6L16 6a5 5 0 011 9.9M15 13l-3-3m0 0l-3 3m3-3v12" />
                  </svg>
                  Import
                </button>
              )}
              <button
                onClick={onCreateAssistant}
                className="px-4 py-2 text-sm font-medium text-white bg-blue-600 rounded-md hover:bg-blue-700 flex items-center gap-2"
                data-tour-id="guides.header.createAssistant"
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
                </svg>
                Create Assistant
              </button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

