import { useEffect, useMemo, useState } from 'react';
import { api } from '../../services/api';
import { ModelDto } from '../../types/guides';
import { ModelSelector } from '../guides/editor/ModelSelector';
import { ConfigParams } from '../guides/editor/ConfigParams';
import { normalizeReasoningEffortForModel } from './reasoning';

export interface ChatModelConfigValue {
  modelId: string;
  temperature: number;
  topP: number;
  reasoningEffort?: string;
  samplingOverrides?: Record<string, number>;
}

export interface ChatModelConfiguratorProps {
  mode: 'entity' | 'default';
  modelId?: string;
  temperature?: number;
  topP?: number;
  reasoningEffort?: string;
  samplingOverrides?: Record<string, number>;
  onChange: (next: ChatModelConfigValue) => void;
  /** When set, sampling controls are disabled and this hint is shown. */
  disabledReason?: string;
  /**
   * Change this value to force a refetch of the catalog model list. Lets callers
   * invalidate the dropdown after an Add Model wizard install completes (so the
   * newly-downloaded llama-cpp model is immediately selectable as the default).
   */
  refreshKey?: number;
}

export function ChatModelConfigurator({
  mode,
  modelId,
  temperature = 1,
  topP = 1,
  reasoningEffort,
  samplingOverrides,
  onChange,
  disabledReason,
  refreshKey,
}: ChatModelConfiguratorProps) {
  const [models, setModels] = useState<ModelDto[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    const loadModels = async () => {
      setLoading(true);
      try {
        const data = await api.guides.catalogs.models();
        if (!cancelled) {
          setModels(data);
        }
      } catch (error) {
        console.error('Failed to load models:', error);
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    };
    void loadModels();
    return () => {
      cancelled = true;
    };
  }, [refreshKey]);

  const overrides = samplingOverrides ?? {};

  const selectedModel = useMemo(() => {
    if (!modelId) {
      return undefined;
    }
    return models.find((m) => m.modelId === modelId);
  }, [models, modelId]);

  const paramsDisabled = !!disabledReason;

  const pushChange = (partial: Partial<ChatModelConfigValue>) => {
    const nextReasoningEffort = Object.prototype.hasOwnProperty.call(partial, 'reasoningEffort')
      ? partial.reasoningEffort
      : reasoningEffort;

    onChange({
      modelId: partial.modelId ?? modelId ?? '',
      temperature: partial.temperature ?? temperature,
      topP: partial.topP ?? topP,
      reasoningEffort: nextReasoningEffort,
      samplingOverrides: partial.samplingOverrides ?? overrides,
    });
  };

  useEffect(() => {
    if (loading) {
      return;
    }

    const normalizedReasoningEffort = normalizeReasoningEffortForModel(selectedModel, reasoningEffort);
    if (normalizedReasoningEffort !== reasoningEffort) {
      pushChange({ reasoningEffort: normalizedReasoningEffort });
    }
  }, [loading, selectedModel, reasoningEffort]);

  return (
    <div className="space-y-4">
      <ModelSelector
        models={models}
        loading={loading}
        selectionMode={mode}
        selectedModelId={modelId}
        onChange={(id) => {
          const nextModelId = id ?? '';
          const nextModel = models.find((model) => model.modelId === nextModelId);
          pushChange({
            modelId: nextModelId,
            reasoningEffort: normalizeReasoningEffortForModel(nextModel, reasoningEffort),
          });
        }}
      />

      {disabledReason && (
        <p className="text-sm text-amber-800 bg-amber-50 border border-amber-200 rounded-md px-3 py-2" role="status">
          {disabledReason}
        </p>
      )}

      <ConfigParams
        model={selectedModel}
        temperature={temperature}
        topP={topP}
        reasoningEffort={reasoningEffort}
        samplingOverrides={overrides}
        disabled={paramsDisabled}
        onTemperatureChange={(v) => pushChange({ temperature: v })}
        onTopPChange={(v) => pushChange({ topP: v })}
        onReasoningEffortChange={(v) => pushChange({ reasoningEffort: v })}
        onSamplingParameterChange={(key, value) =>
          pushChange({ samplingOverrides: { ...overrides, [key]: value } })
        }
      />
    </div>
  );
}
