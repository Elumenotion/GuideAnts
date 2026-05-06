using System.Text.Json;
using System.Text.Json.Nodes;

namespace GuideAntsApi.Models.Settings;

public sealed record SettingsSectionSummaryDto(
    string SectionName,
    bool HasSecrets,
    string ReadinessStatus,
    IReadOnlyList<string> MissingFields);

public sealed record SettingsSectionDto(
    string SectionName,
    int SchemaVersion,
    string RowVersion,
    DateTime UpdatedUtc,
    JsonObject Payload,
    Dictionary<string, bool> SecretHasValue);

public sealed record SettingsSchemaDto(
    IReadOnlyList<SettingsSectionSchemaDto> Sections,
    IReadOnlyList<SettingsServiceDefinitionDto> Services,
    IReadOnlyList<SettingsProviderDefinitionDto> Providers,
    IReadOnlyList<SettingsRuntimeDependencyDto> RuntimeDependencies);

public sealed record SettingsSectionSchemaDto(
    string SectionName,
    int SchemaVersion,
    bool HasSecrets,
    IReadOnlyList<SettingsSectionPropertyDefinitionDto> Properties);

public sealed record SettingsSectionPropertyDefinitionDto(
    string Name,
    string ValueType,
    bool IsSecret,
    bool IsEditable,
    bool IsRequired,
    JsonNode? DefaultValue);

public sealed record SettingsServiceDefinitionDto(
    string ServiceId,
    string SectionName,
    IReadOnlyList<string> ProviderIds,
    IReadOnlyList<SettingsDependencyFieldDto> ServiceFields);

public sealed record SettingsProviderDefinitionDto(
    string ProviderId,
    string ServiceId,
    string Kind,
    string ProviderSectionKey,
    string? ProviderSettingsSection,
    string? SectionName,
    string? RuntimeSectionName,
    IReadOnlyList<SettingsDependencyFieldDto> RequiredSectionFields,
    IReadOnlyList<SettingsRuntimeKeyRequirementDto> RequiredRuntimeKeys);

public sealed record SettingsDependencyFieldDto(
    string SectionName,
    string FieldName);

public sealed record SettingsRuntimeKeyRequirementDto(
    string Key);

public sealed record ProviderFieldMetadataDto(
    string Name,
    string Kind,
    bool Required,
    IReadOnlyList<string>? EnumOptions,
    bool Operative);

public sealed record ProviderFieldValueDto(
    string Name,
    string? Value,
    bool IsSecret,
    bool HasValue);

public sealed record RuntimeKeyDto(
    string Key,
    bool HasValue,
    string? CurrentValue);

public sealed record ProviderEditorStateDto(
    string ProviderId,
    string ProviderKind,
    string ProviderSection,
    string? ModeId,
    bool HasExplicitMode,
    bool IsDefaultMode,
    bool ConnectionConfigured,
    IReadOnlyList<string> ConnectionMissingFields,
    bool CanActivate,
    IReadOnlyList<string> ActivationBlockers,
    IReadOnlyDictionary<string, ProviderFieldValueDto> Fields,
    IReadOnlyList<RuntimeKeyDto> RuntimeDependencies,
    IReadOnlyList<string> OperativeFields,
    IReadOnlyList<string> DiagnosticFields,
    IReadOnlyList<ProviderFieldMetadataDto> FieldMetadata);

public sealed record ServiceEditorReadinessDto(
    string Status,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings);

public sealed record ServiceEditorStateDto(
    string ServiceId,
    string ActiveProviderId,
    IReadOnlyList<ProviderEditorStateDto> Providers,
    ServiceEditorReadinessDto Readiness);

public sealed record SetActiveProviderRequest(string ProviderId);

public sealed record ProviderFieldsUpdateRequest(IReadOnlyDictionary<string, JsonElement> Fields);

/// <summary>
/// Runtime-owned dependency surfaced on the Infrastructure tab (R-5.7).
/// <see cref="Source"/> — one of <c>appsettings</c>, <c>environment</c>, <c>compose</c>,
/// <c>user-secrets</c>, <c>unknown</c> — records which configuration provider
/// last wrote <see cref="CurrentValue"/>. <see cref="HasValue"/> mirrors
/// <see cref="CurrentValue"/>; it exists so callers that might mask
/// <see cref="CurrentValue"/> (e.g. for secret-adjacent keys) can still tell
/// whether the key is present. <see cref="Kind"/> discriminates the UI probe
/// strategy — <c>url</c> values get HEAD reachability probes, <c>path</c> values
/// get filesystem existence+writability probes, <c>other</c> is ineligible.
/// <see cref="IsSecret"/> is set when <see cref="CurrentValue"/> is masked
/// server-side so the UI can render a "••••" placeholder instead of the literal.
/// </summary>
public sealed record SettingsRuntimeDependencyDto(
    string Key,
    string? CurrentValue,
    bool ReadOnly,
    IReadOnlyList<string> UsedByProviderIds,
    string Source,
    bool HasValue,
    bool IsSecret,
    string Kind);

