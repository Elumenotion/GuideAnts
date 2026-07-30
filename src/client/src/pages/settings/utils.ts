import {
  AddModelRequest,
  SettingsProviderDefinitionDto,
  SettingsReadinessDto,
  SettingsSchemaDto,
  SettingsModelDto,
  SettingsSectionDto,
  SettingsSectionPropertyDefinitionDto,
  SettingsSectionSchemaDto,
  SettingsServiceReadinessDto,
  UpdateSettingsModelRequest,
} from '../../types/settings';
import { AddModelWizardState, CanonicalLocalRuntimeConfig, CatalogEditState } from './types';
import { normalizeParameterSurface } from './parameterSurface';
import { getServiceProviderDisplayName } from './constants/displayLabels';
import {
  buildLocalModelAddModelRequest,
} from '../../features/localModelOnboarding/buildCommand';
import { mapSettingsAddModelStateToOnboardingDraft } from '../../features/localModelOnboarding/mapDraft';

export const SECRET_MASK = '********';

export function createEmptyAddModelWizardState(preselectedProvider?: string | null): AddModelWizardState {
  const provider = (preselectedProvider?.trim() ?? '') as AddModelWizardState['provider'];
  return {
    provider,
    catalogModelId: '',
    catalogDisplayName: '',
    catalogDescription: '',
    catalogDisplayOrder: '',
    catalogIsActive: true,
    samplingParametersJson: '{}',
    reasoningChoicesJson: '',
    thinkingControlJson: '{}',
    requestFieldsWhenToolsPresentJson: '{}',
    combineSystemAndDeveloperMessages: true,
    thoughtBlockPattern: '',
    llamaInstallSource: 'huggingface',
    llamaRouterModelId: '',
    llamaHuggingFaceRepository: '',
    llamaHuggingFaceResolvedRevision: '',
    llamaHuggingFaceArtifactGroupId: '',
    llamaHuggingFaceModelFiles: [],
    llamaHuggingFaceMmprojFiles: [],
    llamaHuggingFaceTargetDirectory: '',
    llamaHuggingFaceRouterPresetRows: [],
    llamaHuggingFacePresetMode: 'replace',
    llamaExistingAliasRouterModelId: '',
  };
}

function humanizeRouterAliasForDisplay(routerModelId: string): string {
  return routerModelId
    .replace(/-GGUF$/i, '')
    .replace(/[_-]+/g, ' ')
    .trim();
}

export function createAttachAliasWizardState(
  routerModelId: string,
  preselectedProvider: string | null = 'llama-cpp'
): AddModelWizardState {
  const alias = routerModelId.trim();
  const state = createEmptyAddModelWizardState(preselectedProvider);
  state.llamaInstallSource = 'existingAlias';
  state.llamaExistingAliasRouterModelId = alias;
  state.catalogModelId = alias;
  state.catalogDisplayName = humanizeRouterAliasForDisplay(alias) || alias;
  return state;
}

export function humanizeKey(value: string): string {
  return value
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

export function getErrorMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object') {
    const maybeError = error as { message?: string; body?: any };
    if (Array.isArray(maybeError.body?.errors) && maybeError.body.errors.length > 0) {
      return maybeError.body.errors.join(' ');
    }

    if (typeof maybeError.body?.error === 'string') {
      return maybeError.body.error;
    }

    if (typeof maybeError.message === 'string' && maybeError.message.trim().length > 0) {
      return maybeError.message;
    }
  }

  return fallback;
}

export function formatDateTime(value?: string): string {
  if (!value) {
    return 'Unknown';
  }

  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return value;
  }

  return parsed.toLocaleString();
}

