import type { ModelDto } from '../../types/guides';
import type { ChatDefaultsDto, UpdateChatDefaultsRequest } from '../../types/settings';
import {
  normalizeReasoningEffortForModel,
  normalizeSamplingValueForModel,
} from './reasoning';

export interface ChatModelConfigValue {
  modelId: string;
  temperature?: number | null;
  topP?: number | null;
  reasoningEffort?: string;
  samplingOverrides?: Record<string, number>;
}

export function parseSamplingOverrides(json?: string | null): Record<string, number> {
  if (!json) {
    return {};
  }

  try {
    const parsed = JSON.parse(json) as unknown;
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return {};
    }

    const out: Record<string, number> = {};
    for (const [key, value] of Object.entries(parsed)) {
      if (typeof value === 'number' && !Number.isNaN(value)) {
        out[key] = value;
      }
    }
    return out;
  } catch {
    return {};
  }
}

export function chatDefaultsToConfig(defaults: ChatDefaultsDto): ChatModelConfigValue {
  return {
    modelId: defaults.defaultModelId ?? '',
    temperature: defaults.temperature ?? null,
    topP: defaults.topP ?? null,
    reasoningEffort: defaults.reasoningEffort ?? undefined,
    samplingOverrides: parseSamplingOverrides(defaults.samplingParametersJson),
  };
}

export function normalizeChatModelConfigForModel(
  config: ChatModelConfigValue,
  model: ModelDto | undefined
): ChatModelConfigValue {
  const samplingOverrides = config.samplingOverrides ?? {};
  const temperature = typeof config.temperature === 'number'
    ? config.temperature
    : samplingOverrides.temperature;
  const topP = typeof config.topP === 'number'
    ? config.topP
    : samplingOverrides.top_p;
  const declaredKeys = new Set(
    (model?.samplingParameterPolicy ?? []).map((param) => param.key.toLowerCase())
  );
  const normalizedOverrides: Record<string, number> = {};

  for (const [key, value] of Object.entries(samplingOverrides)) {
    const normalizedKey = key.toLowerCase();
    if (
      normalizedKey !== 'temperature'
      && normalizedKey !== 'top_p'
      && declaredKeys.has(normalizedKey)
      && typeof value === 'number'
      && !Number.isNaN(value)
    ) {
      normalizedOverrides[key] = value;
    }
  }

  return {
    modelId: config.modelId,
    temperature: normalizeSamplingValueForModel(model, 'temperature', temperature),
    topP: normalizeSamplingValueForModel(model, 'top_p', topP),
    reasoningEffort: normalizeReasoningEffortForModel(model, config.reasoningEffort),
    samplingOverrides: normalizedOverrides,
  };
}

export function buildChatDefaultsUpdateRequest(
  base: ChatDefaultsDto,
  config: ChatModelConfigValue,
  overrideAllChatModels: boolean
): UpdateChatDefaultsRequest {
  const samplingParametersJson =
    config.samplingOverrides && Object.keys(config.samplingOverrides).length > 0
      ? JSON.stringify(config.samplingOverrides)
      : null;

  return {
    rowVersion: base.rowVersion,
    defaultModelId: config.modelId ? config.modelId : null,
    overrideAllChatModels,
    temperature: config.temperature ?? null,
    topP: config.topP ?? null,
    reasoningEffort: config.reasoningEffort ?? null,
    samplingParametersJson,
  };
}

export function buildChatDefaultsModelChangeRequest(
  base: ChatDefaultsDto,
  modelId: string,
  model: ModelDto | undefined,
  overrideAllChatModels: boolean
): UpdateChatDefaultsRequest {
  const normalizedConfig = normalizeChatModelConfigForModel(
    {
      ...chatDefaultsToConfig(base),
      modelId,
    },
    model
  );

  return buildChatDefaultsUpdateRequest(base, normalizedConfig, overrideAllChatModels);
}