public sealed record SettingsReadinessDto(
    DateTime GeneratedUtc,
    IReadOnlyList<SettingsServiceReadinessDto> Services,
    IReadOnlyList<string> GlobalBlockers);

public sealed record SettingsServiceReadinessDto(
    string ServiceId,
    string Status,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings);

public sealed record UpdateSettingsSectionRequest(
    string RowVersion,
    JsonObject Payload);

/// <summary>
/// Global default chat model (R-9). Persisted in the <c>ChatDefaults</c> application settings section.
/// </summary>
public sealed record ChatDefaultsDto(
    string? DefaultModelId,
    bool OverrideAllChatModels,
    double? Temperature,
    double? TopP,
    string? ReasoningEffort,
    string? SamplingParametersJson,
    string RowVersion);

/// <summary>
/// Typed update for <see cref="ChatDefaultsDto"/>; maps to <c>PUT /api/settings/sections/ChatDefaults</c> payload merge semantics.
/// </summary>
public sealed record UpdateChatDefaultsRequest(
    string RowVersion,
    string? DefaultModelId,
    bool OverrideAllChatModels,
    double? Temperature,
    double? TopP,
    string? ReasoningEffort,
    string? SamplingParametersJson);

public sealed record SettingsModelDto(
    string ModelId,
    string DisplayName,
    string Provider,
    string? Description,
    string? ReasoningChoicesJson,
    string? RuntimeConfigJson,
    bool IsActive,
    int? DisplayOrder,
    DateTime Created,
    DateTime? Updated);

public sealed record CreateSettingsModelRequest(
    string ModelId,
    string DisplayName,
    string Provider,
    string? Description,
    string? ReasoningChoicesJson,
    string? RuntimeConfigJson,
    bool IsActive,
    int? DisplayOrder);

public sealed record UpdateSettingsModelRequest(
    string ModelId,
    string DisplayName,
    string Provider,
    string? Description,
    string? ReasoningChoicesJson,
    string? RuntimeConfigJson,
    bool IsActive,
    int? DisplayOrder);

public sealed record AddModelCatalogDto(
    string ModelId,
    string DisplayName,
    string? Description,
    int? DisplayOrder,
    bool IsActive);

public sealed record AddModelInstallHuggingFaceDto(
    string Repository,
    string QuantIncludePattern,
    string MmprojIncludePattern,
    string TargetDirectory);

public sealed record AddModelInstallExistingAliasDto(
    string RouterModelId);

public sealed record AddModelInstallDto(
    string Source,
    string RouterModelId,
    string RuntimeProfileId,
    AddModelInstallHuggingFaceDto? HuggingFace,
    AddModelInstallExistingAliasDto? ExistingAlias,
    int? RouterContextSize = null,
    int? RouterCacheRamMib = null);

public sealed record AddModelRequest(
    string Provider,
    AddModelCatalogDto Catalog,
    JsonObject? ProviderConfig,
    AddModelInstallDto? Install);

public sealed record AddModelErrorDto(
    string Code,
    string Step,
    string Message,
    string? Remediation);

public sealed record AddModelOperationDto(
    string Kind,
    SettingsModelDto? CatalogModel,
    string Status,
    AddModelErrorDto? Error);

public sealed record AddModelResponse(
    string? OperationId,
    AddModelOperationDto AddOperation);

public sealed record SettingsRuntimeProfileDto(
    string ProfileId,
    string DisplayName,
    string? Description,
    bool CombineSystemAndDeveloperMessages,
    string? ThoughtBlockPattern,
    string SamplingParametersJson,
    string ThinkingControlJson,
    IReadOnlyList<string> Providers,
    DateTime Created,
    DateTime? Updated);

public sealed record CreateRuntimeProfileRequest(
    string ProfileId,
    string DisplayName,
    string? Description,
    bool CombineSystemAndDeveloperMessages,
    string? ThoughtBlockPattern,
    string SamplingParametersJson,
    string ThinkingControlJson,
    IReadOnlyList<string>? Providers = null);

