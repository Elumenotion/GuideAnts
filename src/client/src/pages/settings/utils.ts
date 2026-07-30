import {
  AddModelRequest,
  CreateRuntimeProfileRequest,
  SettingsProviderDefinitionDto,
  SettingsRuntimeProfileDto,
  SettingsReadinessDto,
  SettingsSchemaDto,
  SettingsModelDto,
  SettingsSectionDto,
  SettingsSectionPropertyDefinitionDto,
  SettingsSectionSchemaDto,
  SettingsServiceReadinessDto,
  UpdateRuntimeProfileRequest,
  UpdateSettingsModelRequest,
} from '../../types/settings';
import {
  ActiveModelOperationKind,
  ActiveModelOperationPollRoute,
  ActiveModelOperationState,
  AddModelWizardState,
  CanonicalLocalRuntimeConfig,
  CatalogEditState,
  ProfileFormState,
} from './types';
import { getServiceProviderDisplayName } from './constants/displayLabels';
import {
  buildLocalModelAddModelRequest,
} from '../../features/localModelOnboarding/buildCommand';
import { mapSettingsAddModelStateToOnboardingDraft } from '../../features/localModelOnboarding/mapDraft';

export const SECRET_MASK = '********';

const ACTIVE_MODEL_OPERATION_KINDS: ActiveModelOperationKind[] = ['add', 'changeQuant'];
const ACTIVE_MODEL_OPERATION_POLL_ROUTES: ActiveModelOperationPollRoute[] = ['operations', 'downloads'];

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

/**
 * Reads a persisted active-operation record. Anything that does not carry a
 * complete, recognized shape is discarded rather than guessed at, so a stale
 * record cannot be polled against the wrong status endpoint.
 */
export function parseActiveModelOperation(raw: string): ActiveModelOperationState | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }
  if (typeof parsed !== 'object' || parsed === null) {
    return null;
  }
  const candidate = parsed as Record<string, unknown>;
  if (!isNonEmptyString(candidate.operationId) || !isNonEmptyString(candidate.catalogModelId)) {
    return null;
  }
  if (typeof candidate.routerModelId !== 'string') {
    return null;
  }
  if (!ACTIVE_MODEL_OPERATION_KINDS.includes(candidate.kind as ActiveModelOperationKind)) {
    return null;
  }
  if (!ACTIVE_MODEL_OPERATION_POLL_ROUTES.includes(candidate.pollRoute as ActiveModelOperationPollRoute)) {
    return null;
  }
  return {
    operationId: candidate.operationId,
    routerModelId: candidate.routerModelId,
    catalogModelId: candidate.catalogModelId,
    kind: candidate.kind as ActiveModelOperationKind,
    pollRoute: candidate.pollRoute as ActiveModelOperationPollRoute,
  };
}

