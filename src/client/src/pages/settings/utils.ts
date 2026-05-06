import {
  AddModelRequest,
  CreateRuntimeProfileRequest,
  SettingsProviderDefinitionDto,
  SettingsRuntimeProfileDto,
  SettingsReadinessDto,
  SettingsSchemaDto,
  SettingsModelDto,
  SettingsSectionPropertyDefinitionDto,
  SettingsServiceReadinessDto,
  UpdateRuntimeProfileRequest,
  UpdateSettingsModelRequest,
} from '../../types/settings';
import { AddModelWizardState, CanonicalLocalRuntimeConfig, CatalogEditState, ProfileFormState } from './types';
import { getServiceProviderDisplayName } from './constants/displayLabels';

export const SECRET_MASK = '********';

export function createEmptyProfileForm(): ProfileFormState {
  return {
    profileId: '',
    displayName: '',
    description: '',
    combineSystemAndDeveloperMessages: false,
    thoughtBlockPattern: '',
    samplingParametersJson: '{}',
    thinkingControlJson: '{}',
    providers: [],
  };
}

export function buildProfileCreateRequest(form: ProfileFormState): CreateRuntimeProfileRequest {
  const profileId = form.profileId.trim();
  const displayName = form.displayName.trim();

  if (!profileId) {
    throw new Error('Profile ID is required.');
  }

  if (!/^[a-z][a-z0-9_]*$/.test(profileId)) {
    throw new Error('Profile ID must start with a lowercase letter and contain only lowercase letters, digits, and underscores.');
  }

  if (!displayName) {
    throw new Error('Display name is required.');
  }

  JSON.parse(form.samplingParametersJson);
  JSON.parse(form.thinkingControlJson);

  return {
    profileId,
    displayName,
    description: form.description.trim() || undefined,
    combineSystemAndDeveloperMessages: form.combineSystemAndDeveloperMessages,
    thoughtBlockPattern: form.thoughtBlockPattern.trim() || undefined,
    samplingParametersJson: form.samplingParametersJson,
    thinkingControlJson: form.thinkingControlJson,
    providers: form.providers,
  };
}

export function buildProfileUpdateRequest(form: ProfileFormState): UpdateRuntimeProfileRequest {
  const base = buildProfileCreateRequest(form);
  return { ...base };
}

type RuntimeProfileContractShape = Pick<
  SettingsRuntimeProfileDto,
  | 'profileId'
  | 'displayName'
  | 'description'
  | 'combineSystemAndDeveloperMessages'
  | 'thoughtBlockPattern'
  | 'samplingParametersJson'
  | 'thinkingControlJson'
  | 'providers'
>;

export function createProfileFormFromContractShape(profile: RuntimeProfileContractShape): ProfileFormState {
  return {
    profileId: profile.profileId,
    displayName: profile.displayName,
    description: profile.description ?? '',
    combineSystemAndDeveloperMessages: profile.combineSystemAndDeveloperMessages,
    thoughtBlockPattern: profile.thoughtBlockPattern ?? '',
    samplingParametersJson: profile.samplingParametersJson,
    thinkingControlJson: profile.thinkingControlJson,
    providers: profile.providers ?? [],
  };
}

export function exportRuntimeProfile(profile: SettingsRuntimeProfileDto): string {
  return JSON.stringify(profile, null, 2);
}

export function importRuntimeProfile(json: string): ProfileFormState {
  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch {
    throw new Error('Runtime profile import file is not valid JSON.');
  }

  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error('Runtime profile import file must contain a single JSON object.');
  }

  const candidate = parsed as Record<string, unknown>;
  if (typeof candidate.profileId !== 'string' || candidate.profileId.trim().length === 0) {
    throw new Error('Runtime profile import file must include a non-empty string profileId.');
  }
  if (typeof candidate.displayName !== 'string' || candidate.displayName.trim().length === 0) {
    throw new Error('Runtime profile import file must include a non-empty string displayName.');
  }
  if (typeof candidate.combineSystemAndDeveloperMessages !== 'boolean') {
    throw new Error('Runtime profile import file must include a boolean combineSystemAndDeveloperMessages.');
  }
  if (typeof candidate.samplingParametersJson !== 'string') {
    throw new Error('Runtime profile import file must include a string samplingParametersJson.');
  }
  if (typeof candidate.thinkingControlJson !== 'string') {
    throw new Error('Runtime profile import file must include a string thinkingControlJson.');
  }
  if (candidate.description !== undefined && candidate.description !== null && typeof candidate.description !== 'string') {
    throw new Error('Runtime profile import file description must be a string when present.');
  }
  if (
    candidate.thoughtBlockPattern !== undefined
    && candidate.thoughtBlockPattern !== null
    && typeof candidate.thoughtBlockPattern !== 'string'
  ) {
    throw new Error('Runtime profile import file thoughtBlockPattern must be a string when present.');
  }

  const form = createProfileFormFromContractShape({
    profileId: candidate.profileId,
    displayName: candidate.displayName,
    description: candidate.description ?? undefined,
    combineSystemAndDeveloperMessages: candidate.combineSystemAndDeveloperMessages,
    thoughtBlockPattern: candidate.thoughtBlockPattern ?? undefined,
    samplingParametersJson: candidate.samplingParametersJson,
    thinkingControlJson: candidate.thinkingControlJson,
    providers: Array.isArray(candidate.providers) ? (candidate.providers as string[]) : [],
  });

  buildProfileCreateRequest(form);
  return form;
}