public sealed record UpdateRuntimeProfileRequest(
    string ProfileId,
    string DisplayName,
    string? Description,
    bool CombineSystemAndDeveloperMessages,
    string? ThoughtBlockPattern,
    string SamplingParametersJson,
    string ThinkingControlJson,
    IReadOnlyList<string>? Providers = null);

public sealed record EmbeddingsRebuildResponse(
    Guid JobId,
    string Status);

public sealed record EmbeddingsRebuildConflictResponse(
    string Error,
    Guid JobId,
    string Status);

public sealed record LlamaRuntimeInventoryItemDto(
    string RouterModelId,
    string RuntimeState,
    string? ModelPath,
    string? MmprojPath,
    bool HasModelFile,
    bool HasMmprojFile,
    IReadOnlyList<string> CatalogModelIds,
    int NotebookReferenceCount,
    int? RouterContextSize = null,
    int? RouterCacheRamMib = null);

public sealed record StartModelDownloadRequest(
    string Repository,
    string QuantIncludePattern,
    string MmprojIncludePattern,
    string RouterModelId,
    string TargetDirectory,
    // Catalog registration fields: when provided, the API auto-creates a
    // matching catalog Model row once the download completes so the alias is
    // immediately selectable as a chat target. Missing fields disable
    // auto-registration; the operator can create the row manually via Settings.
    string? CatalogModelId = null,
    string? CatalogDisplayName = null,
    string? CatalogRuntimeProfileId = null,
    string? CatalogDescription = null,
    bool? CatalogIsActive = null,
    int? CatalogDisplayOrder = null,
    string? CatalogLoadParamsJson = null,
    bool? CatalogParallelToolCalls = null,
    int? CatalogRouterContextSize = null,
    int? CatalogRouterCacheRamMib = null);

public sealed record ModelDownloadOperationDto(
    string OperationId,
    string Status,
    string RouterModelId,
    double? Progress,
    string? ErrorMessage,
    string? LogLine,
    AddModelErrorDto? Error = null);

public sealed record LlamaRuntimeLoadRequest(string RouterModelId);

public sealed record LlamaRuntimeUnloadRequest(string RouterModelId);

/// <summary>
/// Wire shape for a non-chat service routing mode (see <c>ServiceMode</c>).
/// </summary>
public sealed record ServiceModeDto(
    string ServiceName,
    string ModeId,
    string ProviderSection,
    string? ModelId,
    string? RequestPresetJson,
    bool Enabled,
    bool IsDefault);

/// <summary>
/// A chat-dispatch target derived from the model catalog + assistant metadata
/// (R-9.2). Returned to the settings UI so users can pick which model an
/// assistant points at.
/// </summary>
public sealed record ChatTargetDto(
    string ModelId,
    string DisplayName,
    string Provider,
    bool IsActive,
    bool HasLocalRuntime);

/// <summary>
/// Readiness snapshot for a single provider section (e.g. <c>AzureOpenAiEmbedding</c>,
/// <c>LlamaCpp</c>, <c>LocalServiceHosts:EmbeddingsBaseUrl</c>). Surfaced by the
/// routing readiness composition in Phase B.
/// </summary>
public sealed record ProviderSectionReadinessDto(
    string SectionName,
    bool Configured,
    IReadOnlyList<string> MissingFields);

/// <summary>
/// Composed readiness for a non-chat service mode (R-5.2 / R-7.1 composition).
/// Status is <c>ready</c> only when every component probe succeeded; otherwise
/// <c>blocked</c> with a non-empty <see cref="Blockers"/> list.
/// </summary>
public sealed record ModeReadinessDto(
    string Service,
    string ModeId,
    string Status,
    IReadOnlyList<string> Blockers,
    string? ProviderSection,
    string? ModelId,
    string? RuntimeState);

/// <summary>
/// Composed readiness for a chat target (R-9.2). <see cref="ReferenceKind"/> is
/// <c>direct</c>, <c>defaultedTo</c>, or <c>overriddenToDefault</c> depending on
/// global default chat settings and assistant model configuration.
/// </summary>
public sealed record ChatTargetReadinessDto(
    string ModelId,
    string Provider,
    string Status,
    IReadOnlyList<string> Blockers,
    string? RuntimeState,
    int AssistantUsageCount,
    string ReferenceKind);

public sealed record ServiceModeRollupDto(
    string Service,
    int Ready,
    int Total,
    IReadOnlyList<ModeReadinessDto> Modes);

public sealed record ChatTargetsRollupDto(
    int Ready,
    int Total,
    IReadOnlyList<ChatTargetReadinessDto> Targets);

public sealed record ProviderConnectionIssueDto(
    string Section,
    IReadOnlyList<string> MissingFields);

