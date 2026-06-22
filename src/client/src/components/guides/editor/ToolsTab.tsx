import { useSearchParams } from 'react-router-dom';
import { ToolsSelector } from './ToolsSelector';
import { OpenApiSchemas } from './OpenApiSchemas';
import { CustomToolDto, ContextOptionDto, EnvironmentVariableDto } from '../../../types/guides';

interface ToolsTabProps {
  selectedToolIds: string[];
  customTools: CustomToolDto[];
  contextOptions: ContextOptionDto[];
  environmentVariables: EnvironmentVariableDto[];
  onSelectedToolIdsChange: (ids: string[]) => void;
  onCustomToolsChange: (tools: CustomToolDto[]) => void;
  onEnvironmentVariablesChange: (variables: EnvironmentVariableDto[]) => void;
  onValidationChange?: (hasErrors: boolean) => void;
  onDirtyChange?: () => void;
}

export function ToolsTab({
  selectedToolIds,
  customTools,
  contextOptions,
  environmentVariables,
  onSelectedToolIdsChange,
  onCustomToolsChange,
  onEnvironmentVariablesChange,
  onValidationChange,
  onDirtyChange,
}: ToolsTabProps) {
  const [searchParams, setSearchParams] = useSearchParams();
  
  // Persist tools subTab in URL for navigation stability
  const subTab = (searchParams.get('toolsSubTab') || 'global') as 'global' | 'connectors';

  const setSubTab = (tab: 'global' | 'connectors') => {
    setSearchParams((prev) => {
      const newParams = new URLSearchParams(prev);
      newParams.set('toolsSubTab', tab);
      return newParams;
    });
  };

  const subTabs = [
    { id: 'global' as const, label: 'Global Tools' },
    { id: 'connectors' as const, label: 'Tool Sources' },
  ];

  return (
    <div className="bg-white rounded-lg border border-gray-200 overflow-hidden" data-tour-id="guide.tools.section">
      <div className="flex border-b border-gray-200" data-tour-id="guide.tools.subtabs" role="tablist" aria-label="Tools sections">
        {subTabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            role="tab"
            aria-selected={subTab === tab.id}
            onClick={() => setSubTab(tab.id)}
            className={`px-6 py-3 text-sm font-medium focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
              subTab === tab.id
                ? 'text-blue-600 bg-blue-50 border-b-2 border-blue-600'
                : 'text-gray-600 hover:text-gray-900 hover:bg-gray-50'
            }`}
            data-tour-id={tab.id === 'global' ? 'guide.tools.subtab.global' : 'guide.tools.subtab.connectors'}
          >
            {tab.label}
          </button>
        ))}
      </div>

      <div className="p-6">
        {subTab === 'global' && (
          <div data-tour-id="guide.tools.global.section">
          <ToolsSelector
            selectedToolIds={selectedToolIds}
            contextOptions={contextOptions}
            onSelectedToolIdsChange={onSelectedToolIdsChange}
          />
          </div>
        )}

        {subTab === 'connectors' && (
          <div data-tour-id="guide.tools.openapi.section">
          <OpenApiSchemas
            customTools={customTools}
            environmentVariables={environmentVariables}
            onCustomToolsChange={onCustomToolsChange}
            onEnvironmentVariablesChange={onEnvironmentVariablesChange}
            onValidationChange={onValidationChange}
            onDirtyChange={onDirtyChange}
          />
          </div>
        )}
      </div>
    </div>
  );
}