export function createEmptyAddModelWizardState(preselectedProvider?: string | null): AddModelWizardState {
  const provider = (preselectedProvider?.trim() ?? '') as AddModelWizardState['provider'];
  return {
    provider,
    catalogModelId: '',
    catalogDisplayName: '',
    catalogDescription: '',
    catalogDisplayOrder: '',
    catalogIsActive: true,
    runtimeProfileId: '',
    llamaInstallSource: 'huggingface',
    llamaRouterModelId: '',
    llamaHuggingFaceRepository: '',
    llamaHuggingFaceQuantIncludePattern: '',
    llamaHuggingFaceMmprojIncludePattern: '',
    llamaHuggingFaceTargetDirectory: '',
    llamaExistingAliasRouterModelId: '',
    llamaRouterContextSize: '',
    llamaRouterCacheRamMib: '',
  };
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

function normalizeOptionalString(value: string): string | undefined {
  const trimmed = value.trim();
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

/** Optional per-alias llama-server argv written to router-models.ini via admin; blank = no override. */
function parseOptionalRouterContextSize(raw: string): number | undefined {
  const t = raw.trim();
  if (t.length === 0) {
    return undefined;
  }
  const n = Number(t);
  if (!Number.isInteger(n) || n < 1024 || n > 1_048_576) {
    throw new Error('Context size must be a whole number from 1024 to 1048576, or blank to use the container default.');
  }
  return n;
}

function parseOptionalRouterCacheRamMib(raw: string): number | undefined {
  const t = raw.trim();
  if (t.length === 0) {
    return undefined;
  }
  const n = Number(t);
  if (!Number.isInteger(n) || n < 0 || n > 262_144) {
    throw new Error('Prompt cache RAM (MiB) must be a whole number from 0 to 262144, or blank to use the container default.');
  }
  return n;
}

export function parseRuntimeProfileId(runtimeConfigJson?: string): string {
  if (!runtimeConfigJson) return '';
  try {
    const raw = JSON.parse(runtimeConfigJson) as Record<string, unknown>;
    return typeof raw.runtimeProfileId === 'string' ? raw.runtimeProfileId : '';
  } catch {
    return '';
  }
}

export function parseCanonicalLocalRuntimeJson(localRuntimeJson?: string): CanonicalLocalRuntimeConfig | null {
  if (!localRuntimeJson) {
    return null;
  }

  try {
    const parsed = JSON.parse(localRuntimeJson) as Record<string, unknown>;
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return null;
    }

    if (
      typeof parsed.routerModelId !== 'string' ||
      typeof parsed.runtimeProfileId !== 'string'
    ) {
      return null;
    }

    const normalized: CanonicalLocalRuntimeConfig = {
      routerModelId: parsed.routerModelId,
      runtimeProfileId: parsed.runtimeProfileId,
    };

    if (parsed.loadParams && typeof parsed.loadParams === 'object' && !Array.isArray(parsed.loadParams)) {
      normalized.loadParams = parsed.loadParams as Record<string, unknown>;
    }

    if (typeof parsed.parallelToolCalls === 'boolean') {
      normalized.parallelToolCalls = parsed.parallelToolCalls;
    }

    if (typeof parsed.routerContextSize === 'number' && Number.isFinite(parsed.routerContextSize)) {
      normalized.routerContextSize = parsed.routerContextSize;
    }
    if (typeof parsed.routerCacheRamMib === 'number' && Number.isFinite(parsed.routerCacheRamMib)) {
      normalized.routerCacheRamMib = parsed.routerCacheRamMib;
    }

    return normalized;
  } catch {
    return null;
  }
}

export function createCatalogEditStateFromModel(model: SettingsModelDto): CatalogEditState {
  const parsedRuntime = parseCanonicalLocalRuntimeJson(model.runtimeConfigJson);
  return {
    modelId: model.modelId,
    provider: model.provider,
    displayName: model.displayName,
    description: model.description ?? '',
    displayOrder: model.displayOrder?.toString() ?? '',
    isActive: model.isActive,
    runtimeProfileId: parseRuntimeProfileId(model.runtimeConfigJson),
    localRuntimeRouterModelId: parsedRuntime?.routerModelId ?? '',
    localRuntimeLoadParamsJson: parsedRuntime?.loadParams ? JSON.stringify(parsedRuntime.loadParams, null, 2) : '',
    localRuntimeParallelToolCalls: parsedRuntime?.parallelToolCalls === true,
    localRuntimeRouterContextSize:
      parsedRuntime?.routerContextSize !== undefined && parsedRuntime?.routerContextSize !== null
        ? String(parsedRuntime.routerContextSize)
        : '',
    localRuntimeRouterCacheRamMib:
      parsedRuntime?.routerCacheRamMib !== undefined && parsedRuntime?.routerCacheRamMib !== null
        ? String(parsedRuntime.routerCacheRamMib)
        : '',
  };
}

export function buildCanonicalLocalRuntimeFromGuidedForm(
  form: Pick<
    CatalogEditState,
    | 'runtimeProfileId'
    | 'localRuntimeRouterModelId'
    | 'localRuntimeLoadParamsJson'
    | 'localRuntimeParallelToolCalls'
    | 'localRuntimeRouterContextSize'
    | 'localRuntimeRouterCacheRamMib'
  >
): string | undefined {
  const routerModelId = form.localRuntimeRouterModelId.trim();
  const runtimeProfileId = form.runtimeProfileId.trim();
  const loadParamsText = form.localRuntimeLoadParamsJson.trim();
  const parallelToolCalls = form.localRuntimeParallelToolCalls;
  const ctxSizeText = form.localRuntimeRouterContextSize?.trim() ?? '';
  const cacheMibText = form.localRuntimeRouterCacheRamMib?.trim() ?? '';

  const hasAnyGuidedValue =
    routerModelId.length > 0 ||
    runtimeProfileId.length > 0 ||
    loadParamsText.length > 0 ||
    parallelToolCalls ||
    ctxSizeText.length > 0 ||
    cacheMibText.length > 0;

  if (!hasAnyGuidedValue) {
    return undefined;
  }

  if (!routerModelId) {
    throw new Error('Local runtime Router Model ID is required for llama-cpp models.');
  }

  if (!runtimeProfileId) {
    throw new Error('Local runtime Runtime Profile ID is required for llama-cpp models.');
  }

  const canonical: CanonicalLocalRuntimeConfig = {
    routerModelId,
    runtimeProfileId,
  };

  if (loadParamsText.length > 0) {
    const parsed = JSON.parse(loadParamsText) as unknown;
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      throw new Error('Local runtime load params must be a JSON object.');
    }

    canonical.loadParams = parsed as Record<string, unknown>;
  }

  if (parallelToolCalls) {
    canonical.parallelToolCalls = true;
  }

  const ctxOpt = parseOptionalRouterContextSize(form.localRuntimeRouterContextSize);
  const cacheOpt = parseOptionalRouterCacheRamMib(form.localRuntimeRouterCacheRamMib);
  if (ctxOpt !== undefined) {
    canonical.routerContextSize = ctxOpt;
  }
  if (cacheOpt !== undefined) {
    canonical.routerCacheRamMib = cacheOpt;
  }

  return JSON.stringify(canonical, null, 2);
}