function normalizeOptionalString(value: string | undefined | null): string | undefined {
  const trimmed = (value ?? '').trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

function normalizeDisplayOrder(value: string): number | undefined {
  const trimmed = value.trim();
  if (trimmed.length === 0) {
    return undefined;
  }

  const parsed = Number(trimmed);
  if (!Number.isInteger(parsed)) {
    throw new Error('Display order must be a whole number.');
  }

  return parsed;
}

export function parseRuntimeProfileId(runtimeConfigJson?: string): string {
  if (!runtimeConfigJson) return '';
  try {
    const raw = JSON.parse(runtimeConfigJson) as Record<string, unknown>;
    if (typeof raw.runtimeProfileId === 'string') {
      return raw.runtimeProfileId;
    }
    if (typeof raw.RuntimeProfileId === 'string') {
      return raw.RuntimeProfileId;
    }
    const key = Object.keys(raw).find((k) => k.toLowerCase() === 'runtimeprofileid');
    if (key && typeof raw[key] === 'string') {
      return raw[key] as string;
    }
    return '';
  } catch {
    return '';
  }
}

export function parseCanonicalLocalRuntimeJson(localRuntimeJson?: string): CanonicalLocalRuntimeConfig | null {
  if (!localRuntimeJson) {
    return null;
  }

  const getCaseInsensitive = (obj: Record<string, unknown>, key: string): unknown => {
    if (key in obj) {
      return obj[key];
    }
    const found = Object.keys(obj).find((k) => k.toLowerCase() === key.toLowerCase());
    return found ? obj[found] : undefined;
  };

  try {
    const parsed = JSON.parse(localRuntimeJson) as Record<string, unknown>;
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return null;
    }

    const routerModelId = getCaseInsensitive(parsed, 'routerModelId');
    const runtimeProfileId = getCaseInsensitive(parsed, 'runtimeProfileId');
    if (typeof routerModelId !== 'string') {
      return null;
    }

    const normalized: CanonicalLocalRuntimeConfig = {
      routerModelId,
    };
    if (typeof runtimeProfileId === 'string') {
      normalized.runtimeProfileId = runtimeProfileId;
    }

    const loadParams = getCaseInsensitive(parsed, 'loadParams');
    if (loadParams && typeof loadParams === 'object' && !Array.isArray(loadParams)) {
      normalized.loadParams = loadParams as Record<string, unknown>;
    }

    const parallelToolCalls = getCaseInsensitive(parsed, 'parallelToolCalls');
    if (typeof parallelToolCalls === 'boolean') {
      normalized.parallelToolCalls = parallelToolCalls;
    }

    const routerContextSize = getCaseInsensitive(parsed, 'routerContextSize');
    if (typeof routerContextSize === 'number' && Number.isFinite(routerContextSize)) {
      normalized.routerContextSize = routerContextSize;
    }
    const routerCacheRamMib = getCaseInsensitive(parsed, 'routerCacheRamMib');
    if (typeof routerCacheRamMib === 'number' && Number.isFinite(routerCacheRamMib)) {
      normalized.routerCacheRamMib = routerCacheRamMib;
    }

    return normalized;
  } catch {
    return null;
  }
}

export function createCatalogEditStateFromModel(model: SettingsModelDto): CatalogEditState {
  return {
    modelId: model.modelId,
    provider: model.provider,
    displayName: model.displayName,
    description: model.description ?? '',
    displayOrder: model.displayOrder?.toString() ?? '',
    isActive: model.isActive,
    samplingParametersJson: model.samplingParametersJson ?? '{}',
    reasoningChoicesJson: model.reasoningChoicesJson ?? '',
    thinkingControlJson: model.thinkingControlJson ?? '{}',
    requestFieldsWhenToolsPresentJson: model.requestFieldsWhenToolsPresentJson ?? '{}',
    combineSystemAndDeveloperMessages: model.combineSystemAndDeveloperMessages ?? true,
    thoughtBlockPattern: model.thoughtBlockPattern ?? '',
  };
}

export function buildAddModelRequest(state: AddModelWizardState): AddModelRequest {
  const provider = state.provider.trim();
  if (!provider) {
    throw new Error('Pick a provider in Step 1.');
  }
  const modelId = state.catalogModelId.trim();
  const displayName = state.catalogDisplayName.trim();
  if (provider === 'llama-cpp') {
    return buildLocalModelAddModelRequest(
      mapSettingsAddModelStateToOnboardingDraft(state),
      'settings'
    );
  }
  if (!modelId) {
    throw new Error('Catalog Model ID is required.');
  }
  if (!displayName) {
    throw new Error('Catalog display name is required.');
  }
  const parameterSurface = normalizeParameterSurface({
    samplingParametersJson: state.samplingParametersJson,
    reasoningChoicesJson: state.reasoningChoicesJson,
  });
  const providerConfig: Record<string, unknown> = {
    samplingParametersJson: parameterSurface.samplingParametersJson,
  };
  if (parameterSurface.reasoningChoicesJson) {
    providerConfig.reasoningChoicesJson = parameterSurface.reasoningChoicesJson;
  }
  return {
    provider,
    catalog: {
      modelId,
      displayName,
      description: normalizeOptionalString(state.catalogDescription),
      displayOrder: normalizeDisplayOrder(state.catalogDisplayOrder),
      isActive: state.catalogIsActive,
    },
    providerConfig,
  };
}

