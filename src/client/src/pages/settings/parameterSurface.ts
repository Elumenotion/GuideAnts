import { EMPTY_PARAMETER_SURFACE, type NonLocalParameterSurface } from './parameterSurfaceSeeds';

export type { NonLocalParameterSurface };

export function parseReasoningChoicesJson(json?: string): string[] {
  if (!json || json.trim().length === 0) {
    return [];
  }
  try {
    const parsed = JSON.parse(json) as unknown;
    if (!Array.isArray(parsed)) {
      return [];
    }
    return parsed
      .filter((entry): entry is string => typeof entry === 'string')
      .map((entry) => entry.trim())
      .filter((entry) => entry.length > 0);
  } catch {
    return [];
  }
}

export function formatReasoningChoicesJson(choices: readonly string[]): string {
  const normalized = choices
    .map((choice) => choice.trim())
    .filter((choice) => choice.length > 0);
  const unique = [...new Set(normalized)];
  return unique.length === 0 ? '' : JSON.stringify(unique);
}

export function deriveReasoningChoicesFromThinkingControl(thinkingControlJson?: string): string {
  if (!thinkingControlJson || thinkingControlJson.trim().length === 0) {
    return '';
  }
  try {
    const parsed = JSON.parse(thinkingControlJson) as { choiceActions?: Record<string, unknown> };
    if (!parsed.choiceActions || typeof parsed.choiceActions !== 'object') {
      return '';
    }
    return formatReasoningChoicesJson(Object.keys(parsed.choiceActions));
  } catch {
    return '';
  }
}

export function cloneParameterSurface(surface: NonLocalParameterSurface): NonLocalParameterSurface {
  return {
    samplingParametersJson: surface.samplingParametersJson,
    reasoningChoicesJson: surface.reasoningChoicesJson,
  };
}

export function validateSamplingParametersJson(json: string): string | null {
  const trimmed = json.trim();
  if (trimmed.length === 0) {
    return 'Sampling parameters JSON is required.';
  }
  try {
    const parsed = JSON.parse(trimmed) as unknown;
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return 'Sampling parameters JSON must be an object.';
    }
    return null;
  } catch {
    return 'Sampling parameters JSON must be valid JSON.';
  }
}

export function validateReasoningChoicesJson(json: string): string | null {
  const trimmed = json.trim();
  if (trimmed.length === 0) {
    return null;
  }
  try {
    const parsed = JSON.parse(trimmed) as unknown;
    if (!Array.isArray(parsed)) {
      return 'Reasoning choices JSON must be a JSON array of strings.';
    }
    if (parsed.some((entry) => typeof entry !== 'string' || entry.trim().length === 0)) {
      return 'Reasoning choices must be a JSON array of non-empty strings.';
    }
    return null;
  } catch {
    return 'Reasoning choices JSON must be valid JSON.';
  }
}

/**
 * Providers whose chat clients honor row-owned request shaping (ThinkingControlJson and
 * RequestFieldsWhenToolsPresentJson). The other non-local clients build their request bodies
 * from typed shapes and ignore those columns, so the editor does not offer them there.
 */
const ROW_OWNED_REQUEST_SHAPING_PROVIDERS = new Set(['hf-inference-chat', 'openrouter-chat']);

export function providerSupportsRowOwnedRequestShaping(provider: string): boolean {
  return ROW_OWNED_REQUEST_SHAPING_PROVIDERS.has(provider.trim().toLowerCase());
}

/** Validates an optional JSON-object field: blank and `{}` both mean "not configured". */
export function validateOptionalJsonObject(json: string, label: string): string | null {
  const trimmed = json.trim();
  if (trimmed.length === 0) {
    return null;
  }
  try {
    const parsed = JSON.parse(trimmed) as unknown;
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return `${label} must be a JSON object.`;
    }
    return null;
  } catch {
    return `${label} must be valid JSON.`;
  }
}

export function normalizeParameterSurface(surface: NonLocalParameterSurface): NonLocalParameterSurface {
  const samplingError = validateSamplingParametersJson(surface.samplingParametersJson);
  if (samplingError) {
    throw new Error(samplingError);
  }
  const reasoningError = validateReasoningChoicesJson(surface.reasoningChoicesJson);
  if (reasoningError) {
    throw new Error(reasoningError);
  }

  return {
    samplingParametersJson: surface.samplingParametersJson.trim() || '{}',
    reasoningChoicesJson: formatReasoningChoicesJson(parseReasoningChoicesJson(surface.reasoningChoicesJson)),
  };
}

export function createEmptyParameterSurface(): NonLocalParameterSurface {
  return { ...EMPTY_PARAMETER_SURFACE };
}
