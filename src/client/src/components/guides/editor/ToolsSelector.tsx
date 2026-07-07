import { useEffect, useState } from 'react';
import { ToolDto, ContextOptionDto, SandboxWireApiConfigDto } from '../../../types/guides';
import { api } from '../../../services/api';
import { RUN_PYTHON_TOOL_ID } from './skills/skillToolsetMapping';
import { SandboxWireApiPanel } from './SandboxWireApiPanel';
import { classifySchemeFromServerUrl } from './toolSources/toolSourceClassification';
import type { CustomToolDto } from '../../../types/guides';

interface ToolsSelectorProps {
  selectedToolIds: string[];
  contextOptions: ContextOptionDto[];
  customTools: CustomToolDto[];
  requiresRunPython: boolean;
  projectId?: string;
  guideId?: string;
  crewMemberIds: string[];
  sandboxWireApiConfig: SandboxWireApiConfigDto;
  onSelectedToolIdsChange: (toolIds: string[]) => void;
  onSandboxWireApiConfigChange: (config: SandboxWireApiConfigDto) => void;
  onDirtyChange?: () => void;
}

function hasSandboxModuleToolSources(customTools: CustomToolDto[]): boolean {
  return customTools.some((tool) => {
    if (!tool.openApiSpec) {
      return false;
    }
    try {
      const spec = JSON.parse(tool.openApiSpec) as { servers?: Array<{ url?: string }> };
      return (spec.servers ?? []).some(
        (server) => classifySchemeFromServerUrl(server.url ?? '') === 'sandbox-module',
      );
    } catch {
      return false;
    }
  });
}