export function createEmptyProfileForm(): ProfileFormState {
  return {
    profileId: '',
    displayName: '',
    description: '',
    combineSystemAndDeveloperMessages: false,
    thoughtBlockPattern: '',
    samplingParametersJson: '{}',
    thinkingControlJson: '{}',
    requestFieldsWhenToolsPresentJson: '{}',
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
  JSON.parse(form.requestFieldsWhenToolsPresentJson);

  return {
    profileId,
    displayName,
    description: form.description.trim() || undefined,
    combineSystemAndDeveloperMessages: form.combineSystemAndDeveloperMessages,
    thoughtBlockPattern: form.thoughtBlockPattern.trim() || undefined,
    samplingParametersJson: form.samplingParametersJson,
    thinkingControlJson: form.thinkingControlJson,
    requestFieldsWhenToolsPresentJson: form.requestFieldsWhenToolsPresentJson,
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
  | 'requestFieldsWhenToolsPresentJson'
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
    requestFieldsWhenToolsPresentJson: profile.requestFieldsWhenToolsPresentJson ?? '{}',
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
  if (
    candidate.requestFieldsWhenToolsPresentJson !== undefined
    && candidate.requestFieldsWhenToolsPresentJson !== null
    && typeof candidate.requestFieldsWhenToolsPresentJson !== 'string'
  ) {
    throw new Error('Runtime profile import file requestFieldsWhenToolsPresentJson must be a string when present.');
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
    requestFieldsWhenToolsPresentJson:
      typeof candidate.requestFieldsWhenToolsPresentJson === 'string'
        ? candidate.requestFieldsWhenToolsPresentJson
        : '{}',
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

/** Guess a bootstrap runtime profile from a router alias label (operator may override). */
export function suggestRuntimeProfileIdForRouterAlias(routerModelId: string): string {
  const upper = routerModelId.toUpperCase();
  if (upper.includes('QWEN3.6') || upper.includes('QWEN3_6')) {
    return 'qwen3_6';
  }
  if (upper.includes('QWEN3.5') || upper.includes('QWEN3_5') || upper.includes('QWEN3-5')) {
    return 'qwen3_5';
  }
  if (upper.includes('GEMMA')) {
    return 'gemma4';
  }
  if (upper.includes('DEEPSEEK')) {
    return 'deepseek_r1';
  }
  if (upper.includes('CODER')) {
    return 'qwen3_coder';
  }
  if (upper.includes('GPT-OSS') || upper.includes('GPT_OSS')) {
    return 'gpt_oss';
  }
  return '';
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
  state.runtimeProfileId = suggestRuntimeProfileIdForRouterAlias(alias);
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

/** Readable fields of an RFC 9457 problem-details error body. */
export interface ApiProblemDetails {
  title?: string;
  detail?: string;
  code?: string;
  remediation?: string;
  status?: number;
}

/**
 * Extracts the human-readable fields from a problem-details error body so
 * callers can render them as text instead of dumping the raw payload.
 */
export function parseProblemDetails(error: unknown): ApiProblemDetails | null {
  if (!error || typeof error !== 'object') {
    return null;
  }
  const body = (error as { body?: unknown }).body;
  if (!body || typeof body !== 'object') {
    return null;
  }
  const candidate = body as Record<string, unknown>;
  const text = (key: string): string | undefined => {
    const value = candidate[key];
    return typeof value === 'string' && value.trim().length > 0 ? value : undefined;
  };
  const details: ApiProblemDetails = {
    title: text('title'),
    // `detail` is the problem-details field; `message` is the shape our own
    // handlers emit for the same purpose.
    detail: text('detail') ?? text('message'),
    code: text('code'),
    remediation: text('remediation'),
    status: typeof candidate.status === 'number' ? candidate.status : undefined,
  };
  if (!details.title && !details.detail && !details.code && !details.remediation) {
    return null;
  }
  return details;
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
    runtimeProfileId: model.provider === 'llama-cpp' ? '' : parseRuntimeProfileId(model.runtimeConfigJson),
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
  let providerConfig: Record<string, unknown> | undefined;
  if (state.runtimeProfileId.trim()) {
    providerConfig = {
      runtimeProfileId: state.runtimeProfileId.trim(),
    };
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
  options?: {
    runtimeConfigJson?: string;
    profileThinkingControlJson?: string;
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

  const reasoningChoicesJson =
    provider === 'llama-cpp'
      ? options?.preserveModelBehavior?.reasoningChoicesJson
      : deriveReasoningChoicesJsonFromProfile(options?.profileThinkingControlJson);

  let runtimeConfigJson = options?.runtimeConfigJson;
  if (provider !== 'llama-cpp' && state.runtimeProfileId.trim()) {
    runtimeConfigJson = JSON.stringify({ runtimeProfileId: state.runtimeProfileId.trim() });
  }

  return {
    modelId,
    displayName,
    provider,
    description: normalizeOptionalString(state.description),
    reasoningChoicesJson,
    runtimeConfigJson,
    combineSystemAndDeveloperMessages:
      options?.preserveModelBehavior?.combineSystemAndDeveloperMessages ?? true,
    thoughtBlockPattern: options?.preserveModelBehavior?.thoughtBlockPattern,
    samplingParametersJson: options?.preserveModelBehavior?.samplingParametersJson ?? '{}',
    thinkingControlJson: options?.preserveModelBehavior?.thinkingControlJson ?? '{}',
    requestFieldsWhenToolsPresentJson:
      options?.preserveModelBehavior?.requestFieldsWhenToolsPresentJson ?? '{}',
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
