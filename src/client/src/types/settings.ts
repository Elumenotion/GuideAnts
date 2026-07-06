export interface SettingsSectionSummaryDto {
  sectionName: string;
  hasSecrets: boolean;
  readinessStatus: 'configured' | 'blocked' | 'unconfigured' | 'not-applicable' | string;
  missingFields: string[];
}

export interface SettingsSectionDto {
  sectionName: string;
  schemaVersion: number;
  rowVersion: string;
  updatedUtc: string;
  payload: Record<string, unknown>;
  secretHasValue: Record<string, boolean>;
}

export interface SettingsSchemaDto {
  sections: SettingsSectionSchemaDto[];
  services: SettingsServiceDefinitionDto[];
  providers: SettingsProviderDefinitionDto[];
  runtimeDependencies: SettingsRuntimeDependencyDto[];
}

export interface SettingsSectionSchemaDto {
  sectionName: string;
  schemaVersion: number;
  hasSecrets: boolean;
  properties: SettingsSectionPropertyDefinitionDto[];
}

export interface SettingsSectionPropertyDefinitionDto {
  name: string;
  valueType: 'string' | 'int' | 'bool';
  isSecret: boolean;
  isEditable: boolean;
  isRequired: boolean;
  defaultValue?: unknown;
}

export interface SettingsServiceDefinitionDto {
  serviceId: string;
  sectionName: string;
  providerIds: string[];
  serviceFields: SettingsDependencyFieldDto[];
}

export interface SettingsProviderDefinitionDto {
  providerId: string;
  serviceId: string;
  kind: 'cloud' | 'local' | string;
  providerSectionKey: string;
  providerSettingsSection?: string | null;
  sectionName?: string | null;
  runtimeSectionName?: string | null;
  requiredSectionFields: SettingsDependencyFieldDto[];
  requiredRuntimeKeys: SettingsRuntimeKeyRequirementDto[];
}

export interface ProviderFieldMetadataDto {
  name: string;
  kind: 'url' | 'secret' | 'int' | 'enum' | 'text' | 'path' | string;
  required: boolean;
  enumOptions?: string[] | null;
  operative: boolean;
}

export interface ProviderFieldValueDto {
  name: string;
  value?: string | null;
  isSecret: boolean;
  hasValue: boolean;
}

export interface RuntimeKeyDto {
  key: string;
  hasValue: boolean;
  currentValue?: string | null;
}

export interface ProviderEditorStateDto {
  providerId: string;
  providerKind: string;
  providerSection: string;
  modeId?: string | null;
  hasExplicitMode: boolean;
  isDefaultMode: boolean;
  connectionConfigured: boolean;
  connectionMissingFields: string[];
  canActivate: boolean;
  activationBlockers: string[];
  fields: Record<string, ProviderFieldValueDto>;
  runtimeDependencies: RuntimeKeyDto[];
  operativeFields: string[];
  diagnosticFields: string[];
  fieldMetadata: ProviderFieldMetadataDto[];
}

export interface ServiceEditorReadinessDto {
  status: 'ready' | 'blocked' | string;
  blockers: string[];
  warnings: string[];
}

export interface ServiceEditorStateDto {
  serviceId: string;
  activeProviderId: string;
  providers: ProviderEditorStateDto[];
  readiness: ServiceEditorReadinessDto;
}

/**
 * Details about an upstream proxy failure surfaced by the .NET settings
 * endpoints. The server returns these in a 502 Bad Gateway envelope whenever
 * the proxied local service responds with anything outside the allowed
 * pass-through status codes, so the UI can render the full proxy chain
 * verbatim (no 404-to-"unavailable" fallback; no message rewriting).
 */
export interface LocalModelsUpstreamFailure {
  /** The URL the .NET server attempted to proxy to. */
  upstreamTarget: string;
  /** The HTTP status returned by the upstream, or 0 for network errors. */
  upstreamStatus: number;
  /** The reason phrase (e.g. "Not Found"), or "NetworkError". */
  upstreamStatusText: string;
  /** The Content-Type header the upstream returned (may be empty). */
  upstreamContentType: string;
  /** The raw body returned by the upstream (JSON, text, or empty). */
  upstreamBody: string;
}

