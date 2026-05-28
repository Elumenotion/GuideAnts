import { useEffect, useMemo, useState } from 'react';
import { api } from '../../services/api';
import { ModelDto } from '../../types/guides';
import { ModelSelector } from '../guides/editor/ModelSelector';
import { ConfigParams } from '../guides/editor/ConfigParams';
import { normalizeReasoningEffortForModel, normalizeSamplingValueForModel } from './reasoning';
import {
  ChatModelConfigValue,
  normalizeChatModelConfigForModel,
} from './chatDefaults';

export type { ChatModelConfigValue } from './chatDefaults';

export interface ChatModelConfiguratorProps {
  mode: 'entity' | 'default';
  modelId?: string;
  temperature?: number | null;
  topP?: number | null;
  reasoningEffort?: string;
  samplingOverrides?: Record<string, number>;
  onChange: (next: ChatModelConfigValue) => void;
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
  temperature,
  topP,
  reasoningEffort,
  samplingOverrides,
  onChange,
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

  const pushChange = (
    partial: Partial<ChatModelConfigValue>,
    modelForNext: ModelDto | undefined = selectedModel
  ) => {
    const nextReasoningEffort = Object.prototype.hasOwnProperty.call(partial, 'reasoningEffort')
      ? partial.reasoningEffort
      : reasoningEffort;
    const nextTemperature = Object.prototype.hasOwnProperty.call(partial, 'temperature')
      ? partial.temperature
      : temperature;
    const nextTopP = Object.prototype.hasOwnProperty.call(partial, 'topP')
      ? partial.topP
      : topP;

    onChange(normalizeChatModelConfigForModel({
      modelId: partial.modelId ?? modelId ?? '',
      temperature: nextTemperature,
      topP: nextTopP,
      reasoningEffort: nextReasoningEffort,
      samplingOverrides: partial.samplingOverrides ?? overrides,
    }, modelForNext));
  };

  useEffect(() => {
    if (loading) {
      return;
    }

    const normalizedReasoningEffort = normalizeReasoningEffortForModel(selectedModel, reasoningEffort);
    const normalizedTemperature = normalizeSamplingValueForModel(selectedModel, 'temperature', temperature);
    const normalizedTopP = normalizeSamplingValueForModel(selectedModel, 'top_p', topP);
    if (
      normalizedReasoningEffort !== reasoningEffort
      || normalizedTemperature !== temperature
      || normalizedTopP !== topP
    ) {
      pushChange({
        temperature: normalizedTemperature,
        topP: normalizedTopP,
        reasoningEffort: normalizedReasoningEffort
      });
    }
  }, [loading, selectedModel, temperature, topP, reasoningEffort]);

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
          }, nextModel);
        }}
      />

      <ConfigParams
        model={selectedModel}
        temperature={temperature}
        topP={topP}
        reasoningEffort={reasoningEffort}
        samplingOverrides={overrides}
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