public sealed record LlamaRuntimeSnapshotDto(
    int LoadedAliases,
    int TotalAliases,
    IReadOnlyList<string> MissingArtifactAliases);

/// <summary>
/// Overview composite returned by <c>GET /api/settings/overview</c>. Summarizes
/// service-mode readiness counts, chat-target readiness counts, provider
/// connection gaps, and local llama runtime state.
/// </summary>
public sealed record SettingsOverviewDto(
    DateTime GeneratedUtc,
    IReadOnlyList<ServiceModeRollupDto> ServiceModeReadiness,
    ChatTargetsRollupDto ChatTargets,
    IReadOnlyList<ProviderConnectionIssueDto> ProviderIssues,
    LlamaRuntimeSnapshotDto LlamaRuntime);

/// <summary>
/// Consumers referencing a single provider connection section. Drives the
/// "Used by services" chips on the Connections tab (R-5.5 / R-8.1 consumer
/// visibility). <see cref="Modes"/> lists every <c>ServiceModes</c> row whose
/// <c>ProviderSection</c> equals <c>{section}</c>; <see cref="ChatTargets"/>
/// lists catalog models whose provider maps to <c>{section}</c> and that are
/// referenced by at least one active Assistant.
/// </summary>
public sealed record ConnectionUsageDto(
    string Section,
    IReadOnlyList<ConnectionUsageModeRef> Modes,
    IReadOnlyList<ConnectionUsageChatTargetRef> ChatTargets);

public sealed record ConnectionUsageModeRef(
    string Service,
    string ModeId,
    bool IsDefault);

public sealed record ConnectionUsageChatTargetRef(
    string ModelId,
    int AssistantCount);

/// <summary>
/// Phase E (R-5.7): a single probe request item. <see cref="Kind"/> is
/// <c>"url"</c> or <c>"path"</c>. <see cref="Id"/> is an opaque caller-supplied
/// correlation id (usually the dependency key) so the response can be joined
/// back to the originating row. <see cref="Target"/> is the URL or filesystem
/// path to probe.
/// </summary>
public sealed record InfrastructureProbeRequestItemDto(
    string Id,
    string Kind,
    string Target);

public sealed record InfrastructureProbeRequestDto(
    IReadOnlyList<InfrastructureProbeRequestItemDto> Items);

/// <summary>
/// Phase E (R-5.7): a single probe result. The <see cref="Kind"/> discriminator
/// selects which fields are populated.
/// <para>For <c>kind == "url"</c>: <see cref="Reachable"/> is true iff the server
/// answered with a non-5xx status. <see cref="StatusCode"/> carries the HTTP
/// status (null on transport failure). <see cref="Exists"/> / <see cref="Writable"/>
/// are null.</para>
/// <para>For <c>kind == "path"</c>: <see cref="Exists"/> is true iff the path
/// exists; <see cref="Writable"/> is true iff the path is a directory and a
/// sentinel file could be created+deleted. <see cref="Reachable"/> /
/// <see cref="StatusCode"/> are null.</para>
/// <see cref="Error"/> carries a short diagnostic when the probe could not
/// succeed; never thrown out of the service.
/// </summary>
public sealed record InfrastructureProbeResultDto(
    string Id,
    string Kind,
    string Target,
    int? LatencyMs,
    string? Error,
    bool? Reachable,
    int? StatusCode,
    bool? Exists,
    bool? Writable);

public sealed record InfrastructureProbeBatchDto(
    DateTime GeneratedUtc,
    IReadOnlyList<InfrastructureProbeResultDto> Results);

/// <summary>
/// Phase F (R-6.10 / R-7.1): per-alias runtime status snapshot surfaced by
/// <c>GET /api/settings/llama/runtime/status</c>. Derived from existing
/// inventory + coordinator state — does not mutate any locks.
/// <para><see cref="Loaded"/> mirrors the inventory runtime state
/// (<c>"loaded"</c> vs everything else). <see cref="InProgress"/> is true iff a
/// load operation is currently being orchestrated through
/// <c>NotebookModelRuntimeService</c>. <see cref="LastLoadStartedAt"/> /
/// <see cref="LastLoadDurationMs"/> / <see cref="LastError"/> are populated
/// from the most recent terminal operation for this alias (if any).</para>
/// </summary>
public sealed record LlamaRuntimeAliasStatusDto(
    string Alias,
    bool Loaded,
    bool InProgress,
    string RuntimeState,
    string? RouterModelId,
    DateTime? LastLoadStartedAt,
    double? LastLoadDurationMs,
    string? LastError);