/**
 * Result of probing a local-model lifecycle API endpoint. Only two kinds:
 * the upstream responded successfully ({@link LocalModelsAvailableOutcome})
 * or the call failed at some layer ({@link LocalModelsErrorOutcome}). The
 * error variant always carries a human-readable message; when the failure
 * was an upstream proxy response, it also carries the full upstream details
 * so the UI can display exactly what happened and where.
 */
export interface LocalModelsAvailableOutcome {
  kind: 'available';
  payload: unknown;
}
export interface LocalModelsErrorOutcome {
  kind: 'error';
  message: string;
  upstream?: LocalModelsUpstreamFailure;
}
export type LocalModelsListOutcome =
  | LocalModelsAvailableOutcome
  | LocalModelsErrorOutcome;

export interface LocalModelCatalogEntryDto {
  id: string;
  displayName?: string | null;
  license?: string | null;
  multilingual?: boolean;
  producedDimension?: number;
  default?: boolean;
  family?: string | null;
  /** TTS only: how voice selection works for this model. */
  voiceInput?: 'voice_pack' | 'builtin' | 'instruct' | 'optional_ref' | 'none' | null;
  gated?: boolean;
  releaseStatus?: string | null;
}

export interface LocalModelCatalogResponseDto {
  version?: number;
  entries?: LocalModelCatalogEntryDto[];
}

export interface VoicePackVoiceDto {
  voiceId: string;
  displayName?: string | null;
  language?: string | null;
  accent?: string | null;
}

export interface VoicePackResponseDto {
  path?: string;
  count?: number;
  referenceText?: string;
  voices?: VoicePackVoiceDto[];
}

export interface RuntimeVoicesResponseDto {
  modelId?: string;
  voices?: string[];
}

export interface SetActiveProviderRequest {
  providerId: string;
}

export interface ProviderFieldsUpdateRequest {
  fields: Record<string, string | null | undefined>;
}

export interface SettingsDependencyFieldDto {
  sectionName: string;
  fieldName: string;
}

export interface SettingsRuntimeKeyRequirementDto {
  key: string;
}

/**
 * Phase E (R-5.7): Infrastructure tab surface data for a single runtime-owned
 * dependency key. `source` records which configuration provider last wrote the
 * value ('appsettings' | 'environment' | 'compose' | 'user-secrets' | 'application-settings' | 'unknown')
 * so the UI can badge provenance. `hasValue` mirrors `currentValue` but remains
 * accurate when `currentValue` is masked server-side for secret-adjacent keys.
 */
export type SettingsRuntimeDependencySource =
  | 'appsettings'
  | 'environment'
  | 'compose'
  | 'user-secrets'
  | 'application-settings'
  | 'unknown';

/**
 * Phase E (R-5.7): discriminator used by the Infrastructure tab to choose
 * between url-reachability probes and filesystem-existence probes. `other`
 * is returned for runtime keys that match neither shape; the UI renders them
 * without a Probe button.
 */
export type SettingsRuntimeDependencyKind = 'url' | 'path' | 'other';

export interface SettingsRuntimeDependencyDto {
  key: string;
  currentValue?: string | null;
  readOnly: boolean;
  usedByProviderIds: string[];
  source: SettingsRuntimeDependencySource | string;
  hasValue: boolean;
  isSecret: boolean;
  kind: SettingsRuntimeDependencyKind | string;
}

/**
 * Phase E (R-5.7): Infrastructure probes endpoint — per-item probe result.
 * Fields populated depend on `kind`:
 *  - `url`: `reachable` + `statusCode` (never null on successful transport)
 *  - `path`: `exists` + `writable`
 * `error` carries a short diagnostic when the probe failed.
 */
export interface InfrastructureProbeResultDto {
  id: string;
  kind: string;
  target: string;
  latencyMs?: number | null;
  error?: string | null;
  reachable?: boolean | null;
  statusCode?: number | null;
  exists?: boolean | null;
  writable?: boolean | null;
}

export interface InfrastructureProbeRequestItemDto {
  id: string;
  kind: 'url' | 'path' | string;
  target: string;
}

export interface InfrastructureProbeRequestDto {
  items: InfrastructureProbeRequestItemDto[];
}

export interface InfrastructureProbeBatchDto {
  generatedUtc: string;
  results: InfrastructureProbeResultDto[];
}

