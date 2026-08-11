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
import {
  ActiveModelOperationKind,
  ActiveModelOperationPollRoute,
  ActiveModelOperationState,
  AddModelWizardState,
  CanonicalLocalRuntimeConfig,
  CatalogEditState,
} from './types';
import {
  normalizeParameterSurface,
  providerSupportsRowOwnedRequestShaping,
  validateOptionalJsonObject,
} from './parameterSurface';
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
    if (typeof routerModelId !== 'string') {
      return null;
    }

    const normalized: CanonicalLocalRuntimeConfig = {
      routerModelId,
    };

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

/** Returns the trimmed JSON object, or undefined when the field is blank or an empty object. */
function normalizeBehaviorJson(json: string, label: string): string | undefined {
  const error = validateOptionalJsonObject(json, label);
  if (error) {
    throw new Error(error);
  }
  const trimmed = json.trim();
  return trimmed.length === 0 || trimmed === '{}' ? undefined : trimmed;
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
  if (providerSupportsRowOwnedRequestShaping(provider)) {
    const thinkingControlJson = normalizeBehaviorJson(state.thinkingControlJson, 'Thinking control JSON');
    if (thinkingControlJson) {
      providerConfig.thinkingControlJson = thinkingControlJson;
    }
    const requestFieldsJson = normalizeBehaviorJson(
      state.requestFieldsWhenToolsPresentJson,
      'Extra request fields JSON',
    );
    if (requestFieldsJson) {
      providerConfig.requestFieldsWhenToolsPresentJson = requestFieldsJson;
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
    thinkingControlJson: normalizeBehaviorJson(state.thinkingControlJson, 'Thinking control JSON') ?? '{}',
    requestFieldsWhenToolsPresentJson:
      normalizeBehaviorJson(state.requestFieldsWhenToolsPresentJson, 'Extra request fields JSON') ?? '{}',
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

function getSecretPropertyNames(
  section: SettingsSectionDto,
  schema: SettingsSectionSchemaDto | undefined,
): string[] {
  if (schema) {
    return schema.properties.filter((property) => property.isSecret).map((property) => property.name);
  }
  // Schema fetch can fail independently of the section fetch; secretHasValue always carries an
  // entry for every secret property on the section itself (see MaskSecrets server-side), so it
  // is a reliable fallback that keeps the omit-on-blank guard below active either way.
  return section.secretHasValue ? Object.keys(section.secretHasValue) : [];
}

/**
 * Normalizes a Connections-tab draft immediately before
 * {@code PUT /api/settings/sections/{name}} so an untouched secret input is omitted from the
 * save payload entirely, rather than sent as a value. {@code MergeForUpdate} server-side treats
 * a missing property as "keep the existing stored secret" (see ApplicationSettingsJson.cs), so
 * omission - not a mask placeholder - is what preserves it. This also means a field a password
 * manager autofilled without the user noticing is only sent if it is non-blank, same as any
 * other edit; the mask-placeholder approach previously here relied on the input always holding
 * literal "********" until deliberately changed, which autofill silently defeats.
 */
export function prepareSectionPayloadForSave(
  draft: Record<string, unknown>,
  section: SettingsSectionDto,
  schema: SettingsSectionSchemaDto | undefined,
): Record<string, unknown> {
  const payload = clonePayload(draft);
  const secretPropertyNames = getSecretPropertyNames(section, schema);

  for (const propertyName of secretPropertyNames) {
    const raw = getInputTextValue(payload[propertyName]).trim();
    if (raw.length > 0) {
      payload[propertyName] = raw;
      continue;
    }

    if (section.secretHasValue?.[propertyName]) {
      delete payload[propertyName];
    } else {
      payload[propertyName] = '';
    }
  }

  return payload;
}

/**
 * Seeds a freshly-loaded section's draft with blank values for secrets that already have a
 * stored value, instead of the literal {@code SECRET_MASK} placeholder the server echoes back.
 * Used by ConnectionsTab so an untouched secret input renders empty - matching the "leave blank
 * to keep" contract in {@link SecretInput} - rather than holding a fixed sentinel string that a
 * browser password manager can match against and silently overwrite.
 */
export function stripStoredSecretPlaceholders(section: SettingsSectionDto): Record<string, unknown> {
  const draft = clonePayload(section.payload);
  for (const propertyName of Object.keys(section.secretHasValue ?? {})) {
    if (section.secretHasValue?.[propertyName]) {
      draft[propertyName] = '';
    }
  }
  return draft;
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