export function buildAddModelRequest(state: AddModelWizardState): AddModelRequest {
  const provider = state.provider.trim();
  if (!provider) {
    throw new Error('Pick a provider in Step 1.');
  }
  const modelId = state.catalogModelId.trim();
  if (!modelId) {
    throw new Error('Catalog Model ID is required.');
  }
  const displayName = state.catalogDisplayName.trim();
  if (!displayName) {
    throw new Error('Catalog display name is required.');
  }
  let providerConfig: Record<string, unknown> | undefined;
  if (provider !== 'llama-cpp' && state.runtimeProfileId.trim()) {
    providerConfig = {
      runtimeProfileId: state.runtimeProfileId.trim(),
    };
  }
  let install: AddModelRequest['install'] | undefined;
  if (provider === 'llama-cpp') {
    const runtimeProfileId = state.runtimeProfileId.trim();
    if (!runtimeProfileId) {
      throw new Error('Runtime profile is required for llama-cpp.');
    }
    const routerContextSize = parseOptionalRouterContextSize(state.llamaRouterContextSize);
    const routerCacheRamMib = parseOptionalRouterCacheRamMib(state.llamaRouterCacheRamMib);
    const routerKnobs: { routerContextSize?: number; routerCacheRamMib?: number } = {};
    if (routerContextSize !== undefined) {
      routerKnobs.routerContextSize = routerContextSize;
    }
    if (routerCacheRamMib !== undefined) {
      routerKnobs.routerCacheRamMib = routerCacheRamMib;
    }
    if (state.llamaInstallSource === 'existingAlias') {
      const alias = state.llamaExistingAliasRouterModelId.trim();
      if (!alias) {
        throw new Error('Pick an existing alias to attach.');
      }
      install = {
        source: 'existingAlias',
        routerModelId: alias,
        runtimeProfileId,
        existingAlias: { routerModelId: alias },
        ...routerKnobs,
      };
    } else {
      const routerModelId = state.llamaRouterModelId.trim();
      if (!routerModelId) {
        throw new Error('Router alias is required for Hugging Face install.');
      }
      const repository = state.llamaHuggingFaceRepository.trim();
      const quantPattern = state.llamaHuggingFaceQuantIncludePattern.trim();
      const mmprojPattern = state.llamaHuggingFaceMmprojIncludePattern.trim();
      const targetDirectory = state.llamaHuggingFaceTargetDirectory.trim();
      if (!repository || !quantPattern || !mmprojPattern || !targetDirectory) {
        throw new Error('Repository, quant pattern, mmproj pattern, and target directory are required.');
      }
      install = {
        source: 'huggingface',
        routerModelId,
        runtimeProfileId,
        huggingFace: {
          repository,
          quantIncludePattern: quantPattern,
          mmprojIncludePattern: mmprojPattern,
          targetDirectory,
        },
        ...routerKnobs,
      };
    }
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
    install,
  };
}