export interface SettingsReadinessDto {
  generatedUtc: string;
  services: SettingsServiceReadinessDto[];
  globalBlockers: string[];
}

export interface SettingsServiceReadinessDto {
  serviceId: string;
  status: 'ready' | 'blocked' | string;
  blockers: string[];
  warnings: string[];
}

export interface UpdateSettingsSectionRequest {
  rowVersion: string;
  payload: Record<string, unknown>;
}

/** R-9: global default chat model (persisted in ChatDefaults application settings). */
export interface ChatDefaultsDto {
  defaultModelId?: string | null;
  overrideAllChatModels: boolean;
  temperature?: number | null;
  topP?: number | null;
  reasoningEffort?: string | null;
  samplingParametersJson?: string | null;
  rowVersion: string;
}

export interface UpdateChatDefaultsRequest {
  rowVersion: string;
  defaultModelId?: string | null;
  overrideAllChatModels: boolean;
  temperature?: number | null;
  topP?: number | null;
  reasoningEffort?: string | null;
  samplingParametersJson?: string | null;
}

export interface SettingsModelDto {
  modelId: string;
  displayName: string;
  provider: string;
  description?: string;
  reasoningChoicesJson?: string;
  runtimeConfigJson?: string;
  isActive: boolean;
  displayOrder?: number;
  created: string;
  updated?: string;
}

export interface CreateSettingsModelRequest {
  modelId: string;
  displayName: string;
  provider: string;
  description?: string;
  reasoningChoicesJson?: string;
  runtimeConfigJson?: string;
  isActive: boolean;
  displayOrder?: number;
}

export interface UpdateSettingsModelRequest {
  modelId: string;
  displayName: string;
  provider: string;
  description?: string;
  reasoningChoicesJson?: string;
  runtimeConfigJson?: string;
  isActive: boolean;
  displayOrder?: number;
}

export interface AddModelCatalogDto {
  modelId: string;
  displayName: string;
  description?: string;
  displayOrder?: number;
  isActive: boolean;
}

export interface AddModelInstallHuggingFaceDto {
  repository: string;
  quantIncludePattern: string;
  mmprojIncludePattern: string;
  targetDirectory: string;
}

export interface AddModelInstallExistingAliasDto {
  routerModelId: string;
}

export interface AddModelInstallDto {
  source: 'huggingface' | 'existingAlias';
  routerModelId: string;
  runtimeProfileId: string;
  huggingFace?: AddModelInstallHuggingFaceDto;
  existingAlias?: AddModelInstallExistingAliasDto;
  /** Optional. Written to LocalRuntimeJson and included in HF catalog registration. */
  routerContextSize?: number;
  /** Optional. Written to LocalRuntimeJson and included in HF catalog registration. */
  routerCacheRamMib?: number;
}

export interface AddModelRequest {
  provider: string;
  catalog: AddModelCatalogDto;
  providerConfig?: Record<string, unknown>;
  install?: AddModelInstallDto;
}

export interface AddModelErrorDto {
  code: string;
  step: string;
  message: string;
  remediation?: string;
}

export interface AddModelOperationDto {
  kind: 'sync' | 'async';
  catalogModel?: SettingsModelDto;
  status: string;
  error?: AddModelErrorDto;
}

export interface AddModelResponse {
  operationId?: string;
  addOperation: AddModelOperationDto;
}

export interface SettingsRuntimeProfileDto {
  profileId: string;
  displayName: string;
  description?: string;
  combineSystemAndDeveloperMessages: boolean;
  thoughtBlockPattern?: string;
  samplingParametersJson: string;
  thinkingControlJson: string;
  providers: string[];
  created: string;
  updated?: string;
}

export interface CreateRuntimeProfileRequest {
  profileId: string;
  displayName: string;
  description?: string;
  combineSystemAndDeveloperMessages: boolean;
  thoughtBlockPattern?: string;
  samplingParametersJson: string;
  thinkingControlJson: string;
  providers?: string[];
}

export interface UpdateRuntimeProfileRequest {
  profileId: string;
  displayName: string;
  description?: string;
  combineSystemAndDeveloperMessages: boolean;
  thoughtBlockPattern?: string;
  samplingParametersJson: string;
  thinkingControlJson: string;
  providers?: string[];
}

