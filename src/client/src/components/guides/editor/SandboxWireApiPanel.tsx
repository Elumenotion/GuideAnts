import { useEffect, useState } from 'react';
import { api } from '../../../services/api';
import type { SandboxWireApiConfigDto } from '../../../types/guides';

interface TargetAssistantOption {
  id: string;
  name: string;
}

interface SandboxWireApiPanelProps {
  projectId?: string;
  guideId?: string;
  crewMemberIds: string[];
  config: SandboxWireApiConfigDto;
  onChange: (config: SandboxWireApiConfigDto) => void;
  onDirtyChange?: () => void;
}

export function SandboxWireApiPanel({
  projectId,
  guideId,
  crewMemberIds,
  config,
  onChange,
  onDirtyChange,
}: SandboxWireApiPanelProps) {
  const [targetAssistants, setTargetAssistants] = useState<TargetAssistantOption[]>([]);
  const [loadingAssistants, setLoadingAssistants] = useState(false);

  useEffect(() => {
    if (!projectId) {
      setTargetAssistants([]);
      return;
    }

    let cancelled = false;
    setLoadingAssistants(true);

    const loadAssistants = async () => {
      try {
        if (guideId) {
          const assistants = await api.projects.notebookTemplates.getAssistants(guideId, projectId);
          if (!cancelled) {
            setTargetAssistants(
              assistants
                .filter((assistant: { id: string }) => assistant.id && assistant.id !== guideId)
                .map((assistant: { id: string; name: string }) => ({
                  id: assistant.id,
                  name: assistant.name,
                })),
            );
          }
          return;
        }

        const globalAssistants = await api.guides.catalogs.globalAssistants();
        if (!cancelled) {
          const crewSet = new Set(crewMemberIds);
          setTargetAssistants(
            globalAssistants
              .filter((assistant: { id: string }) => crewSet.has(assistant.id))
              .map((assistant: { id: string; name: string }) => ({
                id: assistant.id,
                name: assistant.name,
              })),
          );
        }
      } catch {
        if (!cancelled) {
          setTargetAssistants([]);
        }
      } finally {
        if (!cancelled) {
          setLoadingAssistants(false);
        }
      }
    };

    void loadAssistants();
    return () => {
      cancelled = true;
    };
  }, [projectId, guideId, crewMemberIds]);

  const patchConfig = (patch: Partial<SandboxWireApiConfigDto>) => {
    onChange({ ...config, ...patch });
    onDirtyChange?.();
  };

  return (
    <div className="mt-6 border-t border-gray-200 pt-6 space-y-5" data-tour-id="guide.tools.sandbox-wire">
      <div>
        <h3 className="text-sm font-medium text-gray-900">AI model access for Python tools</h3>
        <p className="text-xs text-gray-500 mt-1">
          When enabled, this guide's Python tool and sandbox-module runs receive{' '}
          <code className="text-xs bg-gray-100 px-1 rounded">OPENAI_BASE_URL</code> and{' '}
          <code className="text-xs bg-gray-100 px-1 rounded">OPENAI_API_KEY</code> so scripts can call
          an OpenAI-compatible API with the standard OpenAI or Anthropic SDKs. Requests run a selected
          target assistant and are metered against this project. The key is scoped to a single run.
        </p>
      </div>

      <label className="inline-flex items-center gap-2 text-sm">
        <input
          type="checkbox"
          checked={config.enabled ?? false}
          onChange={(e) => patchConfig({ enabled: e.target.checked })}
          className="rounded border-gray-300 text-blue-600 focus:ring-blue-500"
        />
        Give this guide's Python tools AI model access
      </label>

      {config.enabled && (
        <div className="space-y-5 pl-1">
          <div>
            <label htmlFor="sandbox-wire-target" className="block text-sm font-medium text-gray-700 mb-1">
              Target assistant
            </label>
            {loadingAssistants ? (
              <p className="text-sm text-gray-500">Loading assistants…</p>
            ) : targetAssistants.length === 0 ? (
              <p className="text-sm text-amber-700">
                Add crew members to this guide to select a target assistant. The owning guide cannot target itself.
              </p>
            ) : (
              <select
                id="sandbox-wire-target"
                value={config.targetAssistantId ?? ''}
                onChange={(e) => patchConfig({
                  targetAssistantId: e.target.value || undefined,
                })}
                className="w-full max-w-md border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-blue-500 focus:border-blue-500"
                required
              >
                <option value="">Select target assistant</option>
                {targetAssistants.map((assistant) => (
                  <option key={assistant.id} value={assistant.id}>{assistant.name}</option>
                ))}
              </select>
            )}
          </div>

          <div>
            <h4 className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-2">Cost limits (USD)</h4>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-3 max-w-lg">
              <div>
                <label className="block text-xs text-gray-600 mb-1">Daily limit</label>
                <input
                  type="number"
                  min={0}
                  step={0.01}
                  value={config.dailyLimitUsd ?? ''}
                  onChange={(e) => patchConfig({
                    dailyLimitUsd: e.target.value === '' ? null : Number(e.target.value),
                  })}
                  className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-blue-500 focus:border-blue-500"
                />
              </div>
              <div>
                <label className="block text-xs text-gray-600 mb-1">Monthly limit</label>
                <input
                  type="number"
                  min={0}
                  step={0.01}
                  value={config.monthlyLimitUsd ?? ''}
                  onChange={(e) => patchConfig({
                    monthlyLimitUsd: e.target.value === '' ? null : Number(e.target.value),
                  })}
                  className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-blue-500 focus:border-blue-500"
                />
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