export function buildCatalogEditRequest(
  state: CatalogEditState,
  options?: {
    runtimeConfigJson?: string;
    preserveModelBehavior?: Pick<
      SettingsModelDto,
      | 'combineSystemAndDeveloperMessages'
      | 'thoughtBlockPattern'
      | 'samplingParametersJson'
      | 'thinkingControlJson'
      | 'requestFieldsWhenToolsPresentJson'
      | 'reasoningChoicesJson'
    >;
  },
): UpdateSettingsModelRequest {
  const modelId = state.modelId.trim();
  const provider = state.provider.trim();
  const displayName = state.displayName.trim();
  if (!modelId) {
    throw new Error('Model ID is required.');
  }
  if (!provider) {
    throw new Error('Provider is required.');
  }
  if (!displayName) {
    throw new Error('Display name is required.');
  }

  const parameterSurface = normalizeParameterSurface({
    samplingParametersJson: state.samplingParametersJson,
    reasoningChoicesJson: state.reasoningChoicesJson,
  });

  return {
    modelId,
    displayName,
    provider,
    description: normalizeOptionalString(state.description),
    reasoningChoicesJson: parameterSurface.reasoningChoicesJson || undefined,
    runtimeConfigJson: options?.runtimeConfigJson,
    combineSystemAndDeveloperMessages: state.combineSystemAndDeveloperMessages,
    thoughtBlockPattern: normalizeOptionalString(state.thoughtBlockPattern),
    samplingParametersJson: parameterSurface.samplingParametersJson,
    thinkingControlJson: state.thinkingControlJson.trim() || '{}',
    requestFieldsWhenToolsPresentJson: state.requestFieldsWhenToolsPresentJson.trim() || '{}',
    isActive: state.isActive,
    displayOrder: normalizeDisplayOrder(state.displayOrder),
  };
}


export function payloadSignature(payload: Record<string, unknown>): string {
  const sortedKeys = Object.keys(payload).sort((left, right) => left.localeCompare(right));
  const normalized: Record<string, unknown> = {};
  for (const key of sortedKeys) {
    normalized[key] = payload[key];
  }

  return JSON.stringify(normalized);
}

export function clonePayload(payload: Record<string, unknown>): Record<string, unknown> {
  return JSON.parse(JSON.stringify(payload)) as Record<string, unknown>;
}

/**
 * When a secret field is left blank but the server reports a stored value,
 * send the mask so {@code MergeForUpdate} preserves the existing ciphertext.
 * Matches the Add AI Services wizard contract.
 */
export function withSecretPreserved(value: string, hasStoredValue: boolean): string {
  const trimmed = value.trim();
  if (trimmed.length > 0) {
    return trimmed;
  }
  return hasStoredValue ? SECRET_MASK : '';
}

/**
 * Normalizes a Connections-tab draft immediately before
 * {@code PUT /api/settings/sections/{name}} so untouched or cleared secret
 * inputs do not overwrite stored secrets with an empty string.
 */
export function prepareSectionPayloadForSave(
  draft: Record<string, unknown>,
  section: SettingsSectionDto,
  schema: SettingsSectionSchemaDto | undefined,
): Record<string, unknown> {
  const payload = clonePayload(draft);
  const secretPropertyNames =
    schema?.properties.filter((property) => property.isSecret).map((property) => property.name) ?? [];

  for (const propertyName of secretPropertyNames) {
    payload[propertyName] = withSecretPreserved(
      getInputTextValue(payload[propertyName]),
      Boolean(section.secretHasValue?.[propertyName]),
    );
  }

  return payload;
}

export function getSectionSchema(schema: SettingsSchemaDto | null, sectionName: string) {
  return schema?.sections.find((section) => section.sectionName === sectionName);
}

export function getServiceReadiness(readiness: SettingsReadinessDto | null, serviceId: string): SettingsServiceReadinessDto | null {
  return readiness?.services.find((service) => service.serviceId === serviceId) ?? null;
}

export function getInputTextValue(value: unknown): string {
  if (value === null || value === undefined) {
    return '';
  }

  if (typeof value === 'string') {
    return value;
  }

  return String(value);
}

export function parseFieldValue(raw: string, property: SettingsSectionPropertyDefinitionDto): unknown {
  if (property.valueType === 'int') {
    if (raw.trim().length === 0) {
      return null;
    }

    const parsed = Number(raw);
    return Number.isNaN(parsed) ? raw : parsed;
  }

  return raw;
}

export function getProviderDisplayName(providers: SettingsProviderDefinitionDto[], providerId: string): string {
  const match = providers.find((provider) => provider.providerId === providerId);
  return getServiceProviderDisplayName(match?.providerId ?? providerId);
}

/**
 * Chat-provider to settings-section mapping. Kept in sync with
 * RoutingReadinessService.MapChatProviderToSection server-side so that overview
 * usage counts attribute assistant-referenced catalog models to the right
 * connection row. The closed set of provider strings must match
 * IChatTargetValidator.KnownProviders.
 */
export function mapChatProviderToSection(provider: string): string | null {
  switch (provider.trim().toLowerCase()) {
    case 'openai-chat':
    case 'openai-responses':
      return 'OpenAI';
    case 'azure-openai-chat':
    case 'azure-openai-responses':
      return 'AzureOpenAI';
    case 'anthropic':
      return 'Anthropic';
    case 'llama-cpp':
      return 'LlamaCpp';
    case 'google-gemini-chat':
      return 'GoogleGeminiApi';
    case 'hf-inference-chat':
      return 'HuggingFace';
    case 'openrouter-chat':
      return 'OpenRouter';
    default:
      return null;
  }
}