/**
 * Derive the canonical reasoning-choices JSON for a llama-cpp catalog row from
 * the selected runtime profile's `thinkingControlJson`. Mirrors the server-side
 * derivation performed at install time by `HuggingFaceModelDownloadService`,
 * so saving a catalog-row edit preserves (rather than wipes) the reasoning
 * choices that dispatch relies on.
 *
 * Returns `undefined` when the profile exposes no choices; the server will
 * then persist `null`, which is the explicit "this profile has no reasoning
 * surface" contract.
 */
function deriveReasoningChoicesJsonFromProfile(profileThinkingControlJson?: string): string | undefined {
  if (!profileThinkingControlJson || profileThinkingControlJson.trim().length === 0) {
    return undefined;
  }
  let parsed: { choiceActions?: Record<string, unknown> };
  try {
    parsed = JSON.parse(profileThinkingControlJson);
  } catch {
    return undefined;
  }
  if (!parsed.choiceActions || typeof parsed.choiceActions !== 'object') {
    return undefined;
  }
  const choices = Object.keys(parsed.choiceActions)
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0);
  return choices.length === 0 ? undefined : JSON.stringify(choices);
}

export function buildCatalogEditRequest(
  state: CatalogEditState,
  options?: { profileThinkingControlJson?: string }
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

  const reasoningChoicesJson = deriveReasoningChoicesJsonFromProfile(options?.profileThinkingControlJson);

  let runtimeConfigJson: string | undefined;
  if (provider === 'llama-cpp') {
    runtimeConfigJson = buildCanonicalLocalRuntimeFromGuidedForm(state);
    if (!runtimeConfigJson) {
      throw new Error('Local runtime configuration is required for llama-cpp models.');
    }
  } else if (state.runtimeProfileId.trim()) {
    runtimeConfigJson = JSON.stringify({ runtimeProfileId: state.runtimeProfileId.trim() });
  }

  return {
    modelId,
    displayName,
    provider,
    description: normalizeOptionalString(state.description),
    reasoningChoicesJson,
    runtimeConfigJson,
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