export interface EmbeddingsRebuildResponse {
  jobId: string;
  status: string;
}

export interface EmbeddingsRebuildConflictResponse {
  error: string;
  jobId: string;
  status: string;
}

export interface LlamaRuntimeInventoryItemDto {
  routerModelId: string;
  runtimeState: string;
  modelPath?: string;
  mmprojPath?: string;
  hasModelFile: boolean;
  hasMmprojFile: boolean;
  catalogModelIds: string[];
  notebookReferenceCount: number;
  /** Per-alias `ctx-size` in router-models.ini when set. */
  routerContextSize?: number | null;
  /** Per-alias `cache-ram` (MiB) in router-models.ini when set. */
  routerCacheRamMib?: number | null;
  runtimeFailed?: boolean;
  runtimeExitCode?: number | null;
}

/**
 * Phase F (R-6.10): per-alias runtime status snapshot surfaced by
 * GET /api/settings/llama/runtime/status. `inProgress` is true when the
 * llama server reports the alias as "loading" OR when the alias-scoped
 * coordinator lock is currently held — i.e. a load/unload is in flight.
 * Callers MUST NOT interpret `inProgress` as an RPC lock; it's an
 * observability snapshot derived from existing state without mutation.
 */
export interface LlamaRuntimeAliasStatusDto {
  alias: string;
  loaded: boolean;
  inProgress: boolean;
  runtimeState: string;
  routerModelId?: string | null;
  lastLoadStartedAt?: string | null;
  lastLoadDurationMs?: number | null;
  lastError?: string | null;
}

export interface StartModelDownloadRequest {
  repository: string;
  quantIncludePattern: string;
  mmprojIncludePattern: string;
  routerModelId: string;
  targetDirectory: string;
  /**
   * Catalog registration fields. When all three are supplied, the API creates
   * a matching catalog Model row as soon as the download completes, so the
   * alias is immediately usable as a chat target. Leaving any of them blank
   * skips auto-registration and the operator must create the row manually via
   * Settings → Models.
   */
  catalogModelId?: string;
  catalogDisplayName?: string;
  catalogRuntimeProfileId?: string;
  catalogDescription?: string;
  catalogIsActive?: boolean;
  catalogDisplayOrder?: number;
  catalogLoadParamsJson?: string;
  catalogParallelToolCalls?: boolean;
  catalogRouterContextSize?: number;
  catalogRouterCacheRamMib?: number;
}

export interface ModelDownloadOperationDto {
  operationId: string;
  status: string;
  routerModelId: string;
  progress?: number;
  errorMessage?: string;
  logLine?: string;
  error?: AddModelErrorDto;
}

/**
 * Wire shape for one file returned by the HF repository browser endpoint.
 * The response is service-agnostic: every Hugging Face-backed service
 * (llama-cpp, Image Generation, Speech Transcription, Speech Synthesis,
 * Embeddings) reads the same listing and classifies files client-side.
 *
 * The <c>category</c> field only reflects the llama-cpp-specific heuristic
 * the server applies while tagging GGUFs vs mmproj files — it is a hint
 * that is useful for llama-cpp and meaningless / inert for other services.
 * Do NOT add new service-specific categories here; per-service classifiers
 * derive their own labels from <c>path</c> and file extension.
 *  - "gguf": llama-cpp quant candidate
 *  - "mmproj": llama-cpp vision projector
 *  - "other": everything else (tokenizer configs, safetensors, images, ...)
 */
export interface HuggingFaceRepositoryFileDto {
  path: string;
  size?: number | null;
  category: 'gguf' | 'mmproj' | 'other';
  quantLabel?: string | null;
  sharded: boolean;
  shardIndex?: number | null;
  shardTotal?: number | null;
}

/**
 * Wire shape for <c>GET /api/settings/huggingface/repositories/{owner}/{repo}/files</c>.
 */
export interface HuggingFaceRepositoryListingDto {
  repository: string;
  gated: boolean;
  tokenUsed: boolean;
  modelCardUrl?: string | null;
  files: HuggingFaceRepositoryFileDto[];
}

/**
 * Structured-error envelope returned by the HF browse endpoint on non-2xx.
 * Surfaced to the operator with a clear, actionable message. Codes:
 *  REPO_INVALID | REPO_NOT_FOUND | REPO_TOKEN_MISSING | REPO_TOKEN_INSUFFICIENT | HF_UPSTREAM
 */