export function ToolsSelector({
  selectedToolIds,
  contextOptions,
  customTools,
  requiresRunPython,
  projectId,
  guideId,
  crewMemberIds,
  sandboxWireApiConfig,
  onSelectedToolIdsChange,
  onSandboxWireApiConfigChange,
  onDirtyChange,
}: ToolsSelectorProps) {
  const [globalTools, setGlobalTools] = useState<ToolDto[]>([]);
  const [loading, setLoading] = useState(true);

  // Check if there are any context options with empty values
  const hasEmptyContextOptions = contextOptions.some((opt: ContextOptionDto) => !opt.value || opt.value.trim() === '');
  
  // The "Set Context Options" tool ID from database seed
  const SET_CONTEXT_OPTIONS_TOOL_ID = 'b0000000-0000-0000-0000-00000000000c';

  useEffect(() => {
    loadTools();
  }, []);

  const loadTools = async () => {
    try {
      const data = await api.guides.catalogs.tools();
      setGlobalTools(data.filter((t: ToolDto) => t.isActive));
    } catch (error) {
      console.error('Failed to load tools:', error);
    } finally {
      setLoading(false);
    }
  };

  const handleToolToggle = (toolId: string) => {
    // Don't allow toggling Set Context Options if there are empty context options
    if (toolId === SET_CONTEXT_OPTIONS_TOOL_ID && hasEmptyContextOptions) {
      return;
    }

    if (toolId === RUN_PYTHON_TOOL_ID && requiresRunPython) {
      return;
    }

    if (selectedToolIds.includes(toolId)) {
      onSelectedToolIdsChange(selectedToolIds.filter((id: string) => id !== toolId));
    } else {
      onSelectedToolIdsChange([...selectedToolIds, toolId]);
    }
  };

  // Auto-enable Set Context Options if there are empty context options
  useEffect(() => {
    if (hasEmptyContextOptions && !selectedToolIds.includes(SET_CONTEXT_OPTIONS_TOOL_ID)) {
      onSelectedToolIdsChange([...selectedToolIds, SET_CONTEXT_OPTIONS_TOOL_ID]);
    }
  }, [hasEmptyContextOptions, selectedToolIds, onSelectedToolIdsChange]);

  // Auto-enable Run Python when the guide has executable payload (CI files or skill scripts/assets)
  useEffect(() => {
    if (requiresRunPython && !selectedToolIds.includes(RUN_PYTHON_TOOL_ID)) {
      onSelectedToolIdsChange([...selectedToolIds, RUN_PYTHON_TOOL_ID]);
    }
  }, [requiresRunPython, selectedToolIds, onSelectedToolIdsChange]);

  // Group tools by category
  const toolsByCategory = globalTools.reduce((acc, tool) => {
    const category = tool.category || 'Other';
    if (!acc[category]) acc[category] = [];
    acc[category].push(tool);
    return acc;
  }, {} as Record<string, ToolDto[]>);

  for (const category of Object.keys(toolsByCategory)) {
    toolsByCategory[category].sort((a, b) => {
      const orderA = a.displayOrder ?? Number.MAX_SAFE_INTEGER;
      const orderB = b.displayOrder ?? Number.MAX_SAFE_INTEGER;
      if (orderA !== orderB) return orderA - orderB;
      return a.displayName.localeCompare(b.displayName);
    });
  }

  const categories = Object.keys(toolsByCategory).sort();
  const showSandboxWirePanel =
    selectedToolIds.includes(RUN_PYTHON_TOOL_ID) || hasSandboxModuleToolSources(customTools);

  return (
    <div className="space-y-4" data-tour-id="guide.tools.catalog">
      <div>
        <h3 className="text-sm font-medium text-gray-700">Global Tools</h3>
        <p className="text-xs text-gray-500 mt-1">
          Select built-in tools to enable for this {/* entity type will be clear from context */}
        </p>
      </div>

      {loading ? (
        <div className="text-sm text-gray-500">Loading tools...</div>
      ) : (
        <div className="space-y-4">
          {categories.map((category) => {
            const categoryHasRunPython = toolsByCategory[category].some((t) => t.id === RUN_PYTHON_TOOL_ID);
            return (
            <div key={category} className="space-y-2">
              <h4 className="text-xs font-medium text-gray-500 uppercase tracking-wide">{category}</h4>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-2">
                {toolsByCategory[category].map((tool) => {
                  const isSetContextOptionsTool = tool.id === SET_CONTEXT_OPTIONS_TOOL_ID;
                  const isRunPythonTool = tool.id === RUN_PYTHON_TOOL_ID;
                  const isLockedDueToEmptyOptions = isSetContextOptionsTool && hasEmptyContextOptions;
                  const isLockedDueToExecutablePayload = isRunPythonTool && requiresRunPython;
                  const isLocked = isLockedDueToEmptyOptions || isLockedDueToExecutablePayload;

                  return (
                    <label
                      key={tool.id}
                      className={`flex items-start p-3 border rounded-md ${
                        isLocked
                          ? 'border-amber-400 bg-amber-50 cursor-not-allowed'
                          : 'border-gray-300 hover:bg-gray-50 cursor-pointer'
                      }`}
                    >
                      <input
                        type="checkbox"
                        checked={selectedToolIds.includes(tool.id)}
                        onChange={() => handleToolToggle(tool.id)}
                        disabled={isLocked}
                        className="mt-1 mr-3"
                      />
                      <div className="flex-1">
                        <div className="flex items-center gap-2">
                          <div className="text-sm font-medium text-gray-900">{tool.displayName}</div>
                          {isLocked && (
                            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-amber-100 text-amber-800">
                              Required
                            </span>
                          )}
                        </div>
                        {tool.description && (
                          <div className="text-xs text-gray-500 mt-1">{tool.description}</div>
                        )}
                        {isLockedDueToEmptyOptions && (
                          <div className="text-xs text-amber-700 mt-1 font-medium">
                            Automatically enabled because you have context options with empty values
                          </div>
                        )}
                        {isLockedDueToExecutablePayload && (
                          <div className="text-xs text-amber-700 mt-1 font-medium">
                            Automatically enabled because this guide has skill scripts or assets to run
                          </div>
                        )}
                      </div>
                    </label>
                  );
                })}
              </div>
              {categoryHasRunPython && showSandboxWirePanel && (
                <SandboxWireApiPanel
                  projectId={projectId}
                  guideId={guideId}
                  crewMemberIds={crewMemberIds}
                  config={sandboxWireApiConfig}
                  onChange={onSandboxWireApiConfigChange}
                  onDirtyChange={onDirtyChange}
                />
              )}
            </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