export interface HuggingFaceBrowseErrorDto {
  code: string;
  message: string;
  detail?: string | null;
}

/** Wire shape for a non-chat service routing mode (mirrors ServiceModeDto). */
export interface ServiceModeDto {
  serviceName: string;
  modeId: string;
  providerSection: string;
  modelId?: string | null;
  requestPresetJson?: string | null;
  enabled: boolean;
  isDefault: boolean;
}

/** Chat-dispatch target derived from the catalog + assistant metadata (R-9.2). */
export interface ChatTargetDto {
  modelId: string;
  displayName: string;
  provider: string;
  isActive: boolean;
  hasLocalRuntime: boolean;
}

/** Readiness snapshot for a single provider section. */
export interface ProviderSectionReadinessDto {
  sectionName: string;
  configured: boolean;
  missingFields: string[];
}

/** Composed readiness for a non-chat service mode (R-5.2 / R-7.1). */
export interface ModeReadinessDto {
  service: string;
  modeId: string;
  status: 'ready' | 'blocked' | string;
  blockers: string[];
  providerSection?: string | null;
  modelId?: string | null;
  runtimeState?: string | null;
}

/** Composed readiness for a chat target (R-9.2). */
export interface ChatTargetReadinessDto {
  modelId: string;
  provider: string;
  status: 'ready' | 'blocked' | string;
  blockers: string[];
  runtimeState?: string | null;
  assistantUsageCount: number;
  referenceKind: string;
}

export interface ServiceModeRollupDto {
  service: string;
  ready: number;
  total: number;
  modes: ModeReadinessDto[];
}

export interface ChatTargetsRollupDto {
  ready: number;
  total: number;
  targets: ChatTargetReadinessDto[];
}

export interface ProviderConnectionIssueDto {
  section: string;
  missingFields: string[];
}

export interface LlamaRuntimeSnapshotDto {
  loadedAliases: number;
  totalAliases: number;
  missingArtifactAliases: string[];
}

/** Overview composite returned by GET /api/settings/overview. */
export interface SettingsOverviewDto {
  generatedUtc: string;
  serviceModeReadiness: ServiceModeRollupDto[];
  chatTargets: ChatTargetsRollupDto;
  providerIssues: ProviderConnectionIssueDto[];
  llamaRuntime: LlamaRuntimeSnapshotDto;
}

/**
 * Stable blocker prefixes emitted by the backend RoutingReadinessService.
 * The UI keys tooltip localizations off these.
 */
export const RoutingBlockerPrefix = {
  ProviderMissing: 'PROVIDER_MISSING_FIELDS',
  ModelMissing: 'MODEL_NOT_FOUND',
  ModelInactive: 'MODEL_INACTIVE',
  RuntimeState: 'RUNTIME_STATE',
  RuntimeArtifactMissing: 'RUNTIME_ARTIFACT_MISSING',
  RuntimeUnregistered: 'RUNTIME_UNREGISTERED',
  InvalidLocalRuntimeJson: 'MODEL_LOCAL_RUNTIME_INVALID',
} as const;

/**
 * Phase D (R-5.5 / R-8.1): consumers referencing a single provider connection section.
 * Drives the "Used by services" chips on the Connections tab. `modes` lists every
 * ServiceModes row whose providerSection equals `{section}`; `chatTargets` lists
 * catalog models whose provider maps to `{section}` and that are referenced by at
 * least one active Assistant.
 */
export interface ConnectionUsageDto {
  section: string;
  modes: ConnectionUsageModeRef[];
  chatTargets: ConnectionUsageChatTargetRef[];
}

export interface ConnectionUsageModeRef {
  service: string;
  modeId: string;
  isDefault: boolean;
}

export interface ConnectionUsageChatTargetRef {
  modelId: string;
  assistantCount: number;
}

/** Canonical ordering used across the settings shell. */
export const RoutedServiceNames = [
  'Embeddings',
  'ImageGeneration',
  'SpeechTranscription',
  'SpeechSynthesis',
  'DocumentIntelligence',
] as const;

export type RoutedServiceName = (typeof RoutedServiceNames)[number];
